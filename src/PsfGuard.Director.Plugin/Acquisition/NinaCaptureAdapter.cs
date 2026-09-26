using System.IO;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace PsfGuard.Director.Plugin.Acquisition;

// Not exported to MEF or the sequencer. A session must own the equipment and
// supply fresh core authorization before this adapter can be wired to dispatch.
internal sealed class NinaCaptureAdapter(IProfileService profiles, ICameraMediator camera,
    IImagingMediator imaging, IImageSaveMediator saves, IImageHistoryVM history,
    string journalRoot, TimeSpan saveTimeout, TimeProvider clock)
{
    internal const string CaptureIdHeader = "PGCAPID";
    private readonly SemaphoreSlim captureGate = new(1, 1);

    internal async Task<CaptureEvidence> CaptureAsync(CaptureIntent intent,
        Func<CancellationToken, Task> revalidateAtDispatch,
        IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(revalidateAtDispatch);
        CaptureJournal.Validate(intent);
        if (saveTimeout <= TimeSpan.Zero || saveTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(saveTimeout));
        if (!await captureGate.WaitAsync(0, token).ConfigureAwait(false))
            throw new InvalidOperationException("Director already owns an in-flight capture.");

        CaptureJournal? journal = null;
        var started = clock.GetTimestamp();
        var downloaded = started;
        try
        {
            CheckLocalContext(intent);
            var captureProfile = profiles.ActiveProfile;
            if (captureProfile.Id != intent.ProfileId) throw new InvalidOperationException("NINA profile changed before capture.");
            var fileSettings = ProfileScopedImageData.Snapshot(captureProfile);
            if (!Path.IsPathFullyQualified(fileSettings.FilePath) || !Directory.Exists(fileSettings.FilePath))
                throw new DirectoryNotFoundException("NINA image destination must be an existing absolute directory.");
            if (fileSettings.FileType is not (FileTypeEnum.FITS or FileTypeEnum.XISF))
                throw new InvalidOperationException("Director requires FITS or XISF image output.");
            journal = new CaptureJournal(journalRoot, intent,
                new(fileSettings.FilePath, fileSettings.FilePattern, fileSettings.FileType.ToString()), clock.GetUtcNow());
            await revalidateAtDispatch(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            CheckLocalContext(intent);
            Report("Exposing and downloading");
            var capture = new CaptureSequence
            {
                ExposureTime = intent.ExposureSeconds,
                Binning = new BinningMode(intent.BinX, intent.BinY),
                Gain = intent.Gain,
                Offset = intent.Offset,
                ImageType = CaptureSequence.ImageTypes.LIGHT,
                TotalExposureCount = 1
            };
            // Filter changes, autofocus, and other preparation happen before
            // revalidation. CaptureImage must not insert a filter change here.
            Record(CapturePhase.Capturing);
            var captureStarted = clock.GetTimestamp();
            var exposure = await imaging.CaptureImage(capture, token, progress, intent.TargetName).ConfigureAwait(false)
                ?? throw new IOException("NINA returned no exposure data.");
            downloaded = clock.GetTimestamp();
            if (exposure.MetaData.Image.Id < 0) throw new InvalidDataException("NINA image has no capture identity.");
            journal.Record(journal.Evidence with
            {
                Phase = CapturePhase.Downloaded,
                UpdatedAt = clock.GetUtcNow(),
                NinaImageId = exposure.MetaData.Image.Id,
                CaptureAndDownloadMs = clock.GetElapsedTime(captureStarted, downloaded).TotalMilliseconds,
                Filter = exposure.MetaData.FilterWheel.Filter
            });
            token.ThrowIfCancellationRequested();
            CheckLocalContext(intent);
            var image = await exposure.ToImageData(progress, token).ConfigureAwait(false)
                ?? throw new IOException("NINA returned no image data.");
            var metadata = image.MetaData;
            if (metadata.Image.Id != journal.Evidence.NinaImageId)
                throw new InvalidDataException("NINA image identity changed during conversion.");
            metadata.GenericHeaders.RemoveAll(header => header.Key == CaptureIdHeader);
            metadata.GenericHeaders.Add(new StringMetaDataHeader(CaptureIdHeader, intent.CaptureId.ToString("D"), "PSF Guard Director capture ID"));
            metadata.Target.Name = intent.TargetName;
            metadata.Target.Coordinates = new Coordinates(intent.RaDegrees, intent.DecDegrees, Epoch.J2000, Coordinates.RAType.Degrees);
            metadata.Target.PositionAngle = intent.PositionAngle;
            history.Add(metadata.Image.Id, CaptureSequence.ImageTypes.LIGHT);
            Report("Preparing image");
            var prepared = await imaging.PrepareImage(image, new PrepareImageParameters(true, true), token).ConfigureAwait(false);
            if (prepared is null) throw new IOException("NINA image preparation returned no result.");
            history.PopulateStatistics(metadata.Image.Id, await image.Statistics.Task.WaitAsync(token).ConfigureAwait(false));
            CheckLocalContext(intent);
            token.ThrowIfCancellationRequested();

            var receipt = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            ObserveFault(receipt.Task);
            bool Matches(ImageMetaData? data) => data?.Image.Id == metadata.Image.Id
                && data.GenericHeaders.OfType<StringMetaDataHeader>().Any(header =>
                    header.Key == CaptureIdHeader && header.Value == intent.CaptureId.ToString("D"));
            void Saved(object? sender, ImageSavedEventArgs args)
            {
                if (!Matches(args.MetaData)) return;
                if (args.PathToImage is not { IsAbsoluteUri: true, IsFile: true } path)
                    receipt.TrySetException(new InvalidDataException("NINA save receipt has no local file path."));
                else receipt.TrySetResult(path.LocalPath);
            }
            Task Failed(object sender, ImageSaveFailedEventArgs args)
            {
                if (Matches(args.MetaData)) receipt.TrySetException(new ImageSaveFailedException(args));
                return Task.CompletedTask;
            }
            var savedAttached = false;
            var failedAttached = false;
            try
            {
                saves.ImageSaved += Saved;
                savedAttached = true;
                saves.ImageSaveFailed += Failed;
                failedAttached = true;
                // Persist before enqueue: a crash here is uncertain, never an
                // authorization to repeat the exposure with this identity.
                Record(CapturePhase.SaveQueued);
                Report("Waiting for image save");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(saveTimeout);
                string path;
                try
                {
                    var enqueue = saves.Enqueue(new ProfileScopedImageData(image, fileSettings), Task.FromResult(prepared), progress, deadline.Token);
                    ObserveFault(enqueue);
                    await enqueue.WaitAsync(deadline.Token).ConfigureAwait(false);
                    path = await receipt.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (receipt.Task.IsCompleted)
                {
                    // An observed save result wins a simultaneous stop.
                    path = await receipt.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                {
                    throw new TimeoutException("NINA did not confirm the image save before the deadline.", error);
                }
                journal.Record(journal.Evidence with
                {
                    Phase = CapturePhase.Saved,
                    UpdatedAt = clock.GetUtcNow(),
                    SavedPath = path,
                    ProcessingAndSaveMs = clock.GetElapsedTime(downloaded).TotalMilliseconds,
                    TotalMs = clock.GetElapsedTime(started).TotalMilliseconds
                });
                return journal.Evidence;
            }
            finally
            {
                try { if (savedAttached) saves.ImageSaved -= Saved; }
                finally { if (failedAttached) saves.ImageSaveFailed -= Failed; }
            }
        }
        catch (Exception error)
        {
            if (journal is not null)
            {
                var phase = journal.Evidence.Phase switch
                {
                    // A camera/transport error cannot prove that hardware did not expose.
                    CapturePhase.Capturing => CapturePhase.CaptureUncertain,
                    CapturePhase.SaveQueued => error is ImageSaveFailedException ? CapturePhase.Failed : CapturePhase.SaveUncertain,
                    _ => error is OperationCanceledException ? CapturePhase.Interrupted : CapturePhase.Failed
                };
                try
                {
                    journal.Record(journal.Evidence with
                    {
                        Phase = phase,
                        UpdatedAt = clock.GetUtcNow(),
                        ErrorType = error.GetType().Name,
                        TotalMs = clock.GetElapsedTime(started).TotalMilliseconds
                    });
                }
                catch (Exception journalError) { throw new AggregateException("Capture and journal update failed.", error, journalError); }
            }
            throw;
        }
        finally
        {
            Report(string.Empty);
            captureGate.Release();
        }

        void Record(CapturePhase phase) => journal!.Record(journal.Evidence with { Phase = phase, UpdatedAt = clock.GetUtcNow() });
        void Report(string message)
        {
            try { progress.Report(new ApplicationStatus { Source = "PSF Guard Director", Status = message }); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Director progress observer failed: {0}", error.GetType().Name); }
        }
    }

    private static void ObserveFault(Task task) => _ = task.ContinueWith(
        completed => { _ = completed.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private void CheckLocalContext(CaptureIntent intent)
    {
        if (profiles.ActiveProfile.Id != intent.ProfileId)
            throw new InvalidOperationException("NINA profile changed during capture.");
        var info = camera.GetInfo();
        if (!info.Connected || info.DeviceId != intent.CameraDeviceId)
            throw new InvalidOperationException("NINA camera no longer matches the capture intent.");
    }
}
