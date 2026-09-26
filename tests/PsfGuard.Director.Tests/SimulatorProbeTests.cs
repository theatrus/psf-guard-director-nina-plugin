using System.Runtime.Serialization;
using System.Text.Json;
using NINA.Profile;
using PsfGuard.Director.SimulatorProbe;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class SimulatorProbeTests
{
    [Fact]
    public void FixtureDeserializesWithNativeDefaultsAndAcceptsOnlyOwnedRoot()
    {
        using var f = new Fixture();
        Assert.Equal(f.Root, f.Validate());
        Assert.Throws<InvalidOperationException>(() => SimulatorSequence.ValidateEnvironment(null, f.Token, f.Root, f.Profile));
        Assert.Throws<InvalidOperationException>(() => SimulatorSequence.ValidateEnvironment(f.Root, Guid.NewGuid().ToString("N"), f.Root, f.Profile));
        Assert.Throws<InvalidOperationException>(() => SimulatorSequence.ValidateEnvironment(f.Root, f.Token, Path.GetTempPath(), f.Profile));
        File.WriteAllText(Path.Combine(f.Root, "isolation-ready.txt"), "not-isolated");
        Assert.Throws<InvalidOperationException>(() => f.Validate());
    }

    [Theory]
    [InlineData("camera")]
    [InlineData("mount")]
    [InlineData("filter")]
    [InlineData("focuser")]
    [InlineData("guider")]
    [InlineData("output")]
    [InlineData("pattern")]
    [InlineData("profile")]
    public void RefusesChangedProfileBeforeDispatch(string change)
    {
        using var f = new Fixture();
        switch (change)
        {
            case "camera": f.Profile.CameraSettings.Id = "ASCOM.Real.Camera"; break;
            case "mount": f.Profile.TelescopeSettings.Id = "ASCOM.Real.Telescope"; break;
            case "filter": f.Profile.FilterWheelSettings.Id = "ASCOM.Real.FilterWheel"; break;
            case "focuser": f.Profile.FocuserSettings.Id = "ASCOM.Real.Focuser"; break;
            case "guider": f.Profile.GuiderSettings.GuiderName = "PHD2"; break;
            case "output": f.Profile.ImageFileSettings.FilePath = Path.GetTempPath(); break;
            case "pattern": f.Profile.ImageFileSettings.FilePattern = "../escape"; break;
            case "profile": f.Profile.Name = "Live profile"; break;
        }
        Assert.Throws<InvalidOperationException>(() => f.Validate());
    }

    [Fact]
    public void SequenceHasThreeNativeSectionsAndContainerDiscriminators()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "smoke.sequence.json")));
        var sections = doc.RootElement.GetProperty("Items").GetProperty("$values");
        Assert.Equal(3, sections.GetArrayLength());
        foreach (var section in sections.EnumerateArray())
            Assert.Contains("SequentialStrategy", section.GetProperty("Strategy").GetProperty("$type").GetString());
        var item = sections[1].GetProperty("Items").GetProperty("$values")[0];
        Assert.Contains(nameof(SimulatorSequence), item.GetProperty("$type").GetString());
        Assert.Equal(1, item.GetProperty("Attempts").GetInt32());
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"nina-smoke-{Guid.NewGuid():N}");
        internal string Token { get; } = Guid.NewGuid().ToString("N");
        internal Profile Profile { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, ".director-test-root"), Token);
            File.WriteAllText(Path.Combine(Root, "isolation-ready.txt"), Root);
            using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "ascom.profile.xml"));
            Profile = (Profile)new DataContractSerializer(typeof(Profile)).ReadObject(input)!;
            Profile.ImageFileSettings.FilePath = Path.Combine(Root, "images");
        }
        internal string Validate() => SimulatorSequence.ValidateEnvironment(Root, Token, Root, Profile);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
