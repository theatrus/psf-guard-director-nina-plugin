using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace PsfGuard.Director.Runtime;

// The host transports recommendations; it owns no equipment or dispatch permit.
internal sealed class RuntimeSession : IAsyncDisposable
{
    private readonly RuntimeBundle? bundle;
    private readonly Stream pipe;
    private readonly Process? process;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource lifetime = new();
    private ulong nextId = 1;
    private volatile bool ready;
    private int disposed;
    private string rigId = "";
    private readonly bool storageEnabled;
    private LedgerIdentity? ledgerIdentity;
    private PlannerAssignment? ledgerAssignment;
    internal bool IsReady => ready && Volatile.Read(ref disposed) == 0;
    internal int? ProcessId => process?.Id;
    internal int? ExitCode => process is { HasExited: true } ? process.ExitCode : null;

    private RuntimeSession(Stream pipe, Process? process = null, RuntimeBundle? bundle = null, bool storageEnabled = false)
    {
        this.pipe = pipe;
        this.process = process;
        this.bundle = bundle;
        this.storageEnabled = storageEnabled;
    }

    internal static async Task<RuntimeSession> StartAsync(string pluginDirectory, string rigId, CancellationToken token, string? storageDirectory = null)
    {
        storageDirectory = NormalizeStorageDirectory(storageDirectory);
        var bundle = await RuntimeBundle.OpenAsync(pluginDirectory, token).ConfigureAwait(false);
        NamedPipeServerStream? pipe = null;
        RuntimeSession? session = null;
        Process? process = null;
        Task<string>? startupError = null;
        try
        {
            var name = $"psf-guard-director-{Guid.NewGuid():N}";
            pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            var info = new ProcessStartInfo(bundle.Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(bundle.Executable)!
            };
            info.ArgumentList.Add("--pipe");
            info.ArgumentList.Add(name);
            info.ArgumentList.Add("--parent-pid");
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (storageDirectory is not null)
            {
                info.ArgumentList.Add("--state-directory");
                info.ArgumentList.Add(storageDirectory);
            }
            token.ThrowIfCancellationRequested();
            process = Process.Start(info) ?? throw new IOException("Director runtime did not start.");
            startupError = ReadBoundedErrorAsync(process.StandardError);
            session = new RuntimeSession(pipe, process, bundle, storageDirectory is not null);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var connection = pipe.WaitForConnectionAsync(deadline.Token);
            var exit = process.WaitForExitAsync(deadline.Token);
            try
            {
                if (await Task.WhenAny(connection, exit).ConfigureAwait(false) == exit)
                    throw new IOException("Director runtime exited before connection.");
                await connection.ConfigureAwait(false);
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var peer) || peer != (uint)process.Id)
                    throw new IOException("Control pipe peer is not the owned runtime.");
                await session.HandshakeAsync(rigId, deadline.Token).ConfigureAwait(false);
                return session;
            }
            finally
            {
                await deadline.CancelAsync().ConfigureAwait(false);
                try { await exit.ConfigureAwait(false); } catch (OperationCanceledException) { }
                try { await connection.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        catch (Exception error)
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            else
            {
                pipe?.Dispose();
                process?.Dispose();
                bundle.Dispose();
            }
            if (startupError is not null)
            {
                var diagnostic = await startupError.ConfigureAwait(false);
                if (error is not OperationCanceledException && !string.IsNullOrWhiteSpace(diagnostic))
                    throw new IOException($"Director runtime startup failed: {diagnostic.Trim()}", error);
            }
            throw;
        }
    }

