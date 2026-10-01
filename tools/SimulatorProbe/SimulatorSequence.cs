using System.ComponentModel.Composition;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Immutable;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Model.Equipment.MySafetyMonitor.Simulator;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;

[assembly: Guid("a8f3b6dd-a195-40f8-9de3-208304473d53")]
[assembly: InternalsVisibleTo("PsfGuard.Director.Tests")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.3.0.1058")]

namespace PsfGuard.Director.SimulatorProbe;

[Export(typeof(IPluginManifest))]
public sealed class ProbeManifest : PluginBase { }

// Deliberately absent from the release bundle. This is not a Director session
// and its fixture assignment is not server authorization.
[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "Director ASCOM smoke test")]
[ExportMetadata("Description", "Test-only native capture probe for an isolated simulator profile")]
[ExportMetadata("Category", "Director Tests")]
[ExportMetadata("Icon", "CameraSVG")]
public sealed class SimulatorSequence : SequenceItem
{
    private readonly IProfileService profiles;
    private readonly ICameraMediator camera;
    private readonly ITelescopeMediator telescope;
    private readonly IFilterWheelMediator filters;
    private readonly ISafetyMonitorMediator safety;
    private readonly IImagingMediator imaging;
    private readonly IImageSaveMediator saves;
    private readonly IImageHistoryVM history;
    private readonly IImageDataFactory imageFactory;
    private readonly NINA.Astrometry.Interfaces.INighttimeCalculator nighttime;

    [ImportingConstructor]
    public SimulatorSequence(IProfileService profiles, ICameraMediator camera, ITelescopeMediator telescope,
        IFilterWheelMediator filters, ISafetyMonitorMediator safety, IImagingMediator imaging, IImageSaveMediator saves, IImageHistoryVM history,
        IImageDataFactory imageFactory, NINA.Astrometry.Interfaces.INighttimeCalculator nighttime)
    {
        this.profiles = profiles;
        this.camera = camera;
        this.telescope = telescope;
        this.filters = filters;
        this.safety = safety;
        this.imaging = imaging;
        this.saves = saves;
        this.history = history;
        this.imageFactory = imageFactory;
        this.nighttime = nighttime;
    }

