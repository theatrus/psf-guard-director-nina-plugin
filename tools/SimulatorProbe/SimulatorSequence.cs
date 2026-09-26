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
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Plugin.Acquisition;
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
    private readonly IImagingMediator imaging;
    private readonly IImageSaveMediator saves;
    private readonly IImageHistoryVM history;
    private readonly IImageDataFactory imageFactory;

    [ImportingConstructor]
    public SimulatorSequence(IProfileService profiles, ICameraMediator camera, ITelescopeMediator telescope,
        IFilterWheelMediator filters, IImagingMediator imaging, IImageSaveMediator saves, IImageHistoryVM history,
        IImageDataFactory imageFactory)
    {
        this.profiles = profiles;
        this.camera = camera;
        this.telescope = telescope;
        this.filters = filters;
        this.imaging = imaging;
        this.saves = saves;
        this.history = history;
        this.imageFactory = imageFactory;
    }

    public override object Clone()
    {
        var clone = new SimulatorSequence(profiles, camera, telescope, filters, imaging, saves, history, imageFactory);
        clone.CopyMetaData(this);
        return clone;
    }

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var root = ValidateEnvironment();
        if (camera.GetInfo().Connected || telescope.GetInfo().Connected || filters.GetInfo().Connected)
            throw new InvalidOperationException("Start the probe with all simulator devices disconnected.");
        var profileId = profiles.ActiveProfile.Id;
        var run = Path.Combine(root, "probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        var steps = new List<string>();
        var errors = new List<Exception>();
        var captures = new List<CaptureEvidence>();
        var evaluations = new List<PlannerDecision>();
        var operations = new List<PreparationCompletion>();
        LedgerIdentity? ledger = null;
        DirectorConfiguration? equipment = null;
        NinaConstraints? constraints = null;
        NinaConstraints? editedConstraints = null;
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
            if (profiles.ActiveProfile.Id != profileId || runtime.Status.State != RuntimeState.Ready
                || camera.GetInfo().DeviceId != "ASCOM.OmniSim.Camera" || !camera.GetInfo().Connected
                || telescope.GetInfo().DeviceId != "ASCOM.OmniSim.Telescope" || !telescope.GetInfo().Connected
                || filters.GetInfo().DeviceId != "ASCOM.OmniSim.FilterWheel" || !filters.GetInfo().Connected)
                throw new InvalidOperationException("Simulator context or sidecar changed.");
            return Task.CompletedTask;
        }
        try
        {
            Step("Starting verified planning sidecar");
            await runtime.StartAsync("ascom-smoke", lifetime.Token);
            if (runtime.Status.State != RuntimeState.Ready) throw new InvalidOperationException("Sidecar failed", runtime.Status.Error);
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
            var equipmentBinding = new NinaEquipmentBinding(profileId, "ascom-smoke", constraints.Revision,
                "ASCOM.OmniSim.Camera", "ASCOM.OmniSim.FilterWheel",
                profiles.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Select(f =>
                    new NinaFilterBinding($"filter-{f.Position}", f.Position, f.Name)).ToImmutableArray(), false, 0);
            var equipmentReader = new NinaEquipmentSnapshot(profiles, camera, filters);
            equipment = equipmentReader.Read(equipmentBinding);
            Step("Unparking simulator");
            if (!await telescope.UnparkTelescope(progress, lifetime.Token)) throw new IOException("Unpark failed.");
            // A small move near the simulator's current position avoids any dependence
            // on a real site's coordinates, sky visibility, or plate-solver catalogs.
            var current = telescope.GetCurrentPosition();
            var target = new Coordinates((current.RA + 0.02) % 24, current.Dec, current.Epoch, Coordinates.RAType.Hours);
            var catalogTarget = target.Transform(Epoch.J2000);
            Step("Slewing simulator");
            if (!await telescope.SlewToCoordinatesAsync(target, lifetime.Token)) throw new IOException("Slew failed.");
            var adapter = new NinaCaptureAdapter(profiles, camera, imaging, saves, history,
                Path.Combine(run, "journal"), TimeSpan.FromSeconds(30), TimeProvider.System);
            var boundCapture = new NinaProgramCapture(equipmentReader, camera, filters, adapter);
            if (equipment.Gain is not CameraControl.Unsupported || equipment.Offset is not CameraControl.Unsupported)
                throw new InvalidOperationException("This fixture requires OmniSim's unsupported gain and offset controls.");
            static ulong NowMs() => checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var started = NowMs();
            var assignment = new PlannerAssignment($"smoke-{Path.GetFileName(run)}", 1, "ascom-smoke", equipment.Id,
                started, started + 180000, Enumerable.Range(0, 3).Select(i => new PlannerGoal(
                    $"filter-{i}", (uint)(3 - i), 1, 0, 0, 1, 1000, 5000, [new(started, started + 180000)])).ToImmutableArray());
            var programTarget = new DirectorTarget("smoke-target", "Director ASCOM Smoke",
                (uint)((ulong)Math.Round(catalogTarget.RA * 15 * 3600000) % 1296000000),
                checked((int)Math.Round(catalogTarget.Dec * 3600000)), null);
            var program = new DirectorProgram(1, assignment, equipment, [programTarget],
                Enumerable.Range(0, 3).Select(i => new ExposureRecipe($"recipe-{i}", 1000, $"filter-{i}", new(1, 1), null, null, 0, null)).ToImmutableArray(),
                Enumerable.Range(0, 3).Select(i => new GoalBinding($"filter-{i}", programTarget.Id, $"recipe-{i}")).ToImmutableArray());
            PlannerState State() => new("ascom-smoke", equipment.Id, NowMs(), started + 180000,
                PlannerSafety.Safe, true, false, new(0, 0));
            ledger = Require(await runtime.OpenProgramAsync(program, State(), lifetime.Token));
            async Task<PlannerDecision> Evaluate()
            {
                await Revalidate(lifetime.Token);
                var result = Require(await runtime.EvaluateLedgerAsync(State(), lifetime.Token));
                evaluations.Add(result);
                Step($"Rust ledger planner: {result.Action} {result.GoalId}");
                return result;
            }
            while (true)
            {
                var selected = await Evaluate();
                if (selected.Action == PlannerAction.Wait && selected.Reason == "pending_assessment" && captures.Count == 3) break;
                if (selected.Action != PlannerAction.Acquire || selected.GoalId is null || captures.Count >= 3)
                    throw new InvalidOperationException("Unexpected simulator planner outcome.");
                var preparationId = Guid.NewGuid().ToString("D");
                var local = new ProgramLocalState(equipment, new(equipment.Id, programTarget), false, false, 0);
                var began = Require(await runtime.BeginProgramPreparationAsync(preparationId, selected.GoalId, local,
                    new(0, 0, 0, 0, 5000, 1000, 5000), State(), lifetime.Token));
                if (!began.Created) throw new InvalidOperationException("Fixture preparation was not newly created.");
                for (var count = 0; ; count++)
                {
                    await Revalidate(lifetime.Token);
                    var next = Require(await runtime.AdvanceProgramPreparationAsync(preparationId, equipmentReader.Read(equipmentBinding), State(), lifetime.Token));
                    if (next is PreparationNext.ReadyToReserve ready && ready.GoalId == selected.GoalId) break;
                    if (next is not PreparationNext.Run issued || count >= 2)
                        throw new InvalidOperationException("Fixture preparation was not a new bounded operation.");
                    var operation = issued.Command;
                    SequenceItem item = operation.Operation switch
                    {
                        PreparationOperation.SwitchFilter filter => new NINA.Sequencer.SequenceItem.FilterWheel.SwitchFilter(profiles, filters)
                        {
                            ComboBoxText = equipmentBinding.Filters.Single(f => f.Id == filter.FilterId).Position!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        },
                        PreparationOperation.SetReadoutMode mode => new NINA.Sequencer.SequenceItem.Camera.SetReadoutMode(camera) { Mode = mode.Mode },
                        _ => throw new InvalidOperationException("Unexpected simulator preparation operation.")
                    };
                    item.Attempts = 1;
                    item.AttachNewParent(Parent);
                    Step($"Executing native {operation.Operation.GetType().Name}");
                    var operationStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    await item.Run(progress, lifetime.Token);
                    if (item.Status != NINA.Core.Enum.SequenceEntityStatus.FINISHED)
                        throw new InvalidOperationException("Native preparation did not finish successfully; leave the ledger unresolved.");
                    var completion = new PreparationCompletion(preparationId, operation.Ordinal, NowMs(),
                        checked((ulong)Math.Ceiling(System.Diagnostics.Stopwatch.GetElapsedTime(operationStart).TotalMilliseconds)), new PreparationOutcome.Succeeded());
                    Require(await runtime.CompletePreparationAsync(completion, lifetime.Token));
                    operations.Add(completion);
                }
                var captureId = Guid.NewGuid().ToString("D");
                var reservation = Require(await runtime.ReserveProgramPreparedAsync(preparationId, captureId,
                    equipmentReader.Read(equipmentBinding), State(), lifetime.Token));
                var binding = Require(await runtime.FindCaptureBindingAsync(captureId, lifetime.Token)).Binding
                    ?? throw new InvalidDataException("New capture binding is missing.");
                Step($"Capturing simulator exposure {captures.Count + 1}/3");
                var evidence = await boundCapture.CaptureAsync(reservation, binding, equipmentBinding, async cancellation =>
                {
                    await Revalidate(cancellation);
                    if (constraintReader.Refresh(constraintBinding).Revision != constraints.Revision || NowMs() >= assignment.ExpiresAtMs)
                        throw new InvalidOperationException("Simulator fixture constraints or deadline changed.");
                    var currentBinding = Require(await runtime.FindCaptureBindingAsync(captureId, cancellation)).Binding;
                    if (currentBinding?.Attempt != binding.Attempt || currentBinding.Attempt.Evidence is not LedgerEvidence.Reserved)
                        throw new InvalidOperationException("Simulator reservation changed before dispatch.");
                }, progress, lifetime.Token);
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
            }
            var savedEvents = Require(await runtime.ReadEventsAsync(0, token: lifetime.Token));
            if (savedEvents.Events.Count(e => e.Attempt.Evidence is LedgerEvidence.Saved) != 3)
                throw new InvalidDataException("The Rust ledger does not contain three saved captures.");
            await runtime.StopAsync();
            await runtime.StartAsync("ascom-smoke", lifetime.Token);
            if (Require(await runtime.OpenProgramAsync(program, State(), lifetime.Token)) != ledger
                || (await Evaluate()) is not { Action: PlannerAction.Wait, Reason: "pending_assessment" })
                throw new InvalidDataException("Restarted ledger lost identity or pending progress.");
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
            Step("Three captures saved with correlated receipts");
        }
        catch (Exception error) { errors.Add(error); Logger.Error(error); }
        finally
        {
            profiles.ProfileChanged -= ProfileChanged;
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
            await Cleanup("Stopping sidecar", runtime.StopAsync);
            var result = new
            {
                passed = errors.Count == 0 && captures.Count == 3,
                nina = "3.3.0.1058",
                scope = "durable-rust-program-native-capture-fixture-not-production-container-or-server",
                steps,
                evaluations,
                operations,
                ledger,
                equipment,
                constraints,
                editedConstraints,
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

    private string ValidateEnvironment() => ValidateEnvironment(
        Environment.GetEnvironmentVariable("DIRECTOR_NINA_TEST_ROOT"),
        Environment.GetEnvironmentVariable("DIRECTOR_NINA_TEST_TOKEN"), CoreUtil.APPLICATIONTEMPPATH, profiles.ActiveProfile);

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
            || profile.FlatDeviceSettings.Id != "No_Device" || profile.SafetyMonitorSettings.Id != "No_Device"
            || profile.WeatherDataSettings.Id != "No_Device")
            throw new InvalidOperationException("The probe requires its simulator-only test profile.");
        return root;
    }
}
