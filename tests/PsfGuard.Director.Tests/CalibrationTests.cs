using Moq;
using NINA.Core.Utility;
using CameraControl = PsfGuard.Director.Runtime.CameraControl;
using Newtonsoft.Json;
using NINA.Astrometry.Interfaces;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class CalibrationTests
{
    private static readonly IProgress<ApplicationStatus> Progress = new Progress<ApplicationStatus>();
    private static ExposureRecipe Recipe(ulong ms = 1000) => new("r", ms, "L", new(1, 1), 100, 30, 0, null);

    [Fact]
    public void FlatsInstructionCopiesSettingsAndRejectsParallelOrSessionPlacement()
    {
        var flats = new DirectorFlatsItem(null!);
        flats.Options.FlatsPerFilter = 17;
        flats.Options.DarkExposureSeconds = "20";
        flats.Options.DarksWhileIdle = true;
        var clone = (DirectorFlatsItem)flats.Clone();
        Assert.NotSame(flats.Options, clone.Options);
        Assert.Equal(17, clone.CaptureOptions().FlatsPerFilter);
        Assert.False(clone.CaptureOptions().DarksWhileIdle);
        Assert.True(clone.Validate());
        var parallel = new NINA.Sequencer.Container.ParallelContainer(); parallel.Add(clone);
        Assert.False(clone.Validate()); Assert.Contains(clone.Issues, text => text.Contains("sequential container"));
        parallel.Remove(clone);
        var session = new DirectorSessionContainer(); session.Add(clone);
        Assert.False(clone.Validate()); Assert.Contains(clone.Issues, text => text.Contains("before or after"));
        session.Remove(clone);
    }

    [Fact]
    public void PlanAndExtraDarksDeduplicateFiltersAndCrossTemperatureSteps()
    {
        var active = new[] { Recipe(30000), Recipe(30000) with { FilterId = "R" }, Recipe(60000) with { Gain = 200 } };
        var recipes = CalibrationRecipes.Darks(active, Recipe(), true, [30, 120], [-10, -20]);
        Assert.Equal(10, recipes.Count);
        Assert.Equal(10, recipes.Distinct().Count());
        Assert.Equal(5, recipes.Count(r => r.Temperature == -10));
        Assert.Contains(recipes, r => r.ExposureMs == 60000 && r.Gain == 200 && r.Temperature == -20);
        Assert.DoesNotContain(recipes, r => r.ExposureMs == 60000 && r.Gain == 100);
    }

    [Fact]
    public void ManualDarksUseCameraDefaultsWithoutActivePlans()
    {
        var manual = CalibrationRecipes.Darks([], Recipe(), false, [5, 10], []);
        Assert.Equal(new ulong[] { 5000, 10000 }, manual.Select(r => r.ExposureMs));
        Assert.All(manual, r => { Assert.Equal(100, r.Gain); Assert.Null(r.Temperature); });
        Assert.Empty(CalibrationRecipes.Darks([], Recipe(), true, [], []));
        Assert.Equal(2, CalibrationRecipes.Flats([Recipe(), Recipe(10000), Recipe() with { FilterId = "R" }]).Count);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("0")]
    [InlineData("7201")]
    [InlineData("thirty")]
    public void InvalidExtraLengthsPreventStart(string value)
    {
        var options = new DirectorSessionOptions { DarksWhileIdle = true, DarkExposureSeconds = value };
        Assert.Contains(options.ValidateSettings(), s => s.Contains("Dark exposure lengths"));
    }

    [Fact]
    public void CalibrationOptionsCloneAndRoundTripWithSafeDefaults()
    {
        var defaults = new DirectorSessionOptions();
        Assert.False(defaults.ScheduledFlats || defaults.DarksWhileIdle || defaults.DarksWhileWeatherHeld || defaults.ClosePanelAtEnd);
        var value = new DirectorSessionOptions
        {
            ScheduledFlats = true,
            FlatsTiming = DirectorDuskTiming.After,
            FlatsDuskMinutes = 15,
            DarksWhileIdle = true,
            DarksWhileWeatherHeld = true,
            DarkExposureSeconds = "30, 60, 30",
            DarkTemperatures = "-10; -20",
            DarksPerSetting = 4
        };
        var restored = JsonConvert.DeserializeObject<DirectorSessionOptions>(JsonConvert.SerializeObject(value.Clone()))!;
        Assert.Equal(15, restored.FlatsDuskOffsetMinutes);
        Assert.Equal(new double[] { 30, 60 }, DirectorSessionOptions.ParseCalibrationList(restored.DarkExposureSeconds, .001, 7200, "lengths"));
        Assert.Equal("-10; -20", restored.DarkTemperatures);
        Assert.True(restored.DarksFromPlans && restored.DarksWhileIdle && restored.DarksWhileWeatherHeld);
        Assert.Equal(4, restored.DarksPerSetting);
    }

    [Fact]
    public async Task PanelLightTurnsOffBeforeOpeningAndMissingPanelDoesNotBlockLights()
    {
        var f = new Fixture();
        f.PanelInfo.LightOn = true;
        await f.Panel.OpenForLightsAsync(() => { }, Progress, default);
        Assert.Equal(new[] { "off", "open" }, f.PanelActions);
        f.Panel.RequireLightsReady();
        f.PanelInfo.Connected = false;
        Assert.Throws<InvalidOperationException>(f.Panel.RequireLightsReady);
        var absent = new NinaFlatPanel(f.Profiles.Object, null, TimeSpan.FromSeconds(1));
        await absent.OpenForLightsAsync(() => { }, Progress, default);
        absent.RequireLightsReady();
        Assert.Throws<InvalidOperationException>(absent.RequireDarkReady);
    }

    [Fact]
    public async Task CoverMustConfirmPositionAndCannotBeReusedAfterTimeout()
    {
        var f = new Fixture();
        f.Flat.Setup(p => p.OpenCover(Progress, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var panel = new NinaFlatPanel(f.Profiles.Object, f.Flat.Object, TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAsync<TimeoutException>(() => panel.OpenForLightsAsync(() => { }, Progress, default));
        f.PanelInfo.CoverState = CoverState.Open;
        Assert.Throws<InvalidOperationException>(panel.RequireLightsReady);
    }

    [Fact]
    public void FlatCoverageRequiresClosedCoverAndPanelLight()
    {
        var f = new Fixture();
        Assert.Throws<InvalidOperationException>(f.Panel.RequireFlatReady);
        f.PanelInfo.LightOn = true;
        f.Panel.RequireFlatReady();
        f.PanelInfo.CoverState = CoverState.Open;
        Assert.Throws<InvalidOperationException>(f.Panel.RequireFlatReady);
        f.PanelInfo.Connected = false;
        Assert.Throws<InvalidOperationException>(f.Panel.RequireFlatReady);
    }

    [Fact]
    public async Task ChangedPanelSelectionRefusesMotion()
    {
        var f = new Fixture();
        f.Profile.SetupGet(p => p.FlatDeviceSettings.Id).Returns("another-cover");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Panel.OpenForLightsAsync(() => { }, Progress, default));
        Assert.Empty(f.PanelActions);
    }

    [Fact]
    public async Task SavesRequireMatchingReceiptAndNeverKeepScienceIdentity()
    {
        var f = new Fixture();
        var data = f.NewImage("DARK");
        data.MetaData.GenericHeaders.Add(new StringMetaDataHeader("PGCAPID", "science", ""));
        var count = 0;
        var sink = new NinaCalibrationSave(f.Saves.Object, f.Profile.Object, "DARK", TimeSpan.FromSeconds(1), _ => count++);
        f.AutomaticReceipt = false;
        var task = sink.NativeMediator().Enqueue(data, Task.FromResult(Mock.Of<IRenderedImage>()), Progress, default);
        await f.Enqueued.Task;
        f.Receipt(new ImageMetaData());
        Assert.False(task.IsCompleted);
        f.Receipt(data.MetaData);
        await task;
        f.Receipt(data.MetaData);
        Assert.Equal(1, count);
        Assert.DoesNotContain(data.MetaData.GenericHeaders, h => h.Key == "PGCAPID");
        Assert.Contains(data.MetaData.GenericHeaders, h => h.Key == "PGCALID");
        f.Saves.VerifyRemove(s => s.ImageSaved -= It.IsAny<EventHandler<ImageSavedEventArgs>>(), Times.Once);
    }

    [Fact]
    public async Task MissingSaveReceiptDoesNotCountCalibration()
    {
        var f = new Fixture { AutomaticReceipt = false };
        var counted = false;
        var sink = new NinaCalibrationSave(f.Saves.Object, f.Profile.Object, "DARK", TimeSpan.FromMilliseconds(50), _ => counted = true);
        await Assert.ThrowsAsync<TimeoutException>(() => sink.SaveAsync(f.NewImage("DARK"), Task.FromResult(Mock.Of<IRenderedImage>()), Progress, default));
        Assert.False(counted);
    }

    [Fact]
    public async Task DarkLoopCombinesPlansAndExtrasAndRestoresCameraBeforeOpening()
    {
        var f = new Fixture();
        f.Container.Options.DarksWhileIdle = true;
        f.Container.Options.DarksPerSetting = 1;
        f.Container.Options.DarkExposureSeconds = "2";
        f.Container.Options.DarkTemperatures = "-10, -20";
        var session = f.Session();
        session.UpdateRecipes(f.Program, f.Configuration);
        for (var i = 0; i < 4; i++) Assert.True(await session.RunOneAsync(false, _ => Task.FromResult(true), () => { }, default));
        Assert.False(await session.RunOneAsync(false, _ => Task.FromResult(true), () => { }, default));
        Assert.Equal(new[] { 1d, 2, 1, 2 }, f.Exposures.Select(c => c.ExposureTime));
        Assert.Equal(new[] { -10d, -10, -20, -20 }, f.Temperatures);
        Assert.All(f.Exposures, c => { Assert.Equal("DARK", c.ImageType); Assert.Equal(100, c.Gain); Assert.Equal(30, c.Offset); });
        await session.PrepareLightsAsync(() => { }, default);
        Assert.Equal(-5, f.CameraInfo.Temperature);
        Assert.Equal((short)1, f.CameraInfo.ReadoutModeForNormalImages);
        Assert.Equal(CoverState.Open, f.PanelInfo.CoverState);
        Assert.False(f.PanelInfo.LightOn);
    }

    [Fact]
    public async Task NewWorkCancelsLongDarkWithoutCountingItAndAllowsLightPreparation()
    {
        var f = new Fixture();
        f.Container.Options.DarksWhileIdle = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Imaging.Setup(i => i.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), Progress, "Director calibration"))
            .Returns(async (CaptureSequence _, CancellationToken ct, IProgress<ApplicationStatus> _, string _) =>
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null!; });
        var session = f.Session();
        session.UpdateRecipes(f.Program, f.Configuration);
        Assert.True(await session.RunOneAsync(false, _ => Task.FromResult(!entered.Task.IsCompleted), () => { }, default));
        Assert.False(f.Enqueued.Task.IsCompleted);
        await session.PrepareLightsAsync(() => { }, default);
        Assert.Equal(CoverState.Open, f.PanelInfo.CoverState);
    }

    [Fact]
    public async Task DarkCompletionAndMonitorSerializeConstraintRefresh()
    {
        var f = new Fixture();
        f.Container.Options.DarksWhileIdle = true;
        var exposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloaded = new TaskCompletionSource<IExposureData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exposure = new Mock<IExposureData>();
        exposure.Setup(e => e.ToImageData(Progress, It.IsAny<CancellationToken>())).ReturnsAsync(f.NewImage("DARK"));
        f.Imaging.Setup(i => i.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), Progress, "Director calibration"))
            .Returns(() => { exposing.TrySetResult(); return downloaded.Task; });
        var active = 0;
        var completedDuringCheck = 0;
        var checksAfterExposure = 0;
        void Check()
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            try
            {
                if (!exposing.Task.IsCompleted) return;
                Interlocked.Increment(ref checksAfterExposure);
                if (Interlocked.Exchange(ref completedDuringCheck, 1) == 0)
                {
                    // Finish the image while the monitor is refreshing constraints.
                    // Its continuation must wait before refreshing them again.
                    downloaded.SetResult(exposure.Object);
                    Thread.Sleep(150);
                }
            }
            finally { Interlocked.Decrement(ref active); }
        }
        var session = f.Session();
        session.UpdateRecipes(f.Program, f.Configuration);
        Assert.True(await session.RunOneAsync(false, _ => Task.FromResult(true), Check, default));
        Assert.True(checksAfterExposure >= 2);
        Assert.True(f.Enqueued.Task.IsCompleted);
    }

    [Fact]
    public async Task WeatherDarksRequireExplicitOptionAndClosedCover()
    {
        var f = new Fixture();
        f.Container.Options.DarksWhileIdle = true;
        var session = f.Session();
        session.UpdateRecipes(f.Program, f.Configuration);
        Assert.False(await session.RunOneAsync(true, _ => Task.FromResult(true), () => { }, default));
        f.Container.Options.DarksWhileWeatherHeld = true;
        f.PanelInfo.CoverState = CoverState.Open;
        session = f.Session(); session.UpdateRecipes(f.Program, f.Configuration);
        Assert.True(await session.RunOneAsync(true, _ => Task.FromResult(true), () => { }, default));
        Assert.Equal("close", Assert.Single(f.PanelActions));
        f.Panel.RequireDarkReady();
    }

    private sealed class Fixture
    {
        internal Mock<IProfile> Profile = new() { DefaultValue = DefaultValue.Mock };
        internal Mock<IProfileService> Profiles = new();
        internal Mock<IFlatDeviceMediator> Flat = new();
        internal Mock<ICameraMediator> Camera = new();
        internal Mock<IImagingMediator> Imaging = new();
        internal Mock<IImageSaveMediator> Saves = new();
        internal FlatDeviceInfo PanelInfo = new() { Connected = true, DeviceId = "panel", SupportsOpenClose = true, SupportsOnOff = true, CoverState = CoverState.Closed };
        internal CameraInfo CameraInfo = new()
        {
            Connected = true,
            DeviceId = "camera",
            BinX = 1,
            BinY = 1,
            DefaultGain = 100,
            DefaultOffset = 30,
            CanSetTemperature = true,
            CoolerOn = true,
            TemperatureSetPoint = -5,
            Temperature = -5,
            ReadoutModeForNormalImages = 1
        };
        internal List<string> PanelActions = [];
        internal List<CaptureSequence> Exposures = [];
        internal List<double> Temperatures = [];
        internal DirectorSessionContainer Container = new();
        internal NinaFlatPanel Panel;
        internal bool AutomaticReceipt = true;
        internal TaskCompletionSource Enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DirectorConfiguration Configuration = new("rig", "configuration", "camera", null, [new("L", null)], [new(1, 1)], [0, 1],
            new CameraControl.Range(0, 200), new CameraControl.Range(0, 100), 1, 7200000, false, 0);
        internal DirectorProgram Program => new(1, PlannerTests.Request().Assignment, Configuration, [], [Recipe()], []);

        internal Fixture()
        {
            Profile.SetupGet(p => p.FlatDeviceSettings.Id).Returns("panel");
            Profile.SetupGet(p => p.CameraSettings.Id).Returns("camera");
            Profile.SetupGet(p => p.ImageFileSettings.FilePath).Returns(Path.GetTempPath());
            Profile.Setup(p => p.ImageFileSettings.GetFilePattern(It.IsAny<string>())).Returns("$$IMAGETYPE$$");
            Profiles.SetupGet(p => p.ActiveProfile).Returns(Profile.Object);
            Flat.Setup(p => p.GetInfo()).Returns(PanelInfo);
            Flat.Setup(p => p.ToggleLight(false, Progress, It.IsAny<CancellationToken>())).Returns(() => { PanelActions.Add("off"); PanelInfo.LightOn = false; return Task.CompletedTask; });
            Flat.Setup(p => p.OpenCover(Progress, It.IsAny<CancellationToken>())).Returns(() => { PanelActions.Add("open"); PanelInfo.CoverState = CoverState.Open; return Task.CompletedTask; });
            Flat.Setup(p => p.CloseCover(Progress, It.IsAny<CancellationToken>())).Returns(() => { PanelActions.Add("close"); PanelInfo.CoverState = CoverState.Closed; return Task.CompletedTask; });
            Panel = new(Profiles.Object, Flat.Object, TimeSpan.FromSeconds(1));
            Camera.Setup(c => c.GetInfo()).Returns(CameraInfo);
            Camera.Setup(c => c.SetReadoutModeForNormalImages(It.IsAny<short>())).Callback<short>(mode => CameraInfo.ReadoutModeForNormalImages = mode);
            Camera.Setup(c => c.CoolCamera(It.IsAny<double>(), TimeSpan.Zero, Progress, It.IsAny<CancellationToken>()))
                .Returns((double t, TimeSpan _, IProgress<ApplicationStatus> _, CancellationToken _) =>
                { CameraInfo.CoolerOn = true; CameraInfo.Temperature = CameraInfo.TemperatureSetPoint = t; return Task.FromResult(true); });
            Imaging.Setup(i => i.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), Progress, "Director calibration"))
                .Returns((CaptureSequence sequence, CancellationToken _, IProgress<ApplicationStatus> _, string _) =>
                {
                    Assert.Equal(CoverState.Closed, PanelInfo.CoverState); Assert.False(PanelInfo.LightOn);
                    Exposures.Add(sequence); Temperatures.Add(CameraInfo.Temperature);
                    var exposure = new Mock<IExposureData>();
                    exposure.Setup(e => e.ToImageData(Progress, It.IsAny<CancellationToken>())).ReturnsAsync(NewImage(sequence.ImageType));
                    return Task.FromResult(exposure.Object);
                });
            Imaging.Setup(i => i.PrepareImage(It.IsAny<IImageData>(), It.IsAny<PrepareImageParameters>(), It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<IRenderedImage>());
            Saves.Setup(s => s.Enqueue(It.IsAny<IImageData>(), It.IsAny<Task<IRenderedImage>>(), Progress, It.IsAny<CancellationToken>()))
                .Returns((IImageData data, Task<IRenderedImage> _, IProgress<ApplicationStatus> _, CancellationToken _) =>
                { Enqueued.TrySetResult(); if (AutomaticReceipt) Receipt(data.MetaData); return Task.CompletedTask; });
        }

        internal IImageData NewImage(string kind)
        {
            var metadata = new ImageMetaData(); metadata.Image.ImageType = kind;
            return Mock.Of<IImageData>(i => i.MetaData == metadata);
        }
        internal void Receipt(ImageMetaData metadata) => Saves.Raise(s => s.ImageSaved += null!, Saves.Object,
            new ImageSavedEventArgs { MetaData = metadata, PathToImage = new Uri(Path.Combine(Path.GetTempPath(), "calibration.fits")) });
        internal NinaCalibrationSession Session() => new(Profiles.Object, Camera.Object,
            Mock.Of<ITelescopeMediator>(t => t.GetInfo() == new TelescopeInfo()), Mock.Of<IFilterWheelMediator>(), Imaging.Object,
            Saves.Object, Mock.Of<IImageHistoryVM>(), Flat.Object, Mock.Of<INighttimeCalculator>(), Container, Progress);
    }
}