    private static async Task<string> ReadBoundedErrorAsync(StreamReader reader)
    {
        var buffer = new char[512];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(length)).ConfigureAwait(false);
                if (count == 0) break;
                length += count;
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        return new string(buffer, 0, length);
    }

    internal static async Task<RuntimeSession> ConnectTestStreamAsync(Stream stream, string rigId, CancellationToken token, bool storageEnabled = false)
    {
        var session = new RuntimeSession(stream, storageEnabled: storageEnabled);
        try
        {
            await session.HandshakeAsync(rigId, token).ConfigureAwait(false);
            return session;
        }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task HandshakeAsync(string rigId, CancellationToken token)
    {
        if (string.IsNullOrEmpty(rigId) || rigId.Length > 128 || rigId.Any(c => c < '!' || c > '~'))
            throw new InvalidDataException("Invalid rig identity.");
        var result = await ExchangeAsync(0, new JsonObject
        {
            ["type"] = "hello",
            ["runtime_version"] = RuntimeContract.RuntimeVersion,
            ["engine_version"] = RuntimeContract.EngineVersion,
            ["contract_version"] = RuntimeContract.ContractVersion,
            ["rig_id"] = rigId
        }, "ready", token).ConfigureAwait(false);
        if (result.GetProperty("runtime_version").GetString() != RuntimeContract.RuntimeVersion ||
            result.GetProperty("engine_version").GetString() != RuntimeContract.EngineVersion ||
            result.GetProperty("contract_version").GetInt32() != RuntimeContract.ContractVersion ||
            result.GetProperty("rig_id").GetString() != rigId || result.GetProperty("storage_enabled").GetBoolean() != storageEnabled)
            throw new InvalidDataException("Runtime identity or version mismatch.");
        this.rigId = rigId;
        ready = true;
    }

    internal Task PingAsync(CancellationToken token) => SendControlAsync("ping", "pong", token);
    internal Task ShutdownAsync(CancellationToken token) => SendControlAsync("shutdown", "stopped", token);

    internal async Task<PlannerEvaluation> EvaluateAsync(PlannerRequest request, CancellationToken token)
    {
        var encoded = PlannerContract.Encode(request, rigId);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (ledgerIdentity is not null) throw new InvalidOperationException("Use durable ledger accounting after opening a ledger.");
            return await EvaluateAdmittedAsync(request, encoded, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<PlannerEvaluation> EvaluateAdmittedAsync(PlannerRequest request, JsonObject encoded, CancellationToken token)
    {
        try
        {
            if (!IsReady) throw new IOException("Director runtime session is unavailable.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var payload = await ExchangeAsync(checked(nextId++), new JsonObject
            {
                ["type"] = "evaluate",
                ["request"] = encoded
            }, "decision", deadline.Token).ConfigureAwait(false);
            var result = PlannerContract.Decode(payload.GetProperty("response"), request);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch { Abort(); throw; }
    }

    internal static string? NormalizeStorageDirectory(string? directory)
    {
        if (directory is null) return null;
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new ArgumentException("Storage requires an existing absolute directory.", nameof(directory));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    internal Task<LedgerResult<LedgerIdentity>> OpenLedgerAsync(PlannerRequest request, CancellationToken token)
    {
        var encoded = PlannerContract.Encode(request, rigId);
        if (request.State.RigId != rigId) throw new ArgumentException("Ledger state belongs to another rig.", nameof(request));
        return SendLedgerAsync(new JsonObject { ["action"] = "open", ["request"] = encoded }, true, response =>
        {
            var result = LedgerContract.Decode(response, "opened", "info", value => LedgerContract.ReadIdentity(value.GetProperty("info"), request.Assignment));
            if (result.Value is not null) { ledgerIdentity = result.Value; ledgerAssignment = request.Assignment; }
            return result;
        }, token);
    }

    internal Task<LedgerResult<LedgerReservation>> ReserveAsync(string captureId, PlannerState state, CancellationToken token)
    {
        LedgerContract.CheckId(captureId);
        ArgumentNullException.ThrowIfNull(state);
        if (state.RigId != rigId) throw new ArgumentException("Ledger state belongs to another rig.", nameof(state));
        var operation = new JsonObject
        {
            ["action"] = "reserve",
            ["capture_id"] = captureId,
            ["state"] = JsonSerializer.SerializeToNode(state, PlannerContract.Options)
        };
        return SendLedgerAsync(operation, false, response => LedgerContract.Decode(response, "reserved", "outcome",
            value => LedgerContract.ReadReservation(value.GetProperty("outcome"), ledgerAssignment!, captureId, state)), token);
    }

    internal Task<LedgerResult<LedgerAttempt>> RecordAsync(string captureId, LedgerEvidence evidence, CancellationToken token)
    {
        LedgerContract.CheckId(captureId);
        var operation = new JsonObject { ["action"] = "record", ["capture_id"] = captureId, ["evidence"] = LedgerContract.EncodeEvidence(evidence) };
        return SendLedgerAsync(operation, false, response => LedgerContract.Decode(response, "recorded", "attempt", value =>
        {
            var attempt = LedgerContract.ReadAttempt(value.GetProperty("attempt"), ledgerAssignment!, captureId);
            if (attempt.Evidence != evidence) throw new InvalidDataException("Recorded evidence differs from the submitted result.");
            return attempt;
        }), token);
    }

    internal Task<LedgerResult<LedgerLookup>> FindAttemptAsync(string captureId, CancellationToken token)
    {
        LedgerContract.CheckId(captureId);
        return SendLedgerAsync(new JsonObject { ["action"] = "attempt", ["capture_id"] = captureId }, false,
            response => LedgerContract.Decode(response, "found", "attempt", value => new LedgerLookup(
                value.GetProperty("attempt").ValueKind == JsonValueKind.Null ? null
                    : LedgerContract.ReadAttempt(value.GetProperty("attempt"), ledgerAssignment!, captureId))), token);
    }

    internal Task<LedgerResult<LedgerEventPage>> ReadEventsAsync(ulong after, int limit, CancellationToken token)
    {
        if (after > long.MaxValue) throw new ArgumentOutOfRangeException(nameof(after));
        if (limit is < 1 or > LedgerContract.MaxPage) throw new ArgumentOutOfRangeException(nameof(limit));
        return SendLedgerAsync(new JsonObject { ["action"] = "events", ["after"] = after, ["limit"] = limit }, false,
            response => LedgerContract.Decode(response, "events", "events",
                value => LedgerContract.ReadPage(value, ledgerIdentity!, ledgerAssignment!, after, limit)), token);
    }

    private async Task<LedgerResult<T>> SendLedgerAsync<T>(JsonObject operation, bool opening,
        Func<JsonElement, LedgerResult<T>> decode, CancellationToken token) where T : class
    {
        LedgerContract.Encode(operation);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Invalid API ordering is a caller error, not an interrupted exchange.
            if (!storageEnabled) throw new InvalidOperationException("Runtime storage was not enabled.");
            if (opening ? ledgerIdentity is not null : ledgerIdentity is null)
                throw new InvalidOperationException(opening ? "A ledger is already open." : "Open the ledger first.");
            try
            {
                if (!IsReady) throw new IOException("Director runtime session is unavailable.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var payload = await ExchangeAsync(checked(nextId++), new JsonObject { ["type"] = "ledger", ["operation"] = operation }, "ledger", deadline.Token).ConfigureAwait(false);
                var result = decode(payload.GetProperty("response"));
                deadline.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch { Abort(); throw; }
        }
        finally { gate.Release(); }
    }

    private async Task SendControlAsync(string command, string expected, CancellationToken token)
    {
        // A caller cancelled before admission must not tear down another request.
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!IsReady) throw new IOException("Director runtime session is unavailable.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await ExchangeAsync(checked(nextId++), new JsonObject { ["type"] = command }, expected, deadline.Token).ConfigureAwait(false);
            if (command == "shutdown")
            {
                ready = false;
                // Closing acknowledges the final reply before waiting for child exit.
                pipe.Dispose();
                if (process is not null)
                {
                    await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                    if (process.ExitCode != 0) throw new IOException("Director runtime did not exit cleanly.");
                }
            }
        }
        catch { Abort(); throw; }
        finally { gate.Release(); }
    }

    private async Task<JsonElement> ExchangeAsync(ulong id, JsonObject payload, string expected, CancellationToken token)
    {
        await PipeProtocol.WriteAsync(pipe, new JsonObject
        {
            ["protocol_version"] = RuntimeContract.ProtocolVersion,
            ["session_id"] = sessionId,
            ["request_id"] = id,
            ["payload"] = payload
        }, token).ConfigureAwait(false);
        return PipeProtocol.ValidateReply(await PipeProtocol.ReadAsync(pipe, token).ConfigureAwait(false), sessionId, id, expected);
    }

    private void Abort()
    {
        ready = false;
        pipe.Dispose();
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception && process.HasExited) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        ready = false;
        await lifetime.CancelAsync().ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Abort();
            if (process is not null)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            process?.Dispose();
            bundle?.Dispose();
            gate.Release();
            lifetime.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