    public override object Clone()
    {
        var clone = new SimulatorSequence(profiles, camera, telescope, filters, safety, imaging, saves, history, imageFactory, nighttime);
        clone.CopyMetaData(this);
        return clone;
    }

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var root = ValidateEnvironment();
        if (camera.GetInfo().Connected || telescope.GetInfo().Connected || filters.GetInfo().Connected || safety.GetInfo().Connected)
            throw new InvalidOperationException("Start the probe with all simulator devices disconnected.");
        var profileId = profiles.ActiveProfile.Id;
        var run = Path.Combine(root, "probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        var steps = new List<string>();
        var errors = new List<Exception>();
        var captures = new List<CaptureEvidence>();
        var evaluations = new List<PlannerDecision>();
        var operations = new List<PreparationCompletion>();
        var exposureHooks = new List<string>();
        var sessionHookEvents = new List<string>();
        NinaSessionHooks? sessionHooks = null;
        DirectorSessionContainer? sessionContainer = null;
        NinaTargetContainer? sessionTarget = null;
        LedgerIdentity? ledger = null;
        CoordinatorProbe? coordinator = null;
        CoordinatorCheckpointResult? checkpoint = null;
        DirectorConfiguration? equipment = null;
        NinaConstraints? constraints = null;
        NinaConstraints? editedConstraints = null;
        DirectorConstraints? geometryConstraints = null;
        NinaOrientationEvidence? orientation = null;
        NinaSafetyInterlock? interlock = null;
        CancellationTokenRegistration safetyCancellation = default;
        var unsafeCancellationVerified = false;
        var stateDirectory = Directory.CreateDirectory(Path.Combine(run, "state")).FullName;
        await using var runtime = new RuntimeController(Path.GetDirectoryName(typeof(DirectorPlugin).Assembly.Location)!, stateDirectory);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(5));
        void ProfileChanged(object? sender, EventArgs args) => lifetime.Cancel();
        profiles.ProfileChanged += ProfileChanged;
        void Step(string value)
        {
            steps.Add(value);
            Logger.Info($"Director simulator probe: {value}");
            try { progress.Report(new ApplicationStatus { Status = value }); }
            catch (Exception error) { Logger.Error(error); }
        }
        void CheckProfile()
        {
            lifetime.Token.ThrowIfCancellationRequested();
            ValidateEnvironment();
            if (profiles.ActiveProfile.Id != profileId) throw new InvalidOperationException("Test profile changed.");
        }
        Task Revalidate(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            CheckProfile();
            if (interlock is not null && interlock.Read().Safety != PlannerSafety.Safe)
                throw new InvalidOperationException("Native safety evidence is not safe and fresh.");
            if (profiles.ActiveProfile.Id != profileId || runtime.Status.State != RuntimeState.Ready
                || camera.GetInfo().DeviceId != "ASCOM.OmniSim.Camera" || !camera.GetInfo().Connected
                || telescope.GetInfo().DeviceId != "ASCOM.OmniSim.Telescope" || !telescope.GetInfo().Connected
                || filters.GetInfo().DeviceId != "ASCOM.OmniSim.FilterWheel" || !filters.GetInfo().Connected)
                throw new InvalidOperationException("Simulator context or sidecar changed.");
            return Task.CompletedTask;
        }
        try
        {
            Step("Rendering Director session editors in the native NINA host");
            await SessionUiProbe.RenderAsync(run);
            Step("Starting verified planning sidecar");
            coordinator = await CoordinatorProbe.PairAsync(root, profileId, lifetime.Token);
            var rigId = coordinator?.RigId ?? "ascom-smoke";
            await runtime.StartAsync(rigId, lifetime.Token);
            if (runtime.Status.State != RuntimeState.Ready) throw new InvalidOperationException("Sidecar failed", runtime.Status.Error);
            Step("Connecting native NINA safety simulator");
            await safety.Rescan();
            CheckProfile();
            if (!await safety.Connect() || safety.GetDevice() is not SafetyMonitorSimulator safetySimulator)
                throw new IOException("Native safety simulator connection failed.");
            interlock = new NinaSafetyInterlock(profiles, safety, TimeProvider.System);
            safetySimulator.IsSafe = true;
            using (var fresh = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                fresh.CancelAfter(TimeSpan.FromSeconds(15));
                while (interlock.Read().Safety != PlannerSafety.Safe) await Task.Delay(100, fresh.Token);
            }
            interlock.Arm();
            safetyCancellation = interlock.Interrupted.Register(() => lifetime.Cancel());
            Step("Connecting ASCOM OmniSim camera, telescope and filter wheel");
            await camera.Rescan();
            CheckProfile();
            if (!await camera.Connect()) throw new IOException("Simulator camera connection failed.");
            await telescope.Rescan();
            CheckProfile();
            if (!await telescope.Connect()) throw new IOException("Simulator telescope connection failed.");
            await filters.Rescan();
            CheckProfile();
            if (!await filters.Connect()) throw new IOException("Simulator filter wheel connection failed.");
            await Revalidate(lifetime.Token);
            Step("Refreshing native site and fixture horizon constraints");
            var horizonPath = Path.Combine(run, "fixture.hpts");
            await File.WriteAllTextAsync(horizonPath, "[[10,0],[10,100],[80,100.0001],[10,100.0002],[10,360]]", lifetime.Token);
            profiles.ChangeHorizon(horizonPath);
            using var constraintReader = new NinaConstraintSnapshot(profiles);
            var constraintBinding = new NinaConstraintBinding(profileId, NinaHorizonMode.RequiredFile, horizonPath, 20, new(0, 0));
            constraints = constraintReader.Refresh(constraintBinding);
            Step("Reading native simulator equipment capabilities");
            var equipmentBinding = new NinaEquipmentBinding(profileId, rigId, constraints.Revision,
                "ASCOM.OmniSim.Camera", "ASCOM.OmniSim.FilterWheel",
                profiles.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Select(f =>
                    new NinaFilterBinding($"filter-{f.Position}", f.Position, f.Name)).ToImmutableArray(), false, 0,
                "ASCOM.OmniSim.Telescope");
            var equipmentReader = new NinaEquipmentSnapshot(profiles, camera, filters, telescope);
            equipment = equipmentReader.Read(equipmentBinding);
            Step("Parking simulator for Rust-issued unpark");
            if (!await telescope.ParkTelescope(progress, lifetime.Token) || !telescope.GetInfo().AtPark)
                throw new IOException("Fixture could not establish parked simulator state.");
            // This fixture tests program dispatch, not pointing/plate-solving. Use
            // the simulator's current coordinates without requesting a slew.
            var current = telescope.GetCurrentPosition();
            var target = new Coordinates(current.RA, current.Dec, current.Epoch, Coordinates.RAType.Hours);
            var catalogTarget = target.Transform(Epoch.J2000);
            var adapter = new NinaCaptureAdapter(profiles, camera, imaging, saves, history,
                Path.Combine(run, "journal"), TimeSpan.FromSeconds(30), TimeProvider.System);
            var boundCapture = new NinaProgramCapture(equipmentReader, camera, filters, adapter);
            var nativeItems = new NinaPreparationItems(profiles, camera, filters, equipmentReader, TimeProvider.System, telescope);
            if (equipment.Gain is not CameraControl.Unsupported || equipment.Offset is not CameraControl.Unsupported)
                throw new InvalidOperationException("This fixture requires OmniSim's unsupported gain and offset controls.");
            static ulong NowMs() => checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var started = NowMs();
            var assignment = new PlannerAssignment($"smoke-{Path.GetFileName(run)}", 1, rigId, equipment.Id,
                started, started + 180000, Enumerable.Range(0, 3).Select(i => new PlannerGoal(
                    $"filter-{i}", (uint)(3 - i), 1, 0, 0, 1, 1000, 5000, [new(started, started + 180000)])).ToImmutableArray());
            var programTarget = new DirectorTarget("smoke-target", "Director ASCOM Smoke",
                (uint)((ulong)Math.Round(catalogTarget.RA * 15 * 3600000) % 1296000000),
                checked((int)Math.Round(catalogTarget.Dec * 3600000)), null);
            var program = new DirectorProgram(1, assignment, equipment, [programTarget],
                Enumerable.Range(0, 3).Select(i => new ExposureRecipe($"recipe-{i}", 1000, $"filter-{i}", new(1, 1), null, null, 0, null)).ToImmutableArray(),
                Enumerable.Range(0, 3).Select(i => new GoalBinding($"filter-{i}", programTarget.Id, $"recipe-{i}")).ToImmutableArray());
            DirectorAcquisition? publicService = null;
            if (coordinator is { ActivateSimulatorPlan: true })
            {
                if (coordinator.PublicAcquisition)
                {
                    var settings = new NINA.Profile.PluginOptionsAccessor(profiles, new Guid("03a1d13e-67eb-4e24-a407-82bce7e576a5"));
                    settings.SetValueString("CoordinatorUrl", coordinator.Endpoint.AbsoluteUri);
                    publicService = new DirectorAcquisition(profiles, camera, telescope, filters, safety, imaging, saves, history, nighttime)
                    { LocalStateRoot = Path.Combine(run, "public-state") };
                    sessionContainer = new DirectorSessionContainer(publicService);
                    sessionContainer.Options.MaximumAltitude = 89;
                    sessionContainer.Options.SlewCenter = sessionContainer.Options.Focus = sessionContainer.Options.Guiding =
                        sessionContainer.Options.Dither = sessionContainer.Options.MeridianFlip = DirectorOperationOwner.Sequence;
                    Step("Reporting native equipment with acquisition disabled");
                    if (!sessionContainer.ReportEquipmentCommand.CanExecute(null)) throw new InvalidDataException("Native report button was disabled.");
                    await sessionContainer.ReportEquipmentAsync();
                    if (sessionContainer.Options.EnableAcquisition || AcquisitionLease.IsActive
                        || sessionContainer.Display.Phase != "Equipment reported; awaiting operator review")
                        throw new InvalidDataException("Equipment report incorrectly enabled acquisition or retained ownership.");
                }
                Step("Activating and pulling the isolated server's simulator plan");
                program = await coordinator.ActivateAndReadProgramAsync(Path.Combine(run, "program-preview"), equipment,
                    equipmentReader.ReadFilterNames(equipmentBinding, equipment), programTarget, lifetime.Token);
                assignment = program.Assignment;
                programTarget = program.Targets.Single();
                await coordinator.ReportStatusAsync(programTarget, "simulator_outage_test", lifetime.Token);
                if (!coordinator.PublicAcquisition) await coordinator.BeginOutageAsync(root, equipment, lifetime.Token);
            }
            if (coordinator is { PublicAcquisition: true })
            {
                Step("Running the public Director Session acquisition path");
                var settings = new NINA.Profile.PluginOptionsAccessor(profiles, new Guid("03a1d13e-67eb-4e24-a407-82bce7e576a5"));
                settings.SetValueString("CoordinatorUrl", coordinator.Endpoint.AbsoluteUri);
                var service = publicService ?? new DirectorAcquisition(profiles, camera, telescope, filters, safety, imaging, saves, history, nighttime)
                { LocalStateRoot = Path.Combine(run, "public-state") };
                sessionContainer ??= new DirectorSessionContainer(service);
                sessionContainer.Options.EnableAcquisition = true;
                sessionContainer.Options.AutomaticWorkloads = coordinator.AutomaticWorkloads;
                sessionContainer.Options.MaximumAltitude = 89;
                sessionContainer.Options.SlewCenter = sessionContainer.Options.Focus = sessionContainer.Options.Guiding =
                    sessionContainer.Options.Dither = sessionContainer.Options.MeridianFlip = DirectorOperationOwner.Sequence;
                sessionContainer.AttachNewParent(Parent);
                foreach (var slot in Enum.GetValues<NinaInstructionSlot>())
                    sessionContainer.Slots[slot].Add(new SessionHookMarker(slot.ToString(), sessionHookEvents));
                // The public session must own safety cancellation, not this probe.
                safetyCancellation.Dispose();
                using var publicLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var executing = sessionContainer.Execute(progress, publicLifetime.Token);
                try
                {
                    while (!executing.IsCompleted && sessionContainer.Display.Phase != "Acquiring") await Task.Delay(50, lifetime.Token);
                    if (executing.IsCompleted) await executing;
                    await coordinator.BeginOutageAsync(root, equipment, lifetime.Token);
                    if (coordinator.PublicUnsafe)
                    {
                        using var dispatchDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        while (!camera.GetInfo().IsExposing && !executing.IsCompleted) await Task.Delay(50, dispatchDeadline.Token);
                        if (executing.IsCompleted || !camera.GetInfo().IsExposing) throw new InvalidDataException("Public safety test never entered native exposure.");
                        safetySimulator.IsSafe = false;
                        try { await executing.WaitAsync(TimeSpan.FromSeconds(15)); throw new InvalidDataException("Unsafe public session completed successfully."); }
                        catch (OperationCanceledException) when (interlock.Interrupted.IsCancellationRequested) { }
                        if (!telescope.GetInfo().AtPark || camera.GetInfo().IsExposing || AcquisitionLease.IsActive)
                            throw new InvalidDataException("Unsafe public session did not abort, park and release ownership.");
                        safetySimulator.IsSafe = true;
                        using (var fresh = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                            while (!safety.GetInfo().IsSafe) await Task.Delay(100, fresh.Token);
                        if (interlock.Read().Safety == PlannerSafety.Safe || !executing.IsCompleted || lifetime.IsCancellationRequested)
                            throw new InvalidDataException("Safety recovery revived the public session or the probe caused cancellation.");
                        unsafeCancellationVerified = true;
                        ledger = service.LastLedger;
                        Step("Public unsafe monitor aborted exposure, parked and stayed stopped after recovery");
                        return;
                    }
                    if (coordinator.AutomaticWorkloads)
                    {
                        using var waitingDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        waitingDeadline.CancelAfter(TimeSpan.FromSeconds(90));
                        while (!executing.IsCompleted && sessionContainer.Display.Phase != "Waiting for eligible work or quality assessment")
                            await Task.Delay(100, waitingDeadline.Token);
                        if (executing.IsCompleted) await executing;
                        if (!telescope.GetInfo().AtPark) throw new InvalidDataException("Automatic session did not park before waiting for more work.");
                        publicLifetime.Cancel();
                        try { await executing; throw new InvalidDataException("Automatic session ignored cancellation."); }
                        catch (OperationCanceledException) when (publicLifetime.IsCancellationRequested) { }
                        await coordinator.VerifyAutomaticWorkloadAsync(Path.Combine(run, "public-state"), equipment, lifetime.Token);
                    }
                    else await executing;
                }
                finally { if (!executing.IsCompleted) { lifetime.Cancel(); try { await executing; } catch (OperationCanceledException) { } } }
                ledger = service.LastLedger ?? throw new InvalidDataException("Public session has no ledger.");
                foreach (var file in Directory.GetFiles(Path.Combine(service.LastRunDirectory!, "journal"), "*.json", SearchOption.AllDirectories))
                {
                    var capture = CaptureJournal.Read(file);
                    if (capture.Phase != CapturePhase.Saved || !File.Exists(capture.SavedPath)) throw new InvalidDataException("Public capture lacks a saved file.");
                    var restored = await imageFactory.CreateFromFile(capture.SavedPath, 16, false, lifetime.Token);
                    if (!restored.MetaData.GenericHeaders.OfType<StringMetaDataHeader>().Any(h => h.Key == NinaCaptureAdapter.CaptureIdHeader
                        && h.Value.Trim() == capture.Intent.CaptureId.ToString("D"))) throw new InvalidDataException("Public FITS capture identity missing.");
                    captures.Add(capture);
                }
                await coordinator.EndOutageAsync(root, lifetime.Token);
                await using var reopened = new RuntimeController(Path.GetDirectoryName(typeof(DirectorPlugin).Assembly.Location)!, Path.Combine(service.LastRunDirectory!, "ledger"));
                await reopened.StartAsync(rigId, lifetime.Token);
                var eop = NinaEarthOrientation.Read(NinaEarthOrientation.DatabasePath, assignment.ValidFromMs, assignment.ExpiresAtMs, lifetime.Token);
                var g = new NinaGeometrySnapshot(constraintReader, equipmentReader).Read(constraintBinding, equipmentBinding,
                    new(1, 89, eop.Orientation, assignment.Goals.Select(x => new DirectorGoalLimits(x.Id, 20, 89, 0)).ToImmutableArray()));
                var state = new PlannerState(rigId, equipment.Id, NowMs(), assignment.ExpiresAtMs, PlannerSafety.Safe, true, false, new(0, 0));
                if (Require(await reopened.OpenGeometryAsync(program, g.Constraints, state, lifetime.Token)) != ledger) throw new InvalidDataException("Public ledger identity changed.");
                checkpoint = await coordinator.DeliverAsync(Path.Combine(run, "public-checkin"), reopened, ledger, lifetime.Token);
                await coordinator.VerifyProgramAsync(Path.Combine(run, "program-preview"), equipment, true, lifetime.Token);
                await coordinator.ReportStatusAsync(programTarget, "public_acquisition_complete", lifetime.Token);
                var retry = new DirectorSessionContainer(service);
                retry.Options.EnableAcquisition = true;
                retry.Options.MaximumAltitude = 89;
                retry.Options.SlewCenter = retry.Options.Focus = retry.Options.Guiding = retry.Options.Dither = retry.Options.MeridianFlip = DirectorOperationOwner.Sequence;
                try { await retry.Execute(progress, lifetime.Token); throw new InvalidDataException("Public allocation replay was accepted."); }
                catch (CoordinatorIntakeException e) when (e.Failure == CoordinatorIntakeFailure.UnexpectedStatus) { }
                Step("Public acquisition saved three frames; server refused a second launch");
                if (!sessionHookEvents.SequenceEqual(new[] { "BeforeNewTarget", "AfterEachExposure", "AfterEachExposure", "AfterEachExposure", "AfterNewTarget", "AfterEachTarget" }))
                    throw new InvalidDataException("Public session hooks did not follow confirmed save boundaries.");
                await SessionUiProbe.RenderAsync(run, sessionContainer.Display);
                return;
            }
            sessionContainer = new DirectorSessionContainer();
            sessionContainer.Report(DirectorSessionDisplay.Empty with
            {
                Phase = "Isolated simulator session",
                Rig = rigId,
                Target = programTarget.Name,
                ProgramRevision = coordinator?.ProgramRevision ?? "Local fixture",
                Safety = "Native safety simulator: safe",
                Connectivity = coordinator?.ExerciseOutage == true ? "Offline test" : "Connected test"
            });
            sessionContainer.AttachNewParent(Parent);
            foreach (var slot in Enum.GetValues<NinaInstructionSlot>())
                sessionContainer.Slots[slot].Add(new SessionHookMarker(slot.ToString(), sessionHookEvents));
            sessionHooks = new NinaSessionHooks(sessionContainer, TimeProvider.System);
            await sessionHooks.WaitAsync(_ => Task.CompletedTask, progress, lifetime.Token);
            sessionTarget = new NinaTargetContainer(profiles, profileId, programTarget, nighttime.Calculate(), TimeProvider.System);
            sessionTarget.AttachNewParent(sessionContainer);
            await sessionHooks.SelectTargetAsync(programTarget.Id, sessionTarget, progress, lifetime.Token);
            // This fixture retains a three-minute conditions horizon. Fresh
            // monitor evidence is an additional continuous prerequisite, not a
            // prediction that the weather will remain safe for three minutes.
            PlannerState State() => new(rigId, equipment.Id, NowMs(), started + 180000,
                interlock.Read().Safety, true, false, new(0, 0));
            var geometryReader = new NinaGeometrySnapshot(constraintReader, equipmentReader);
            Step("Reading dated Earth-orientation evidence from NINA's local cache");
            orientation = NinaEarthOrientation.Read(NinaEarthOrientation.DatabasePath, assignment.ValidFromMs,
                assignment.ExpiresAtMs, lifetime.Token);
            var geometryInputs = new NinaGeometryInputs(1, 89, orientation.Orientation,
                assignment.Goals.Select(g => new DirectorGoalLimits(g.Id, 20, 89, 0)).ToImmutableArray());
            NinaDispatchSnapshot DispatchSnapshot()
            {
                CheckProfile();
                if (NowMs() >= assignment.ExpiresAtMs)
                    throw new InvalidOperationException("Simulator fixture constraints or deadline changed.");
                var snapshot = geometryReader.Read(constraintBinding, equipmentBinding, geometryInputs);
                return new(snapshot.Configuration, snapshot.Constraints, State());
            }
            geometryConstraints = DispatchSnapshot().Constraints;
            ledger = Require(await runtime.OpenGeometryAsync(program, geometryConstraints, State(), lifetime.Token));
            var nativeDispatch = new NinaGeometryDispatch(runtime, DispatchSnapshot, CheckProfile);
            async Task<PlannerDecision> Evaluate()
            {
                await Revalidate(lifetime.Token);
                var snapshot = DispatchSnapshot();
                var result = Require(await runtime.EvaluateGeometryAsync(snapshot.Constraints, snapshot.State, lifetime.Token));
                evaluations.Add(result);
                Step($"Rust ledger planner: {result.Action} {result.GoalId}");
                return result;
            }
            while (true)
            {
                var selected = await Evaluate();
                if (selected.Action == PlannerAction.Wait && selected.Reason == "pending_assessment" && captures.Count == 3)
                {
                    if (operations.Count != 7) throw new InvalidOperationException("Expected one unpark and six filter/readout receipts.");
                    break;
                }
                if (selected.Action != PlannerAction.Acquire || selected.GoalId is null || captures.Count >= 3)
                    throw new InvalidOperationException("Unexpected simulator planner outcome.");
                var preparationId = Guid.NewGuid().ToString("D");
                var local = new ProgramLocalState(equipment, new(equipment.Id, programTarget), telescope.GetInfo().AtPark, false, 0);
                var began = Require(await runtime.BeginGeometryPreparationAsync(preparationId, selected.GoalId, local,
                    new(5000, 0, 0, 0, 5000, 1000, 5000), DispatchSnapshot().Constraints, State(), lifetime.Token));
                if (!began.Created) throw new InvalidOperationException("Fixture preparation was not newly created.");
                for (var count = 0; ; count++)
                {
                    await Revalidate(lifetime.Token);
                    var snapshot = DispatchSnapshot();
                    var next = Require(await runtime.AdvanceGeometryPreparationAsync(preparationId, snapshot.Configuration, snapshot.Constraints, snapshot.State, lifetime.Token));
                    if (next is PreparationNext.ReadyToReserve ready && ready.GoalId == selected.GoalId) break;
                    if (next is not PreparationNext.Run issued || count >= 3)
                        throw new InvalidOperationException("Fixture preparation was not a new bounded operation.");
                    var operation = issued.Command;
                    var block = new NinaTargetContainer(profiles, profileId, programTarget, nighttime.Calculate(), TimeProvider.System);
                    var issuedItem = nativeItems.Create(next, program, equipmentBinding, nativeDispatch.Pending(next, block.ValidateContext));
                    var item = issuedItem.Item;
                    block.AttachNewParent(sessionContainer);
                    block.Add(item);
                    Step($"Executing native {operation.Operation.GetType().Name}");
                    try { await block.Run(progress, lifetime.Token); }
                    finally { block.Remove(item); block.AttachNewParent(null); }
                    if (item.Status != NINA.Core.Enum.SequenceEntityStatus.FINISHED
                        || issuedItem.Fence.Completion is not { Outcome: PreparationOutcome.Succeeded } completion)
                        throw new InvalidOperationException("Native preparation did not finish successfully; leave the ledger unresolved.");
                    Require(await runtime.CompletePreparationAsync(completion, lifetime.Token));
                    operations.Add(completion);
                }
                var captureId = Guid.NewGuid().ToString("D");
                var captureSnapshot = DispatchSnapshot();
                var reservation = Require(await runtime.ReserveGeometryPreparedAsync(preparationId, captureId,
                    captureSnapshot.Configuration, captureSnapshot.Constraints, captureSnapshot.State, lifetime.Token));
                var binding = Require(await runtime.FindCaptureBindingAsync(captureId, lifetime.Token)).Binding
                    ?? throw new InvalidDataException("New capture binding is missing.");
                Step($"Capturing simulator exposure {captures.Count + 1}/3");
                sessionContainer.Report(sessionContainer.Display with { Goal = selected.GoalId, Operation = $"Exposure {captures.Count + 1}/3" });
                var hookBlock = new NinaTargetContainer(profiles, profileId, programTarget, nighttime.Calculate(), TimeProvider.System);
                var checkCapture = nativeDispatch.Capture(preparationId, reservation, () =>
                {
                    hookBlock.ValidateContext();
                    CheckProfile();
                    if (exposureHooks.LastOrDefault() != $"before:{captureId}")
                        throw new InvalidOperationException("Exposure did not inherit its native before-trigger.");
                    if (telescope.GetInfo().AtPark || telescope.GetInfo().Slewing)
                        throw new InvalidOperationException("Simulator mount is parked or moving before capture.");
                });
                var exposureItem = new NinaExposureItem(boundCapture, reservation, binding, equipmentBinding,
                    checkCapture);
                var exposureBlock = new NINA.Sequencer.Container.SequentialContainer();
                hookBlock.AttachNewParent(sessionContainer);
                hookBlock.Add(exposureBlock);
                exposureBlock.Add(exposureItem);
                var hook = new ExposureHook(exposureItem, programTarget, after => exposureHooks.Add($"{(after ? "after" : "before")}:{captureId}"));
                hookBlock.Add(hook);
                try { await hookBlock.Run(progress, lifetime.Token); }
                finally
                {
                    hookBlock.Remove(hook);
                    exposureBlock.Remove(exposureItem);
                    hookBlock.Remove(exposureBlock);
                    hookBlock.AttachNewParent(null);
                }
                var evidence = exposureItem.Evidence;
                if (exposureItem.Status != NINA.Core.Enum.SequenceEntityStatus.FINISHED || evidence is null
                    || exposureHooks.Count != (captures.Count + 1) * 2 || exposureHooks.LastOrDefault() != $"after:{captureId}")
                    throw new InvalidOperationException("Native exposure or inherited hooks did not finish successfully.", exposureItem.ExecutionError);
                if (evidence.Phase != CapturePhase.Saved || !File.Exists(evidence.SavedPath))
                    throw new IOException("Capture has no confirmed file.");
                var restored = await imageFactory.CreateFromFile(evidence.SavedPath, 16, false, lifetime.Token);
                if (!restored.MetaData.GenericHeaders.OfType<StringMetaDataHeader>().Any(h =>
                        h.Key == NinaCaptureAdapter.CaptureIdHeader && h.Value.Trim() == captureId)
                    || restored.Data.FlatArray.Length == 0
                    || restored.Data.FlatArray.Min() == restored.Data.FlatArray.Max())
                    throw new InvalidDataException("Saved FITS identity or pixel data failed readback.");
                var journal = CaptureJournal.Read(Path.Combine(run, "journal", profileId.ToString("N"), $"{evidence.Intent.CaptureId:N}.json"));
                if (journal != evidence || journal.SchemaVersion != 2 || journal.Intent.Program?.LedgerId != ledger.LedgerId)
                    throw new InvalidDataException("Durable program journal differs from the save receipt.");
                Require(await runtime.RecordAsync(captureId, new LedgerEvidence.Saved(captureId,
                    checked((ulong)Math.Ceiling(evidence.TotalMs!.Value))), lifetime.Token));
                captures.Add(evidence);
                await sessionHooks.ExposureSavedAsync(captureId, progress, lifetime.Token);
            }
            await sessionHooks.FinishAsync(progress, lifetime.Token);
            var expectedSlots = new[] { "BeforeWait", "AfterWait", "BeforeNewTarget", "AfterEachExposure", "AfterEachExposure", "AfterEachExposure", "AfterNewTarget", "AfterEachTarget" };
            if (!sessionHookEvents.SequenceEqual(expectedSlots) || sessionHooks.Receipts.Any(receipt => !receipt.Completed))
                throw new InvalidOperationException("Director session hook cadence did not match confirmed captures. Pending assessment is not target completion.");
            var savedEvents = Require(await runtime.ReadEventsAsync(0, token: lifetime.Token));
            if (savedEvents.Events.Count(e => e.Attempt.Evidence is LedgerEvidence.Saved) != 3)
                throw new InvalidDataException("The Rust ledger does not contain three saved captures.");
            await runtime.StopAsync();
            await runtime.StartAsync(rigId, lifetime.Token);
            if (Require(await runtime.OpenGeometryAsync(program, DispatchSnapshot().Constraints, State(), lifetime.Token)) != ledger
                || (await Evaluate()) is not { Action: PlannerAction.Wait, Reason: "pending_assessment" })
                throw new InvalidDataException("Restarted ledger lost identity or pending progress.");
            if (coordinator is not null)
            {
                await coordinator.EndOutageAsync(root, lifetime.Token);
                await coordinator.VerifyProgramAsync(Path.Combine(run, "program-preview"), equipment, false, lifetime.Token);
                Step("Delivering capture receipts to the isolated coordinator");
                checkpoint = await coordinator.DeliverAsync(Path.Combine(run, "checkin"), runtime, ledger, lifetime.Token);
                await coordinator.VerifyProgramAsync(Path.Combine(run, "program-preview"), equipment, true, lifetime.Token);
                await coordinator.ReportStatusAsync(programTarget, "pending_assessment", lifetime.Token);
                sessionContainer.Report(sessionContainer.Display with
                {
                    Connectivity = "Connected test",
                    QueueDepth = "0",
                    LastCheckIn = DateTimeOffset.UtcNow.ToString("u"),
                    WaitReason = "Pending assessment",
                    Operation = "Batch check-in completed"
                });
            }
            await SessionUiProbe.RenderAsync(run, sessionContainer.Display);
            await Revalidate(lifetime.Token);
            if (equipmentReader.Read(equipmentBinding).Id != equipment.Id)
                throw new InvalidOperationException("Simulator capability identity changed during capture.");
            var refreshed = constraintReader.Refresh(constraintBinding);
            if (refreshed.Revision != constraints.Revision) throw new InvalidOperationException("Native constraints changed during capture.");
            Step("Checking same-path horizon edit detection");
            var horizonTime = File.GetLastWriteTimeUtc(horizonPath);
            await File.WriteAllTextAsync(horizonPath, "[[10,0],[10,100],[85,100.0001],[10,100.0002],[10,360]]", lifetime.Token);
            File.SetLastWriteTimeUtc(horizonPath, horizonTime);
            editedConstraints = constraintReader.Refresh(constraintBinding);
            if (editedConstraints.Revision == constraints.Revision
                || profiles.ActiveProfile.AstrometrySettings.Horizon.GetAltitude(100.0001) != 85
                || equipmentReader.Read(equipmentBinding with { ConstraintRevision = editedConstraints.Revision }).Id == equipment.Id)
                throw new InvalidOperationException("Same-path horizon edit did not reach NINA and the equipment identity.");
            Step("Checking unsafe native-monitor cancellation and recovery latch");
            var nativeWait = new NINA.Sequencer.SequenceItem.Utility.WaitForTimeSpan { Time = 120 };
            nativeWait.AttachNewParent(sessionContainer);
            var waiting = nativeWait.Run(progress, lifetime.Token);
            try
            {
                if (waiting.IsCompleted || nativeWait.Status != NINA.Core.Enum.SequenceEntityStatus.RUNNING)
                    throw new InvalidOperationException("The native wait did not start.");
                safetySimulator.IsSafe = false;
                try { await waiting.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (OperationCanceledException) when (interlock.Interrupted.IsCancellationRequested) { }
                if (nativeWait.Status == NINA.Core.Enum.SequenceEntityStatus.FINISHED)
                    throw new InvalidOperationException("Unsafe interruption incorrectly completed the native wait.");
            }
            finally
            {
                lifetime.Cancel();
                try { await waiting; }
                catch (OperationCanceledException) { }
                nativeWait.AttachNewParent(null);
            }
            if (!interlock.Interrupted.IsCancellationRequested)
                throw new InvalidOperationException("Unsafe monitor did not interrupt the session.");
            safetySimulator.IsSafe = true;
            using (var recoveryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                while (!safety.GetInfo().IsSafe) await Task.Delay(100, recoveryDeadline.Token);
            if (interlock.Read().Safety == PlannerSafety.Safe || !lifetime.IsCancellationRequested)
                throw new InvalidOperationException("Monitor recovery revived interrupted work.");
            unsafeCancellationVerified = true;
            Step("Three captures saved with correlated receipts");
        }
        catch (Exception error) { errors.Add(error); Logger.Error(error); }
        finally
        {
            profiles.ProfileChanged -= ProfileChanged;
            safetyCancellation.Dispose();
            interlock?.Dispose();
            sessionTarget?.AttachNewParent(null);
            sessionTarget?.NighttimeData.Ticker.Stop();
            sessionContainer?.AttachNewParent(null);
            // Cleanup uses verified connected identities, never the newly selected
            // profile, and continues after one cleanup operation fails.
            async Task Cleanup(string step, Func<Task> action)
            {
                try { Step(step); await action(); }
                catch (Exception error) { errors.Add(error); Logger.Error(error); }
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (telescope.GetInfo().Connected && telescope.GetInfo().DeviceId == "ASCOM.OmniSim.Telescope")
            {
                await Cleanup("Parking simulator", async () =>
                {
                    if (!await telescope.ParkTelescope(progress, cleanup.Token)) throw new IOException("Park failed.");
                });
                await Cleanup("Disconnecting simulator telescope", telescope.Disconnect);
            }
            if (filters.GetInfo().Connected && filters.GetInfo().DeviceId == "ASCOM.OmniSim.FilterWheel")
                await Cleanup("Disconnecting simulator filter wheel", filters.Disconnect);
            if (camera.GetInfo().Connected && camera.GetInfo().DeviceId == "ASCOM.OmniSim.Camera")
                await Cleanup("Disconnecting simulator camera", camera.Disconnect);
            if (safety.GetInfo().Connected && safety.GetInfo().DeviceId == SafetySimulatorId)
                await Cleanup("Disconnecting native safety simulator", safety.Disconnect);
            await Cleanup("Stopping sidecar", runtime.StopAsync);
            if (coordinator is not null)
            {
                await Cleanup("Restoring isolated server after outage", () => coordinator.EndOutageAsync(root, cleanup.Token));
                await Cleanup("Revoking test pairing", () => coordinator.DisposeAsync().AsTask());
            }
            var result = new
            {
                passed = errors.Count == 0 && (coordinator?.PublicUnsafe == true ? unsafeCancellationVerified : captures.Count == 3),
                nina = System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).FileVersion,
                ninaApi = "3.3.0.1058-nightly",
                runtime = RuntimeContract.RuntimeVersion,
                ipc = RuntimeContract.ProtocolVersion,
                scope = coordinator is { PublicAcquisition: true } ? "public-director-session-server-allocation-native-capture" : coordinator is { ActivateSimulatorPlan: true }
                    ? "server-plan-native-dispatch-simulator-not-production-container"
                    : "durable-rust-geometry-native-dispatch-fixture-not-production-container-or-server",
                programRevision = coordinator?.ProgramRevision,
                allocationId = coordinator?.AllocationId,
                serverOutage = coordinator?.ExerciseOutage ?? false,
                liveStatusVerified = coordinator?.LiveStatusVerified ?? false,
                equipmentReviewVerified = coordinator?.EquipmentReviewVerified ?? false,
                automaticWorkloadVerified = coordinator?.AutomaticWorkloadVerified ?? false,
                steps,
                evaluations,
                operations,
                exposureHooks,
                sessionHookEvents,
                sessionHookReceipts = sessionHooks?.Receipts,
                ledger,
                checkpoint,
                equipment,
                constraints,
                editedConstraints,
                geometryConstraints,
                orientation,
                unsafeCancellationVerified,
                captures = captures.Select(c => new { c.Intent.CaptureId, c.SavedPath, c.TotalMs }),
                errors = errors.Select(e => e.ToString())
            };
            var output = Path.Combine(run, "result.json");
            await File.WriteAllTextAsync(output + ".tmp", JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
            }));
            File.Move(output + ".tmp", output);
            Logger.Info($"Director simulator probe result: {output}");
        }
        if (errors.Count > 0) throw new AggregateException("Director simulator probe failed", errors);
    }

