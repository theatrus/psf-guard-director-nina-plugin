using Moq;
using NINA.Core.Model;
using NINA.Equipment.Model;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed partial class NinaCaptureTests
{
    [Fact]
    public async Task RealRustPreparedReservationFlowsThroughNativeAdapterAndBackToLedger()
    {
        using var f = new Fixture();
        var source = Bound(f);
        source = source with { Target = source.Target with { PositionAngleMas = null } };
        var request = PlannerTests.Request();
        var assignment = request.Assignment with
        {
            RigId = "rig",
            ConfigurationId = source.Configuration.Id,
            Goals = [request.Assignment.Goals[0] with { ExposureMs = 1500 }]
        };
        var program = new DirectorProgram(1, assignment, source.Configuration,
            [source.Target], [source.Recipe], [new("goal", "target", "recipe")]);
        var state = request.State with { RigId = "rig", ConfigurationId = source.Configuration.Id };
        var directory = Directory.CreateTempSubdirectory("director-native-program-");
        try
        {
            await using var runtime = new RuntimeController(ProcessTests.BundleDirectory, directory.FullName);
            await runtime.StartAsync("rig");
            Assert.Null((await runtime.OpenProgramAsync(program, state)).Error);
            var local = new ProgramLocalState(source.Configuration, new(source.Configuration.Id, source.Target), false, false, 0);
            var began = await runtime.BeginProgramPreparationAsync("prep", "goal", local, new(0, 0, 0, 0, 0, 0, 0), state);
            Assert.Null(began.Error);
            Assert.True(began.Value!.Created);
            foreach (var expected in new PreparationOperation[] { new PreparationOperation.SwitchFilter("filter-l"), new PreparationOperation.SetReadoutMode(1) })
            {
                var command = Assert.IsType<PreparationNext.Run>((await runtime.AdvanceProgramPreparationAsync("prep", source.Configuration, state)).Value).Command;
                Assert.Equal(expected, command.Operation);
                state = state with { NowMs = state.NowMs + 100 };
                Assert.Null((await runtime.CompletePreparationAsync(new("prep", command.Ordinal, state.NowMs, 100, new PreparationOutcome.Succeeded()))).Error);
            }
            var reservation = (await runtime.ReserveProgramPreparedAsync("prep", source.Attempt.CaptureId, source.Configuration, state)).Value!;
            var binding = (await runtime.FindCaptureBindingAsync(source.Attempt.CaptureId)).Value!.Binding!;
            // Mock mediators only: the callback is not a production safety fence.
            var task = f.BoundCapture.CaptureAsync(reservation, binding, f.Local, _ => Task.CompletedTask, f.Progress, default);
            await f.Enqueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Saved();
            var saved = await task;
            Assert.Equal(assignment.Revision, saved.Intent.AssignmentRevision);
            Assert.Equal(binding.Ledger.LedgerId, saved.Intent.Program!.LedgerId);
            Assert.Null((await runtime.RecordAsync(binding.Attempt.CaptureId, new LedgerEvidence.Saved("image", (ulong)saved.TotalMs!.Value))).Error);
            Assert.IsType<LedgerEvidence.Saved>((await runtime.FindCaptureBindingAsync(binding.Attempt.CaptureId)).Value!.Binding!.Attempt.Evidence);
            var existing = (await runtime.ReserveProgramPreparedAsync("prep", binding.Attempt.CaptureId, source.Configuration, state)).Value!;
            Assert.Throws<InvalidOperationException>(() => f.BoundCapture.CreateIntent(existing, binding, f.Local));
            await runtime.StopAsync();
        }
        finally { directory.Delete(true); }
    }

    private static CaptureBinding Bound(Fixture f)
    {
        var configuration = f.Equipment.Read(f.Local);
        return new(new(Guid.NewGuid().ToString("D"), "assignment", 1, "rig", configuration.Id),
            new(f.Intent.CaptureId.ToString("D"), "goal", 2000, new LedgerEvidence.Reserved()),
            new("target", f.Intent.TargetName, 648000000, 72000000, 108000000),
            new("recipe", 1500, "filter-l", new(1, 1), 10, 20, 1, null), configuration);
    }

    private static LedgerReservation Created(CaptureBinding binding) => new(ReservationKind.Created, binding.Attempt, null);

    [Fact]
    public async Task BoundCaptureKeepsExactRecipeAndJournalIdentity()
    {
        using var f = new Fixture();
        var binding = Bound(f);
        var task = f.BoundCapture.CaptureAsync(Created(binding), binding, f.Local, _ => Task.CompletedTask, f.Progress, default);
        await f.Enqueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Saved();
        var result = await task;
        Assert.Equal(2, result.SchemaVersion);
        Assert.Equal(new(binding.Ledger.LedgerId, "target", "recipe", "filter-l", 1), result.Intent.Program);
        Assert.Equal(binding.Configuration.Id, result.Intent.ConfigurationId);
        Assert.Equal(180, result.Intent.RaDegrees);
        Assert.Equal(20, result.Intent.DecDegrees);
        Assert.Equal(30, result.Intent.PositionAngle);
        Assert.Equal(1.5, result.Intent.ExposureSeconds);
        Assert.Equal(result, f.Read());
        await Assert.ThrowsAsync<IOException>(() => f.BoundCapture.CaptureAsync(Created(binding), binding, f.Local,
            _ => Task.CompletedTask, f.Progress, default));
    }

    [Theory]
    [InlineData(ReservationKind.Existing)]
    [InlineData(ReservationKind.RecoveryRequired)]
    [InlineData(ReservationKind.Decision)]
    public void ExistingOrRecoveryBindingNeverBecomesNewCapture(ReservationKind kind)
    {
        using var f = new Fixture();
        var binding = Bound(f);
        Assert.Throws<InvalidOperationException>(() => f.BoundCapture.CreateIntent(Created(binding) with { Kind = kind }, binding, f.Local));
        Assert.Empty(Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("different-attempt")]
    [InlineData("saved")]
    [InlineData("bad-guid")]
    [InlineData("configuration")]
    [InlineData("rig")]
    public void WrongBindingCannotCreateIntent(string fault)
    {
        using var f = new Fixture();
        var binding = Bound(f);
        var reservation = Created(binding);
        binding = fault switch
        {
            "different-attempt" => binding with { Attempt = binding.Attempt with { CaptureId = Guid.NewGuid().ToString("D") } },
            "saved" => binding with { Attempt = binding.Attempt with { Evidence = new LedgerEvidence.Saved("image", 1) } },
            "bad-guid" => binding with { Attempt = binding.Attempt with { CaptureId = "not-a-guid" } },
            "configuration" => binding with { Configuration = binding.Configuration with { CameraId = "different" } },
            "rig" => binding with { Ledger = binding.Ledger with { RigId = "different" } },
            _ => throw new InvalidOperationException()
        };
        if (fault is "saved" or "bad-guid") reservation = Created(binding);
        Assert.Throws<InvalidOperationException>(() => f.BoundCapture.CreateIntent(reservation, binding, f.Local));
    }

    [Theory]
    [InlineData("gain-missing")]
    [InlineData("gain-negative")]
    [InlineData("offset-missing")]
    [InlineData("unsupported-gain")]
    [InlineData("bin")]
    [InlineData("readout")]
    [InlineData("exposure")]
    [InlineData("filter")]
    public void InvalidNativeRecipeDoesNotUseCurrentSettings(string fault)
    {
        using var f = new Fixture();
        var binding = Bound(f);
        binding = binding with
        {
            Recipe = fault switch
            {
                "gain-missing" => binding.Recipe with { Gain = null },
                "gain-negative" => binding.Recipe with { Gain = -1 },
                "offset-missing" => binding.Recipe with { Offset = null },
                "unsupported-gain" => binding.Recipe with { Gain = 11 },
                "bin" => binding.Recipe with { Binning = new(3, 3) },
                "readout" => binding.Recipe with { ReadoutMode = 2 },
                "exposure" => binding.Recipe with { ExposureMs = 60001 },
                "filter" => binding.Recipe with { FilterId = "wrong" },
                _ => throw new InvalidOperationException()
            }
        };
        Assert.Throws<InvalidDataException>(() => f.BoundCapture.CreateIntent(Created(binding), binding, f.Local));
    }

    [Theory]
    [InlineData("normal-readout")]
    [InlineData("filter")]
    [InlineData("moving")]
    [InlineData("missing-filter")]
    [InlineData("capability")]
    [InlineData("disconnected")]
    public async Task DriftInsideAuthorizationCallbackPreventsDispatch(string fault)
    {
        using var f = new Fixture();
        var binding = Bound(f);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.BoundCapture.CaptureAsync(Created(binding), binding, f.Local, _ =>
        {
            switch (fault)
            {
                case "normal-readout": f.CameraInfo.ReadoutModeForNormalImages = 0; break;
                case "filter": f.WheelInfo.SelectedFilter.Position = 3; break;
                case "moving": f.WheelInfo.IsMoving = true; break;
                case "missing-filter": f.WheelInfo.SelectedFilter = null; break;
                case "capability": f.CameraInfo.Gains = new[] { 10, 41 }; break;
                case "disconnected": f.WheelInfo.Connected = false; break;
            }
            return Task.CompletedTask;
        }, f.Progress, default));
        f.Imaging.Verify(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(),
            It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<string>()), Times.Never);
        Assert.Equal(CapturePhase.Failed, f.Read().Phase);
    }

    [Fact]
    public async Task NullPositionAngleStaysUnspecifiedInNinaMetadata()
    {
        using var f = new Fixture();
        var binding = Bound(f);
        binding = binding with { Target = binding.Target with { PositionAngleMas = null } };
        var task = f.BoundCapture.CaptureAsync(Created(binding), binding, f.Local, _ => Task.CompletedTask, f.Progress, default);
        await f.Enqueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Saved();
        Assert.Null((await task).Intent.PositionAngle);
        Assert.True(double.IsNaN(f.Metadata.Target.PositionAngle));
    }

    [Fact]
    public void UnsupportedControlsUseNativeSentinelButNeverReplaceSupportedSettings()
    {
        using var f = new Fixture();
        f.CameraInfo.CanSetGain = false;
        f.CameraInfo.CanSetOffset = false;
        var binding = Bound(f);
        binding = binding with { Recipe = binding.Recipe with { Gain = null, Offset = null } };
        var intent = f.BoundCapture.CreateIntent(Created(binding), binding, f.Local);
        Assert.Equal(-1, intent.Gain);
        Assert.Equal(-1, intent.Offset);
    }

    [Fact]
    public void LegacyJournalWithoutProgramFieldStillReads()
    {
        using var f = new Fixture();
        _ = new CaptureJournal(f.Root, f.Intent, new(f.Root, "image", "FITS"), f.Clock.GetUtcNow());
        var path = Path.Combine(f.Root, f.Intent.ProfileId.ToString("N"), $"{f.Intent.CaptureId:N}.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        Assert.False(json["intent"]!.AsObject().ContainsKey("program"));
        Assert.Equal(f.Intent, CaptureJournal.Read(path).Intent);
    }

    [Theory]
    [InlineData("missing-context")]
    [InlineData("old-version")]
    [InlineData("future-version")]
    public void BoundJournalCannotLoseItsProgramContext(string fault)
    {
        using var f = new Fixture();
        var binding = Bound(f);
        var intent = f.BoundCapture.CreateIntent(Created(binding), binding, f.Local);
        _ = new CaptureJournal(f.Root, intent, new(f.Root, "image", "FITS"), f.Clock.GetUtcNow());
        var path = Path.Combine(f.Root, intent.ProfileId.ToString("N"), $"{intent.CaptureId:N}.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        if (fault == "missing-context") json["intent"]!.AsObject().Remove("program");
        else json["schema_version"] = fault == "old-version" ? 1 : 3;
        File.WriteAllText(path, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => CaptureJournal.Read(path));
    }

    [Fact]
    public void FixedFilterRequiresNoWheelAndRetainsExactCoordinatesAndBinning()
    {
        using var f = new Fixture();
        var binding = Bound(f);
        Mock.Get(f.Profile.Object.FilterWheelSettings).SetupGet(x => x.Id).Returns("No_Device");
        f.WheelInfo.Connected = false;
        var local = f.Local with { FilterWheelDeviceId = null, Filters = [new("osc", null, null)] };
        var configuration = f.Equipment.Read(local);
        binding = binding with
        {
            Configuration = configuration,
            Ledger = binding.Ledger with { ConfigurationId = configuration.Id },
            Recipe = binding.Recipe with { FilterId = "osc", Binning = new(2, 2), ExposureMs = 1501 },
            Target = binding.Target with { IcrsRaMas = 1, IcrsDecMas = -1, PositionAngleMas = 1 }
        };
        var intent = f.BoundCapture.CreateIntent(Created(binding), binding, local);
        Assert.Equal(1.0 / 3600000, intent.RaDegrees);
        Assert.Equal(-1.0 / 3600000, intent.DecDegrees);
        Assert.Equal(1.0 / 3600000, intent.PositionAngle);
        Assert.Equal(1.501, intent.ExposureSeconds);
        Assert.Equal((short)2, intent.BinX);
        Assert.Equal((short)2, intent.BinY);
        f.BoundCapture.CheckPrepared(binding, local);
    }
}
