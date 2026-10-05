using System.ComponentModel.Composition;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Immutable;
using NINA.Astrometry;
using NINA.Core.Enum;
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
    private readonly IGuiderMediator guider;
    private readonly IFilterWheelMediator filters;
    private readonly ISafetyMonitorMediator safety;
    private readonly IDomeMediator dome;
    private readonly IImagingMediator imaging;
    private readonly IImageSaveMediator saves;
    private readonly IImageHistoryVM history;
    private readonly IImageDataFactory imageFactory;
    private readonly NINA.Astrometry.Interfaces.INighttimeCalculator nighttime;
    private readonly INinaActionFactory? factory;
    private readonly IFocuserMediator? focuser;
    private readonly IRotatorMediator? rotator;
    private readonly NINA.Equipment.Interfaces.IDomeFollower? follower;

    [ImportingConstructor]
    public SimulatorSequence(IProfileService profiles, ICameraMediator camera, ITelescopeMediator telescope,
        IFilterWheelMediator filters, ISafetyMonitorMediator safety, IImagingMediator imaging, IImageSaveMediator saves, IImageHistoryVM history,
        IImageDataFactory imageFactory, NINA.Astrometry.Interfaces.INighttimeCalculator nighttime, IGuiderMediator guider, IDomeMediator dome,
        INinaActionFactory? factory = null, IFocuserMediator? focuser = null, IRotatorMediator? rotator = null,
        NINA.Equipment.Interfaces.IDomeFollower? follower = null)
    {
        this.profiles = profiles;
        this.camera = camera;
        this.telescope = telescope;
        this.guider = guider;
        this.filters = filters;
        this.safety = safety;
        this.dome = dome;
        this.imaging = imaging;
        this.saves = saves;
        this.history = history;
        this.imageFactory = imageFactory;
        this.nighttime = nighttime;
        this.factory = factory;
        this.focuser = focuser;
        this.rotator = rotator;
        this.follower = follower;
    }

    public override object Clone()
    {
        var clone = new SimulatorSequence(profiles, camera, telescope, filters, safety, imaging, saves, history, imageFactory, nighttime, guider, dome, factory, focuser, rotator, follower);
        clone.CopyMetaData(this);
        return clone;
    }

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var root = ValidateEnvironment();
        if (camera.GetInfo().Connected || telescope.GetInfo().Connected || filters.GetInfo().Connected || safety.GetInfo().Connected || dome.GetInfo().Connected)
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
        var constraintChangeVerified = false;
        NativeImagingProbe? nativeProbe = null;
        var phd2 = Phd2Fixture.Read(root);
        float? rotatorFinalPosition = null;
        var nativeFailureVerified = false;
        var nativeOptions = new DirectorSessionOptions();
        void ConfigureImaging(DirectorSessionOptions options)
        {
            options.SlewCenter = options.Focus = options.Guiding = options.Dither = options.MeridianFlip =
                coordinator?.NativeImaging == true ? DirectorOperationOwner.Director : DirectorOperationOwner.Sequence;
            options.DitherEveryExposures = 1;
        }
        var enclosureCancellationVerified = false;
        var stateDirectory = Directory.CreateDirectory(Path.Combine(run, "state")).FullName;
        await using var runtime = new RuntimeController(Path.GetDirectoryName(typeof(DirectorPlugin).Assembly.Location)!, stateDirectory);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(phd2 is null ? 5 : 9));
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
            if (coordinator?.UsesEnclosure == true)
            {
                Step("Connecting and opening ASCOM OmniSim enclosure");
                profiles.ActiveProfile.DomeSettings.Id = "ASCOM.OmniSim.Dome";
                await dome.Rescan(); CheckProfile();
                if (!await dome.Connect() || dome.GetInfo().DeviceId != "ASCOM.OmniSim.Dome") throw new IOException("Simulator enclosure connection failed.");
                if (!await dome.OpenShutter(lifetime.Token)) throw new IOException("Simulator enclosure did not open.");
            }
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
            ConfigureImaging(nativeOptions);
            if (coordinator?.NativeImaging == true)
            {
                if (factory is null || focuser is null || rotator is null || follower is null) throw new InvalidOperationException("Native test services unavailable.");
                Step(phd2 is null ? "Connecting ASCOM OmniSim focuser and native direct guider; synthetic optical results only"
                    : "Connecting ASCOM OmniSim focuser/rotator and isolated PHD2 simulator; synthetic optical results only");
                profiles.ActiveProfile.FocuserSettings.Id = "ASCOM.OmniSim.Focuser";
                profiles.ActiveProfile.GuiderSettings.GuiderName = "Direct_Guider";
                phd2?.Configure(profiles);
                profiles.ActiveProfile.CameraSettings.PixelSize = 3.76;
                profiles.ActiveProfile.TelescopeSettings.FocalLength = 250;
                profiles.ActiveProfile.GuiderSettings.SettleTime = 1;
                await focuser.Rescan();
                if (!await focuser.Connect() || focuser.GetInfo().DeviceId != "ASCOM.OmniSim.Focuser") throw new IOException("Simulator focuser connection failed.");
                await guider.Rescan();
                if (!await guider.Connect() || guider.GetInfo().DeviceId != (phd2 is null ? "Direct_Guider" : "PHD2_Single")) throw new IOException("Simulator guider connection failed.");
                if (phd2 is not null)
                {
                    profiles.ActiveProfile.RotatorSettings.Id = "ASCOM.OmniSim.Rotator";
                    await rotator.Rescan();
                    if (!await rotator.Connect() || rotator.GetInfo().DeviceId != "ASCOM.OmniSim.Rotator")
                        throw new IOException("Simulator rotator connection failed.");
                    await rotator.MoveMechanical(0, lifetime.Token);
                    var initialRotation = ((NINA.Equipment.Interfaces.IRotator)rotator.GetDevice()).MechanicalPosition;
                    if (!float.IsFinite(initialRotation) || Math.Abs(initialRotation) > 1)
                        throw new IOException("Simulator rotator did not reach its mechanical test zero.");
                    rotator.Sync(0); // Synthetic optical zero for the isolated simulator only.
                }
                if (coordinator.ForceNativeFlip)
                {
                    var flip = profiles.ActiveProfile.MeridianFlipSettings;
                    flip.MinutesAfterMeridian = 0;
                    flip.MaxMinutesAfterMeridian = 0;
                    flip.PauseTimeBeforeMeridian = phd2 is null ? 0 : 5;
                    flip.SettleTime = 1;
                    flip.UseSideOfPier = false;
                    flip.Recenter = false; // No real sky solve is available to this simulator.
                    flip.AutoFocusAfterFlip = true;
                    if (phd2 is not null)
                    {
                        flip.Recenter = true;
                        var solve = profiles.ActiveProfile.PlateSolveSettings;
                        solve.ASTAPLocation = Path.Combine(root, "synthetic-solver", "SyntheticSolver.exe");
                        solve.PlateSolverType = NINA.Core.Enum.PlateSolverEnum.ASTAP;
                        solve.BlindFailoverEnabled = false;
                        solve.ExposureTime = 0.2;
                        solve.NumberOfAttempts = 1;
                        solve.SearchRadius = 5;
                    }
                }
                nativeProbe = new NativeImagingProbe(factory, profiles, camera, telescope, filters, guider, focuser, dome, follower, imaging, history, safety, coordinator.RecoveryScenario ?? coordinator.NativeImagingFailure, coordinator.ForceNativeFlip, phd2 is null ? null : rotator);
            }
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
                    new NinaFilterBinding($"filter-{f.Position}", f.Position, f.Name)).ToImmutableArray(), coordinator?.NativeImaging == true, coordinator?.NativeImaging == true ? 1U : 0U,
                "ASCOM.OmniSim.Telescope", NativeImagingScope: nativeProbe is null ? null : NinaNativeImaging.ScopeFor(profiles, nativeOptions));
            var equipmentReader = new NinaEquipmentSnapshot(profiles, camera, filters, telescope);
            equipment = equipmentReader.Read(equipmentBinding);
            Step("Parking simulator for Rust-issued unpark");
            if (!await telescope.ParkTelescope(progress, lifetime.Token) || !telescope.GetInfo().AtPark)
                throw new IOException("Fixture could not establish parked simulator state.");
            // This fixture tests program dispatch, not pointing/plate-solving. Use
            // the simulator's current coordinates without requesting a slew.
            var current = telescope.GetCurrentPosition();
            var target = new Coordinates(current.RA, current.Dec, current.Epoch, Coordinates.RAType.Hours);
            // Arrive east of the meridian; after two saves the real flip VM
            // waits through transit before issuing the ASCOM flip slew.
            if (coordinator?.ForceNativeFlip == true)
                target = new Coordinates((telescope.GetInfo().SiderealTime + (phd2 is null ? 0.025 : 0.06)) % 24, 10, Epoch.JNOW, Coordinates.RAType.Hours);
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
                checked((int)Math.Round(catalogTarget.Dec * 3600000)), phd2 is null ? null : 30 * 3600000U);
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
                    publicService = new DirectorAcquisition(profiles, camera, telescope, filters, safety, imaging, saves, history, nighttime, dome,
                        nativeProbe?.Factory ?? factory, guider, focuser, rotator)
                    { LocalStateRoot = Path.Combine(run, "public-state") };
                    sessionContainer = new DirectorSessionContainer(publicService);
                    sessionContainer.Options.MaximumAltitude = 89;
                    ConfigureImaging(sessionContainer.Options);
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
                programTarget = program.Targets.First();
                await coordinator.ReportStatusAsync(programTarget, "simulator_outage_test", lifetime.Token);
                if (!coordinator.PublicAcquisition) await coordinator.BeginOutageAsync(root, equipment, lifetime.Token);
            }
            if (coordinator is { PublicAcquisition: true })
            {
                Step("Running the public Director Session acquisition path");
                var settings = new NINA.Profile.PluginOptionsAccessor(profiles, new Guid("03a1d13e-67eb-4e24-a407-82bce7e576a5"));
                settings.SetValueString("CoordinatorUrl", coordinator.Endpoint.AbsoluteUri);
                var service = publicService ?? new DirectorAcquisition(profiles, camera, telescope, filters, safety, imaging, saves, history, nighttime, dome,
                    nativeProbe?.Factory ?? factory, guider, focuser, rotator)
                { LocalStateRoot = Path.Combine(run, "public-state") };
                sessionContainer ??= new DirectorSessionContainer(service);
                sessionContainer.Options.EnableAcquisition = true;
                sessionContainer.Options.Enclosure = coordinator.UsesEnclosure ? DirectorEnclosurePolicy.RequireOpenShutter : DirectorEnclosurePolicy.OpenAir;
                sessionContainer.Options.OnAbort = coordinator.AbortWithoutPark ? DirectorAbortPolicy.StopMount : DirectorAbortPolicy.ParkMount;
                if (coordinator.DeferredCheckIn)
                {
                    sessionContainer.Options.CheckInMode = DirectorCheckInMode.Deferred;
                    sessionContainer.Options.CheckInAtEnd = false;
                }
                sessionContainer.Options.AutomaticWorkloads = coordinator.AutomaticWorkloads;
                sessionContainer.Options.LocalTargetScheduling = coordinator.LocalTargetScheduling;
                sessionContainer.Options.MaximumAltitude = 89;
                sessionContainer.Options.StatusSeconds = 5;
                if (coordinator.WeatherHoldScenario is not null)
                {
                    sessionContainer.Options.Weather = DirectorWeatherPolicy.HoldAndResume;
                    sessionContainer.Options.StableSafeSeconds = 15;
                    sessionContainer.Options.MaximumWeatherMinutes = 3;
                    sessionContainer.Options.MaximumWeatherInterruptions = 3;
                }
                if (coordinator.NightEndScenario is not null)
                {
                    sessionContainer.Options.MaximumHours = coordinator.WeatherHoldScenario == "roof-night-end" ? 0.025 : 0.04;
                    sessionContainer.Options.RetryFocusAndGuiding = coordinator.NightEndScenario == "workload-wait";
                }
                if (coordinator.RecoveryScenario is not null)
                {
                    sessionContainer.Options.RetryFocusAndGuiding = true;
                    sessionContainer.Options.RetryCooldownSeconds = 1;
                    sessionContainer.Options.MaximumRecoveryAttempts = 1;
                }
                ConfigureImaging(sessionContainer.Options);
                sessionContainer.AttachNewParent(Parent);
                foreach (var slot in Enum.GetValues<NinaInstructionSlot>())
                    sessionContainer.Slots[slot].Add(new SessionHookMarker(slot.ToString(), sessionHookEvents));
                if (nativeProbe?.Flip is { } flipProbe) sessionContainer.AfterEachExposure.Add(flipProbe.SaveMarker());
                if ((coordinator.LocalTargetScheduling || coordinator.WeatherHoldScenario is "safety-exposure" or "roof-exposure") && !coordinator.NativeImaging)
                    sessionContainer.BeforeNewTarget.Add(new NINA.Sequencer.SequenceItem.Telescope.SlewScopeToRaDec(telescope, guider) { Inherited = true });
                var slowSetup = new SlowSetupProbe();
                if (coordinator.ConstraintChange is not null) sessionContainer.BeforeNewTarget.Add(slowSetup);
                // The public session must own safety cancellation, not this probe.
                safetyCancellation.Dispose();
                using var publicLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var followingStepRan = false;
                NINA.Sequencer.Container.SequentialContainer? nightSequence = null;
                if (coordinator.NightEndScenario is not null)
                {
                    sessionContainer.ErrorBehavior = NINA.Sequencer.Utility.InstructionErrorBehavior.AbortOnError;
                    nightSequence = new();
                    nightSequence.AttachNewParent(Parent);
                    nightSequence.Add(sessionContainer);
                    nightSequence.Add(new NightEndMarker(() => followingStepRan = true));
                }
                var startupHold = coordinator.WeatherHoldScenario is "safety-startup" or "roof-startup";
                if (startupHold)
                {
                    if (coordinator.UsesEnclosure) { if (!await dome.CloseShutter(lifetime.Token)) throw new IOException("Simulator roof did not close before startup."); }
                    else safetySimulator.IsSafe = false;
                }
                var executing = nightSequence is null ? sessionContainer.Execute(progress, publicLifetime.Token) : nightSequence.Run(progress, publicLifetime.Token);
                try
                {
                    if (startupHold)
                    {
                        using var startupDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        startupDeadline.CancelAfter(TimeSpan.FromSeconds(90));
                        while (!executing.IsCompleted && !sessionContainer.Display.Phase.Contains("waiting for Safe/Open before workload", StringComparison.Ordinal))
                            await Task.Delay(50, startupDeadline.Token);
                        if (executing.IsCompleted) { await executing; throw new InvalidDataException("Unsafe startup did not hold."); }
                        if (service.LastLedger is not null || camera.GetInfo().IsExposing || telescope.GetInfo().TrackingEnabled || telescope.GetInfo().Slewing)
                            throw new InvalidDataException("Unsafe startup admitted work or failed to stop equipment.");
                        var cleared = System.Diagnostics.Stopwatch.StartNew();
                        if (coordinator.UsesEnclosure) { if (!await dome.OpenShutter(lifetime.Token)) throw new IOException("Simulator roof did not reopen."); }
                        else safetySimulator.IsSafe = true;
                        while (!executing.IsCompleted && service.LastLedger is null) await Task.Delay(50, startupDeadline.Token);
                        if (executing.IsCompleted || cleared.Elapsed < TimeSpan.FromSeconds(sessionContainer.Options.StableSafeSeconds))
                            throw new InvalidDataException("Startup bypassed stability or failed to admit fresh work.");
                        await File.WriteAllTextAsync(Path.Combine(run, "weather-hold-verified.txt"), "Startup held without a ledger; fresh work admitted only after stable Safe/Open", lifetime.Token);
                        Step("Unsafe startup held before workload admission and resumed after stable Safe/Open");
                    }
                    if (coordinator.NativeImagingFailure is { } fault)
                    {
                        Exception? failure = null;
                        // Native AbortOnError cancels the parent NINA sequence too.
                        // Await actual shutdown with an independent deadline.
                        try { await executing.WaitAsync(TimeSpan.FromSeconds(150)); }
                        catch (Exception error) when (error is not TimeoutException) { failure = error; }
                        if (fault == "meridian")
                        {
                            nativeProbe!.Flip!.Verify(sessionContainer, failed: true);
                            if (failure is null || camera.GetInfo().IsExposing || !telescope.GetInfo().AtPark || AcquisitionLease.IsActive
                                || Directory.EnumerateFiles(Path.Combine(root, "images"), "*.fits", SearchOption.AllDirectories).Count() != 2)
                                throw new InvalidDataException("Failed flip did not preserve two saves, block the third exposure and park.", failure);
                            nativeFailureVerified = true;
                            Step("Native flip failure blocked the next exposure, parked and retained the two earlier saves");
                            return;
                        }
                        if (failure is null || nativeProbe?.FailureInjected != true || !nativeProbe.Operations.Contains(fault == "center" ? "Center" : "Autofocus")
                            || camera.GetInfo().IsExposing || !telescope.GetInfo().AtPark || AcquisitionLease.IsActive
                            || sessionHookEvents.Contains("AfterEachExposure")
                            || Directory.EnumerateFiles(Path.Combine(root, "images"), "*.fits", SearchOption.AllDirectories).Any())
                            throw new InvalidDataException("Failed native preparation did not stop before capture and park.", failure);
                        nativeFailureVerified = true;
                        if (coordinator.RecoveryScenario == "focus-always" && nativeProbe.Operations.Count(x => x == "Autofocus") != 2)
                            throw new InvalidDataException("Recovery did not stop after exactly one failed autofocus retry.");
                        Step($"Native {fault} failure prevented capture, parked and released local ownership without restarting");
                        using var reportFailure = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await coordinator.ReportStatusAsync(programTarget, "native_preparation_failed", reportFailure.Token);
                        return;
                    }
                    while (!executing.IsCompleted && (coordinator.ConstraintChange is not null
                        ? !slowSetup.Entered.Task.IsCompleted : sessionContainer.Display.Phase != "Acquiring"))
                        await Task.Delay(50, lifetime.Token);
                    if (executing.IsCompleted) await executing;
                    if (coordinator.ConstraintChange is null && !coordinator.PublicUnsafe && !coordinator.EnclosureClosure)
                    {
                        await coordinator.VerifyPluginTelemetryAsync(lifetime.Token);
                        Step("Plugin live telemetry reported operation duration, goal, pointing and freshness");
                    }
                    await coordinator.BeginOutageAsync(root, equipment, lifetime.Token);
                    if (coordinator.ConstraintChange is { } change)
                    {
                        await slowSetup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token);
                        var originalHorizon = await File.ReadAllTextAsync(horizonPath, lifetime.Token);
                        var timestamp = File.GetLastWriteTimeUtc(horizonPath);
                        var latitude = profiles.ActiveProfile.AstrometrySettings.Latitude;
                        var meridian = profiles.ActiveProfile.MeridianFlipSettings.PauseTimeBeforeMeridian;
                        Step($"Changing {change} during a long native setup hook while the server is offline");
                        try
                        {
                            if (change == "horizon")
                            {
                                await File.WriteAllTextAsync(horizonPath, "[[80,0],[80,100],[80,100.0001],[80,100.0002],[80,360]]", lifetime.Token);
                                File.SetLastWriteTimeUtc(horizonPath, timestamp);
                            }
                            else if (change == "site") profiles.ActiveProfile.AstrometrySettings.Latitude = latitude + 1;
                            else if (change == "meridian") profiles.ActiveProfile.MeridianFlipSettings.PauseTimeBeforeMeridian = meridian + 1;
                            else throw new InvalidDataException("Unknown constraint test.");
                            try { await executing.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidDataException("Changed constraints did not stop acquisition."); }
                            catch (InvalidOperationException error) when (error.Message.StartsWith("Rig constraints changed", StringComparison.Ordinal)) { }
                            if (!slowSetup.Canceled || slowSetup.Completed || camera.GetInfo().IsExposing || !telescope.GetInfo().AtPark
                                || telescope.GetInfo().TrackingEnabled || AcquisitionLease.IsActive
                                || sessionHookEvents.Contains("AfterEachExposure")
                                || Directory.EnumerateFiles(Path.Combine(root, "images"), "*.fits", SearchOption.AllDirectories).Any()
                                || sessionContainer.Display.Operation.Length != 0
                                || sessionContainer.Display.Phase != "Stopped; parked; rig constraints changed; review required")
                                throw new InvalidDataException("Constraint stop did not cancel setup, prevent capture and park with a useful status.");
                        }
                        finally
                        {
                            await File.WriteAllTextAsync(horizonPath, originalHorizon, lifetime.Token);
                            profiles.ActiveProfile.AstrometrySettings.Latitude = latitude;
                            profiles.ActiveProfile.MeridianFlipSettings.PauseTimeBeforeMeridian = meridian;
                        }
                        await Task.Delay(1500, lifetime.Token);
                        if (!executing.IsCompleted || AcquisitionLease.IsActive || camera.GetInfo().IsExposing || !telescope.GetInfo().AtPark)
                            throw new InvalidDataException("Restoring constraints revived acquisition.");
                        constraintChangeVerified = true;
                        ledger = service.LastLedger;
                        await SessionUiProbe.RenderAsync(run, sessionContainer.Display);
                        Step("Changed constraints canceled slow setup, saved no exposure, parked and stayed stopped after restoration");
                        return;
                    }
                    if (coordinator.PriorityRefresh)
                    {
                        using var dispatchDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        while (!camera.GetInfo().IsExposing && !executing.IsCompleted) await Task.Delay(20, dispatchDeadline.Token);
                        if (executing.IsCompleted) throw new InvalidDataException("Priority test never entered capture.");
                        await coordinator.ReverseProjectOrderAsync(lifetime.Token);
                        Step("Changed global project order while a native exposure was running");
                    }
                    if (coordinator.WeatherHoldScenario is "safety-exposure" or "roof-exposure")
                    {
                        using var dispatchDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        while (!camera.GetInfo().IsExposing && !executing.IsCompleted) await Task.Delay(50, dispatchDeadline.Token);
                        if (executing.IsCompleted) throw new InvalidDataException("Weather test never entered native exposure.");
                        var roof = coordinator.WeatherHoldScenario == "roof-exposure";
                        if (roof) { if (!await dome.CloseShutter(lifetime.Token)) throw new IOException("Simulator roof did not close."); }
                        else safetySimulator.IsSafe = false;
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        deadline.CancelAfter(TimeSpan.FromMinutes(3));
                        while (!executing.IsCompleted && !sessionContainer.Display.Phase.Contains("hold; waiting", StringComparison.Ordinal))
                            await Task.Delay(50, deadline.Token);
                        if (executing.IsCompleted) { await executing; throw new InvalidDataException("Exposure interruption ended the session."); }
                        if (camera.GetInfo().IsExposing || telescope.GetInfo().Slewing || telescope.GetInfo().TrackingEnabled
                            || !AcquisitionLease.IsActive || roof && telescope.GetInfo().AtPark)
                            throw new InvalidDataException("Exposure weather hold did not stop equipment safely.");
                        var journal = Path.Combine(service.LastRunDirectory!, "journal");
                        var interruptedCapture = Directory.GetFiles(journal, "*.json", SearchOption.AllDirectories).Select(CaptureJournal.Read).Single();
                        if (interruptedCapture.Phase != CapturePhase.Failed || interruptedCapture.SavedPath is not null)
                            throw new InvalidDataException("Canceled native exposure was not settled as a spent failed attempt.");
                        await SessionUiProbe.RenderAsync(run, sessionContainer.Display);
                        var cleared = System.Diagnostics.Stopwatch.StartNew();
                        if (roof) { if (!await dome.OpenShutter(lifetime.Token)) throw new IOException("Simulator roof did not reopen."); }
                        else safetySimulator.IsSafe = true;
                        while (!executing.IsCompleted && !camera.GetInfo().IsExposing) await Task.Delay(50, deadline.Token);
                        if (executing.IsCompleted || cleared.Elapsed < TimeSpan.FromSeconds(sessionContainer.Options.StableSafeSeconds))
                            throw new InvalidDataException("Safe/Open did not resume a fresh exposure after stability.");
                        await executing.WaitAsync(deadline.Token);
                        var settled = Directory.GetFiles(journal, "*.json", SearchOption.AllDirectories).Select(CaptureJournal.Read).ToArray();
                        if (settled.Length != 3 || settled.Count(x => x.Phase == CapturePhase.Failed) != 1
                            || settled.Count(x => x.Phase == CapturePhase.Saved && File.Exists(x.SavedPath)) != 2
                            || settled.Select(x => x.Intent.CaptureId).Distinct().Count() != 3
                            || camera.GetInfo().IsExposing || !telescope.GetInfo().AtPark || telescope.GetInfo().TrackingEnabled || AcquisitionLease.IsActive
                            || sessionHookEvents.Count(x => x == "AfterEachExposure") != 2)
                            throw new InvalidDataException("Weather resume replayed/refunded an exposure or skipped fresh saved-image hooks/shutdown.");
                        await coordinator.EndOutageAsync(root, lifetime.Token);
                        var checkIn = new DirectorCheckInService(profiles) { LocalStateRoot = service.LocalStateRoot };
                        var replay = await checkIn.RunAsync(null, lifetime.Token);
                        if (!replay.CaughtUp || replay.AcknowledgedThrough != 6 || (await checkIn.RunAsync(null, lifetime.Token)).DeliveredEvents != 0)
                            throw new InvalidDataException("Interrupted and resumed captures did not batch replay exactly once.");
                        ledger = service.LastLedger;
                        captures.AddRange(settled.Where(x => x.Phase == CapturePhase.Saved));
                        unsafeCancellationVerified = !roof;
                        enclosureCancellationVerified = roof;
                        await File.WriteAllTextAsync(Path.Combine(run, "weather-hold-verified.txt"), "Interrupted exposure consumed; fresh captures resumed after stable Safe/Open; batch replay verified", lifetime.Token);
                        Step("Weather/roof interrupted exposure settled without refund; fresh native captures resumed offline and replayed once");
                        return;
                    }
                    if (coordinator.EnclosureClosure)
                    {
                        using var dispatchDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        while (!camera.GetInfo().IsExposing && !executing.IsCompleted) await Task.Delay(50, dispatchDeadline.Token);
                        if (executing.IsCompleted || !camera.GetInfo().IsExposing || telescope.GetInfo().AtPark)
                            throw new InvalidDataException("Enclosure test never entered an unparked exposure.");
                        Step("Closing simulator enclosure during public acquisition while weather remains safe");
                        var closing = dome.CloseShutter(lifetime.Token);
                        try
                        {
                            try { await executing.WaitAsync(TimeSpan.FromSeconds(15)); throw new InvalidDataException("Enclosure closure incorrectly completed acquisition."); }
                            catch (AggregateException) when (sessionContainer.Display.Phase == "Stopped; enclosure blocks parking") { }
                        }
                        finally { if (!await closing) throw new IOException("Simulator enclosure did not close."); }
                        if (telescope.GetInfo().AtPark || telescope.GetInfo().TrackingEnabled || telescope.GetInfo().Slewing
                            || camera.GetInfo().IsExposing || AcquisitionLease.IsActive || !safety.GetInfo().IsSafe)
                            throw new InvalidDataException("Enclosure stop failed to abort or incorrectly parked/changed weather safety.");
                        if (!await dome.OpenShutter(lifetime.Token)) throw new IOException("Simulator enclosure did not reopen.");
                        await Task.Delay(1500, lifetime.Token);
                        if (telescope.GetInfo().AtPark || camera.GetInfo().IsExposing || !executing.IsCompleted || AcquisitionLease.IsActive)
                            throw new InvalidDataException("Enclosure reopening revived acquisition or parking.");
                        enclosureCancellationVerified = true;
                        if (coordinator.WeatherHoldScenario is not null)
                            await File.WriteAllTextAsync(Path.Combine(run, "weather-hold-verified.txt"), "Uncertain exposure blocks roof resume", lifetime.Token);
                        ledger = service.LastLedger;
                        Step("Enclosure closure aborted exposure, stopped mount motion, blocked parking and stayed stopped after reopening");
                        return;
                    }
                    if (coordinator.PublicUnsafe)
                    {
                        using var dispatchDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        while (!camera.GetInfo().IsExposing && !executing.IsCompleted) await Task.Delay(50, dispatchDeadline.Token);
                        if (executing.IsCompleted || !camera.GetInfo().IsExposing) throw new InvalidDataException("Public safety test never entered native exposure.");
                        safetySimulator.IsSafe = false;
                        try { await executing.WaitAsync(TimeSpan.FromSeconds(15)); throw new InvalidDataException("Unsafe public session completed successfully."); }
                        catch (OperationCanceledException) when (interlock.Interrupted.IsCancellationRequested) { }
                        catch (InvalidOperationException error) when (coordinator.WeatherHoldScenario == "safety-exposure"
                            && error.Message == "Unresolved operations prevent normal session completion.")
                        { }
                        if (telescope.GetInfo().AtPark == coordinator.AbortWithoutPark || camera.GetInfo().IsExposing || AcquisitionLease.IsActive
                            || telescope.GetInfo().TrackingEnabled || telescope.GetInfo().Slewing)
                            throw new InvalidDataException("Unsafe public session did not abort, apply the selected mount policy and release ownership.");
                        var expectedPhase = coordinator.AbortWithoutPark ? "Stopped; tracking off; reconciliation required" : "Stopped; parked; reconciliation required";
                        if (sessionContainer.Display.Phase != expectedPhase)
                            throw new InvalidDataException("Unsafe public session did not report the selected shutdown outcome.");
                        safetySimulator.IsSafe = true;
                        using (var fresh = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                            while (!safety.GetInfo().IsSafe) await Task.Delay(100, fresh.Token);
                        if (interlock.Read().Safety == PlannerSafety.Safe || !executing.IsCompleted || lifetime.IsCancellationRequested)
                            throw new InvalidDataException("Safety recovery revived the public session or the probe caused cancellation.");
                        unsafeCancellationVerified = true;
                        if (coordinator.WeatherHoldScenario is not null)
                            await File.WriteAllTextAsync(Path.Combine(run, "weather-hold-verified.txt"), "Uncertain exposure blocks weather resume", lifetime.Token);
                        ledger = service.LastLedger;
                        Step(coordinator.AbortWithoutPark ? "Public unsafe monitor aborted exposure, stopped slew/tracking without parking and stayed stopped after recovery"
                            : "Public unsafe monitor aborted exposure, parked and stayed stopped after recovery");
                        return;
                    }
                    if (coordinator.MoonAvoidance)
                    {
                        using var waitingDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        waitingDeadline.CancelAfter(TimeSpan.FromSeconds(90));
                        while (!executing.IsCompleted && (sessionContainer.Display.Phase != "moon_avoidance"
                            || !telescope.GetInfo().AtPark || !sessionHookEvents.Contains("BeforeWait")))
                            await Task.Delay(100, waitingDeadline.Token);
                        if (executing.IsCompleted) throw new InvalidDataException("Moon avoidance did not retain a parked waiting session.");
                        if (coordinator.WeatherHoldScenario is not null && !startupHold) await VerifyWeatherHold();
                        if (coordinator.NightEndScenario is not null) await VerifyNightEnd();
                        else
                        {
                            publicLifetime.Cancel();
                            try { await executing; throw new InvalidDataException("Moon wait ignored cancellation."); }
                            catch (OperationCanceledException) when (publicLifetime.IsCancellationRequested) { }
                        }
                    }
                    else if (coordinator.AutomaticWorkloads && !coordinator.OfflineWorkloadRelease)
                    {
                        using var waitingDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        waitingDeadline.CancelAfter(TimeSpan.FromSeconds(phd2 is not null ? 420 : coordinator.ForceNativeFlip ? 180 : 90));
                        while (!executing.IsCompleted && sessionContainer.Display.Phase != "Waiting for eligible work or quality assessment")
                            await Task.Delay(100, waitingDeadline.Token);
                        if (executing.IsCompleted) await executing;
                        if (!telescope.GetInfo().AtPark) throw new InvalidDataException("Automatic session did not park before waiting for more work.");
                        if (coordinator.WeatherHoldScenario == "safety-workload") await VerifyWeatherHold();
                        if (coordinator.NightEndScenario is not null) await VerifyNightEnd();
                        else
                        {
                            publicLifetime.Cancel();
                            try { await executing; throw new InvalidDataException("Automatic session ignored cancellation."); }
                            catch (OperationCanceledException) when (publicLifetime.IsCancellationRequested) { }
                        }
                        if (!coordinator.PriorityRefresh) await coordinator.VerifyAutomaticWorkloadAsync(Path.Combine(run, "public-state"), equipment, lifetime.Token);
                    }
                    else await executing;
                }
                finally
                {
                    if (!executing.IsCompleted) { lifetime.Cancel(); try { await executing; } catch (OperationCanceledException) { } }
                    if (nightSequence is not null) { nightSequence.Remove(sessionContainer); nightSequence.AttachNewParent(null); }
                }
                async Task VerifyNightEnd()
                {
                    await executing.WaitAsync(TimeSpan.FromMinutes(3), lifetime.Token);
                    if (!followingStepRan || sessionContainer.Status != SequenceEntityStatus.FINISHED || !telescope.GetInfo().AtPark || telescope.GetInfo().TrackingEnabled
                        || camera.GetInfo().IsExposing || AcquisitionLease.IsActive || publicLifetime.IsCancellationRequested
                        || sessionHookEvents.Contains("AfterTargetComplete")
                        || sessionContainer.Display.Phase != (coordinator.WeatherHoldScenario == "roof-night-end"
                            ? "Night ended; tracking off; enclosure blocks parking" : "Night ended; parked"))
                        throw new InvalidDataException("Night end did not park, release ownership and execute the following native sequence step.");
                    await File.WriteAllTextAsync(Path.Combine(run, "night-end-verified.txt"), coordinator.NightEndScenario, lifetime.Token);
                    Step("Normal night end parked and ran the following native sequence step without cancellation");
                    // The later duplicate-launch test needs fresh admission
                    // evidence so it reaches the allocation replay guard.
                    if (coordinator.WeatherHoldScenario == "roof-night-end"
                        && !await dome.OpenShutter(lifetime.Token)) throw new IOException("Simulator roof did not reopen after night-end verification.");
                }
                async Task VerifyWeatherHold()
                {
                    var roof = coordinator.UsesEnclosure;
                    var idle = coordinator.WeatherHoldScenario == "safety-workload";
                    Step(roof ? "Closing simulator roof during offline target wait" : "Making simulator weather unsafe during offline target wait");
                    if (roof)
                    {
                        if (!await dome.CloseShutter(lifetime.Token)) throw new IOException("Simulator roof did not close.");
                    }
                    else safetySimulator.IsSafe = false;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(90));
                    while (!executing.IsCompleted && !sessionContainer.Display.Phase.Contains("hold; waiting", StringComparison.Ordinal))
                        await Task.Delay(50, deadline.Token);
                    if (executing.IsCompleted) await executing;
                    if (camera.GetInfo().IsExposing || telescope.GetInfo().Slewing || telescope.GetInfo().TrackingEnabled || !AcquisitionLease.IsActive)
                        throw new InvalidDataException("Weather hold did not stop equipment and retain local ownership.");
                    await SessionUiProbe.RenderAsync(run, sessionContainer.Display);
                    if (coordinator.WeatherHoldScenario != "roof-night-end")
                    {
                        async Task Clear()
                        {
                            if (roof) { if (!await dome.OpenShutter(lifetime.Token)) throw new IOException("Simulator roof did not open."); }
                            else safetySimulator.IsSafe = true;
                        }
                        await Clear();
                        while (!sessionContainer.Display.Phase.Contains("stable Safe/Open", StringComparison.Ordinal) && !executing.IsCompleted)
                            await Task.Delay(50, deadline.Token);
                        await Task.Delay(500, deadline.Token);
                        if (roof) { if (!await dome.CloseShutter(lifetime.Token)) throw new IOException("Simulator roof did not close again."); }
                        else safetySimulator.IsSafe = false;
                        while (!sessionContainer.Display.Phase.Contains("hold; waiting", StringComparison.Ordinal) && !executing.IsCompleted)
                            await Task.Delay(50, deadline.Token);
                        var cleared = System.Diagnostics.Stopwatch.StartNew();
                        await Clear();
                        while (!executing.IsCompleted && !sessionContainer.ActionHistory.Any(x => x.Outcome == (idle ? "Weather cleared; requesting fresh work" : "Weather cleared; selecting fresh work")))
                            await Task.Delay(50, deadline.Token);
                        if (executing.IsCompleted) throw new InvalidDataException("Weather hold ended the observing session.");
                        if (cleared.Elapsed < TimeSpan.FromSeconds(sessionContainer.Options.StableSafeSeconds) || !idle && !sessionHookEvents.Contains("AfterWait"))
                            throw new InvalidDataException("Weather hold resumed before stable clearance or skipped the interrupted wait hook.");
                        Step("Stable Safe/Open resumed fresh local selection; flapping reset the delay");
                    }
                    await File.WriteAllTextAsync(Path.Combine(run, "weather-hold-verified.txt"), coordinator.WeatherHoldScenario, lifetime.Token);
                }
                ledger = service.LastLedger ?? throw new InvalidDataException("Public session has no ledger.");
                var journalRoots = coordinator.PriorityRefresh
                    ? Directory.EnumerateDirectories(Path.Combine(service.LocalStateRoot, profiles.ActiveProfile.Id.ToString("N"))).Select(d => Path.Combine(d, "journal")).Where(Directory.Exists)
                    : [Path.Combine(service.LastRunDirectory!, "journal")];
                foreach (var file in journalRoots.SelectMany(d => Directory.GetFiles(d, "*.json", SearchOption.AllDirectories)))
                {
                    var capture = CaptureJournal.Read(file);
                    if (capture.Phase != CapturePhase.Saved || !File.Exists(capture.SavedPath)) throw new InvalidDataException("Public capture lacks a saved file.");
                    var restored = await imageFactory.CreateFromFile(capture.SavedPath, 16, false, lifetime.Token);
                    if (!restored.MetaData.GenericHeaders.OfType<StringMetaDataHeader>().Any(h => h.Key == NinaCaptureAdapter.CaptureIdHeader
                        && h.Value.Trim() == capture.Intent.CaptureId.ToString("D"))) throw new InvalidDataException("Public FITS capture identity missing.");
                    captures.Add(capture);
                }
                if (coordinator.PriorityRefresh)
                {
                    await coordinator.VerifyPriorityRefreshAsync(service.LocalStateRoot, equipment, captures, lifetime.Token);
                    var checkIn = new DirectorCheckInService(profiles) { LocalStateRoot = service.LocalStateRoot };
                    var replay = await checkIn.RunAsync(null, lifetime.Token);
                    if (replay.Runs != 2 || !replay.CaughtUp || replay.DeliveredEvents != 0)
                        throw new InvalidDataException("Handoff batch replay did not preserve both original ledgers.");
                    await new DirectorCheckInItem(checkIn).Execute(progress, lifetime.Token);
                    await coordinator.ReportStatusAsync(programTarget, "priority_refresh_complete", lifetime.Token);
                    await SessionUiProbe.RenderAsync(run, sessionContainer.Display);
                    Step("Priority handoff sealed two ledgers; successor preserved pending captures and spent attempts; batch replay changed nothing");
                    return;
                }
                await coordinator.EndOutageAsync(root, lifetime.Token);
                if (coordinator.DeferredCheckIn && (Directory.GetFiles(service.LastRunDirectory!, "capture-cursor-*.json").Length != 0
                    || Directory.GetFiles(service.LastRunDirectory!, "preparation-cursor-*.json").Length != 0))
                    throw new InvalidDataException("Deferred mode delivered events before explicit check-in.");
                var statusBeforeReplay = await coordinator.ReadRigStatusAsync(lifetime.Token);
                var savedCheckIn = new DirectorCheckInService(profiles) { LocalStateRoot = service.LocalStateRoot };
                var delivered = await savedCheckIn.RunAsync(null, lifetime.Token);
                if (delivered.Runs != 1 || !delivered.CaughtUp || delivered.AcknowledgedThrough != 6)
                    throw new InvalidDataException("Saved-run check-in did not deliver the original six ledger events.");
                if (coordinator.OfflineWorkloadRelease)
                {
                    if (delivered.ReleasedWorkloads != 1) throw new InvalidDataException("Completed offline workload was not released during check-in.");
                    await coordinator.VerifyAutomaticWorkloadAsync(Path.Combine(run, "public-state"), equipment, lifetime.Token);
                    Step("Completed offline workload released after reconnect; next request waits for quality without duplicating acquisition");
                }
                var repeat = await savedCheckIn.RunAsync(null, lifetime.Token);
                if (repeat.DeliveredEvents != 0 || repeat.AcknowledgedThrough != 6)
                    throw new InvalidDataException("Saved-run check-in did not preserve its acknowledgement cursor.");
                await new DirectorCheckInItem(savedCheckIn).Execute(progress, lifetime.Token);
                await coordinator.VerifyOperationReplayAsync(statusBeforeReplay, lifetime.Token);
                Step("Saved-run batch check-in and sequencer action replayed no hardware or acknowledged events");
                await using var reopened = new RuntimeController(Path.GetDirectoryName(typeof(DirectorPlugin).Assembly.Location)!, Path.Combine(service.LastRunDirectory!, "ledger"));
                await reopened.StartAsync(rigId, lifetime.Token);
                var eop = NinaEarthOrientation.Read(NinaEarthOrientation.DatabasePath, assignment.ValidFromMs, assignment.ExpiresAtMs, lifetime.Token);
                var g = new NinaGeometrySnapshot(constraintReader, equipmentReader).Read(constraintBinding, equipmentBinding,
                    new(1, 89, eop.Orientation, assignment.Goals.Select(x => new DirectorGoalLimits(x.Id, 20, 89, 0)).ToImmutableArray()));
                var state = new PlannerState(rigId, equipment.Id, NowMs(), assignment.ExpiresAtMs, PlannerSafety.Safe, true, false, new(0, 0));
                if (Require(await reopened.OpenGeometryAsync(program, g.Constraints, state, lifetime.Token)) != ledger) throw new InvalidDataException("Public ledger identity changed.");
                checkpoint = await coordinator.DeliverAsync(Path.Combine(run, "public-checkin"), reopened, ledger, lifetime.Token);
                await reopened.StopAsync();
                await coordinator.VerifyProgramAsync(Path.Combine(run, "program-preview"), equipment, true, lifetime.Token);
                await coordinator.ReportStatusAsync(programTarget, "public_acquisition_complete", lifetime.Token);
                var retry = new DirectorSessionContainer(service);
                retry.Options.EnableAcquisition = true;
                retry.Options.Enclosure = sessionContainer.Options.Enclosure;
                retry.Options.MaximumAltitude = 89;
                retry.Options.LocalTargetScheduling = coordinator.LocalTargetScheduling;
                ConfigureImaging(retry.Options);
                try { await retry.Execute(progress, lifetime.Token); throw new InvalidDataException("Public allocation replay was accepted."); }
                catch (CoordinatorIntakeException e) when (e.Failure == CoordinatorIntakeFailure.UnexpectedStatus) { }
                catch (InvalidOperationException e) when ((coordinator.RecoveryScenario is not null || coordinator.NightEndScenario is not null) && e.Message.Contains("previous observing session", StringComparison.Ordinal)) { }
                Step("Public acquisition saved three frames; server refused a second launch");
                coordinator.VerifyLocalTargets(captures, sessionHookEvents);
                if (nativeProbe is not null && (nativeProbe.Operations.Count(x => x == "Center") != 2 || nativeProbe.Operations.Count(x => x == "Autofocus") < 2
                    || !sessionContainer.ActionHistory.Any(x => x.Action == "Dither" && x.Outcome == "Succeeded")
                    || sessionContainer.GetTriggersSnapshot().Any()))
                    throw new InvalidDataException("Native target centering/autofocus did not run for both targets.");
                nativeProbe?.Flip?.Verify(sessionContainer, failed: false);
                if (phd2 is not null)
                {
                    rotatorFinalPosition = ((NINA.Equipment.Interfaces.IRotator)rotator!.GetDevice()).Position;
                    if (!float.IsFinite(rotatorFinalPosition.Value) || Math.Abs(rotatorFinalPosition.Value - 30) > 1
                        || captures.Any(c => c.Intent.PositionAngle != 30))
                        throw new InvalidDataException("Native rotation did not reach the server-requested position angle.");
                    if (!File.Exists(Path.Combine(root, "synthetic-solver", "synthetic-solves.log"))
                        || nativeProbe?.Flip?.Workflow?.Steps.Single(s => s.Id == "Recenter").Finished != true)
                        throw new InvalidDataException("Native recenter did not capture and run the synthetic solver.");
                }
                if (!coordinator.LocalTargetScheduling && !sessionHookEvents.SequenceEqual(new[] { "BeforeNewTarget", "AfterEachExposure", "AfterEachExposure", "AfterEachExposure", "AfterNewTarget", "AfterEachTarget" }))
                    throw new InvalidDataException("Public session hooks did not follow confirmed save boundaries.");
                await SessionUiProbe.RenderAsync(run, sessionContainer.Display, sessionContainer);
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
            var clearForCleanup = !dome.GetInfo().Connected;
            if (dome.GetInfo().Connected && dome.GetInfo().DeviceId == "ASCOM.OmniSim.Dome")
                await Cleanup("Opening simulator enclosure for fixture cleanup", async () =>
                {
                    clearForCleanup = await dome.OpenShutter(cleanup.Token);
                    if (!clearForCleanup) throw new IOException("Simulator enclosure failed to open for cleanup.");
                });
            if (telescope.GetInfo().Connected && telescope.GetInfo().DeviceId == "ASCOM.OmniSim.Telescope")
            {
                if (clearForCleanup) await Cleanup("Parking simulator", async () =>
                {
                    if (!await telescope.ParkTelescope(progress, cleanup.Token)) throw new IOException("Park failed.");
                });
                await Cleanup("Disconnecting simulator telescope", telescope.Disconnect);
            }
            if (filters.GetInfo().Connected && filters.GetInfo().DeviceId == "ASCOM.OmniSim.FilterWheel")
                await Cleanup("Disconnecting simulator filter wheel", filters.Disconnect);
            if (guider.GetInfo().Connected && guider.GetInfo().DeviceId == (phd2 is null ? "Direct_Guider" : "PHD2_Single"))
                await Cleanup("Disconnecting simulator guider", guider.Disconnect);
            if (focuser?.GetInfo().Connected == true && focuser.GetInfo().DeviceId == "ASCOM.OmniSim.Focuser")
                await Cleanup("Disconnecting simulator focuser", focuser.Disconnect);
            if (rotator?.GetInfo().Connected == true && rotator.GetInfo().DeviceId == "ASCOM.OmniSim.Rotator")
                await Cleanup("Disconnecting simulator rotator", rotator.Disconnect);
            if (camera.GetInfo().Connected && camera.GetInfo().DeviceId == "ASCOM.OmniSim.Camera")
                await Cleanup("Disconnecting simulator camera", camera.Disconnect);
            if (safety.GetInfo().Connected && safety.GetInfo().DeviceId == SafetySimulatorId)
                await Cleanup("Disconnecting native safety simulator", safety.Disconnect);
            if (dome.GetInfo().Connected && dome.GetInfo().DeviceId == "ASCOM.OmniSim.Dome")
                await Cleanup("Disconnecting simulator enclosure", dome.Disconnect);
            await Cleanup("Stopping sidecar", runtime.StopAsync);
            if (coordinator is not null)
            {
                await Cleanup("Restoring isolated server after outage", () => coordinator.EndOutageAsync(root, cleanup.Token));
                await Cleanup("Revoking test pairing", () => coordinator.DisposeAsync().AsTask());
            }
            var result = new
            {
                passed = errors.Count == 0 && (coordinator?.NativeImagingFailure is not null ? nativeFailureVerified : coordinator?.ConstraintChange is not null ? constraintChangeVerified : coordinator?.EnclosureClosure == true ? enclosureCancellationVerified : coordinator?.PublicUnsafe == true ? unsafeCancellationVerified : captures.Count == 3),
                enclosureCancellationVerified,
                nativeImagingVerified = nativeProbe is not null && errors.Count == 0 && captures.Count == 3,
                nativeFailureVerified,
                nativeOperations = nativeProbe?.Operations,
                phd2Simulator = phd2 is not null,
                rotatorFinalPosition,
                nativeFlip = nativeProbe?.Flip is { } flipEvidence ? new
                {
                    flipEvidence.Attempts,
                    flipEvidence.MountResult,
                    flipEvidence.FailureInjected,
                    flipEvidence.NativeAfterSuccess,
                    flipEvidence.PierBefore,
                    flipEvidence.PierAfter,
                    flipEvidence.Events,
                    flipEvidence.RecenterSolutions,
                    Steps = flipEvidence.Workflow?.Steps.Select(s => new { s.Id, s.Finished }).ToArray()
                } : null,
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
                localTargetsVerified = coordinator?.LocalTargetsVerified ?? false,
                moonAvoidanceVerified = coordinator is { MoonAvoidance: true, LocalTargetsVerified: true },
                observingPreferencesVerified = coordinator is { ObservingPreferences: true, LocalTargetsVerified: true },
                projectOrderVerified = coordinator is { ProjectOrder: true, LocalTargetsVerified: true },
                priorityRefreshVerified = coordinator?.PriorityRefreshVerified ?? false,
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
                constraintChangeVerified,
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

    private sealed class SlowSetupState
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Canceled;
        internal bool Completed;
    }

    private sealed class SlowSetupProbe(SlowSetupState? shared = null) : SequenceItem
    {
        private readonly SlowSetupState state = shared ?? new();
        internal TaskCompletionSource Entered => state.Entered;
        internal bool Canceled => state.Canceled;
        internal bool Completed => state.Completed;
        public override object Clone() => new SlowSetupProbe(state);
        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            Entered.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(45), token); state.Completed = true; }
            catch (OperationCanceledException) { state.Canceled = true; throw; }
        }
    }

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

    private sealed class NightEndMarker(Action completed) : SequenceItem
    {
        public override object Clone() => new NightEndMarker(completed);
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            completed();
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
            || profile.FocuserSettings.Id is not ("No_Device" or "ASCOM.OmniSim.Focuser") || profile.RotatorSettings.Id is not ("No_Device" or "ASCOM.OmniSim.Rotator")
            || (profile.GuiderSettings.GuiderName is not ("No_Guider" or "Direct_Guider")
                && Phd2Fixture.Read(root)?.Matches(profile.GuiderSettings) != true)
            || profile.DomeSettings.Id is not ("No_Device" or "ASCOM.OmniSim.Dome") || profile.SwitchSettings.Id != "No_Device"
            || profile.FlatDeviceSettings.Id != "No_Device" || profile.SafetyMonitorSettings.Id != SafetySimulatorId
            || profile.WeatherDataSettings.Id != "No_Device")
            throw new InvalidOperationException("The probe requires its simulator-only test profile.");
        return root;
    }
}