    private static T Require<T>(LedgerResult<T> result) where T : class => result.Error is null && result.Value is { } value
        ? value : throw new InvalidDataException($"Simulator ledger operation failed: {result.Error}");

    private sealed class SessionHookMarker(string slot, List<string> events) : SequenceItem
    {
        public override object Clone() => new SessionHookMarker(slot, events);
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (slot is not ("BeforeWait" or "AfterWait")
                && NINA.Sequencer.Utility.ItemUtility.FindDeepSkyObjectContainer(Parent) is not NinaTargetContainer)
                throw new InvalidOperationException("Director target hook lost its native target context.");
            events.Add(slot);
            return Task.CompletedTask;
        }
    }

    private sealed class ExposureHook(NinaExposureItem exposure, DirectorTarget expected, Action<bool> observe) : NINA.Sequencer.Trigger.SequenceTrigger
    {
        private int calls;
        public override object Clone() => throw new NotSupportedException("Test-only transient hook.");
        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) =>
            nextItem is NINA.Sequencer.Interfaces.IExposureItem && ReferenceEquals(nextItem, exposure);
        public override bool ShouldTriggerAfter(ISequenceItem previousItem, ISequenceItem nextItem) => ReferenceEquals(previousItem, exposure);
        public override Task Execute(NINA.Sequencer.Container.ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var native = NINA.Sequencer.Utility.ItemUtility.RetrieveContextCoordinates(context);
            if (native is null || native.Coordinates.Epoch != Epoch.J2000
                || native.Coordinates.RADegrees != expected.IcrsRaMas / 3600000.0
                || native.Coordinates.Dec != expected.IcrsDecMas / 3600000.0
                || !native.PositionAngle.Equals(expected.PositionAngleMas is { } angle ? angle / 3600000.0 : double.NaN))
                throw new InvalidOperationException("Native hook did not receive the immutable target context.");
            var after = calls++ != 0;
            if (after && exposure.Evidence is not { Phase: CapturePhase.Saved })
                throw new InvalidOperationException("After-exposure hook ran before the correlated save.");
            observe(after);
            return Task.CompletedTask;
        }
    }

    private string ValidateEnvironment() => ValidateEnvironment(
        Environment.GetEnvironmentVariable("DIRECTOR_NINA_TEST_ROOT"),
        Environment.GetEnvironmentVariable("DIRECTOR_NINA_TEST_TOKEN"), CoreUtil.APPLICATIONTEMPPATH, profiles.ActiveProfile);

    private const string SafetySimulatorId = "613EC0FF-87D7-4475-9352-F6F6EB1CDE75";

    internal static string ValidateEnvironment(string? root, string? token, string applicationRoot, IProfile profile)
    {
        if (root is null || !Path.IsPathFullyQualified(root) || !Guid.TryParseExact(token, "N", out _)
            || !Path.GetFileName(root).StartsWith("nina-smoke-", StringComparison.Ordinal)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
            || File.ReadAllText(Path.Combine(root, ".director-test-root")) != token
            || applicationRoot != root
            || File.ReadAllText(Path.Combine(root, "isolation-ready.txt")) != root)
            throw new InvalidOperationException("The probe requires the isolated NINA test launcher.");
        if (profile.Name != "Director ASCOM Smoke" || profile.CameraSettings.Id != "ASCOM.OmniSim.Camera"
            || profile.TelescopeSettings.Id != "ASCOM.OmniSim.Telescope"
            || profile.FilterWheelSettings.Id != "ASCOM.OmniSim.FilterWheel"
            || profile.ImageFileSettings.FilePath != Path.Combine(root, "images")
            || profile.ImageFileSettings.FilePattern != "$$DATETIME$$_$$FILTER$$_$$FRAMENR$$"
            || profile.ImageFileSettings.FileType != NINA.Core.Enum.FileTypeEnum.FITS
            || profile.FocuserSettings.Id != "No_Device" || profile.RotatorSettings.Id != "No_Device"
            || profile.GuiderSettings.GuiderName != "No_Guider"
            || profile.DomeSettings.Id != "No_Device" || profile.SwitchSettings.Id != "No_Device"
            || profile.FlatDeviceSettings.Id != "No_Device" || profile.SafetyMonitorSettings.Id != SafetySimulatorId
            || profile.WeatherDataSettings.Id != "No_Device")
            throw new InvalidOperationException("The probe requires its simulator-only test profile.");
        return root;
    }
}
