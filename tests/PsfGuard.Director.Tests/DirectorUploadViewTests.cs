using PsfGuard.Director.Plugin;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class DirectorUploadViewTests
{
    private static readonly DirectorUploadDestination Destination = new(new Uri("https://psf.example/"), new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

    [Theory]
    [InlineData(0, 0, 0, "No frames are queued.")]
    [InlineData(1, 0, 0, "1 frame is queued.")]
    [InlineData(3, 2, 0, "3 frames are queued; 2 frames for an earlier pairing.")]
    [InlineData(0, 1, 1, "0 frames are queued; 1 frame for an earlier pairing; 1 frame refused by PSF Guard.")]
    public void TheQueueLineCountsEarlierPairingsApartFromQueuedFrames(int queued, int earlier, int refused, string expected) =>
        Assert.Equal(expected, DirectorPlugin.DescribeQueue(new(queued, earlier, refused, null)));

    [Fact]
    public void TheStatusLineShowsTheServersReason()
    {
        Assert.Equal("Pair with PSF Guard to upload frames.", DirectorPlugin.DescribeUploads(null, new(0, 0, 0, null)));
        Assert.Equal("Image uploads are off for this database. Turn them on under Rig setup › Plugins.",
            DirectorPlugin.DescribeUploads(Destination, new(0, 0, 0,
                new(DirectorUploadStateKind.Disabled, "Image uploads are off for this database. Turn them on under Rig setup › Plugins."))));
        Assert.Contains("asks again every 10 minutes",
            DirectorPlugin.DescribeUploads(Destination, new(0, 0, 0, new(DirectorUploadStateKind.Unsupported, "PSF Guard answered HTTP 404."))));
    }
}
