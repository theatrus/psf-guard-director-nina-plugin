using Moq;
using Newtonsoft.Json;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Plugin.Sequencer;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class DirectorUploadSettingsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"CheckInMode\":\"Live\"}")]
    [InlineData("{\"CheckInMode\":\"Deferred\"}")]
    public void NewAndExistingSequencesDoNotEnableFileUploads(string savedOptions)
    {
        var pluginId = new Guid("03a1d13e-67eb-4e24-a407-82bce7e576a5");
        var settings = new NINA.Profile.PluginSettings();
        var profile = new Mock<IProfile>();
        profile.SetupGet(p => p.Id).Returns(Guid.NewGuid());
        profile.SetupGet(p => p.PluginSettings).Returns(settings);
        var profiles = new Mock<IProfileService>();
        profiles.SetupGet(p => p.ActiveProfile).Returns(profile.Object);
        var options = JsonConvert.DeserializeObject<DirectorSessionOptions>(savedOptions)!;
        var mode = options.CheckInMode;
        var plugin = new DirectorPlugin(profiles.Object);

        Assert.False(plugin.UploadCapturedFrames);
        plugin.UploadCapturedFrames = true;
        Assert.True(settings.TryGetValue(pluginId, DirectorImageUploads.SettingKey, out bool stored));
        Assert.True(stored);
        Assert.True(new DirectorPlugin(profiles.Object).UploadCapturedFrames);
        Assert.Equal(mode, options.CheckInMode);

        plugin.UploadCapturedFrames = false;
        Assert.False(new DirectorPlugin(profiles.Object).UploadCapturedFrames);
        Assert.Equal(mode, options.CheckInMode);

        var other = new Mock<IProfile>();
        other.SetupGet(p => p.Id).Returns(Guid.NewGuid());
        other.SetupGet(p => p.PluginSettings).Returns(new NINA.Profile.PluginSettings());
        profiles.SetupGet(p => p.ActiveProfile).Returns(other.Object);
        Assert.False(new DirectorPlugin(profiles.Object).UploadCapturedFrames);
    }
}
