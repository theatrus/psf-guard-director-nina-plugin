using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace PsfGuard.Director.Runtime;

// This host only negotiates and supervises a planner. It exposes no equipment
// or evaluation API until the execution adapter can enforce decision freshness.
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
    internal bool IsReady => ready && Volatile.Read(ref disposed) == 0;
    internal int? ProcessId => process?.Id;
    internal int? ExitCode => process is { HasExited: true } ? process.ExitCode : null;

    private RuntimeSession(Stream pipe, Process? process = null, RuntimeBundle? bundle = null)
    {
        this.pipe = pipe;
        this.process = process;
        this.bundle = bundle;
    }

    internal static async Task<RuntimeSession> StartAsync(string pluginDirectory, string rigId, CancellationToken token)
    {
        var bundle = await RuntimeBundle.OpenAsync(pluginDirectory, token).ConfigureAwait(false);
        NamedPipeServerStream? pipe = null;
        RuntimeSession? session = null;
        Process? process = null;
        try
        {
            var name = $"psf-guard-director-{Guid.NewGuid():N}";
            pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            var info = new ProcessStartInfo(bundle.Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(bundle.Executable)!
            };
            info.ArgumentList.Add("--pipe");
            info.ArgumentList.Add(name);
            info.ArgumentList.Add("--parent-pid");
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            token.ThrowIfCancellationRequested();
            process = Process.Start(info) ?? throw new IOException("Director runtime did not start.");
            session = new RuntimeSession(pipe, process, bundle);
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
        catch
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            else
            {
                pipe?.Dispose();
                process?.Dispose();
                bundle.Dispose();
            }
            throw;
        }
    }

    internal static async Task<RuntimeSession> ConnectTestStreamAsync(Stream stream, string rigId, CancellationToken token)
    {
        var session = new RuntimeSession(stream);
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
            result.GetProperty("rig_id").GetString() != rigId)
            throw new InvalidDataException("Runtime identity or version mismatch.");
        ready = true;
    }

    internal Task PingAsync(CancellationToken token) => SendControlAsync("ping", "pong", token);
    internal Task ShutdownAsync(CancellationToken token) => SendControlAsync("shutdown", "stopped", token);

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
