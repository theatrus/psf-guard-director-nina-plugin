using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Immutable;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.SimulatorProbe;

// Test-only operator access to a disposable loopback server. Never shipped in the plugin ZIP.
internal sealed class CoordinatorProbe : IAsyncDisposable
{
    private sealed record Fixture(string Endpoint, Guid CoordinatorInstanceId, Guid CatalogId, Guid RigId,
        bool ActivateSimulatorPlan = false, bool ExerciseOutage = false, bool PublicAcquisition = false, bool PublicUnsafe = false, bool AutomaticWorkloads = false, bool LocalTargetScheduling = false);
    private readonly HttpClient operatorClient;
    private readonly Uri endpoint;
    private readonly CoordinatorPairing pairing;
    internal bool ActivateSimulatorPlan { get; private init; }
    internal string? ProgramRevision { get; private set; }
    internal bool ExerciseOutage { get; private init; }
    internal bool PublicAcquisition { get; private init; }
    internal bool PublicUnsafe { get; private init; }
    internal bool AutomaticWorkloads { get; private init; }
    internal bool LocalTargetScheduling { get; private init; }
    internal bool LocalTargetsVerified { get; private set; }
    internal bool AutomaticWorkloadVerified { get; private set; }
    internal Uri Endpoint => endpoint;
    internal bool LiveStatusVerified { get; private set; }
    internal bool EquipmentReviewVerified { get; private set; }
    internal Guid? AllocationId => allocation?.Envelope.AllocationId;
    private CoordinatorAllocation? allocation;
    private string? allocationRoot;
    private string? previewRevision;
    private bool outageRequested;
    private readonly string statusSession = Guid.NewGuid().ToString("D");
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };
    internal string RigId => pairing.Binding.RigId.ToString("D");
    private CoordinatorProbe(Uri endpoint, HttpClient operatorClient, CoordinatorPairing pairing) =>
        (this.endpoint, this.operatorClient, this.pairing) = (endpoint, operatorClient, pairing);

    internal static async Task<CoordinatorProbe?> PairAsync(string root, Guid profile, CancellationToken token)
    {
        var path = Path.Combine(root, "coordinator-fixture.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 8192) throw new InvalidDataException("Invalid coordinator fixture.");
        var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(path, token)) ?? throw new InvalidDataException();
        var endpoint = new Uri(fixture.Endpoint);
        if (!endpoint.IsLoopback || endpoint.Scheme != "http" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0
            || endpoint.Fragment.Length != 0 || fixture.CoordinatorInstanceId == Guid.Empty || fixture.CatalogId == Guid.Empty || fixture.RigId == Guid.Empty)
            throw new InvalidDataException("Coordinator probe requires an explicit disposable loopback server and binding.");
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = endpoint };
        CoordinatorPairing? pairing = null;
        try
        {
            using var issued = await http.PostAsJsonAsync($"api/director/v1/rigs/{fixture.RigId:D}/pairing-token",
                new { coordinator_instance_id = fixture.CoordinatorInstanceId, catalog_id = fixture.CatalogId }, token);
            issued.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await issued.Content.ReadAsByteArrayAsync(token));
            var code = body.RootElement.GetProperty("data").GetProperty("pairing_token").GetString()!;
            using var client = new CoordinatorPairingClient(endpoint);
            pairing = await client.PairAsync(code, profile, token);
            if (pairing.Binding != new CoordinatorBinding(fixture.CoordinatorInstanceId, fixture.CatalogId, fixture.RigId, profile))
                throw new InvalidDataException("Coordinator fixture pairing changed identity.");
            DirectorCredentialStore.Store(endpoint, pairing);
            if (DirectorCredentialStore.Read(endpoint, profile)?.Binding != pairing.Binding) throw new InvalidDataException("Pairing vault readback failed.");
            return new(endpoint, http, pairing) { ActivateSimulatorPlan = fixture.ActivateSimulatorPlan, ExerciseOutage = fixture.ExerciseOutage, PublicAcquisition = fixture.PublicAcquisition, PublicUnsafe = fixture.PublicUnsafe, AutomaticWorkloads = fixture.AutomaticWorkloads, LocalTargetScheduling = fixture.LocalTargetScheduling };
        }
        catch
        {
            if (pairing is not null)
            {
                // Best-effort cleanup must not replace the primary validation/vault failure.
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var response = await http.DeleteAsync($"api/director/v1/rigs/{pairing.Binding.RigId:D}/clients/{pairing.ClientId:D}", cleanup.Token);
                }
                catch (Exception) { }
                try { DirectorCredentialStore.Forget(endpoint, profile); }
                catch (Exception) { }
            }
            http.Dispose();
            throw;
        }
    }

    // Explicit operator setup for a disposable fixture, never an acquisition API
    // available to a paired production client. Readback uses that paired client.
    internal async Task<DirectorProgram> ActivateAndReadProgramAsync(string root, DirectorConfiguration configuration,
        ImmutableDictionary<string, string> filterNames, DirectorTarget target, CancellationToken token)
    {
        if (!ActivateSimulatorPlan) throw new InvalidOperationException("Server plan fixture was not explicitly enabled.");
        var binding = pairing.Binding;
        if (PublicAcquisition)
        {
            using var reportsResponse = await operatorClient.GetAsync($"api/director/v1/rigs/{RigId}/equipment-reports", token);
            reportsResponse.EnsureSuccessStatusCode();
            using var reports = JsonDocument.Parse(await reportsResponse.Content.ReadAsByteArrayAsync(token));
            var report = reports.RootElement.GetProperty("data").EnumerateArray().Single();
            if (report.GetProperty("client_id").GetGuid() != pairing.ClientId || !report.GetProperty("accepted_revision").ValueKind.Equals(JsonValueKind.Null)
                || report.GetProperty("configuration").Deserialize<DirectorConfiguration>(Wire)?.Id != configuration.Id)
                throw new InvalidDataException("Native equipment report did not match the paired simulator.");
            using var beforeResponse = await operatorClient.GetAsync("api/director/v1/catalogs/director-simulator/rig/profile", token);
            beforeResponse.EnsureSuccessStatusCode();
            using var before = JsonDocument.Parse(await beforeResponse.Content.ReadAsByteArrayAsync(token));
            if (before.RootElement.GetProperty("data").GetProperty("profile").GetProperty("configuration").ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("Reporting equipment silently changed the active rig setup.");
            var manual = await OperatorAsync(HttpMethod.Put, "catalogs/director-simulator/rig/profile", new
            {
                expected_revision = 0,
                optics = new
                {
                    value = new
                    {
                        sensor_width_px = 1280,
                        sensor_height_px = 1024,
                        pixel_size_um = 3.76,
                        focal_length_mm = 250.0,
                        aperture_mm = (double?)null,
                        rotation = new { mode = "fixed", angle_degrees = 0.0 }
                    },
                    source = new { kind = "manual" }
                },
                site = (object?)null,
                horizon = (object?)null,
                sky_quality = (object?)null,
                limits = new
                {
                    value = new
                    {
                        minimum_altitude_degrees = 20.0,
                        maximum_altitude_degrees = 90.0,
                        meridian_exclusion = new { before_ms = 0, after_ms = 0 }
                    },
                    source = new { kind = "manual" }
                }
            }, token);
            var accepted = await OperatorAsync(HttpMethod.Post, $"rigs/{RigId}/equipment-reports/{pairing.ClientId:D}/accept", new
            {
                binding.CoordinatorInstanceId,
                binding.CatalogId,
                report_id = report.GetProperty("report_id").GetGuid(),
                expected_revision = manual.GetProperty("profile").GetProperty("revision").GetUInt64()
            }, token);
            if (accepted.GetProperty("configuration").GetProperty("value").Deserialize<DirectorConfiguration>(Wire)?.Id != configuration.Id
                || accepted.GetProperty("optics").GetRawText() != manual.GetProperty("profile").GetProperty("optics").GetRawText())
                throw new InvalidDataException("Review changed manual optics or accepted the wrong native configuration.");
            EquipmentReviewVerified = true;
        }
        else
            await OperatorAsync(HttpMethod.Put, $"rigs/{RigId}/equipment", new
            {
                binding.CoordinatorInstanceId,
                binding.CatalogId,
                configuration,
                filterNames,
                optics = new
                {
                    sensor_width_px = 1280,
                    sensor_height_px = 1024,
                    pixel_size_um = 3.76,
                    focal_length_mm = 250.0,
                    aperture_mm = (double?)null,
                    rotation = new { mode = "fixed", angle_degrees = 0.0 }
                },
                site = (object?)null,
                horizon = (object?)null,
                limits = (object?)null,
                reported_at_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, token);
        var projectIds = new List<Guid>();
        for (var targetIndex = 0; targetIndex < (LocalTargetScheduling ? 2 : 1); targetIndex++)
        {
            var frame = targetIndex == 0 ? target : target with { Name = "Higher priority remote target", IcrsRaMas = (target.IcrsRaMas + 1080000) % 1296000000 };
            var project = Guid.NewGuid();
            projectIds.Add(project);
            await OperatorAsync(HttpMethod.Post, "projects", new { id = project, name = "Director ASCOM server-plan fixture" }, token);
            await OperatorAsync(HttpMethod.Put, $"projects/{project:D}/framing", new
            {
                project_id = project,
                revision = 0,
                updated_at_ms = 0,
                target_name = frame.Name,
                center = new { ra_degrees = frame.IcrsRaMas / 3600000.0, dec_degrees = frame.IcrsDecMas / 3600000.0 },
                position_angle_degrees = 0.0,
                mosaic = new { rows = 1, columns = 1, overlap_percent = 0 },
                panel_rig_id = binding.RigId,
                panel = new { width_degrees = 2.0, height_degrees = 1.5 },
                shown_rig_ids = Array.Empty<Guid>(),
                rig_framings = Array.Empty<object>(),
                survey_id = "dss2_color",
                view_fov_degrees = 5.0
            }, token);
            var objectives = Enumerable.Range(0, LocalTargetScheduling ? targetIndex == 0 ? 2 : 1 : 3).Select(i => new
            {
                id = Guid.NewGuid(),
                bandpass_id = $"smoke_{i}",
                purpose = "simulator",
                goal = new { kind = "frames", value = 1 },
                priority = targetIndex == 0 ? 3 - i : 10
            }).ToArray();
            var contributions = objectives.Select((objective, i) => new
            {
                id = Guid.NewGuid(),
                objective_id = objective.id,
                rig_id = binding.RigId,
                template = new
                {
                    template_guid = (Guid?)null,
                    template_id = (int?)null,
                    name = $"Simulator filter {i}",
                    filter_name = filterNames[$"filter-{i}"],
                    gain = (int?)null,
                    offset = (int?)null,
                    bin = 1,
                    readout_mode = 0
                },
                exposure_seconds = PublicUnsafe ? 30.0 : 1.0,
                panel_ids = Array.Empty<string>(),
                enabled = true
            }).ToArray();
            await OperatorAsync(HttpMethod.Put, $"projects/{project:D}/plan", new
            { project_id = project, revision = 0, updated_at_ms = 0, objectives, contributions }, token);
            var preview = await OperatorAsync(HttpMethod.Post, $"projects/{project:D}/activation/preview", new { }, token);
            await OperatorAsync(HttpMethod.Post, $"projects/{project:D}/activation/apply",
                new { preview_digest = preview.GetProperty("preview_digest").GetString() }, token);
        }
        using var client = new CoordinatorProgramClient(endpoint, Credential);
        var cache = new CoordinatorPreviewCache(root, endpoint, binding, configuration);
        var first = await client.ReadAndCachePreviewAsync(binding, configuration, cache, token);
        // A new client and cache instance must accept an unconditional repeat.
        using var restarted = new CoordinatorProgramClient(endpoint, Credential);
        var second = await restarted.ReadAndCachePreviewAsync(binding, configuration,
            new CoordinatorPreviewCache(root, endpoint, binding, configuration), token);
        if (first.ETag != second.ETag || first.Envelope.Program.Assignment.Id != second.Envelope.Program.Assignment.Id
            || second.Envelope.Omitted.Length != 0 || second.Envelope.Program.Assignment.Goals.Length != 3
            || second.Envelope.Program.Assignment.Goals.Any(g => g.Requested != 1 || g.ExposureMs != (PublicUnsafe ? 30000UL : 1000UL))
            || second.Envelope.Program.Targets.Length != (LocalTargetScheduling ? 2 : 1))
            throw new InvalidDataException("Server program does not match the bounded simulator fixture.");
        previewRevision = second.Envelope.Revision;
        var admission = new
        {
            binding.CoordinatorInstanceId,
            binding.CatalogId,
            allocation_id = Guid.NewGuid(),
            client_id = pairing.ClientId,
            preview_revision = previewRevision
        };
        if (AutomaticWorkloads)
        {
            await OperatorAsync(HttpMethod.Put, $"rigs/{RigId}/workload-policy", new
            {
                coordinator_instance_id = binding.CoordinatorInstanceId,
                expected_revision = 0,
                policy = new
                {
                    rig_id = binding.RigId,
                    catalog_id = binding.CatalogId,
                    client_id = pairing.ClientId,
                    profile_id = binding.ProfileId,
                    profile_revision = second.Envelope.Rig.ProfileRevision,
                    configuration_id = configuration.Id,
                    project_ids = projectIds,
                    enabled = true,
                    revision = 1
                }
            }, token);
            using var work = new CoordinatorWorkloadClient(Path.Combine(Path.GetDirectoryName(root)!, "public-state"), endpoint, binding, pairing.ClientId, configuration, Credential,
                localTargetScheduling: LocalTargetScheduling);
            allocation = (await work.RequestAsync(token)).Allocation ?? throw new InvalidDataException("Commissioned work was not issued.");
            if ((await work.RequestAsync(token)).Allocation?.Fingerprint != allocation.Fingerprint)
                throw new InvalidDataException("Automatic request retry changed its immutable grant.");
        }
        else
        {
            await OperatorAsync(HttpMethod.Post, $"rigs/{RigId}/allocation", admission, token);
            // Repeat operator admission models a lost successful HTTP response.
            await OperatorAsync(HttpMethod.Post, $"rigs/{RigId}/allocation", admission, token);
        }
        using var allocations = new CoordinatorAllocationClient(endpoint, Credential);
        allocation = await allocations.ReadAsync(binding, pairing.ClientId, configuration, token);
        allocationRoot = root;
        var allocationCache = new CoordinatorAllocationCache(root, endpoint, binding, pairing.ClientId, configuration);
        allocationCache.Store(allocation, NowMs());
        VerifyCachedAllocation(configuration);
        if (allocation.Envelope.Snapshot.Program.Assignment.Id == second.Envelope.Program.Assignment.Id)
            throw new InvalidDataException("Allocation reused the preview's assignment identity.");
        ProgramRevision = allocation.Envelope.Snapshot.Revision;
        return allocation.Envelope.Snapshot.Program;
    }

    internal void VerifyLocalTargets(IReadOnlyList<PsfGuard.Director.Plugin.Acquisition.CaptureEvidence> captures, IReadOnlyList<string> hooks)
    {
        if (!LocalTargetScheduling) return;
        var program = allocation!.Envelope.Snapshot.Program;
        var expected = program.Bindings.Single(b => b.GoalId == program.Assignment.Goals.Single(g => g.Priority == 10).Id).TargetId;
        var ordered = captures.OrderBy(c => c.StartedAt).ToArray();
        if (ordered.Length != 3 || ordered[0].Intent.Program?.TargetId != expected
            || ordered[1].Intent.Program?.TargetId == expected || ordered[1].Intent.Program?.TargetId != ordered[2].Intent.Program?.TargetId
            || !hooks.SequenceEqual(new[] { "BeforeNewTarget", "AfterEachExposure", "AfterNewTarget", "AfterEachTarget",
                "BeforeNewTarget", "AfterEachExposure", "AfterEachExposure", "AfterNewTarget", "AfterEachTarget" }))
            throw new InvalidDataException("Local scheduling did not prioritize and visit both targets through native hooks.");
        LocalTargetsVerified = true;
    }

    private static ulong NowMs() => checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    internal async Task VerifyAutomaticWorkloadAsync(string root, DirectorConfiguration configuration, CancellationToken token)
    {
        using var client = new CoordinatorWorkloadClient(root, endpoint, pairing.Binding, pairing.ClientId, configuration, Credential,
            localTargetScheduling: LocalTargetScheduling);
        var waiting = await client.RequestAsync(token);
        if (waiting.Allocation is not null || waiting.RetryAfterSeconds != 30)
            throw new InvalidDataException("Pending assessment authorized duplicate acquisition.");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/director/v1/rigs/{RigId}/workloads/request");
        request.Headers.Authorization = new("Bearer", pairing.Token);
        request.Headers.Add("X-PSF-Director-Profile", pairing.Binding.ProfileId.ToString("D"));
        request.Content = JsonContent.Create(new
        {
            coordinator_instance_id = pairing.Binding.CoordinatorInstanceId,
            catalog_id = pairing.Binding.CatalogId,
            request_id = AllocationId,
            configuration_id = configuration.Id,
            execution_mode = LocalTargetScheduling ? "local_sequence_v1" : "prepared_target_v1"
        });
        using var response = await operatorClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        var data = document.RootElement.GetProperty("data");
        if (data.GetProperty("state").GetString() != "released" || data.GetProperty("workload").GetProperty("terminal_sequence").GetUInt64() != 6)
            throw new InvalidDataException("Automatic session did not seal its six capture events.");
        AutomaticWorkloadVerified = true;
    }
    private void VerifyCachedAllocation(DirectorConfiguration configuration)
    {
        var cached = new CoordinatorAllocationCache(allocationRoot!, endpoint, pairing.Binding, pairing.ClientId, configuration).Read(NowMs());
        if (allocation is null || cached?.Fingerprint != allocation.Fingerprint)
            throw new InvalidDataException("Offline allocation did not retain the issued identity and budgets.");
    }

    private ValueTask<string?> Credential(CancellationToken _) => ValueTask.FromResult<string?>(
        DirectorCredentialStore.Read(endpoint, pairing.Binding.ProfileId)?.Token
        ?? throw new InvalidOperationException("Simulator credential disappeared."));

    internal async Task BeginOutageAsync(string root, DirectorConfiguration configuration, CancellationToken token)
    {
        if (!ExerciseOutage) return;
        if (!ActivateSimulatorPlan) throw new InvalidOperationException("Outage testing requires the server plan fixture.");
        outageRequested = true;
        await File.WriteAllTextAsync(Path.Combine(root, "stop-server.request"), "ready", token);
        await WaitForMarker(root, "server-stopped", token);
        using var client = new CoordinatorAllocationClient(endpoint, Credential);
        try
        {
            await client.ReadAsync(pairing.Binding, pairing.ClientId, configuration, token);
            throw new InvalidDataException("The isolated server was still reachable during the outage.");
        }
        catch (CoordinatorIntakeException error) when (error.Failure == CoordinatorIntakeFailure.Transport) { }
        VerifyCachedAllocation(configuration);
    }

    internal async Task EndOutageAsync(string root, CancellationToken token)
    {
        if (!ExerciseOutage || !outageRequested) return;
        await File.WriteAllTextAsync(Path.Combine(root, "start-server.request"), "ready", token);
        await WaitForMarker(root, "server-started", token);
        outageRequested = false;
    }

    internal async Task VerifyProgramAsync(string root, DirectorConfiguration configuration, bool delivered, CancellationToken token)
    {
        if (!ActivateSimulatorPlan) return;
        using var client = new CoordinatorProgramClient(endpoint, Credential);
        var preview = await client.ReadAndCachePreviewAsync(pairing.Binding, configuration,
            new CoordinatorPreviewCache(root, endpoint, pairing.Binding, configuration), token);
        if (!delivered && preview.Envelope.Revision != previewRevision)
            throw new InvalidDataException("Server restart changed unchanged program identity.");
        if (delivered && (preview.Envelope.Revision == previewRevision || preview.Envelope.Program.Assignment.Goals.Length != 3
            || preview.Envelope.Program.Assignment.Goals.Any(g => g.Pending != 1 || g.Accepted != 0)))
            throw new InvalidDataException("Server did not retain exactly one pending capture per goal after duplicate delivery.");
        using var allocations = new CoordinatorAllocationClient(endpoint, Credential);
        var current = await allocations.ReadAsync(pairing.Binding, pairing.ClientId, configuration, token);
        if (current.Fingerprint != allocation?.Fingerprint)
            throw new InvalidDataException("Server restart or receipt delivery changed the issued allocation.");
        VerifyCachedAllocation(configuration);
    }

    internal async Task ReportStatusAsync(DirectorTarget target, string reason, CancellationToken token)
    {
        if (!ActivateSimulatorPlan) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/director/v1/rigs/{RigId}/status")
        {
            Content = JsonContent.Create(new
            {
                pairing.Binding.CoordinatorInstanceId,
                pairing.Binding.CatalogId,
                session_id = statusSession,
                reported_at_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                program_revision = previewRevision,
                status = new
                {
                    allocation_id = AllocationId,
                    allocation_revision = ProgramRevision,
                    phase = "waiting",
                    wait_reason = reason,
                    target_name = target.Name,
                    safety = "simulated",
                    connectivity = "online",
                    queue_depth = 0,
                    pointing = new { ra_degrees = target.IcrsRaMas / 3600000.0, dec_degrees = target.IcrsDecMas / 3600000.0 }
                }
            }, options: Wire)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Credential(token));
        request.Headers.Add("X-PSF-Director-Profile", pairing.Binding.ProfileId.ToString("D"));
        using var response = await operatorClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var ack = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        if (!ack.RootElement.GetProperty("data").GetProperty("accepted").GetBoolean())
            throw new InvalidDataException("Coordinator did not accept the simulator status.");
        if (reason == "simulator_outage_test" && ack.RootElement.GetProperty("data").GetProperty("program_changed").GetBoolean())
            throw new InvalidDataException("Allocation identity caused a false plan-change hint.");
        using var listed = await operatorClient.GetAsync("api/director/v1/rigs/status", token);
        listed.EnsureSuccessStatusCode();
        using var statuses = JsonDocument.Parse(await listed.Content.ReadAsByteArrayAsync(token));
        var own = statuses.RootElement.GetProperty("data").EnumerateArray().Single(row =>
            row.GetProperty("rig").GetProperty("id").GetGuid() == pairing.Binding.RigId);
        LiveStatusVerified = own.GetProperty("status").GetProperty("session_id").GetString() == statusSession
            && own.GetProperty("status").GetProperty("payload").GetProperty("wait_reason").GetString() == reason;
        if (!LiveStatusVerified) throw new InvalidDataException("Live rig inventory did not expose the paired report.");
    }

    private static async Task WaitForMarker(string root, string name, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(45));
        while (!File.Exists(Path.Combine(root, name))) await Task.Delay(100, wait.Token);
    }

    private async Task<JsonElement> OperatorAsync(HttpMethod method, string path, object value, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, $"api/director/v1/{path}") { Content = JsonContent.Create(value, options: Wire) };
        using var response = await operatorClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        if (!body.RootElement.GetProperty("success").GetBoolean()) throw new InvalidDataException("Simulator operator setup failed.");
        return body.RootElement.GetProperty("data").Clone();
    }

    internal async Task<CoordinatorCheckpointResult> DeliverAsync(string root, RuntimeController runtime, LedgerIdentity ledger, CancellationToken token)
    {
        ValueTask<string?> Credential(CancellationToken _) => ValueTask.FromResult(DirectorCredentialStore.Read(endpoint, pairing.Binding.ProfileId)?.Token
            ?? throw new InvalidOperationException("Simulator credential disappeared."))!;
        async Task<LedgerEventPage> Events(ulong after, int limit, CancellationToken cancellation)
        {
            var result = await runtime.ReadEventsAsync(after, limit, cancellation);
            return result.Value ?? throw new InvalidDataException("Simulator ledger read failed.");
        }
        using (var client = new CoordinatorCheckpointClient(root, endpoint, pairing.Binding, ledger, Credential))
        {
            var result = await client.DeliverAsync(Events, programRevision: previewRevision, maxPages: 1, token: token);
            if (result.DeliveredEvents != 6) throw new InvalidDataException("Expected three reservation/save receipt pairs.");
        }
        using (var restarted = new CoordinatorCheckpointClient(root, endpoint, pairing.Binding, ledger, Credential))
        {
            var result = await restarted.DeliverAsync(Events, programRevision: previewRevision, token: token);
            if (!result.CaughtUp || result.AcknowledgedThrough != 6 || result.DeliveredEvents != 0)
                throw new InvalidDataException("Checkpoint restart did not retain its cursor.");
        }
        using var replay = new CoordinatorCheckpointClient(Path.Combine(root, "replay"), endpoint, pairing.Binding, ledger, Credential);
        var duplicate = await replay.DeliverAsync(Events, programRevision: previewRevision, token: token);
        if (!duplicate.CaughtUp || duplicate.AcknowledgedThrough != 6 || duplicate.DeliveredEvents != 6)
            throw new InvalidDataException("Coordinator did not acknowledge idempotent replay.");
        return duplicate;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await operatorClient.DeleteAsync($"api/director/v1/rigs/{pairing.Binding.RigId:D}/clients/{pairing.ClientId:D}", cleanup.Token);
            response.EnsureSuccessStatusCode();
        }
        finally { DirectorCredentialStore.Forget(endpoint, pairing.Binding.ProfileId); operatorClient.Dispose(); }
    }
}
