using Moq;
using NINA.Core.Model;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.FileFormat.FITS;
using NINA.Image.FileFormat.XISF;
using NINA.Image.FileFormat;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using Nito.AsyncEx;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed partial class NinaCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QualityEvidenceUsesNativePixelsAndProbeNeverEnqueuesScience(bool probe)
    {
        using var f = new Fixture();
        var task = f.RunQuality(probe);
        if (!probe) { await f.Enqueued.Task; f.Saved(); }
        var evidence = await task;
        Assert.NotNull(evidence.Quality);
        Assert.Equal(100u, evidence.Quality.Metrics.Stars);
        Assert.Equal(1000, evidence.Quality.Metrics.BackgroundAdu);
        Assert.Equal(2, evidence.Quality.Metrics.HfrPixels);
        Assert.Equal(probe ? CapturePhase.ProbeMeasured : CapturePhase.Saved, evidence.Phase);
        Assert.Equal(probe, evidence.Intent.QualityProbe);
        Assert.Equal(evidence, f.Read());
        if (probe)
        {
            Assert.Null(evidence.SavedPath);
            Assert.False(f.Enqueued.Task.IsCompleted);
            f.Saves.Verify(x => x.Enqueue(It.IsAny<IImageData>(), It.IsAny<Task<IRenderedImage>>(),
                It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
    [Fact]
    public async Task WaitsForCorrelatedFinalSaveAndRecordsNativeMetadataAndTiming()
    {
        using var f = new Fixture();
        var task = f.Run(token => { f.Clock.Advance(100); return Task.CompletedTask; });
        await f.Enqueued.Task;
        Assert.False(task.IsCompleted);
        Assert.Equal(CapturePhase.SaveQueued, f.Read().Phase);
        Assert.Equal(200, f.Read().CaptureAndDownloadMs);
        Assert.Equal(180, f.Metadata.Target.Coordinates.RADegrees);
        Assert.Equal(12, f.Metadata.Target.Coordinates.RA);
        Assert.Equal(f.Intent.TargetName, f.Metadata.Target.Name);
        Assert.Equal(f.Intent.PositionAngle, f.Metadata.Target.PositionAngle);
        Assert.Equal(f.Intent.CaptureId.ToString("D"), Assert.Single(f.Metadata.GenericHeaders.OfType<StringMetaDataHeader>()).Value);
        var unrelated = new ImageMetaData();
        unrelated.Image.Id = f.Metadata.Image.Id;
        f.Saved(unrelated);
        Assert.False(task.IsCompleted);
        f.Clock.Advance(400);
        f.Saved();
        var result = await task;
        Assert.Equal(CapturePhase.Saved, result.Phase);
        Assert.Equal(700, result.ProcessingAndSaveMs);
        Assert.Equal(1000, result.TotalMs);
        Assert.Equal(f.ImagePath, result.SavedPath);
        Assert.Equal(result, f.Read());
        Assert.Equal(string.Empty, f.Progress.Messages.Last());
        f.AssertDetached();
    }

    [Fact]
    public async Task DispatchIsReservedBeforeAuthorizationAndNoCaptureFollowsDenial()
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Run(token =>
        {
            Assert.Equal(CapturePhase.Reserved, f.Read().Phase);
            throw new InvalidOperationException("Stale assignment");
        }));
        f.Imaging.Verify(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(),
            It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<string>()), Times.Never);
        Assert.Equal(CapturePhase.Failed, f.Read().Phase);
    }

    [Fact]
    public async Task NativeFitsAndXisfHeadersPreserveCaptureIdentity()
    {
        using var f = new Fixture();
        var task = f.Run();
        await f.Enqueued.Task;
        f.Saved();
        await task;
        var fits = new FITSHeader(2, 2);
        fits.PopulateFromMetaData(f.Metadata);
        var card = Assert.Single(fits.HeaderCards, card => card.Key == NinaCaptureAdapter.CaptureIdHeader);
        Assert.Contains(f.Intent.CaptureId.ToString("D"), System.Text.Encoding.ASCII.GetString(card.Encode()));
        var xisf = new XISFHeader();
        xisf.AddImageMetaData(new ImageProperties(2, 2, 16, false, 10, 20), CaptureSequence.ImageTypes.LIGHT);
        xisf.Populate(f.Metadata);
        var keyword = Assert.Single(xisf.Image.Elements(), element =>
            element.Name.LocalName == "FITSKeyword" && (string?)element.Attribute("name") == NinaCaptureAdapter.CaptureIdHeader);
        Assert.Contains(f.Intent.CaptureId.ToString("D"), (string?)keyword.Attribute("value"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureErrorOrCancellationCannotProveNoExposureOccurred(bool cancelled)
    {
        using var f = new Fixture();
        f.Imaging.Setup(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), f.Progress, f.Intent.TargetName))
            .Callback(() => Assert.Equal(CapturePhase.Capturing, f.Read().Phase))
            .ThrowsAsync(cancelled ? new OperationCanceledException("camera interrupted") : new IOException("camera transport lost"));
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run());
        else await Assert.ThrowsAsync<IOException>(() => f.Run());
        Assert.Equal(CapturePhase.CaptureUncertain, f.Read().Phase);
        Assert.Null(f.Read().NinaImageId);
        Assert.False(f.Enqueued.Task.IsCompleted);
        await Assert.ThrowsAsync<IOException>(() => f.Run());
        f.Imaging.Verify(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), f.Progress, f.Intent.TargetName), Times.Once);
    }

    [Fact]
    public async Task MissingCaptureResultIsUncertainAndCannotBeRetried()
    {
        using var f = new Fixture();
        f.Imaging.Setup(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), f.Progress, f.Intent.TargetName))
            .ReturnsAsync((IExposureData)null!);
        await Assert.ThrowsAsync<IOException>(() => f.Run());
        Assert.Equal(CapturePhase.CaptureUncertain, f.Read().Phase);
        await Assert.ThrowsAsync<IOException>(() => f.Run());
        f.Imaging.Verify(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), f.Progress, f.Intent.TargetName), Times.Once);
    }

    [Fact]
    public async Task ProfileChangeDuringRevalidationCannotDispatch()
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Run(token =>
        {
            f.Profile.SetupGet(x => x.Id).Returns(Guid.NewGuid());
            return Task.CompletedTask;
        }));
        f.Imaging.Verify(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(),
            It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CameraChangeCannotDispatch()
    {
        using var f = new Fixture();
        f.CameraInfo.DeviceId = "another-camera";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Run());
        Assert.Empty(Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellationBeforeDispatchPropagatesWithoutCapture()
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run(token =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token));
        Assert.Equal(CapturePhase.Interrupted, f.Read().Phase);
        f.Imaging.Verify(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(),
            It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SaveFailureIsNotSuccessfulEnqueue()
    {
        using var f = new Fixture();
        var task = f.Run();
        await f.Enqueued.Task;
        f.Saves.Raise(x => x.ImageSaveFailed += null!, f.Saves.Object,
            new ImageSaveFailedEventArgs(f.Image.Object, f.Root, "test", ImageSaveFailureStage.SaveToDisk, new IOException("disk full")));
        var error = await Assert.ThrowsAsync<ImageSaveFailedException>(() => task);
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(CapturePhase.Failed, f.Read().Phase);
        Assert.Null(f.Read().SavedPath);
        f.AssertDetached();
    }

    [Fact]
    public async Task PreparationFailureNeverEnqueuesAndRetainsDownloadedIdentity()
    {
        using var f = new Fixture();
        f.Imaging.Setup(x => x.PrepareImage(f.Image.Object, It.IsAny<PrepareImageParameters>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("prepare failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Run());
        Assert.Equal(CapturePhase.Failed, f.Read().Phase);
        Assert.Equal(42, f.Read().NinaImageId);
        Assert.False(f.Enqueued.Task.IsCompleted);
    }

    [Fact]
    public async Task CancellationOfQueuedSaveIsUncertainAndLateEventCannotAuthorizeRetry()
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var task = f.Run(token: cancellation.Token);
        await f.Enqueued.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(CapturePhase.SaveUncertain, f.Read().Phase);
        f.Saved();
        Assert.Equal(CapturePhase.SaveUncertain, f.Read().Phase);
        await Assert.ThrowsAsync<IOException>(() => f.Run());
        Assert.Equal(CapturePhase.SaveUncertain, f.Read().Phase);
        Assert.Equal(CapturePhase.Saved, (await f.Reconcile())!.Phase);
        f.Acknowledge();
        f.AssertDetached();
    }

    [Fact]
    public async Task MissingReceiptTimesOutWithoutClaimingSaveFailure()
    {
        using var f = new Fixture(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<TimeoutException>(() => f.Run());
        Assert.Equal(CapturePhase.SaveUncertain, f.Read().Phase);
        f.DisposeAdapter();
        f.AssertDetached();
    }

    [Fact]
    public async Task LiveSettledCaptureWithoutEnqueueSpendsAttemptButDoesNotClaimNoExposure()
    {
        using var f = new Fixture();
        f.Imaging.Setup(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), f.Progress, f.Intent.TargetName))
            .ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run());
        Assert.Equal(CapturePhase.CaptureUncertain, f.Read().Phase);
        Assert.Null(await f.Reconcile(Guid.NewGuid().ToString("D")));
        f.CameraInfo.IsExposing = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reconcile());
        Assert.Equal(CapturePhase.CaptureUncertain, f.Read().Phase);
        f.CameraInfo.IsExposing = false;
        Assert.Equal(CapturePhase.Failed, (await f.Reconcile())!.Phase);
        Assert.Null(f.Read().SavedPath);
        Assert.False(f.Enqueued.Task.IsCompleted);
        f.Acknowledge();
        await Assert.ThrowsAsync<IOException>(() => f.Run());
        f.Imaging.Verify(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), f.Progress, f.Intent.TargetName), Times.Once);
    }

    [Theory]
    [InlineData(CameraStates.NoState)]
    [InlineData(CameraStates.Waiting)]
    [InlineData(CameraStates.Exposing)]
    [InlineData(CameraStates.Reading)]
    [InlineData(CameraStates.Download)]
    [InlineData(CameraStates.Error)]
    [InlineData(CameraStates.LoadingFile)]
    public async Task SettlementRequiresDriverIdleEvenAfterInvocationFlagClears(CameraStates state)
    {
        using var f = new Fixture();
        f.Imaging.Setup(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), f.Progress, f.Intent.TargetName))
            .ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run());
        f.CameraInfo.IsExposing = false;
        f.CameraInfo.CameraState = state;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reconcile());
        Assert.Equal(CapturePhase.CaptureUncertain, f.Read().Phase);
        f.CameraInfo.CameraState = CameraStates.Idle;
        Assert.Equal(CapturePhase.Failed, (await f.Reconcile())!.Phase);
    }

    [Fact]
    public async Task SettlementRefusesChangedCameraOrProfile()
    {
        using var f = new Fixture();
        f.Imaging.Setup(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), f.Progress, f.Intent.TargetName))
            .ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run());
        f.CameraInfo.Connected = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reconcile());
        f.CameraInfo.Connected = true;
        f.Profile.SetupGet(x => x.Id).Returns(Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reconcile());
        Assert.Equal(CapturePhase.CaptureUncertain, f.Read().Phase);
    }

    [Fact]
    public async Task LateSaveFailureSettlesWithoutAnotherExposure()
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var task = f.Run(token: cancellation.Token);
        await f.Enqueued.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        f.Saves.Raise(x => x.ImageSaveFailed += null!, f.Saves.Object,
            new ImageSaveFailedEventArgs(f.Image.Object, f.Root, "test", ImageSaveFailureStage.SaveToDisk, new IOException("disk full")));
        Assert.Equal(CapturePhase.Failed, (await f.Reconcile())!.Phase);
        f.Acknowledge();
        f.AssertDetached();
    }

    [Fact]
    public async Task MissingLateSaveRemainsUncertainAndCannotBeAcknowledged()
    {
        using var f = new Fixture(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<TimeoutException>(() => f.Run());
        await Assert.ThrowsAsync<TimeoutException>(() => f.Reconcile());
        Assert.Throws<InvalidOperationException>(() => f.Acknowledge());
        Assert.Equal(CapturePhase.SaveUncertain, f.Read().Phase);
    }

    [Fact]
    public async Task SavedReceiptSurvivesCancellationAfterAdapterReturned()
    {
        using var f = new Fixture();
        var task = f.Run();
        await f.Enqueued.Task;
        f.Saved();
        var saved = await task;
        Assert.Equal(saved, await f.Reconcile());
        f.Acknowledge();
        Assert.Null(await f.Reconcile());
    }

    [Fact]
    public async Task ReplacementOwnerCannotInferSettlementFromAnExistingJournal()
    {
        using var f = new Fixture();
        f.Imaging.Setup(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), f.Progress, f.Intent.TargetName))
            .ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run());
        f.DisposeAdapter();
        using var replacement = new NinaCaptureAdapter(Mock.Of<IProfileService>(), Mock.Of<ICameraMediator>(),
            f.Imaging.Object, f.Saves.Object, Mock.Of<IImageHistoryVM>(), f.Root, TimeSpan.FromSeconds(1), f.Clock);
        Assert.Null(await replacement.ReconcileInterruptedAsync(f.Intent.CaptureId.ToString("D"), default));
        Assert.Equal(CapturePhase.CaptureUncertain, f.Read().Phase);
    }

    [Fact]
    public async Task ReconciliationCannotRaceAnActiveCapture()
    {
        using var f = new Fixture();
        var run = f.Run();
        await f.Enqueued.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reconcile());
        f.Saved();
        await run;
    }

    [Fact]
    public async Task LateReceiptTimingExcludesTimeSpentWaitingToReconcile()
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var run = f.Run(token: cancellation.Token);
        await f.Enqueued.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        f.Clock.Advance(20);
        f.Saved();
        f.Clock.Advance(60000);
        var evidence = await f.Reconcile();
        Assert.Equal(520, evidence!.TotalMs);
        Assert.Equal(320, evidence.ProcessingAndSaveMs);
        f.Acknowledge();
    }

    [Fact]
    public async Task DisposalDuringEnqueueDetachesObserversEvenBeforeInvocationSettles()
    {
        using var f = new Fixture(TimeSpan.FromMilliseconds(50));
        f.Saves.Setup(x => x.Enqueue(It.IsAny<IImageData>(), It.IsAny<Task<IRenderedImage>>(), f.Progress, It.IsAny<CancellationToken>()))
            .Callback(f.DisposeAdapter).Returns(Task.CompletedTask);
        await Assert.ThrowsAsync<TimeoutException>(() => f.Run());
        f.AssertDetached();
        Assert.Equal(CapturePhase.SaveUncertain, f.Read().Phase);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => f.Reconcile());
    }

    [Fact]
    public async Task MalformedSaveReceiptCannotClaimSaved()
    {
        using var f = new Fixture();
        var task = f.Run();
        await f.Enqueued.Task;
        f.Saves.Raise(x => x.ImageSaved += null!, f.Saves.Object,
            new ImageSavedEventArgs { MetaData = f.Metadata, PathToImage = new Uri("https://example.invalid/image.fits") });
        await Assert.ThrowsAsync<InvalidDataException>(() => task);
        Assert.Equal(CapturePhase.SaveUncertain, f.Read().Phase);
    }

    [Fact]
    public async Task CorrelatedReceiptCanArriveBeforeEnqueueReturns()
    {
        using var f = new Fixture();
        f.Saves.Setup(x => x.Enqueue(It.IsAny<IImageData>(), It.IsAny<Task<IRenderedImage>>(), f.Progress, It.IsAny<CancellationToken>()))
            .Callback(() => f.Saved()).Returns(Task.CompletedTask);
        Assert.Equal(CapturePhase.Saved, (await f.Run()).Phase);
    }

    [Fact]
    public async Task KnownSaveWinsCancellationRace()
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        f.Saves.Setup(x => x.Enqueue(It.IsAny<IImageData>(), It.IsAny<Task<IRenderedImage>>(), f.Progress, It.IsAny<CancellationToken>()))
            .Callback(() => { f.Saved(); cancellation.Cancel(); }).Returns(Task.CompletedTask);
        Assert.Equal(CapturePhase.Saved, (await f.Run(token: cancellation.Token)).Phase);
    }

    [Fact]
    public async Task KnownSaveWinsCancelledEnqueueTask()
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        f.Saves.Setup(x => x.Enqueue(It.IsAny<IImageData>(), It.IsAny<Task<IRenderedImage>>(), f.Progress, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                f.Saved();
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            });
        Assert.Equal(CapturePhase.Saved, (await f.Run(token: cancellation.Token)).Phase);
    }

    [Fact]
    public async Task StalledEnqueueIsBoundedAndRemainsUncertain()
    {
        using var f = new Fixture(TimeSpan.FromMilliseconds(50));
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Saves.Setup(x => x.Enqueue(It.IsAny<IImageData>(), It.IsAny<Task<IRenderedImage>>(), f.Progress, It.IsAny<CancellationToken>()))
            .Returns(stalled.Task);
        await Assert.ThrowsAsync<TimeoutException>(() => f.Run());
        Assert.Equal(CapturePhase.SaveUncertain, f.Read().Phase);
        stalled.SetException(new IOException("late queue failure"));
        f.DisposeAdapter();
        f.AssertDetached();
    }

    [Fact]
    public async Task KnownSaveFailureWinsCancellationRace()
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        f.Saves.Setup(x => x.Enqueue(It.IsAny<IImageData>(), It.IsAny<Task<IRenderedImage>>(), f.Progress, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                f.Saves.Raise(x => x.ImageSaveFailed += null!, f.Saves.Object,
                    new ImageSaveFailedEventArgs(f.Image.Object, f.Root, "test", ImageSaveFailureStage.SaveToDisk, new IOException("disk full")));
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            });
        await Assert.ThrowsAsync<ImageSaveFailedException>(() => f.Run(token: cancellation.Token));
        Assert.Equal(CapturePhase.Failed, f.Read().Phase);
    }

    [Fact]
    public async Task QueuedImageKeepsOriginalDestinationAfterProfileChange()
    {
        using var f = new Fixture();
        var task = f.Run();
        await f.Enqueued.Task;
        f.Profile.SetupGet(x => x.Id).Returns(Guid.NewGuid());
        FileSaveInfo? used = null;
        f.Image.Setup(x => x.SaveToDisk(It.IsAny<FileSaveInfo>(), It.IsAny<CancellationToken>(), false, It.IsAny<IList<ImagePattern>>()))
            .Callback<FileSaveInfo, CancellationToken, bool, IList<ImagePattern>>((settings, token, force, patterns) => used = settings)
            .ReturnsAsync(f.ImagePath);
        await f.EnqueuedImage!.SaveToDisk(new FileSaveInfo { FilePath = "C:\\wrong-profile", FileType = FileTypeEnum.XISF },
            CancellationToken.None, false, []);
        Assert.Equal(f.Root, used!.FilePath);
        Assert.Equal("$$TARGETNAME$$", used.FilePattern);
        Assert.Equal(FileTypeEnum.FITS, used.FileType);
        Assert.Equal(f.Root, f.Read().Destination.Directory);
        Assert.Equal("FITS", f.Read().Destination.Format);
        Assert.Same(f.Metadata, f.EnqueuedImage.MetaData);
        f.Saved();
        Assert.Equal(CapturePhase.Saved, (await task).Phase);
    }

    [Fact]
    public async Task SaveScopeCopiesEveryNativeSaveOptionAndDoesNotShareMutableSettings()
    {
        var settings = new FileSaveInfo();
        var properties = typeof(FileSaveInfo).GetProperties();
        foreach (var property in properties)
        {
            var type = property.PropertyType;
            property.SetValue(settings, type == typeof(string) ? $"original-{property.Name}"
                : type == typeof(bool) ? true : Enum.GetValues(type).GetValue(Enum.GetValues(type).Length - 1));
        }
        var expected = properties.ToDictionary(property => property.Name, property => property.GetValue(settings));
        var image = new Mock<IImageData>();
        var scoped = new ProfileScopedImageData(image.Object, settings);
        settings.FilePath = "changed-after-snapshot";
        image.Setup(x => x.SaveToDisk(It.IsAny<FileSaveInfo>(), It.IsAny<CancellationToken>(), false, It.IsAny<IList<ImagePattern>>()))
            .Callback<FileSaveInfo, CancellationToken, bool, IList<ImagePattern>>((actual, token, force, patterns) =>
            {
                foreach (var property in properties) Assert.Equal(expected[property.Name], property.GetValue(actual));
                actual.FilePath = "changed-by-writer";
            }).ReturnsAsync("saved");
        await scoped.SaveToDisk(new FileSaveInfo(), CancellationToken.None, false, []);
        await scoped.SaveToDisk(new FileSaveInfo(), CancellationToken.None, false, []);
    }

    [Fact]
    public async Task CancellationAfterDownloadKeepsImageIdentityAndTiming()
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var exposure = new Mock<IExposureData>();
        exposure.SetupGet(x => x.MetaData).Returns(f.Metadata);
        f.Imaging.Setup(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), f.Progress, f.Intent.TargetName))
            .Callback(() => { f.Clock.Advance(200); cancellation.Cancel(); }).ReturnsAsync(exposure.Object);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run(token: cancellation.Token));
        Assert.Equal(CapturePhase.Interrupted, f.Read().Phase);
        Assert.Equal(42, f.Read().NinaImageId);
        Assert.Equal(200, f.Read().CaptureAndDownloadMs);
        Assert.False(f.Enqueued.Task.IsCompleted);
    }

    [Fact]
    public async Task ConcurrentDispatchIsRejectedInsteadOfQueuingStaleWork()
    {
        using var f = new Fixture();
        var first = f.Run();
        await f.Enqueued.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Run());
        f.Saved();
        await first;
    }

    [Fact]
    public async Task BrokenProgressObserverCannotMaskSavedEvidence()
    {
        using var f = new Fixture();
        f.Progress.Throw = true;
        var task = f.Run();
        await f.Enqueued.Task;
        f.Saved();
        Assert.Equal(CapturePhase.Saved, (await task).Phase);
    }

    [Fact]
    public async Task UnwritableJournalPreventsHardwareDispatch()
    {
        using var f = new Fixture();
        var profilePath = Path.Combine(f.Root, f.Intent.ProfileId.ToString("N"));
        File.WriteAllText(profilePath, "not a directory");
        await Assert.ThrowsAsync<IOException>(() => f.Run());
        f.Imaging.Verify(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(),
            It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task PartialEventSubscriptionFailureDetachesFirstHandler()
    {
        using var f = new Fixture();
        f.Saves.SetupAdd(x => x.ImageSaveFailed += It.IsAny<Func<object, ImageSaveFailedEventArgs, Task>>())
            .Throws(new InvalidOperationException("save controller unavailable"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Run());
        f.Saves.VerifyRemove(x => x.ImageSaved -= It.IsAny<EventHandler<ImageSavedEventArgs>>(), Times.Once);
        Assert.Equal(CapturePhase.Failed, f.Read().Phase);
        Assert.False(f.Enqueued.Task.IsCompleted);
    }

    [Fact]
    public async Task FinalJournalWriteFailureNeverReturnsSuccessfulCapture()
    {
        using var f = new Fixture();
        var task = f.Run();
        await f.Enqueued.Task;
        var path = Path.Combine(f.Root, f.Intent.ProfileId.ToString("N"), $"{f.Intent.CaptureId:N}.json");
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            f.Saved();
            var error = await Assert.ThrowsAsync<AggregateException>(() => task);
            Assert.Equal(2, error.InnerExceptions.Count);
        }
        Assert.Equal(CapturePhase.SaveQueued, f.Read().Phase);
        f.AssertDetached();
    }

    [Fact]
    public async Task CancellationWhileComputingStatisticsDoesNotHangOrEnqueue()
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statistics = new TaskCompletionSource<IImageStatistics>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Image.SetupGet(x => x.Statistics).Returns(new AsyncLazy<IImageStatistics>(() =>
        {
            requested.SetResult();
            return statistics.Task;
        }));
        var task = f.Run(token: cancellation.Token);
        await requested.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(CapturePhase.Interrupted, f.Read().Phase);
        Assert.False(f.Enqueued.Task.IsCompleted);
        statistics.SetResult(Mock.Of<IImageStatistics>());
    }

    [Fact]
    public void JournalRejectsIdentityChangesAndClosedAttemptUpdates()
    {
        using var f = new Fixture();
        var journal = new CaptureJournal(f.Root, f.Intent, new(f.Root, "image", "FITS"), f.Clock.GetUtcNow());
        Assert.Throws<InvalidOperationException>(() => journal.Record(journal.Evidence with
        {
            Intent = f.Intent with { CaptureId = Guid.NewGuid() }
        }));
        journal.Record(journal.Evidence with { Phase = CapturePhase.Interrupted });
        Assert.Throws<InvalidOperationException>(() => journal.Record(journal.Evidence with { Phase = CapturePhase.Capturing }));
        Assert.Empty(Directory.GetFiles(f.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("schema_version", "2")]
    [InlineData("phase", "\"saved\"")]
    [InlineData("total_ms", "-1")]
    public void JournalReaderRejectsUnsupportedOrIncompleteEvidence(string field, string value)
    {
        using var f = new Fixture();
        _ = new CaptureJournal(f.Root, f.Intent, new(f.Root, "image", "FITS"), f.Clock.GetUtcNow());
        var path = Path.Combine(f.Root, f.Intent.ProfileId.ToString("N"), $"{f.Intent.CaptureId:N}.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json[field] = System.Text.Json.Nodes.JsonNode.Parse(value);
        File.WriteAllText(path, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => CaptureJournal.Read(path));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(86401)]
    public void InvalidExposureCannotBeReserved(double exposure)
    {
        using var f = new Fixture();
        Assert.Throws<ArgumentException>(() => new CaptureJournal(f.Root, f.Intent with { ExposureSeconds = exposure },
            new(f.Root, "image", "FITS"), f.Clock.GetUtcNow()));
        Assert.Empty(Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long milliseconds;
        public Action? OnTimestamp { get; set; }
        public void Advance(long value) => milliseconds += value;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() { OnTimestamp?.Invoke(); return milliseconds; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds);
    }

    private sealed class TestProgress : IProgress<ApplicationStatus>
    {
        public bool Throw { get; set; }
        public Action<ApplicationStatus>? OnReport { get; set; }
        public List<string> Messages { get; } = [];
        public void Report(ApplicationStatus value)
        {
            if (Throw) throw new InvalidOperationException("observer failed");
            OnReport?.Invoke(value);
            Messages.Add(value.Status);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"director-capture-{Guid.NewGuid():N}");
        public string ImagePath => Path.Combine(Root, "image.fits");
        public CaptureIntent Intent { get; } = new(Guid.NewGuid(), Guid.NewGuid(), "rig", "configuration", "assignment", 1,
            "goal", "camera", 1.5, "Test target", 180, 20, 30, Gain: 10, Offset: 20);
        public ManualClock Clock { get; } = new();
        public TestProgress Progress { get; } = new();
        public Mock<IProfile> Profile { get; } = new();
        public Mock<IImagingMediator> Imaging { get; } = new(MockBehavior.Strict);
        public Mock<IImageSaveMediator> Saves { get; } = new(MockBehavior.Strict);
        public Mock<IImageData> Image { get; } = new();
        public CameraInfo CameraInfo { get; } = new() { Connected = true, DeviceId = "camera", CameraState = CameraStates.Idle };
        public FilterWheelInfo WheelInfo { get; } = new() { Connected = true, DeviceId = "wheel", SelectedFilter = new() { Position = 2, Name = "L" } };
        public NinaEquipmentBinding Local { get; private set; } = null!;
        public NinaProgramCapture BoundCapture { get; private set; } = null!;
        public NinaEquipmentSnapshot Equipment { get; private set; } = null!;
        public ImageMetaData Metadata { get; } = new();
        public TaskCompletionSource Enqueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IImageData? EnqueuedImage { get; private set; }
        private readonly NinaCaptureAdapter adapter;

        public Fixture(TimeSpan? timeout = null)
        {
            Directory.CreateDirectory(Root);
            Profile.SetupGet(x => x.Id).Returns(Intent.ProfileId);
            var fileSettings = new Mock<IImageFileSettings>();
            fileSettings.SetupGet(x => x.FilePath).Returns(Root);
            fileSettings.SetupGet(x => x.FileType).Returns(FileTypeEnum.FITS);
            fileSettings.Setup(x => x.GetFilePattern(CaptureSequence.ImageTypes.LIGHT)).Returns("$$TARGETNAME$$");
            Profile.SetupGet(x => x.ImageFileSettings).Returns(fileSettings.Object);
            var profiles = new Mock<IProfileService>();
            profiles.SetupGet(x => x.ActiveProfile).Returns(Profile.Object);
            var camera = new Mock<ICameraMediator>();
            camera.Setup(x => x.GetInfo()).Returns(CameraInfo);
            var device = new Mock<ICamera>();
            device.SetupGet(x => x.Connected).Returns(() => CameraInfo.Connected);
            device.SetupGet(x => x.Id).Returns(() => CameraInfo.DeviceId);
            device.SetupGet(x => x.CameraState).Returns(() => CameraInfo.CameraState);
            camera.Setup(x => x.GetDevice()).Returns(device.Object);
            var cameraSettings = new Mock<ICameraSettings>();
            cameraSettings.SetupGet(x => x.Id).Returns("camera");
            Profile.SetupGet(x => x.CameraSettings).Returns(cameraSettings.Object);
            var wheelSettings = new Mock<IFilterWheelSettings>();
            wheelSettings.SetupGet(x => x.Id).Returns("wheel");
            wheelSettings.SetupGet(x => x.FilterWheelFilters).Returns(new ObserveAllCollection<NINA.Core.Model.Equipment.FilterInfo>([new() { Position = 2, Name = "L" }]));
            Profile.SetupGet(x => x.FilterWheelSettings).Returns(wheelSettings.Object);
            var wheel = new Mock<IFilterWheelMediator>();
            wheel.Setup(x => x.GetInfo()).Returns(WheelInfo);
            CameraInfo.BinningModes = new([new(1, 1), new(2, 2)]);
            CameraInfo.ReadoutModes = new[] { "Normal", "Fast" };
            CameraInfo.ReadoutModeForNormalImages = 1;
            CameraInfo.ExposureMin = 0.001;
            CameraInfo.ExposureMax = 60;
            CameraInfo.CanSetGain = true;
            CameraInfo.Gains = new[] { 10, 40 };
            CameraInfo.CanSetOffset = true;
            CameraInfo.OffsetMin = 0;
            CameraInfo.OffsetMax = 50;
            Local = new(Intent.ProfileId, "rig", "constraints", "camera", "wheel", [new("filter-l", 2, "L")], false, 0);
            Equipment = new(profiles.Object, camera.Object, wheel.Object);
            Metadata.Image.Id = 42;
            Image.SetupGet(x => x.MetaData).Returns(Metadata);
            Image.SetupGet(x => x.Statistics).Returns(new AsyncLazy<IImageStatistics>(() => Task.FromResult(Mock.Of<IImageStatistics>())));
            var exposure = new Mock<IExposureData>();
            exposure.SetupGet(x => x.MetaData).Returns(Metadata);
            exposure.Setup(x => x.ToImageData(Progress, It.IsAny<CancellationToken>())).ReturnsAsync(Image.Object);
            Imaging.Setup(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), Progress, Intent.TargetName))
                .Callback<CaptureSequence, CancellationToken, IProgress<ApplicationStatus>, string>((capture, token, progress, name) =>
                {
                    Assert.Equal(CapturePhase.Capturing, Read().Phase);
                    Assert.Equal(Intent.ExposureSeconds, capture.ExposureTime);
                    Assert.Equal(Intent.Gain, capture.Gain);
                    Assert.Equal(Intent.Offset, capture.Offset);
                    Assert.Null(capture.FilterType);
                    Assert.Equal(CaptureSequence.ImageTypes.LIGHT, capture.ImageType);
                    Clock.Advance(200);
                }).ReturnsAsync(exposure.Object);
            Imaging.Setup(x => x.PrepareImage(Image.Object, It.IsAny<PrepareImageParameters>(), It.IsAny<CancellationToken>()))
                .Callback(() => Clock.Advance(300)).ReturnsAsync(Mock.Of<IRenderedImage>());
            Saves.Setup(x => x.Enqueue(It.IsAny<IImageData>(), It.IsAny<Task<IRenderedImage>>(), Progress, It.IsAny<CancellationToken>()))
                .Callback<IImageData, Task<IRenderedImage>, IProgress<ApplicationStatus>, CancellationToken>((image, prepared, progress, token) =>
                {
                    EnqueuedImage = image;
                    Enqueued.TrySetResult();
                }).Returns(Task.CompletedTask);
            adapter = new(profiles.Object, camera.Object, Imaging.Object, Saves.Object, Mock.Of<IImageHistoryVM>(),
                Root, timeout ?? TimeSpan.FromSeconds(5), Clock);
            BoundCapture = new(Equipment, camera.Object, wheel.Object, adapter);
        }

        public Task<CaptureEvidence> Run(Func<CancellationToken, Task>? authorize = null, CancellationToken token = default) =>
            adapter.CaptureAsync(Intent, NativeDispatchTest.After(authorize), Progress, token);
        public Task<CaptureEvidence> RunQuality(bool probe)
        {
            Profile.SetupGet(x => x.ImageSettings).Returns(Mock.Of<IImageSettings>());
            Image.SetupGet(x => x.Properties).Returns(new ImageProperties(1000, 1000, 16, false, 10, 20));
            var stats = Mock.Of<IImageStatistics>(x => x.BitDepth == 16 && x.Median == 1000);
            Image.SetupGet(x => x.Statistics).Returns(new AsyncLazy<IImageStatistics>(() => Task.FromResult(stats)));
            Image.SetupGet(x => x.StarDetectionAnalysis).Returns(Mock.Of<IStarDetectionAnalysis>(x => x.HFR == 2 && x.DetectedStars == 100
                && x.Eccentricity == 0.4 && x.HFRUnit == StarMeasurementUnit.Pixels));
            adapter.CollectQuality = true;
            return adapter.CaptureAsync(Intent with
            {
                QualityProbe = probe,
                Program = new(Guid.NewGuid().ToString("D"), "target", "recipe", "L", 0)
            },
                NativeDispatchTest.After(null), Progress, CancellationToken.None);
        }
        public Task<CaptureEvidence?> Reconcile(string? id = null) => adapter.ReconcileInterruptedAsync(id ?? Intent.CaptureId.ToString("D"), CancellationToken.None);
        public void Acknowledge() => adapter.AcknowledgeInterrupted(Intent.CaptureId.ToString("D"));
        public void DisposeAdapter() => adapter.Dispose();
        public CaptureEvidence Read() => CaptureJournal.Read(Path.Combine(Root, Intent.ProfileId.ToString("N"), $"{Intent.CaptureId:N}.json"));
        public void Saved(ImageMetaData? metadata = null) => Saves.Raise(x => x.ImageSaved += null!, Saves.Object,
            new ImageSavedEventArgs { MetaData = metadata ?? Metadata, PathToImage = new Uri(ImagePath) });
        public void AssertDetached()
        {
            Saves.VerifyRemove(x => x.ImageSaved -= It.IsAny<EventHandler<ImageSavedEventArgs>>(), Times.Once);
            Saves.VerifyRemove(x => x.ImageSaveFailed -= It.IsAny<Func<object, ImageSaveFailedEventArgs, Task>>(), Times.Once);
        }
        public void Dispose() { adapter.Dispose(); Directory.Delete(Root, recursive: true); }
    }
}
