using System.IO;
using NINA.Astrometry.Interfaces;
using System.Collections.Concurrent;
using NINA.Core.Utility;
using NINA.Sequencer.Utility;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem.FlatDevice;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

// Runs only while the session owner and caller confirm no light exposure is
// eligible. All camera, cover and save operations remain native NINA operations.
internal sealed class NinaCalibrationSession
{
    private readonly IProfileService profiles;
    private readonly IProfile profile;
    private readonly ICameraMediator camera;
    private readonly ITelescopeMediator telescope;
    private readonly IFilterWheelMediator filters;
    private readonly IImagingMediator imaging;
    private readonly IImageSaveMediator saves;
    private readonly IImageHistoryVM history;
    private readonly IFlatDeviceMediator? flatDevice;
    private readonly DirectorSessionOptions options;
    private readonly DirectorSessionContainer container;
    private readonly IProgress<ApplicationStatus> progress;
    private readonly ConcurrentDictionary<DarkRecipe, int> darkCounts = [];
    private readonly ConcurrentDictionary<string, int> flatCounts = [];
    private readonly DateTimeOffset? flatsAt;
    private readonly bool immediateFlats;
    private DirectorFlatCoverage? coverage;
    private Func<double?> rotation = () => null;
    private readonly string observingDate = DateTime.Now.AddHours(-12).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    private IReadOnlyList<ExposureRecipe> active = [];
    private ExposureRecipe? fallback;
    private DirectorConfiguration? configuration;
    private (bool On, double Temperature)? originalCooling;
    private short? originalReadout;
    private bool failed, interruptible, unfinished;
    private string? lastNotice;
    internal NinaFlatPanel Panel { get; }
    internal bool ClosePanelAtEnd => options.ClosePanelAtEnd;
    internal TimeSpan CleanupTimeout => TimeSpan.FromSeconds(options.HookTimeoutSeconds);
    internal bool CameraChanged => originalCooling is not null || originalReadout is not null;
    internal bool Enabled => options.ScheduledFlats || options.DarksWhileIdle || options.DarksWhileWeatherHeld;
    internal bool FlatsDue => options.ScheduledFlats && (immediateFlats || flatsAt is { } at && DateTimeOffset.UtcNow >= at)
        && CalibrationRecipes.Flats(active).Any(r => FlatCount(r) < options.FlatsPerFilter);

    internal NinaCalibrationSession(IProfileService profiles, ICameraMediator camera, ITelescopeMediator telescope,
        IFilterWheelMediator filters, IImagingMediator imaging, IImageSaveMediator saves, IImageHistoryVM history,
        IFlatDeviceMediator? flatDevice, INighttimeCalculator nighttime, DirectorSessionContainer container, IProgress<ApplicationStatus> progress, bool immediateFlats = false)
    {
        this.profiles = profiles; profile = profiles.ActiveProfile; this.camera = camera; this.telescope = telescope;
        this.filters = filters; this.imaging = imaging; this.saves = saves; this.history = history; this.flatDevice = flatDevice;
        this.container = container; options = container.Options.Clone(); this.progress = progress;
        this.immediateFlats = immediateFlats;
        Panel = new(profiles, flatDevice, TimeSpan.FromSeconds(Math.Min(options.HookTimeoutSeconds, 60)));
        if (options.ScheduledFlats && nighttime.Calculate()?.TwilightRiseAndSet?.Set is { } dusk)
            flatsAt = new DateTimeOffset(dusk).ToUniversalTime().AddMinutes(options.FlatsDuskOffsetMinutes);
        if (options.ScheduledFlats && flatsAt is null && !immediateFlats)
            Notice("Flats cannot be scheduled: N.I.N.A. has no astronomical dusk for this night and site");
    }

    internal void UpdateRecipes(DirectorProgram? program, DirectorConfiguration current)
        => SetRecipes(program?.Recipes.ToArray() ?? [], current);

