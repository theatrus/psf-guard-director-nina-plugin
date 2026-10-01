using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.IO;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Interfaces.Mediator;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

// The first public mode executes an already prepared single target. It does not
// pretend to implement automatic centering, focus, guiding, or meridian flips.
[Export(typeof(DirectorAcquisition))]
public sealed class DirectorAcquisition
{
    private readonly IProfileService profiles;
    private readonly ICameraMediator camera;
    private readonly ITelescopeMediator telescope;
    private readonly IFilterWheelMediator filters;
    private readonly ISafetyMonitorMediator safety;
    private readonly IImagingMediator imaging;
    private readonly IImageSaveMediator saves;
    private readonly IImageHistoryVM history;
    private readonly INighttimeCalculator nighttime;
    private static readonly Guid PluginId = new("03a1d13e-67eb-4e24-a407-82bce7e576a5");
    internal static string StateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PSFGuardDirector", "acquisition");
    internal string LocalStateRoot { get; init; } = StateRoot;
    internal string? LastRunDirectory { get; private set; }
    internal LedgerIdentity? LastLedger { get; private set; }

    [ImportingConstructor]
    public DirectorAcquisition(IProfileService profiles, ICameraMediator camera, ITelescopeMediator telescope,
        IFilterWheelMediator filters, ISafetyMonitorMediator safety, IImagingMediator imaging,
        IImageSaveMediator saves, IImageHistoryVM history, INighttimeCalculator nighttime)
    {
        this.profiles = profiles; this.camera = camera; this.telescope = telescope; this.filters = filters;
        this.safety = safety; this.imaging = imaging; this.saves = saves; this.history = history; this.nighttime = nighttime;
    }

    internal static IReadOnlyList<string> PolicyIssues(DirectorSessionOptions options, bool requireEnabled = true)
    {
        var issues = new List<string>();
        if (requireEnabled && !options.EnableAcquisition) issues.Add("Enable prepared-target acquisition to run this session.");
        if (options.Safety != DirectorSafetyPolicy.RequireMonitor) issues.Add("Public acquisition requires a connected safety monitor.");
        if (new[] { options.SlewCenter, options.Focus, options.Guiding, options.Dither, options.MeridianFlip }.Any(x => x != DirectorOperationOwner.Sequence))
            issues.Add("Prepared-target mode requires sequence ownership for centering, autofocus, guiding, dithering and meridian flips.");
        if (options.Startup != DirectorOperationOwner.Director || options.Shutdown != DirectorOperationOwner.Director)
            issues.Add("Prepared-target mode requires Director startup and shutdown (unpark/park).");
        return issues;
    }

