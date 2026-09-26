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
        var evaluations = new List<PlannerEvaluation>();
        DirectorConfiguration? equipment = null;
        NinaConstraints? constraints = null;
        NinaConstraints? editedConstraints = null;
        await using var runtime = new RuntimeController(Path.GetDirectoryName(typeof(DirectorPlugin).Assembly.Location)!);
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
            static ulong NowMs() => checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var started = NowMs();
            var assignment = new PlannerAssignment($"smoke-{Path.GetFileName(run)}", 1, "ascom-smoke", "omnisim",
                started, started + 180000, Enumerable.Range(0, 3).Select(i => new PlannerGoal(
                    $"filter-{i}", (uint)(3 - i), 1, 0, 0, 1, 1000, 5000, [new(started, started + 180000)])).ToImmutableArray());
            var filterPositions = new Dictionary<string, short> { ["filter-0"] = 0, ["filter-1"] = 1, ["filter-2"] = 2 };
            async Task<PlannerEvaluation> Evaluate()
            {
                await Revalidate(lifetime.Token);
                var snapshot = new PlannerRequest(assignment, new("ascom-smoke", "omnisim", NowMs(),
                    started + 180000, PlannerSafety.Safe, true, false, new(0, 0)));
                var result = await runtime.EvaluateAsync(snapshot, lifetime.Token);
                evaluations.Add(result);
                Step($"Rust planner: {result.Decision?.Action.ToString() ?? result.Error?.ToString()} {result.Decision?.GoalId}");
                if (result.Error is not null) throw new InvalidDataException($"Planner rejected simulator snapshot: {result.Error}");
                return result;
            }
            while (true)
            {
                var selected = (await Evaluate()).Decision!;
                if (selected.Action == PlannerAction.Wait && selected.Reason == "pending_assessment" && captures.Count == 3) break;
                if (selected.Action != PlannerAction.Acquire || selected.GoalId is null || captures.Count >= 3)
                    throw new InvalidOperationException("Unexpected simulator planner outcome.");
                var goal = assignment.Goals.Single(g => g.Id == selected.GoalId);
                var i = filterPositions[goal.Id];
                Step($"Selecting simulator filter {i}");
                await filters.ChangeFilter(new FilterInfo { Name = $"Smoke-{i}", Position = i }, lifetime.Token, progress);
                Step($"Capturing simulator exposure {captures.Count + 1}/3");
                var intent = new CaptureIntent(Guid.NewGuid(), profileId, "ascom-smoke", "omnisim",
                    assignment.Id, assignment.Revision, goal.Id, "ASCOM.OmniSim.Camera", goal.ExposureMs / 1000.0,
                    "Director ASCOM Smoke", catalogTarget.RA * 15, catalogTarget.Dec, 0);
                var evidence = await adapter.CaptureAsync(intent, async cancellation =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    var fresh = (await Evaluate()).Decision!;
                    if (fresh.Action != PlannerAction.Acquire || fresh.GoalId != goal.Id)
                        throw new InvalidOperationException("Planner no longer recommends this capture after preparation.");
                }, progress, lifetime.Token);
                if (evidence.Phase != CapturePhase.Saved || !File.Exists(evidence.SavedPath))
                    throw new IOException("Capture has no confirmed file.");
                var restored = await imageFactory.CreateFromFile(evidence.SavedPath, 16, false, lifetime.Token);
                if (!restored.MetaData.GenericHeaders.OfType<StringMetaDataHeader>().Any(h =>
                        h.Key == NinaCaptureAdapter.CaptureIdHeader && h.Value.Trim() == intent.CaptureId.ToString("D"))
                    || restored.Data.FlatArray.Length == 0
                    || restored.Data.FlatArray.Min() == restored.Data.FlatArray.Max())
                    throw new InvalidDataException("Saved FITS identity or pixel data failed readback.");
                var journal = CaptureJournal.Read(Path.Combine(run, "journal", profileId.ToString("N"), $"{intent.CaptureId:N}.json"));
                if (journal != evidence) throw new InvalidDataException("Durable journal differs from the save receipt.");
                captures.Add(evidence);
                // Save evidence reserves pending work; only a future PSF Guard grade
                // can credit accepted work. This fixture has no resume/retry path.
                assignment = assignment with
                {
                    Goals = assignment.Goals.Select(g => g.Id == goal.Id
                    ? g with { Pending = g.Pending + 1, AttemptsRemaining = g.AttemptsRemaining - 1 } : g).ToImmutableArray()
                };
            }
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
                scope = "rust-selected-native-capture-with-fixture-assignment-not-server-or-recovery",
                steps,
                evaluations,
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
