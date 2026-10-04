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

// Rust selects local work; native sequence slots own target preparation. The
// prepared-target mode remains available for existing single-target sessions.
[Export(typeof(DirectorAcquisition))]
public sealed class DirectorAcquisition
{
    private readonly IProfileService profiles;
    private readonly ICameraMediator camera;
    private readonly ITelescopeMediator telescope;
    private readonly IFilterWheelMediator filters;
    private readonly ISafetyMonitorMediator safety;
    private readonly IDomeMediator dome;
    private readonly INinaActionFactory? sequenceFactory;
    private readonly IGuiderMediator? guider;
    private readonly IFocuserMediator? focuser;
    private readonly IRotatorMediator? rotator;
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
        IImageSaveMediator saves, IImageHistoryVM history, INighttimeCalculator nighttime, IDomeMediator dome,
        INinaActionFactory? sequenceFactory = null, IGuiderMediator? guider = null,
        IFocuserMediator? focuser = null, IRotatorMediator? rotator = null)
    {
        this.profiles = profiles; this.camera = camera; this.telescope = telescope; this.filters = filters;
        this.safety = safety; this.imaging = imaging; this.saves = saves; this.history = history; this.nighttime = nighttime;
        this.dome = dome;
        this.sequenceFactory = sequenceFactory; this.guider = guider; this.focuser = focuser; this.rotator = rotator;
    }

    internal static IReadOnlyList<string> PolicyIssues(DirectorSessionOptions options, bool requireEnabled = true)
    {
        var issues = new List<string>();
        if (requireEnabled && !options.EnableAcquisition) issues.Add("Enable acquisition to run this session.");
        if (requireEnabled && options.Enclosure == DirectorEnclosurePolicy.Unconfigured)
            issues.Add("Select an enclosure clearance policy before acquisition.");
        if (options.Safety != DirectorSafetyPolicy.RequireMonitor) issues.Add("Public acquisition requires a connected safety monitor.");
        if (options.Startup != DirectorOperationOwner.Director || options.Shutdown != DirectorOperationOwner.Director)
            issues.Add("Acquisition requires Director startup and shutdown (unpark/park).");
        return issues;
    }

    internal async Task ReportEquipmentAsync(DirectorSessionContainer container, CancellationToken token)
    {
        var options = container.Options.Clone();
        var issues = options.ValidateSettings().Concat(PolicyIssues(options, false)).ToArray();
        if (issues.Length != 0) throw new InvalidOperationException(string.Join(" ", issues));
        using var native = CreateNativeImaging(options);
        native?.CheckEquipment();
        NinaNativeImaging.ValidateOwnership(container, options);
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
        container.UpdateDisplay(d => d with { Phase = "Reporting equipment", Operation = "Equipment report" });
        var ack = await client.ReportAsync(pairing.Binding, pairing.ClientId, Guid.NewGuid(), configuration, names,
            checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), token);
        await Credential(token);
        if (constraints.Refresh(constraintBinding).Revision != equipmentBinding.ConstraintRevision || reader.Read(equipmentBinding).Id != configuration.Id
            || !System.Text.Json.JsonSerializer.Serialize(container.Options).Equals(System.Text.Json.JsonSerializer.Serialize(options), StringComparison.Ordinal))
            throw new InvalidOperationException("Equipment report was received, but local settings changed. Report the current setup again.");
        container.UpdateDisplay(d => d with
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
            options.SlewCenter == DirectorOperationOwner.Director,
            options.Dither == DirectorOperationOwner.Director ? checked((uint)options.DitherEveryExposures) : 0,
            profiles.ActiveProfile.TelescopeSettings.Id,
            NinaNativeImaging.Required(options) ? NinaNativeImaging.ScopeFor(profiles, options) : null);
        return (constraints, equipment, new NinaEquipmentSnapshot(profiles, camera, filters, telescope).Read(equipment));
    }

    internal async Task ExecuteAsync(DirectorSessionContainer container, IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var issues = container.Options.ValidateSettings().Concat(PolicyIssues(container.Options)).ToArray();
        if (issues.Length != 0) throw new InvalidOperationException(string.Join(" ", issues));
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        session.CancelAfter(TimeSpan.FromHours(container.Options.MaximumHours));
        if (container.Options.CheckInAtStart)
        {
            try
            {
                container.UpdateDisplay(d => d with { Phase = "Checking in saved runs" });
                var updates = new InlineProgress<CoordinatorRunCheckInProgress>(p => container.UpdateDisplay(d => d with
                { QueueDepth = $"{p.Runs} runs; {p.DeliveredEvents} events; cursor {p.AcknowledgedThrough}" }));
                await new DirectorCheckInService(profiles) { LocalStateRoot = LocalStateRoot }.RunAsync(updates, session.Token);
            }
            catch (CoordinatorIntakeException e) when (container.Options.AllowOffline && OfflineFailure(e.Failure))
            { Logger.Info("Director saved-run check-in offline; evidence retained."); }
        }
        using var owner = new AcquisitionLease(LocalStateRoot);
        do
        {
            if (!await ExecuteAllocationAsync(container, progress, owner, session.Token)) break;
        } while (container.Options.AutomaticWorkloads);
    }

    private async Task<bool> ExecuteAllocationAsync(DirectorSessionContainer container, IProgress<ApplicationStatus> progress, AcquisitionLease owner, CancellationToken token)
    {
        var options = container.Options.Clone();
        var issues = options.ValidateSettings().Concat(PolicyIssues(options)).ToArray();
        if (issues.Length != 0) throw new InvalidOperationException(string.Join(" ", issues));
        using var native = CreateNativeImaging(options);
        native?.CheckEquipment();
        NinaNativeImaging.ValidateOwnership(container, options);
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
        DirectorTarget? target = null;
        string? lastLoggedPhase = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromHours(options.MaximumHours));
        using var interlock = new NinaSafetyInterlock(profiles, safety, TimeProvider.System);
        using var enclosure = new NinaEnclosureInterlock(profiles, dome, options.Enclosure, TimeProvider.System);
        using (var fresh = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            fresh.CancelAfter(TimeSpan.FromSeconds(15));
            while (interlock.Read().Safety != PlannerSafety.Safe || enclosure.Read().Motion != RecoveryMotion.Permitted)
                await Task.Delay(100, fresh.Token);
        }
        interlock.Arm();
        enclosure.Arm();
        using var safetyCancellation = interlock.Interrupted.Register(lifetime.Cancel);
        using var enclosureCancellation = enclosure.Interrupted.Register(lifetime.Cancel);
        using var constraintsReader = new NinaConstraintSnapshot(profiles);
        var (constraintBinding, equipmentBinding, configuration) = ReadNativeEquipment(options, profile, rig, constraintsReader);
        var admittedConstraints = constraintsReader.Refresh(constraintBinding);
        if (admittedConstraints.Revision != equipmentBinding.ConstraintRevision)
            throw new InvalidOperationException("Native constraints changed during admission.");
        var equipmentReader = new NinaEquipmentSnapshot(profiles, camera, filters, telescope);
        using var intake = new CoordinatorAllocationClient(endpoint, Credential, connection.AllowInsecureHttp);
        using var workloads = new CoordinatorWorkloadClient(LocalStateRoot, endpoint, pairing.Binding, pairing.ClientId, configuration, Credential,
            connection.AllowInsecureHttp, options.LocalTargetScheduling, native is not null);
        using var previews = new CoordinatorProgramClient(endpoint, Credential, connection.AllowInsecureHttp);
        CoordinatorAllocation allocation;
        if (options.AutomaticWorkloads)
        {
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested(); owner.CheckClock();
                if (profiles.ActiveProfile.Id != profile || equipmentReader.Read(equipmentBinding).Id != configuration.Id
                    || constraintsReader.Refresh(constraintBinding).Revision != equipmentBinding.ConstraintRevision
                    || !System.Text.Json.JsonSerializer.Serialize(container.Options).Equals(System.Text.Json.JsonSerializer.Serialize(options), StringComparison.Ordinal))
                    throw new InvalidOperationException("Workload request context changed.");
                try
                {
                    Report("Requesting commissioned work", "Online");
                    var result = await workloads.RequestAsync(lifetime.Token);
                    if (result.Allocation is not null) { allocation = result.Allocation; break; }
                    Report("Waiting for eligible work or quality assessment", "Online");
                    await Task.Delay(TimeSpan.FromSeconds(result.RetryAfterSeconds), lifetime.Token);
                }
                catch (CoordinatorIntakeException e) when (options.AllowOffline && OfflineFailure(e.Failure))
                {
                    Report("Offline; waiting for workload authority", "Offline");
                    await Task.Delay(TimeSpan.FromSeconds(30), lifetime.Token);
                }
            }
        }
        else
        {
            Report("Reading issued allocation", "Online");
            allocation = await intake.ReadAsync(pairing.Binding, pairing.ClientId, configuration, lifetime.Token);
        }
        var program = allocation.Envelope.Snapshot.Program;
        if (program.Targets.IsEmpty || !options.LocalTargetScheduling && program.Targets.Length != 1
            || program.Targets.Any(t => t.PositionAngleMas is not null) && (native?.RotatorConnected != true || !program.Configuration.EnableSlewCenter)
            || options.Dither == DirectorOperationOwner.Sequence && program.Recipes.Any(r => r.DitherOverride is > 0))
            throw new InvalidOperationException("The allocation exceeds this mode's target, rotation or sequence-owned preparation capabilities.");
        DirectorPointing? previousPointing = null;
        var targetContexts = new Dictionary<string, NinaTargetContainer>(StringComparer.Ordinal);
        var filterCounts = new Dictionary<string, uint>(StringComparer.Ordinal);
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
        Task? constraintWatchdog = null;
        Task? telemetryTask = null;
        Task? checkpointTask = null;
        using var background = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using var checkpointWake = new SemaphoreSlim(0, 1);
        using var checkpointDelivery = new SemaphoreSlim(1, 1);
        NinaTargetContainer? context = null;
        CoordinatorCheckpointClient? checkpoint = null;
        CoordinatorSessionReporter? telemetry = null;
        Exception? executionError = null;
        Exception? reportingError = null;
        Exception? shutdownError = null;
        Exception? constraintError = null;
        var released = false;
        var priorityRefresh = 0;
        var terminalFailed = false;
        var mountShutdown = new NinaMountShutdown();
        var archive = new CoordinatorRunArchive(runRoot, endpoint, pairing.Binding, pairing.ClientId, connection.AllowInsecureHttp);
        try
        {
            await runtime.StartAsync(rig, lifetime.Token);
            Check();
            var snapshot = Snapshot();
            var ledger = Require(await runtime.OpenGeometryAsync(program, snapshot.Constraints, snapshot.State, lifetime.Token));
            LastLedger = ledger;
            archive.Store(new(allocation.Envelope, snapshot.Constraints, snapshot.State, ledger,
                options.AutomaticWorkloads, options.LocalTargetScheduling));
            // Server accepts this only once. Nothing on disk can replay this permit.
            await intake.StartOnceAsync(pairing.Binding, pairing.ClientId, allocation, ledger, lifetime.Token);
            launched = true;
            watchdog = WatchdogAsync();
            constraintWatchdog = Task.Run(ConstraintWatchdogAsync);
            checkpoint = new(runRoot, endpoint, pairing.Binding, ledger, Credential, connection.AllowInsecureHttp);
            telemetry = new CoordinatorSessionReporter(endpoint, pairing.Binding, Credential, connection.AllowInsecureHttp);
            if (options.LiveStatus) telemetryTask = TelemetryAsync(telemetry, ledger.LedgerId);
            var hooks = new NinaSessionHooks(container, TimeProvider.System, native is null ? null : native.ConfigureTargetSetup);
            native?.Install(container);
            if (options.CheckInAtStart) await CheckIn();
            var adapter = new NinaCaptureAdapter(profiles, camera, imaging, saves, history, Path.Combine(runRoot, "journal"),
                TimeSpan.FromSeconds(options.SaveTimeoutSeconds), TimeProvider.System);
            var capture = new NinaProgramCapture(equipmentReader, camera, filters, adapter);
            var preparation = new NinaPreparationItems(profiles, camera, filters, equipmentReader, TimeProvider.System, telescope);
            var dispatch = new NinaGeometryDispatch(runtime, Snapshot, Check);
            var nextCheckIn = DateTimeOffset.UtcNow;
            if (options.CheckInMode == DirectorCheckInMode.Live) checkpointTask = CheckpointPumpAsync();
            while (true)
            {
                Check();
                if (Volatile.Read(ref priorityRefresh) != 0)
                {
                    Report("Priority changed; parking for a refreshed workload", "Online");
                    break;
                }
                var current = Snapshot();
                var decision = Require(await runtime.EvaluateGeometryAsync(current.Constraints, current.State, lifetime.Token));
                Report(decision.Reason, null);
                if (decision.Action == PlannerAction.Complete)
                {
                    if (context is not null) await hooks.TargetCompletedAsync(progress, lifetime.Token);
                    break;
                }
                if (decision.Action == PlannerAction.Wait && decision.Reason == "pending_assessment") break;
                if (decision.Action == PlannerAction.Wait)
                {
                    if (DateTimeOffset.UtcNow >= nextCheckIn)
                    {
                        QueueCheckIn();
                        nextCheckIn = DateTimeOffset.UtcNow.AddMinutes(options.CheckInMinutes);
                    }
                    if (options.ParkOnWait)
                    {
                        if (native is not null) await native.StopGuidingAsync(progress, lifetime.Token);
                        await Park(lifetime.Token);
                    }
                    await hooks.WaitAsync(ct => Task.Delay(TimeSpan.FromSeconds(5), ct), progress, lifetime.Token);
                    // Waiting ends the target visit, so re-enter its setup on
                    // the next core selection even if the mount has not moved.
                    previousPointing = null;
                    continue;
                }
                if (decision.Action != PlannerAction.Acquire || decision.GoalId is null)
                    throw new InvalidOperationException($"Director stopped: {decision.Reason}.");
                // Resolve the core's choice, never sort targets in the adapter.
                var selected = program.Bindings.Single(b => b.GoalId == decision.GoalId);
                target = program.Targets.Single(t => t.Id == selected.TargetId);
                if (!targetContexts.TryGetValue(target.Id, out context))
                {
                    context = new NinaTargetContainer(profiles, profile, target, nighttime.Calculate(), TimeProvider.System);
                    context.AttachNewParent(container);
                    targetContexts.Add(target.Id, context);
                }
                var selectedTarget = target;
                var selectedContext = context;
                container.ShowSky(context.Target.DeepSkyObject, context.NighttimeData);
                var newTarget = previousPointing?.Target.Id != target.Id;
                var id = Guid.NewGuid().ToString("D");
                var begin = await runtime.BeginGeometryPreparationAsync(id, decision.GoalId,
                    new(configuration, previousPointing, telescope.GetInfo().AtPark,
                        native?.RotatorConnected == true && target.PositionAngleMas is not null,
                        filterCounts.GetValueOrDefault(program.Recipes.Single(r => r.Id == selected.RecipeId).FilterId)),
                    new(30000, native is not null && configuration.EnableSlewCenter ? 60000UL : 0,
                        options.Focus == DirectorOperationOwner.Director ? 120000UL : 0,
                        configuration.DitherEvery > 0 ? 30000UL : 0, 10000, 1000, 5000), current.Constraints, current.State, lifetime.Token);
                if (begin.Error == LedgerError.PreparationNotSelected)
                {
                    // No operation was issued. Conditions/time can change
                    // between selection and preparation admission.
                    Report("Reevaluating local work", null);
                    await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
                    continue;
                }
                var began = Require(begin);
                if (!began.Created) throw new InvalidOperationException("Preparation is not new.");
                var reselect = false;
                while (true)
                {
                    current = Snapshot();
                    var next = Require(await runtime.AdvanceGeometryPreparationAsync(id, current.Configuration, current.Constraints, current.State, lifetime.Token));
                    if (next is PreparationNext.ReadyToReserve) break;
                    if (next is PreparationNext.Decision stopped && CanReselect(stopped.Value, program.Recipes.Any(r => r.Moon?.Enabled == true)))
                    {
                        // A clean boundary is not an uncertain hardware result.
                        // Close the unused preparation and ask Rust for work again.
                        Require(await runtime.ClosePreparationAsync(id, lifetime.Token));
                        reselect = true;
                        break;
                    }
                    if (next is not PreparationNext.Run) throw new InvalidOperationException("Preparation requires reconciliation.");
                    var block = TargetBlock();
                    var operationName = ((PreparationNext.Run)next).Command.Operation.GetType().Name;
                    container.UpdateDisplay(d => d with { Phase = "Preparing target", Target = selectedTarget.Name, Goal = decision.GoalId, Operation = operationName });
                    container.RecordAction(selectedTarget.Name, operationName, "Started");
                    var issued = preparation.Create(next, program, equipmentBinding, dispatch.Pending(next, () =>
                        { native?.CheckTriggers(); block.ValidateContext(); }),
                        async (p, ct) =>
                        {
                            container.UpdateDisplay(d => d with { Phase = "Preparing target", Target = selectedTarget.Name, Goal = decision.GoalId, Operation = "Target setup" });
                            await hooks.SelectTargetAsync(selectedTarget.Id, selectedContext, p, ct);
                            Check(); CheckPointing();
                            if (previousPointing?.Target.Id != selectedTarget.Id) filterCounts.Clear();
                            previousPointing = new(configuration.Id, selectedTarget);
                        }, native is null ? null : (operation, p, ct) => native.PrepareAsync(operation, block, p, ct));
                    using var operationDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    operationDeadline.CancelAfter(TimeSpan.FromSeconds(options.HookTimeoutSeconds));
                    try { await RunItem(block, issued.Item, operationDeadline.Token); }
                    finally
                    {
                        if (issued.Fence.Completion is { } completed)
                        {
                            Require(await runtime.CompletePreparationAsync(completed, CancellationToken.None));
                            container.RecordAction(selectedTarget.Name, operationName, completed.Outcome.GetType().Name, completed.ElapsedMs);
                        }
                        else container.RecordAction(selectedTarget.Name, operationName, "Not dispatched");
                    }
                    if (issued.Fence.Completion?.Outcome is not PreparationOutcome.Succeeded)
                        throw new InvalidOperationException("Native preparation failed.");
                    if (next is PreparationNext.Run { Command.Operation: PreparationOperation.Dither })
                        filterCounts[program.Recipes.Single(r => r.Id == selected.RecipeId).FilterId] = 0;
                }
                if (reselect) continue;
                if (Volatile.Read(ref priorityRefresh) != 0)
                {
                    // Preparation is settled and no capture was reserved.
                    // Close it before yielding instead of starting another sub.
                    Require(await runtime.ClosePreparationAsync(id, lifetime.Token));
                    continue;
                }
                CheckPointing();
                if (options.CheckInOnTarget && newTarget) QueueCheckIn();
                current = Snapshot();
                var captureId = Guid.NewGuid().ToString("D");
                var reservation = Require(await runtime.ReserveGeometryPreparedAsync(id, captureId, current.Configuration, current.Constraints, current.State, lifetime.Token));
                if (reservation.Kind == ReservationKind.Decision && CanReselect(reservation.Decision, program.Recipes.Any(r => r.Moon?.Enabled == true)))
                {
                    Require(await runtime.ClosePreparationAsync(id, lifetime.Token));
                    continue;
                }
                if (reservation.Kind != ReservationKind.Created) throw new InvalidOperationException("Capture reservation is not newly authorized.");
                var binding = Require(await runtime.FindCaptureBindingAsync(captureId, lifetime.Token)).Binding ?? throw new InvalidDataException("Missing capture binding.");
                var captureBlock = TargetBlock();
                var exposure = new NinaExposureItem(capture, reservation, binding, equipmentBinding, dispatch.Capture(id, reservation, () =>
                {
                    captureBlock.ValidateContext(); Check(); CheckPointing();
                    if (camera.GetInfo().IsExposing) throw new InvalidOperationException("Camera is already exposing.");
                    if (telescope.GetInfo().AtPark || telescope.GetInfo().Slewing || !telescope.GetInfo().TrackingEnabled)
                        throw new InvalidOperationException("Mount is not tracking and ready to expose.");
                }));
                container.UpdateDisplay(d => d with { Phase = "Acquiring", Target = target.Name, Goal = decision.GoalId, Operation = "Exposure" });
                container.RecordAction(target.Name, "Exposure", "Started");
                using var captureDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                captureDeadline.CancelAfter(TimeSpan.FromSeconds(options.HookTimeoutSeconds + options.SaveTimeoutSeconds)
                    + TimeSpan.FromMilliseconds(binding.Recipe.ExposureMs));
                try { await RunItem(captureBlock, exposure, captureDeadline.Token); }
                catch { container.RecordAction(target.Name, "Exposure", "Failed or interrupted; reconcile"); throw; }
                var evidence = exposure.Evidence;
                if (evidence?.Phase != CapturePhase.Saved) throw new InvalidOperationException("Capture requires reconciliation.");
                Require(await runtime.RecordAsync(captureId, new LedgerEvidence.Saved(captureId, checked((ulong)Math.Ceiling(evidence.TotalMs!.Value))), lifetime.Token));
                container.RecordAction(target.Name, "Exposure", "Saved; pending assessment", checked((ulong)Math.Ceiling(evidence.TotalMs!.Value)));
                filterCounts[binding.Recipe.FilterId] = checked(filterCounts.GetValueOrDefault(binding.Recipe.FilterId) + 1);
                await hooks.ExposureSavedAsync(captureId, progress, lifetime.Token);
                if (DateTimeOffset.UtcNow >= nextCheckIn)
                {
                    QueueCheckIn();
                    nextCheckIn = DateTimeOffset.UtcNow.AddMinutes(options.CheckInMinutes);
                }
            }
            await hooks.FinishAsync(progress, lifetime.Token);
            if (options.CheckInAtEnd) await CheckIn(drain: true);
            Report(Volatile.Read(ref priorityRefresh) != 0 ? "Reconciling priority change before requesting work"
                : "Allocation finished; awaiting assessment or a new reconciled plan", null);

            void QueueCheckIn()
            {
                if (options.CheckInMode != DirectorCheckInMode.Live)
                {
                    container.UpdateDisplay(d => d with { QueueDepth = "Deferred" });
                    return;
                }
                if (checkpointWake.CurrentCount == 0) checkpointWake.Release();
            }
            async Task CheckpointPumpAsync()
            {
                try
                {
                    while (true)
                    {
                        await checkpointWake.WaitAsync(background.Token);
                        await CheckIn(background.Token);
                    }
                }
                catch (OperationCanceledException) when (background.IsCancellationRequested) { }
                catch (Exception error) { Logger.Error(error); lifetime.Cancel(); throw; }
            }
            async Task CheckIn(CancellationToken? cancellation = null, bool drain = false)
            {
                var ct = cancellation ?? lifetime.Token;
                await checkpointDelivery.WaitAsync(ct);
                try
                {
                    CoordinatorCheckpointResult result;
                    do
                    {
                        result = await checkpoint.DeliverAsync(async (after, limit, ct) => Require(await runtime.ReadEventsAsync(after, limit, ct)),
                            allocation.Envelope.PreviewRevision, token: ct);
                        container.UpdateDisplay(d => d with { Connectivity = "Online", QueueDepth = result.CaughtUp ? "0" : "Pending", LastCheckIn = DateTimeOffset.UtcNow.ToString("u") });
                    } while (drain && !result.CaughtUp);
                    if (!drain && result.CaughtUp && options.AutomaticWorkloads && options.CheckInMode == DirectorCheckInMode.Live)
                    {
                        // Progress alone changes the preview revision. Compare
                        // priority intent so ordinary saves cannot churn grants.
                        CoordinatorProgramPreview preview;
                        try { preview = await previews.ReadPreviewAsync(pairing.Binding, configuration, token: ct); }
                        catch (CoordinatorIntakeException e) when (e.Failure == CoordinatorIntakeFailure.NotReady)
                        {
                            Logger.Info("Director priority preview is not ready; retaining the current allocation.");
                            return;
                        }
                        var changed = CoordinatorPriorityRefresh.Required(program, preview.Envelope.Program);
                        if (changed && Volatile.Read(ref priorityRefresh) == 0)
                            Logger.Info("Director priority update queued for the next settled exposure boundary.");
                        Volatile.Write(ref priorityRefresh, changed ? 1 : 0);
                    }
                }
                catch (CoordinatorIntakeException e) when (options.AllowOffline && OfflineFailure(e.Failure))
                {
                    container.UpdateDisplay(d => d with { Connectivity = "Offline", QueueDepth = "Pending" });
                    Logger.Info("Director check-in offline; local scheduling continues within the allocation.");
                }
                finally { checkpointDelivery.Release(); }
            }
        }
        catch (Exception error)
        {
            executionError = Volatile.Read(ref constraintError) is { } changed
                ? new InvalidOperationException("Rig constraints changed or became unavailable; review and re-arm the session.", changed) : error;
            Report(launched ? "Stopped; allocation consumed, reconciliation required" : "Acquisition admission failed", null);
            if (!ReferenceEquals(executionError, error)) throw executionError;
            throw;
        }
        finally
        {
            background.Cancel();
            if (watchdog is not null) await watchdog;
            if (constraintWatchdog is not null) await constraintWatchdog;
            if (telemetryTask is not null) await telemetryTask;
            if (checkpointTask is not null)
            {
                // Do not let a reporting fault skip the physical shutdown.
                try { await checkpointTask; }
                catch (Exception error) { reportingError = error; executionError ??= error; }
            }
            foreach (var c in targetContexts.Values) { c.AttachNewParent(null); c.NighttimeData.Ticker.Stop(); }
            if (launched)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var parked = false;
                var stopped = false;
                var aborted = executionError is not null || lifetime.IsCancellationRequested;
                try
                {
                    // A guider failure must not prevent physical mount shutdown.
                    if (native is not null)
                        try
                        {
                            using var guiderStop = CancellationTokenSource.CreateLinkedTokenSource(cleanup.Token);
                            guiderStop.CancelAfter(TimeSpan.FromSeconds(15));
                            await native.StopGuidingAsync(progress, guiderStop.Token).WaitAsync(guiderStop.Token);
                        }
                        catch (Exception error) { shutdownError = error; executionError ??= error; aborted = true; Logger.Error(error); }
                    if (aborted && options.OnAbort == DirectorAbortPolicy.StopMount)
                    {
                        NinaMountShutdown.Stop(profiles, profile, telescope, equipmentBinding.TelescopeDeviceId!);
                        stopped = true;
                    }
                    else { await Park(cleanup.Token); parked = true; }
                    native?.CheckTriggers();
                    native?.Dispose();
                    if (options.AutomaticWorkloads && executionError is null && !lifetime.IsCancellationRequested)
                    {
                        if (Require(await runtime.FindUnresolvedAttemptAsync(cleanup.Token)).Attempt is not null
                            || Require(await runtime.FindActivePreparationAsync(cleanup.Token)).Record is not null)
                            throw new InvalidOperationException("Unresolved operations prevent terminal workload release.");
                        archive.MarkCompleted();
                        try
                        {
                            var final = await checkpoint!.DeliverAsync(async (after, limit, ct) => Require(await runtime.ReadEventsAsync(after, limit, ct)),
                                allocation.Envelope.PreviewRevision, maxPages: 256, token: cleanup.Token);
                            if (!final.CaughtUp) throw new InvalidOperationException("Capture feed is not fully delivered; workload remains outstanding.");
                            await workloads.ReleaseAsync(allocation, LastLedger!, final.AcknowledgedThrough, cleanup.Token);
                            archive.MarkReleased();
                            released = true;
                        }
                        catch (CoordinatorIntakeException e) when (options.AllowOffline && OfflineFailure(e.Failure))
                        { Report("Parked; terminal check-in pending; no successor authorized", "Offline"); }
                    }
                }
                catch (Exception error) when (executionError is not null)
                { throw new AggregateException("Director acquisition and shutdown failed.", executionError, error); }
                catch { terminalFailed = true; throw; }
                finally
                {
                    var phase = stopped ? "Stopped; tracking off; reconciliation required"
                        : !parked ? enclosure.Read().Motion != RecoveryMotion.Permitted ? "Stopped; enclosure blocks parking" : "Shutdown failed"
                        : Volatile.Read(ref constraintError) is not null ? "Stopped; parked; rig constraints changed; review required"
                        : terminalFailed || aborted ? "Stopped; parked; reconciliation required"
                        : options.AutomaticWorkloads ? released ? "Workload released; parked" : "Parked; terminal check-in pending" : "Finished; parked";
                    container.UpdateDisplay(d => d with { Operation = "" });
                    Report(phase, null);
                    if (options.LiveStatus && telemetry is not null && LastLedger is not null)
                    {
                        try { await telemetry.ReportAsync(allocation, LastLedger.LedgerId, phase, target?.Name ?? "", interlock.Read().Safety.ToString(), cleanup.Token); }
                        catch (Exception error) { Logger.Error(error); }
                    }
                    telemetry?.Dispose();
                    checkpoint?.Dispose();
                }
            }
            else { telemetry?.Dispose(); checkpoint?.Dispose(); }
        }
        if (reportingError is not null) throw new InvalidOperationException("Director check-in failed.", reportingError);
        if (shutdownError is not null) throw new InvalidOperationException("Director guider shutdown failed; reconciliation required.", shutdownError);
        return released;

        static ulong Now() => checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        void Check()
        {
            lifetime.Token.ThrowIfCancellationRequested(); owner.CheckClock();
            native?.CheckEquipment();
            if (profiles.ActiveProfile.Id != profile || interlock.Read().Safety != PlannerSafety.Safe || enclosure.Read().Motion != RecoveryMotion.Permitted
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
            native?.CheckTriggers();
            if (target is null) throw new InvalidOperationException("No core-selected target.");
            var info = telescope.GetInfo();
            if (!info.Connected || info.DeviceId != equipmentBinding.TelescopeDeviceId || info.Slewing)
                throw new InvalidOperationException("Prepared telescope is unavailable or moving.");
            var p = telescope.GetCurrentPosition().Transform(Epoch.J2000);
            var expected = new Coordinates(target.IcrsRaMas / 3600000.0, target.IcrsDecMas / 3600000.0, Epoch.J2000, Coordinates.RAType.Degrees);
            if (!double.IsFinite(p.RA) || !double.IsFinite(p.Dec) || (p - expected).Distance.Degree > 0.05)
                throw new InvalidOperationException("Prepare the selected target using native slew/center instructions in Before New Target (pointing tolerance 3 arcmin).");
        }
        NinaTargetContainer TargetBlock()
        {
            var block = new NinaTargetContainer(profiles, profile, target ?? throw new InvalidOperationException("No selected target."), nighttime.Calculate(), TimeProvider.System);
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
        Task Park(CancellationToken ct) => mountShutdown.ParkAsync(profiles, profile, telescope,
            equipmentBinding.TelescopeDeviceId!, enclosure.Read, enclosure.Interrupted, progress, ct);
        async Task WatchdogAsync()
        {
            try
            {
                while (true) { await Task.Delay(250, background.Token); Check(); }
            }
            catch (OperationCanceledException) when (background.IsCancellationRequested) { }
            catch (Exception e) { Logger.Error(e); lifetime.Cancel(); }
        }
        async Task ConstraintWatchdogAsync()
        {
            try
            {
                while (true)
                {
                    // Keep slow file I/O independent of the safety watchdog.
                    // A stalled read cannot postpone the acquisition stop.
                    await Task.Run(() => constraintsReader.VerifyUnchangedAsync(constraintBinding, admittedConstraints, background.Token), background.Token)
                        .WaitAsync(TimeSpan.FromSeconds(2), background.Token);
                    await Task.Delay(1000, background.Token);
                }
            }
            catch (OperationCanceledException) when (background.IsCancellationRequested) { }
            catch (Exception error)
            {
                Volatile.Write(ref constraintError, error);
                Logger.Error(error);
                lifetime.Cancel();
            }
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
        void Report(string phase, string? connectivity)
        {
            container.UpdateDisplay(d => d with { Phase = phase, Rig = rig, Connectivity = connectivity ?? d.Connectivity, Safety = interlock.Read().Safety.ToString() });
            if (lastLoggedPhase != phase)
            {
                container.RecordAction(target?.Name ?? "", "Planner", phase.Replace('_', ' '));
                lastLoggedPhase = phase;
            }
            try { progress.Report(new ApplicationStatus { Status = phase }); }
            catch (Exception error) { Logger.Error(error); }
        }
    }

    private NinaNativeImaging? CreateNativeImaging(DirectorSessionOptions options) => !NinaNativeImaging.Required(options) ? null
        : new(sequenceFactory ?? throw new InvalidOperationException("NINA's sequence action factory is unavailable."), profiles,
            guider ?? throw new InvalidOperationException("NINA guider service unavailable."),
            focuser ?? throw new InvalidOperationException("NINA focuser service unavailable."),
            rotator ?? throw new InvalidOperationException("NINA rotator service unavailable."), options);

    private static bool OfflineFailure(CoordinatorIntakeFailure failure) => failure is CoordinatorIntakeFailure.Transport
        or CoordinatorIntakeFailure.Timeout or CoordinatorIntakeFailure.ServerUnavailable or CoordinatorIntakeFailure.Busy;
    private static T Require<T>(LedgerResult<T> result) where T : class => result.Value ?? throw new InvalidOperationException($"Director ledger refused: {result.Error}");
    internal static bool CanReselect(PlannerDecision? decision, bool moonScheduling = false) => decision is { Action: PlannerAction.Wait }
        or { Action: PlannerAction.CheckIn, Reason: "preparation_goal_changed" }
        || moonScheduling && decision is { Action: PlannerAction.CheckIn, Reason: "no_authorized_feasible_work" };
}
