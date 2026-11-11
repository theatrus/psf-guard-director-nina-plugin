using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NINA.Core.Model;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

namespace PsfGuard.Director.Plugin.Acquisition;

// Use NINA's queue and per-image receipt. Calibration files have their own ID;
// they never enter the science ledger or count toward a target's image goal.
internal sealed class NinaCalibrationSave(IImageSaveMediator saves, IProfile profile, string imageType, TimeSpan timeout,
    Action<string> saved)
{
    internal async Task SaveAsync(IImageData image, Task<IRenderedImage> prepared, IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        if (image.MetaData.Image.ImageType != imageType) throw new InvalidDataException("Calibration image type changed before saving.");
        var id = Guid.NewGuid().ToString("D");
        image.MetaData.GenericHeaders.RemoveAll(h => h.Key is "PGCAPID" or "PGCALID");
        image.MetaData.GenericHeaders.Add(new StringMetaDataHeader("PGCALID", id, "Director calibration image ID"));
        image.MetaData.Target.Name = "Director calibration";
        var receipt = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Matches(ImageMetaData? data) => data?.Image.Id == image.MetaData.Image.Id
            && data.GenericHeaders.OfType<StringMetaDataHeader>().Any(h => h.Key == "PGCALID" && h.Value == id);
        void OnSaved(object? sender, ImageSavedEventArgs args)
        {
            if (!Matches(args.MetaData)) return;
            if (args.PathToImage is { IsAbsoluteUri: true, IsFile: true } path) receipt.TrySetResult(path.LocalPath);
            else receipt.TrySetException(new IOException("N.I.N.A. did not report a local path for the calibration image."));
        }
        using var failure = NinaSaveFailure.Observe(saves, args =>
        {
            if (Matches(NinaCompatibility.Read(args, "MetaData") as ImageMetaData))
                receipt.TrySetException(NinaCompatibility.Read(args, "Exception") as Exception ?? new IOException("Calibration image save failed."));
            return Task.CompletedTask;
        });
        saves.ImageSaved += OnSaved;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            var settings = ProfileScopedImageData.Snapshot(profile, imageType);
            var writer = new ProfileScopedImageData(image, settings, error => receipt.TrySetException(error));
            var enqueue = saves.Enqueue(writer, prepared, progress, deadline.Token);
            try
            {
                await enqueue.WaitAsync(deadline.Token);
                saved(await receipt.Task.WaitAsync(deadline.Token));
            }
            catch (OperationCanceledException) when (receipt.Task.IsCompletedSuccessfully) { saved(await receipt.Task); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("N.I.N.A. did not confirm saving the calibration image. Director will not count it as saved."); }
            finally
            {
                _ = enqueue.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                _ = receipt.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        finally { saves.ImageSaved -= OnSaved; }
    }

    internal IImageSaveMediator NativeMediator()
    {
        var proxy = DispatchProxy.Create<IImageSaveMediator, CalibrationSaveProxy>();
        ((CalibrationSaveProxy)(object)proxy).Configure(this, saves);
        return proxy;
    }

    // Generate the proxy against the loaded NINA interface: 3.3 adds a save
    // failure event that is not present in the pinned 3.2 reference assembly.
    public class CalibrationSaveProxy : DispatchProxy
    {
        private NinaCalibrationSave owner = null!;
        private IImageSaveMediator native = null!;
        internal void Configure(NinaCalibrationSave value, IImageSaveMediator mediator) { owner = value; native = mediator; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(method);
            if (method.Name == nameof(IImageSaveMediator.Enqueue))
                return owner.SaveAsync((IImageData)args![0]!, (Task<IRenderedImage>)args[1]!, (IProgress<ApplicationStatus>)args[2]!, (CancellationToken)args[3]!);
            try { return method.Invoke(native, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
}
