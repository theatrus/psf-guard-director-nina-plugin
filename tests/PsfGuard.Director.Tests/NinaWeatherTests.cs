using Newtonsoft.Json;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaWeatherTests
{
    [Fact]
    public void PolicyIsOptInSavedAndValidated()
    {
        var old = JsonConvert.DeserializeObject<DirectorSessionOptions>("{}")!;
        Assert.Equal(DirectorWeatherPolicy.StopForNight, old.Weather);
        var options = new DirectorSessionOptions { Weather = DirectorWeatherPolicy.HoldAndResume, StableSafeSeconds = 300, MaximumWeatherMinutes = 60, MaximumWeatherInterruptions = 3 };
        var restored = JsonConvert.DeserializeObject<DirectorSessionOptions>(JsonConvert.SerializeObject(options))!;
        Assert.Equal(DirectorWeatherPolicy.HoldAndResume, restored.Clone().Weather);
        Assert.Equal(300, restored.StableSafeSeconds);
        Assert.Equal(60, restored.MaximumWeatherMinutes);
        Assert.Equal(3, restored.MaximumWeatherInterruptions);
        Assert.Empty(restored.ValidateSettings());
        restored.StableSafeSeconds = 3600;
        Assert.NotEmpty(restored.ValidateSettings());
    }

    [Fact]
    public void RenewedOperationGenerationCannotReviveOldWorkOrOperatorCancellation()
    {
        using var owner = new CancellationTokenSource();
        using var unsafeWeather = new CancellationTokenSource();
        using var operations = new NinaOperationLifetime(owner.Token, unsafeWeather.Token, default);
        var old = operations.Token;
        unsafeWeather.Cancel();
        operations.Renew(owner.Token, default, default);
        Assert.True(old.IsCancellationRequested);
        Assert.False(operations.Token.IsCancellationRequested);
        owner.Cancel();
        Assert.True(operations.Token.IsCancellationRequested);
        operations.Renew(owner.Token, default, default);
        Assert.True(operations.Token.IsCancellationRequested);
    }
}