    internal async Task ReportEquipmentAsync(DirectorSessionContainer container, CancellationToken token)
    {
        var options = container.Options.Clone();
        var issues = options.ValidateSettings().Concat(PolicyIssues(options, false)).ToArray();
        if (issues.Length != 0) throw new InvalidOperationException(string.Join(" ", issues));
        using var owner = new AcquisitionLease(LocalStateRoot);
        var profile = profiles.ActiveProfile.Id;
        var settings = new PluginOptionsAccessor(profiles, PluginId);
        using var connection = new DirectorConnection(() => profiles.ActiveProfile.Id, () => false,
            () => settings.GetValueString("CoordinatorUrl", ""), _ => { },
            readHttpConsent: () => settings.GetValueString("HttpConsentOrigin", ""));
        var endpoint = connection.ResolvedEndpoint ?? throw new InvalidOperationException("Configure and pair a Director coordinator first.");
        var pairing = connection.ReadPairing() ?? throw new InvalidOperationException("Pairing credentials are unavailable. Re-pair first.");
        ValueTask<string?> Credential(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var current = DirectorCredentialStore.Read(endpoint, profile);
            if (profiles.ActiveProfile.Id != profile || current?.ClientId != pairing.ClientId || current.Binding != pairing.Binding)
                throw new InvalidOperationException("Pairing or profile changed while reporting equipment.");
            return ValueTask.FromResult<string?>(current.Token);
        }
        using var constraints = new NinaConstraintSnapshot(profiles);
        var (constraintBinding, equipmentBinding, configuration) = ReadNativeEquipment(options, profile, pairing.Binding.RigId.ToString("D"), constraints);
        var reader = new NinaEquipmentSnapshot(profiles, camera, filters, telescope);
        var names = reader.ReadFilterNames(equipmentBinding, configuration);
        using var client = new CoordinatorEquipmentClient(endpoint, Credential, connection.AllowInsecureHttp);
        container.Report(container.Display with { Phase = "Reporting equipment", Operation = "Equipment report" });
        var ack = await client.ReportAsync(pairing.Binding, pairing.ClientId, Guid.NewGuid(), configuration, names,
            checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), token);
        await Credential(token);
        if (constraints.Refresh(constraintBinding).Revision != equipmentBinding.ConstraintRevision || reader.Read(equipmentBinding).Id != configuration.Id
            || !System.Text.Json.JsonSerializer.Serialize(container.Options).Equals(System.Text.Json.JsonSerializer.Serialize(options), StringComparison.Ordinal))
            throw new InvalidOperationException("Equipment report was received, but local settings changed. Report the current setup again.");
        container.Report(container.Display with
        {
            Phase = ack.AcceptedRevision is null ? "Equipment reported; awaiting operator review" : "Equipment report already reviewed",
            Rig = pairing.Binding.RigId.ToString("D"),
            Operation = "",
            Connectivity = "Online"
        });
    }

    private (NinaConstraintBinding Constraints, NinaEquipmentBinding Equipment, DirectorConfiguration Configuration) ReadNativeEquipment(
        DirectorSessionOptions options, Guid profile, string rig, NinaConstraintSnapshot constraintsReader)
    {
        var horizon = options.Horizon == DirectorHorizonPolicy.NinaProfile ? profiles.ActiveProfile.AstrometrySettings.HorizonFilePath : null;
        var constraints = new NinaConstraintBinding(profile,
            options.Horizon == DirectorHorizonPolicy.NinaProfile ? NinaHorizonMode.RequiredFile : NinaHorizonMode.FixedMinimum,
            horizon, options.MinimumAltitude,
            new(checked((ulong)(options.MeridianBeforeMinutes * 60000)), checked((ulong)(options.MeridianAfterMinutes * 60000))));
        var native = constraintsReader.Refresh(constraints);
        var wheel = profiles.ActiveProfile.FilterWheelSettings.Id;
        var equipment = new NinaEquipmentBinding(profile, rig, native.Revision,
            profiles.ActiveProfile.CameraSettings.Id, wheel == "No_Device" ? null : wheel,
            wheel == "No_Device" ? [new("fixed-filter", null, null)] : profiles.ActiveProfile.FilterWheelSettings.FilterWheelFilters
                .Select(f => new NinaFilterBinding($"filter-{f.Position}", f.Position, f.Name)).ToImmutableArray(),
            false, 0, profiles.ActiveProfile.TelescopeSettings.Id);
        return (constraints, equipment, new NinaEquipmentSnapshot(profiles, camera, filters, telescope).Read(equipment));
    }

    internal async Task ExecuteAsync(DirectorSessionContainer container, IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var options = container.Options.Clone();
        var issues = options.ValidateSettings().Concat(PolicyIssues(options)).ToArray();
        if (issues.Length != 0) throw new InvalidOperationException(string.Join(" ", issues));
        using var owner = new AcquisitionLease(LocalStateRoot);
        var profile = profiles.ActiveProfile.Id;
        var settings = new PluginOptionsAccessor(profiles, PluginId);
        using var connection = new DirectorConnection(() => profiles.ActiveProfile.Id, () => false,
            () => settings.GetValueString("CoordinatorUrl", ""), _ => { },
            readHttpConsent: () => settings.GetValueString("HttpConsentOrigin", ""));
        var endpoint = connection.ResolvedEndpoint ?? throw new InvalidOperationException("Configure and pair a Director coordinator first.");
        var pairing = connection.ReadPairing() ?? throw new InvalidOperationException("Pairing credentials are unavailable. Re-pair before acquisition.");
        ValueTask<string?> Credential(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var current = DirectorCredentialStore.Read(endpoint, profile);
            if (current?.ClientId != pairing.ClientId || current.Binding != pairing.Binding)
                throw new InvalidOperationException("Pairing changed during acquisition.");
            return ValueTask.FromResult<string?>(current.Token);
        }
        var rig = pairing.Binding.RigId.ToString("D");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromHours(options.MaximumHours));
        using var interlock = new NinaSafetyInterlock(profiles, safety, TimeProvider.System);
        using (var fresh = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            fresh.CancelAfter(TimeSpan.FromSeconds(15));
            while (interlock.Read().Safety != PlannerSafety.Safe) await Task.Delay(100, fresh.Token);
        }
        interlock.Arm();
        using var safetyCancellation = interlock.Interrupted.Register(lifetime.Cancel);
        using var constraintsReader = new NinaConstraintSnapshot(profiles);
        var (constraintBinding, equipmentBinding, configuration) = ReadNativeEquipment(options, profile, rig, constraintsReader);
        var equipmentReader = new NinaEquipmentSnapshot(profiles, camera, filters, telescope);
        using var intake = new CoordinatorAllocationClient(endpoint, Credential, connection.AllowInsecureHttp);
        Report("Reading issued allocation", "Online");
        var allocation = await intake.ReadAsync(pairing.Binding, pairing.ClientId, configuration, lifetime.Token);
        var program = allocation.Envelope.Snapshot.Program;
        if (program.Targets.Length != 1 || program.Targets[0].PositionAngleMas is not null
            || program.Configuration.EnableSlewCenter || program.Configuration.DitherEvery != 0
            || program.Recipes.Any(r => r.DitherOverride is > 0))
            throw new InvalidOperationException("Prepared-target mode requires one target, no rotation request, and sequence-owned centering/dithering.");
        var target = program.Targets.Single();
        var assignment = program.Assignment;
        var orientation = NinaEarthOrientation.Read(NinaEarthOrientation.DatabasePath, assignment.ValidFromMs, assignment.ExpiresAtMs, lifetime.Token);
        var inputs = new NinaGeometryInputs(1, options.MaximumAltitude, orientation.Orientation,
            assignment.Goals.Select(g => new DirectorGoalLimits(g.Id, options.MinimumAltitude, options.MaximumAltitude, 0)).ToImmutableArray());
        var geometry = new NinaGeometrySnapshot(constraintsReader, equipmentReader);
        var runRoot = Path.Combine(LocalStateRoot, profile.ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        LastRunDirectory = runRoot;
        new CoordinatorAllocationCache(runRoot, endpoint, pairing.Binding, pairing.ClientId, configuration, connection.AllowInsecureHttp)
            .Store(allocation, Now());
        var ledgerDirectory = Directory.CreateDirectory(Path.Combine(runRoot, "ledger")).FullName;
        await using var runtime = new RuntimeController(Path.GetDirectoryName(typeof(DirectorPlugin).Assembly.Location)!, ledgerDirectory);
        var launched = false;
        Task? watchdog = null;
        Task? telemetryTask = null;
        using var background = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        NinaTargetContainer? context = null;
        CoordinatorCheckpointClient? checkpoint = null;
        CoordinatorSessionReporter? telemetry = null;
        Exception? executionError = null;
        try
        {
            await runtime.StartAsync(rig, lifetime.Token);
            Check();
            var snapshot = Snapshot();
            var ledger = Require(await runtime.OpenGeometryAsync(program, snapshot.Constraints, snapshot.State, lifetime.Token));
            LastLedger = ledger;
            // Server accepts this only once. Nothing on disk can replay this permit.
            await intake.StartOnceAsync(pairing.Binding, pairing.ClientId, allocation, ledger, lifetime.Token);
            launched = true;
            watchdog = WatchdogAsync();
            checkpoint = new(runRoot, endpoint, pairing.Binding, ledger, Credential, connection.AllowInsecureHttp);
            telemetry = new CoordinatorSessionReporter(endpoint, pairing.Binding, Credential, connection.AllowInsecureHttp);
            if (options.LiveStatus) telemetryTask = TelemetryAsync(telemetry, ledger.LedgerId);
            var hooks = new NinaSessionHooks(container, TimeProvider.System);
            context = new NinaTargetContainer(profiles, profile, target, nighttime.Calculate(), TimeProvider.System);
            context.AttachNewParent(container);
            await hooks.SelectTargetAsync(target.Id, context, progress, lifetime.Token);
            CheckPointing();
            if (options.CheckInAtStart || options.CheckInOnTarget) await CheckIn();
            var adapter = new NinaCaptureAdapter(profiles, camera, imaging, saves, history, Path.Combine(runRoot, "journal"),
                TimeSpan.FromSeconds(options.SaveTimeoutSeconds), TimeProvider.System);
            var capture = new NinaProgramCapture(equipmentReader, camera, filters, adapter);
            var preparation = new NinaPreparationItems(profiles, camera, filters, equipmentReader, TimeProvider.System, telescope);
            var dispatch = new NinaGeometryDispatch(runtime, Snapshot, Check);
            var nextCheckIn = DateTimeOffset.UtcNow;
            while (true)
            {
                Check();
                var current = Snapshot();
                var decision = Require(await runtime.EvaluateGeometryAsync(current.Constraints, current.State, lifetime.Token));
                Report(decision.Reason, container.Display.Connectivity);
                if (decision.Action == PlannerAction.Complete)
                {
                    await hooks.TargetCompletedAsync(progress, lifetime.Token);
                    break;
                }
                if (decision.Action == PlannerAction.Wait && decision.Reason == "pending_assessment") break;
                if (decision.Action == PlannerAction.Wait)
                {
                    if (options.ParkOnWait) await Park(lifetime.Token);
                    await hooks.WaitAsync(ct => Task.Delay(TimeSpan.FromSeconds(5), ct), progress, lifetime.Token);
                    await hooks.SelectTargetAsync(target.Id, context, progress, lifetime.Token);
                    continue;
                }
                if (decision.Action != PlannerAction.Acquire || decision.GoalId is null)
                    throw new InvalidOperationException($"Director stopped: {decision.Reason}.");
                CheckPointing();
                var id = Guid.NewGuid().ToString("D");
                var began = Require(await runtime.BeginGeometryPreparationAsync(id, decision.GoalId,
                    new(configuration, new(configuration.Id, target), telescope.GetInfo().AtPark, false, 0),
                    new(30000, 0, 0, 0, 10000, 1000, 5000), current.Constraints, current.State, lifetime.Token));
                if (!began.Created) throw new InvalidOperationException("Preparation is not new.");
                while (true)
                {
                    current = Snapshot();
                    var next = Require(await runtime.AdvanceGeometryPreparationAsync(id, current.Configuration, current.Constraints, current.State, lifetime.Token));
                    if (next is PreparationNext.ReadyToReserve) break;
                    if (next is not PreparationNext.Run) throw new InvalidOperationException("Preparation requires reconciliation.");
                    var block = TargetBlock();
                    var issued = preparation.Create(next, program, equipmentBinding, dispatch.Pending(next, block.ValidateContext));
                    using var operationDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    operationDeadline.CancelAfter(TimeSpan.FromSeconds(options.HookTimeoutSeconds));
                    try { await RunItem(block, issued.Item, operationDeadline.Token); }
                    finally
                    {
                        if (issued.Fence.Completion is { } completed)
                            Require(await runtime.CompletePreparationAsync(completed, CancellationToken.None));
                    }
                    if (issued.Fence.Completion?.Outcome is not PreparationOutcome.Succeeded)
                        throw new InvalidOperationException("Native preparation failed.");
                }
                current = Snapshot();
                var captureId = Guid.NewGuid().ToString("D");
                var reservation = Require(await runtime.ReserveGeometryPreparedAsync(id, captureId, current.Configuration, current.Constraints, current.State, lifetime.Token));
                var binding = Require(await runtime.FindCaptureBindingAsync(captureId, lifetime.Token)).Binding ?? throw new InvalidDataException("Missing capture binding.");
                var captureBlock = TargetBlock();
                var exposure = new NinaExposureItem(capture, reservation, binding, equipmentBinding, dispatch.Capture(id, reservation, () =>
                {
                    captureBlock.ValidateContext(); Check(); CheckPointing();
                    if (camera.GetInfo().IsExposing) throw new InvalidOperationException("Camera is already exposing.");
                    if (telescope.GetInfo().AtPark || telescope.GetInfo().Slewing || !telescope.GetInfo().TrackingEnabled)
                        throw new InvalidOperationException("Mount is not tracking and ready to expose.");
                }));
                container.Report(container.Display with { Phase = "Acquiring", Target = target.Name, Goal = decision.GoalId, Operation = "Exposure" });
                await RunItem(captureBlock, exposure, lifetime.Token);
                var evidence = exposure.Evidence;
                if (evidence?.Phase != CapturePhase.Saved) throw new InvalidOperationException("Capture requires reconciliation.");
                Require(await runtime.RecordAsync(captureId, new LedgerEvidence.Saved(captureId, checked((ulong)Math.Ceiling(evidence.TotalMs!.Value))), lifetime.Token));
                await hooks.ExposureSavedAsync(captureId, progress, lifetime.Token);
                if (DateTimeOffset.UtcNow >= nextCheckIn)
                {
                    await CheckIn();
                    nextCheckIn = DateTimeOffset.UtcNow.AddMinutes(options.CheckInMinutes);
                }
            }
            await hooks.FinishAsync(progress, lifetime.Token);
            if (options.CheckInAtEnd) await CheckIn();
            Report("Allocation finished; awaiting assessment or a new reconciled plan", container.Display.Connectivity);

            async Task CheckIn()
            {
                try
                {
                    var result = await checkpoint.DeliverAsync(async (after, limit, ct) => Require(await runtime.ReadEventsAsync(after, limit, ct)),
                        allocation.Envelope.PreviewRevision, token: lifetime.Token);
                    container.Report(container.Display with { Connectivity = "Online", QueueDepth = result.CaughtUp ? "0" : "Pending", LastCheckIn = DateTimeOffset.UtcNow.ToString("u") });
                }
                catch (CoordinatorIntakeException e) when (options.AllowOffline && OfflineFailure(e.Failure))
                { Report("Offline; capture evidence retained locally", "Offline"); }
            }
        }
        catch (Exception error)
        {
            executionError = error;
            Report(launched ? "Stopped; allocation consumed, reconciliation required" : "Acquisition admission failed", container.Display.Connectivity);
            throw;
        }
        finally
        {
            background.Cancel();
            if (watchdog is not null) await watchdog;
            if (telemetryTask is not null) await telemetryTask;
            context?.AttachNewParent(null);
            context?.NighttimeData.Ticker.Stop();
            checkpoint?.Dispose();
            if (launched)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var parked = false;
                try { await Park(cleanup.Token); parked = true; }
                catch (Exception error) when (executionError is not null)
                { throw new AggregateException("Director acquisition and shutdown failed.", executionError, error); }
                finally
                {
                    var phase = !parked ? "Shutdown failed" : executionError is null ? "Finished; parked" : "Stopped; parked; reconciliation required";
                    Report(phase, container.Display.Connectivity);
                    if (options.LiveStatus && telemetry is not null && LastLedger is not null)
                    {
                        try { await telemetry.ReportAsync(allocation, LastLedger.LedgerId, phase, target.Name, interlock.Read().Safety.ToString(), cleanup.Token); }
                        catch (Exception error) { Logger.Error(error); }
                    }
                    telemetry?.Dispose();
                }
            }
            else telemetry?.Dispose();
        }

        static ulong Now() => checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        void Check()
        {
            lifetime.Token.ThrowIfCancellationRequested(); owner.CheckClock();
            if (profiles.ActiveProfile.Id != profile || interlock.Read().Safety != PlannerSafety.Safe
                || runtime.Status.State != RuntimeState.Ready || Now() >= assignment.ExpiresAtMs)
                throw new InvalidOperationException("Director context, safety, runtime or allocation validity changed.");
        }
        NinaDispatchSnapshot Snapshot()
        {
            Check();
            var g = geometry.Read(constraintBinding, equipmentBinding, inputs);
            return new(g.Configuration, g.Constraints, new(rig, configuration.Id, Now(), assignment.ExpiresAtMs,
                interlock.Read().Safety, true, false, constraintBinding.MeridianExclusion));
        }
        void CheckPointing()
        {
            var info = telescope.GetInfo();
            if (!info.Connected || info.DeviceId != equipmentBinding.TelescopeDeviceId || info.Slewing)
                throw new InvalidOperationException("Prepared telescope is unavailable or moving.");
            var p = telescope.GetCurrentPosition().Transform(Epoch.J2000);
            var expected = new Coordinates(target.IcrsRaMas / 3600000.0, target.IcrsDecMas / 3600000.0, Epoch.J2000, Coordinates.RAType.Degrees);
            if (!double.IsFinite(p.RA) || !double.IsFinite(p.Dec) || (p - expected).Distance.Degree > 0.05)
                throw new InvalidOperationException("Prepare the allocated target with native slew/center instructions before acquisition (pointing tolerance 3 arcmin).");
        }
        NinaTargetContainer TargetBlock()
        {
            var block = new NinaTargetContainer(profiles, profile, target, nighttime.Calculate(), TimeProvider.System);
            block.AttachNewParent(container); return block;
        }
        async Task RunItem(NinaTargetContainer block, NINA.Sequencer.SequenceItem.SequenceItem item, CancellationToken ct)
        {
            block.Add(item);
            try
            {
                await block.Run(progress, ct);
                if (item.Status != SequenceEntityStatus.FINISHED) throw new InvalidOperationException("Native Director instruction did not finish.");
            }
            finally { block.Remove(item); block.AttachNewParent(null); block.NighttimeData.Ticker.Stop(); }
        }
        async Task Park(CancellationToken ct)
        {
            var info = telescope.GetInfo();
            if (!info.Connected || info.DeviceId != equipmentBinding.TelescopeDeviceId || profiles.ActiveProfile.Id != profile)
                throw new InvalidOperationException("Cannot safely park: original telescope/profile is unavailable.");
            if (!info.AtPark && !await telescope.ParkTelescope(progress, ct)) throw new IOException("Director shutdown park failed.");
        }
        async Task WatchdogAsync()
        {
            try
            {
                while (true) { await Task.Delay(250, background.Token); Check(); }
            }
            catch (OperationCanceledException) when (background.IsCancellationRequested) { }
            catch (Exception e) { Logger.Error(e); lifetime.Cancel(); }
        }
        async Task TelemetryAsync(CoordinatorSessionReporter reporter, string sessionId)
        {
            try
            {
                while (true)
                {
                    try { await reporter.ReportAsync(allocation, sessionId, container.Display.Phase, container.Display.Target, interlock.Read().Safety.ToString(), background.Token); }
                    catch (CoordinatorIntakeException e) when (options.AllowOffline && OfflineFailure(e.Failure))
                    { Logger.Info("Director telemetry offline; evidence remains in the local ledger."); }
                    await Task.Delay(TimeSpan.FromSeconds(options.StatusSeconds), background.Token);
                }
            }
            catch (OperationCanceledException) when (background.IsCancellationRequested) { }
            catch (Exception e) { Logger.Error(e); lifetime.Cancel(); }
        }
        void Report(string phase, string connectivity)
        {
            container.Report(container.Display with { Phase = phase, Rig = rig, Connectivity = connectivity, Safety = interlock.Read().Safety.ToString() });
            Logger.Info($"PSF Guard Director: {phase}");
            try { progress.Report(new ApplicationStatus { Status = phase }); }
            catch (Exception error) { Logger.Error(error); }
        }
    }

    private static bool OfflineFailure(CoordinatorIntakeFailure failure) => failure is CoordinatorIntakeFailure.Transport
        or CoordinatorIntakeFailure.Timeout or CoordinatorIntakeFailure.ServerUnavailable or CoordinatorIntakeFailure.Busy;
    private static T Require<T>(LedgerResult<T> result) where T : class => result.Value ?? throw new InvalidOperationException($"Director ledger refused: {result.Error}");
}