    internal void AttachCoverage(DirectorFlatCoverage value, Func<double?> readRotation)
    { coverage = value; rotation = readRotation; }

    internal void SetRecipes(IReadOnlyList<ExposureRecipe> recipes, DirectorConfiguration current)
    {
        configuration = current;
        active = recipes;
        var info = camera.GetInfo();
        fallback ??= new("manual-dark", 1000, current.Filters[0].Id, new(info.BinX, info.BinY),
            current.Gain is CameraControl.Unsupported ? null : info.DefaultGain,
            current.Offset is CameraControl.Unsupported ? null : info.DefaultOffset, info.ReadoutModeForNormalImages, null);
    }

    private void Check(bool cleanup = false)
    {
        if (failed && !cleanup) throw new InvalidOperationException("Calibration did not finish cleanly. Check the camera and flat panel before restarting Director.");
        if (!ReferenceEquals(profile, profiles.ActiveProfile) || !camera.GetInfo().Connected
            || camera.GetInfo().DeviceId != profile.CameraSettings.Id)
            throw new InvalidOperationException("The calibration camera or N.I.N.A. profile changed.");
        if (!cleanup && System.Text.Json.JsonSerializer.Serialize(options) != System.Text.Json.JsonSerializer.Serialize(container.Options))
            throw new InvalidOperationException("Director settings changed during calibration. Start a new session.");
    }

    private void CheckStopped()
    {
        Check();
        var mount = telescope.GetInfo();
        if (mount.Slewing || mount.TrackingEnabled)
            throw new InvalidOperationException("Calibration requires mount motion and tracking to be stopped.");
    }

    private void Notice(string message)
    {
        container.UpdateDisplay(d => d with { Operation = message });
        progress.Report(new ApplicationStatus { Status = message });
        if (lastNotice != message) container.RecordAction("", "Calibration", message);
        lastNotice = message;
    }

    // One saved image per call lets the caller poll plans and weather between
    // images. During a long exposure it can cancel promptly when work is ready.
    internal async Task<bool> RunOneAsync(bool weatherHold, Func<CancellationToken, Task<bool>> stillWaiting,
        Action checkContext, CancellationToken token)
    {
        if (!Enabled || fallback is null || configuration is null) return false;
        var flat = FlatsDue ? CalibrationRecipes.Flats(active).First(r => FlatCount(r) < options.FlatsPerFilter) : null;
        var darksEnabled = weatherHold ? options.DarksWhileWeatherHeld : options.DarksWhileIdle;
        var dark = darksEnabled && flat is null ? CalibrationRecipes.Darks(active, fallback, options.DarksFromPlans,
            DirectorSessionOptions.ParseCalibrationList(options.DarkExposureSeconds, 0.001, 7200, "Dark exposure lengths"),
            DirectorSessionOptions.ParseCalibrationList(options.DarkTemperatures, -80, 40, "Dark temperatures"))
            .FirstOrDefault(r => darkCounts.GetValueOrDefault(r) < options.DarksPerSetting) : null;
        if (flat is null && dark is null) return false;
        if (!Panel.CanClose) { Notice("Calibration is waiting for a connected motorized flat-panel cover"); return false; }
        if (flat is not null && flatDevice!.GetInfo().SupportsOnOff != true)
        { Notice("Flats require a connected panel with a controllable light"); return false; }
        checkContext(); CheckStopped();
        if (!await stillWaiting(token)) return false;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(TimeSpan.FromSeconds(options.HookTimeoutSeconds + options.SaveTimeoutSeconds
            + (flat is not null ? options.FlatMaximumSeconds : dark!.ExposureMs / 1000d)));
        var interrupted = false;
        interruptible = false;
        Exception? primaryFailure = null;
        var work = flat is not null ? FlatAsync(flat, checkContext, stop.Token) : DarkAsync(dark!, checkContext, stop.Token);
        try
        {
            while (!work.IsCompleted)
            {
                if (await Task.WhenAny(work, Task.Delay(500, token)) == work) break;
                stop.Token.ThrowIfCancellationRequested(); checkContext(); CheckStopped();
                if (camera.GetInfo().IsExposing)
                {
                    if (flat is not null) Panel.RequireFlatReady();
                    else Panel.RequireDarkReady();
                }
                if (!interrupted && !await stillWaiting(token)) interrupted = true;
                // Finish cover movement before interrupting a calibration image.
                if (interrupted && interruptible) { stop.Cancel(); break; }
            }
            try { await (interrupted ? work.WaitAsync(TimeSpan.FromSeconds(10), token) : work.WaitAsync(token)); }
            catch (OperationCanceledException) when (interrupted && !token.IsCancellationRequested)
            { Notice("Stopping calibration to check the observing plans and conditions"); }
            if (camera.GetInfo().IsExposing) throw new IOException("The camera did not confirm that calibration stopped.");
            return true;
        }
        catch (Exception error)
        {
            primaryFailure = error;
            stop.Cancel();
            try { await work.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            unfinished = !work.IsCompleted;
            _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            failed = true;
            error.Data["DirectorPolicyStop"] = true;
            throw;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await Panel.LightOffAsync(() => Check(cleanup: true), progress, cleanup.Token, cleanup: true); }
            catch (Exception error)
            {
                if (primaryFailure is null) { error.Data["DirectorPolicyStop"] = true; throw; }
                Logger.Error(error); Notice($"Could not turn off the flat-panel light: {error.Message}");
            }
        }
    }

    private void ValidateRecipe(ulong milliseconds, CameraBinning binning, int? gain, int? offset, short readout)
    {
        var c = configuration!;
        if (milliseconds < c.ExposureMinMs || milliseconds > c.ExposureMaxMs || !c.BinningModes.Contains(binning)
            || !c.ReadoutModes.Contains(readout) || !NinaProgramCapture.Supported(c.Gain, gain) || !NinaProgramCapture.Supported(c.Offset, offset))
            throw new InvalidOperationException("The calibration settings are not supported by the connected camera.");
    }

    private async Task SetReadoutAsync(short mode, Action check, CancellationToken token)
    {
        check(); CheckStopped();
        originalReadout ??= camera.GetInfo().ReadoutModeForNormalImages;
        camera.SetReadoutModeForNormalImages(mode);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (camera.GetInfo().ReadoutModeForNormalImages != mode) { check(); CheckStopped(); await Task.Delay(100, deadline.Token); }
    }

    private async Task TemperatureAsync(double target, Action check, CancellationToken token)
    {
        check(); CheckStopped();
        var info = camera.GetInfo();
        if (!info.CanSetTemperature) throw new InvalidOperationException("Temperature steps require a camera with a controllable cooler.");
        if (info.CoolerOn && !double.IsFinite(info.TemperatureSetPoint))
            throw new InvalidOperationException("The camera has not reported its imaging temperature setting.");
        originalCooling ??= (info.CoolerOn, info.TemperatureSetPoint);
        if (info.CoolerOn && double.IsFinite(info.Temperature) && Math.Abs(info.Temperature - target) <= 0.5
            && Math.Abs(info.TemperatureSetPoint - target) < 0.01) return;
        Notice($"Cooling for dark frames: {target:g} °C");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.HookTimeoutSeconds));
        if (!await camera.CoolCamera(target, TimeSpan.Zero, progress, deadline.Token).WaitAsync(deadline.Token))
            throw new IOException($"The camera could not reach {target:g} °C for dark frames.");
        while (!camera.GetInfo().CoolerOn || !double.IsFinite(camera.GetInfo().Temperature)
            || Math.Abs(camera.GetInfo().Temperature - target) > 0.5)
        { check(); CheckStopped(); await Task.Delay(250, deadline.Token); }
    }

    private async Task DarkAsync(DarkRecipe recipe, Action check, CancellationToken token)
    {
        ValidateRecipe(recipe.ExposureMs, recipe.Binning, recipe.Gain, recipe.Offset, recipe.ReadoutMode);
        await Panel.CloseAsync(check, progress, token); Panel.RequireDarkReady();
        interruptible = true;
        if (recipe.Temperature is { } temperature) await TemperatureAsync(temperature, check, token);
        await SetReadoutAsync(recipe.ReadoutMode, check, token);
        Notice($"Dark {darkCounts.GetValueOrDefault(recipe) + 1}/{options.DarksPerSetting}: {recipe.ExposureMs / 1000d:g} s"
            + (recipe.Temperature is { } t ? $", {t:g} °C" : ", current camera temperature"));
        await CaptureAsync(recipe.ExposureMs / 1000d, recipe.Binning, recipe.Gain, recipe.Offset, CaptureSequence.ImageTypes.DARK,
            () => { check(); CheckStopped(); Panel.RequireDarkReady(); },
            _ => darkCounts[recipe] = darkCounts.GetValueOrDefault(recipe) + 1, token);
    }

    private double? Rotation() => rotation() is { } angle && double.IsFinite(angle) ? Math.Round(angle, 1) : null;
    private string FlatKey(ExposureRecipe r) => DirectorFlatCoverage.Hash(new
    {
        observingDate,
        camera = configuration?.CameraId,
        wheel = configuration?.FilterWheelId,
        focalLength = profile.TelescopeSettings.FocalLength,
        r.FilterId,
        r.Binning,
        r.Gain,
        r.Offset,
        r.ReadoutMode,
        rotation = Rotation()
    });
    private int FlatCount(ExposureRecipe r) => coverage?.Count(FlatKey(r), options.FlatsPerFilter) ?? flatCounts.GetValueOrDefault(FlatKey(r));

    private async Task FlatAsync(ExposureRecipe recipe, Action check, CancellationToken token)
    {
        var key = FlatKey(recipe);
        ValidateRecipe((ulong)Math.Round(options.FlatMinimumSeconds * 1000), recipe.Binning, recipe.Gain, recipe.Offset, recipe.ReadoutMode);
        ValidateRecipe((ulong)Math.Round(options.FlatMaximumSeconds * 1000), recipe.Binning, recipe.Gain, recipe.Offset, recipe.ReadoutMode);
        await RestoreCameraAsync(check, token);
        await Panel.CloseAsync(check, progress, token);
        await SetReadoutAsync(recipe.ReadoutMode, check, token);
        interruptible = true;
        var filterPosition = configuration!.Filters.Single(f => f.Id == recipe.FilterId).Position;
        var filterName = profile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(f => f.Position == filterPosition)?.Name ?? recipe.FilterId;
        Notice($"Flat {FlatCount(recipe) + 1}/{options.FlatsPerFilter}: {filterName}");
        var sink = new NinaCalibrationSave(saves, profile, CaptureSequence.ImageTypes.FLAT, TimeSpan.FromSeconds(options.SaveTimeoutSeconds),
            path =>
            {
                Panel.RequireFlatReady();
                if (key != FlatKey(recipe)) throw new IOException("The camera angle or flat settings changed during the exposure.");
                coverage?.Saved(key, options.FlatsPerFilter, recipe, filterName, Rotation(), path);
                flatCounts[key] = flatCounts.GetValueOrDefault(key) + 1;
            });
        var native = NinaSequencerDispatcher.Invoke(() =>
        {
            var item = new AutoExposureFlat(profiles, camera, imaging, sink.NativeMediator(), history, filters, flatDevice!)
            {
                MinExposure = options.FlatMinimumSeconds,
                MaxExposure = options.FlatMaximumSeconds,
                KeepPanelClosed = true,
                HistogramTargetPercentage = 0.5,
                HistogramTolerancePercentage = 0.1,
                Attempts = 1,
                ErrorBehavior = InstructionErrorBehavior.AbortOnError
            };
            item.GetIterations().Iterations = 1;
            var exposure = item.GetExposureItem();
            exposure.Gain = recipe.Gain ?? -1; exposure.Offset = recipe.Offset ?? -1;
            exposure.Binning = new(recipe.Binning.X, recipe.Binning.Y);
            var position = configuration!.Filters.Single(f => f.Id == recipe.FilterId).Position;
            item.GetSwitchFilterItem().Filter = position is null ? null : profile.FilterWheelSettings.FilterWheelFilters.Single(f => f.Position == position);
            var panelInfo = flatDevice!.GetInfo();
            item.GetSetBrightnessItem().Brightness = panelInfo.MinBrightness
                + (int)Math.Round((panelInfo.MaxBrightness - panelInfo.MinBrightness) * options.FlatBrightness / 100d);
            return item;
        });
        check(); CheckStopped();
        var count = FlatCount(recipe);
        await native.Execute(progress, token);
        if (FlatCount(recipe) != count + 1) throw new IOException("N.I.N.A. did not confirm saving the flat frame.");
    }

    private async Task CaptureAsync(double seconds, CameraBinning bin, int? gain, int? offset, string kind,
        Action check, Action<string> saved, CancellationToken token)
    {
        check(); token.ThrowIfCancellationRequested();
        var exposure = await imaging.CaptureImage(new CaptureSequence
        { ExposureTime = seconds, Binning = new BinningMode(bin.X, bin.Y), Gain = gain ?? -1, Offset = offset ?? -1, ImageType = kind, TotalExposureCount = 1 },
            token, progress, "Director calibration") ?? throw new IOException("N.I.N.A. returned no calibration exposure.");
        check();
        var image = await exposure.ToImageData(progress, token);
        var prepared = imaging.PrepareImage(image, new PrepareImageParameters(false, false), token);
        await new NinaCalibrationSave(saves, profile, kind, TimeSpan.FromSeconds(options.SaveTimeoutSeconds), saved)
            .SaveAsync(image, prepared, progress, token);
    }

    internal async Task RestoreCameraAsync(Action check, CancellationToken token)
    {
        if (!CameraChanged) return;
        check(); Check(cleanup: true);
        if (unfinished || camera.GetInfo().IsExposing) throw new IOException("The camera is still exposing; imaging settings cannot be restored yet.");
        if (originalReadout is { } readout)
        {
            camera.SetReadoutModeForNormalImages(readout);
            using var setting = CancellationTokenSource.CreateLinkedTokenSource(token);
            setting.CancelAfter(TimeSpan.FromSeconds(10));
            while (camera.GetInfo().ReadoutModeForNormalImages != readout)
            { check(); Check(cleanup: true); await Task.Delay(100, setting.Token); }
            originalReadout = null;
        }
        if (originalCooling is { } cooling)
        {
            Notice("Restoring the camera temperature for imaging");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.HookTimeoutSeconds));
            var restored = cooling.On
                ? await camera.CoolCamera(cooling.Temperature, TimeSpan.Zero, progress, deadline.Token).WaitAsync(deadline.Token)
                : await camera.WarmCamera(TimeSpan.Zero, progress, deadline.Token).WaitAsync(deadline.Token);
            if (!restored) throw new IOException("The camera could not restore its imaging temperature.");
            while (camera.GetInfo().CoolerOn != cooling.On || cooling.On &&
                (!double.IsFinite(camera.GetInfo().Temperature) || Math.Abs(camera.GetInfo().Temperature - cooling.Temperature) > 0.5
                 || Math.Abs(camera.GetInfo().TemperatureSetPoint - cooling.Temperature) > 0.01))
            { check(); Check(cleanup: true); await Task.Delay(250, deadline.Token); }
            originalCooling = null;
        }
    }

    internal async Task PrepareLightsAsync(Action check, CancellationToken token)
    {
        await RestoreCameraAsync(check, token);
        await Panel.OpenForLightsAsync(check, progress, token);
    }
}
