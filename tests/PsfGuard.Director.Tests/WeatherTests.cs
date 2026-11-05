using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class WeatherTests
{
    private static readonly CoordinatorBinding Binding = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly Uri Endpoint = new("https://psf.example/base/");
    private const ulong Now = 1800000000000;
    private static RigWeather View(string reported = "clear", string forecast = "clear") => new(Binding.RigId, Guid.NewGuid(), Now, 30,
        new(Binding.RigId, new(Guid.NewGuid(), Binding.ProfileId, Now, "NINA source", new string('a', 64), true, new(15, TemperatureCelsius: 7)), Now),
        new("open_meteo", "Weather data by Open-Meteo.com (CC BY 4.0)", Now - 1000, Now + 3600000,
            new(Now - 1000, Now + 3600000, 20, null, null, null), true),
        new(reported, "reported_cloud", reported == "unknown" ? 0UL : 30000UL), new(forecast, "forecast_conditions", forecast == "unknown" ? 0UL : 30000UL));

    [Fact]
    public void ReportedAndForecastCloudsRemainSeparateAndEverySelectedSourceMustBeClear()
    {
        var clock = new Clock(); var cache = new CloudWeatherCache(clock);
        cache.Store(Binding.ProfileId, Endpoint, Binding, View("cloudy", "clear"), clock.GetTimestamp(), clock.GetUtcNow());
        Assert.True(cache.Read(Binding.ProfileId, Endpoint, Binding, false, true).Clear);
        Assert.False(cache.Read(Binding.ProfileId, Endpoint, Binding, true, false).Clear);
        Assert.False(cache.Read(Binding.ProfileId, Endpoint, Binding, true, true).Clear);
        cache.Store(Binding.ProfileId, Endpoint, Binding, View("unknown", "clear"), clock.GetTimestamp(), clock.GetUtcNow());
        Assert.False(cache.Read(Binding.ProfileId, Endpoint, Binding, true, true).Clear);
        Assert.True(cache.Read(Binding.ProfileId, Endpoint, Binding, false, false).Clear);
    }

    [Fact]
    public void WeatherLeaseIncludesRequestTimeAndRejectsScopeOrClockChanges()
    {
        var clock = new Clock(); var cache = new CloudWeatherCache(clock);
        var stamp = clock.GetTimestamp(); var utc = clock.GetUtcNow();
        clock.Ms = clock.WallMs = 10000;
        cache.Store(Binding.ProfileId, Endpoint, Binding, View(), stamp, utc);
        Assert.True(cache.Read(Binding.ProfileId, Endpoint, Binding, true, true).Clear);
        Assert.False(cache.Read(Guid.NewGuid(), Endpoint, Binding, false, true).Clear);
        Assert.False(cache.Read(Binding.ProfileId, new Uri("https://other.example/"), Binding, false, true).Clear);
        Assert.False(cache.Read(Binding.ProfileId, Endpoint, Binding with { RigId = Guid.NewGuid() }, false, true).Clear);
        clock.Ms = clock.WallMs = 30000;
        Assert.False(cache.Read(Binding.ProfileId, Endpoint, Binding, false, true).Clear);
        clock.Ms = 1000; clock.WallMs = -1;
        Assert.False(cache.Read(Binding.ProfileId, Endpoint, Binding, false, true).Clear);
        cache.Clear();
        Assert.Null(cache.Latest);
    }

    [Fact]
    public void ReaderKeepsUnitsAndDoesNotInventUnsupportedValuesOrExposeConnectionStrings()
    {
        var profile = new Mock<IProfile> { DefaultValue = DefaultValue.Mock };
        profile.SetupGet(x => x.Id).Returns(Binding.ProfileId);
        const string device = "ASCOM.weather/private-connection-CANARY";
        profile.SetupGet(x => x.WeatherDataSettings.Id).Returns(device);
        var profiles = new Mock<IProfileService>(); profiles.SetupGet(x => x.ActiveProfile).Returns(profile.Object);
        using var reader = new NinaWeatherReader(profiles.Object, new Clock());
        reader.UpdateDeviceInfo(new WeatherDataInfo
        {
            DeviceId = device,
            Name = "Weather sensor",
            Connected = true,
            CloudCover = double.NaN,
            Temperature = 7.5,
            WindSpeed = 3.25,
            Humidity = 125
        });
        var read = Assert.IsType<WeatherSample>(reader.Read());
        Assert.Null(read.Values.CloudPercent);
        Assert.Null(read.Values.HumidityPercent);
        Assert.Equal(7.5, read.Values.TemperatureCelsius);
        Assert.Equal(3.25, read.Values.WindSpeedMS);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(read));
        Assert.Equal(64, read.SourceKey.Length);
        profile.SetupGet(x => x.Id).Returns(Guid.NewGuid());
        Assert.Null(reader.Read());
    }

    [Fact]
    public void ReaderOptOutAndDisconnectionCannotSupplyClearClouds()
    {
        var profile = new Mock<IProfile> { DefaultValue = DefaultValue.Mock };
        profile.SetupGet(x => x.Id).Returns(Binding.ProfileId);
        profile.SetupGet(x => x.WeatherDataSettings.Id).Returns("sensor");
        var profiles = Mock.Of<IProfileService>(x => x.ActiveProfile == profile.Object);
        var enabled = false;
        using var reader = new NinaWeatherReader(profiles, new Clock(), () => enabled);
        reader.UpdateDeviceInfo(new WeatherDataInfo { DeviceId = "sensor", Connected = true, CloudCover = 0 });
        Assert.Null(reader.Read());
        enabled = true;
        reader.UpdateDeviceInfo(new WeatherDataInfo { DeviceId = "sensor", Connected = true, CloudCover = 0 });
        Assert.Equal(0, reader.Read()!.Values.CloudPercent);
        reader.UpdateDeviceInfo(new WeatherDataInfo { Connected = false, CloudCover = 0 });
        Assert.False(reader.Read()!.Connected);
        Assert.Null(reader.Read()!.Values.CloudPercent);
    }

    [Fact]
    public void CloudHoldsDefaultOffAndRequireTheBoundedWeatherPolicy()
    {
        var options = new DirectorSessionOptions();
        Assert.False(options.UseReportedCloudCover); Assert.False(options.UseSiteCloudForecast);
        options.UseSiteCloudForecast = true;
        Assert.Contains(options.ValidateSettings(), message => message.Contains("Cloud holds require"));
        options.Weather = DirectorWeatherPolicy.HoldAndResume;
        Assert.DoesNotContain(options.ValidateSettings(), message => message.Contains("Cloud holds require"));
        Assert.True(options.Clone().UseSiteCloudForecast);
    }

    [Fact]
    public async Task ClientUsesThePairedPsfGuardConnectionForForecastsAndAcknowledgedTelemetry()
    {
        var sample = View().Reported!.Sample;
        var calls = 0;
        using var client = new CoordinatorWeatherClient(Endpoint, Binding, _ => ValueTask.FromResult<string?>("paired-token"),
            new Handler(async request =>
            {
                Assert.Equal($"/base/api/director/v1/rigs/{Binding.RigId:D}/weather", request.RequestUri!.AbsolutePath);
                Assert.Equal("paired-token", request.Headers.Authorization!.Parameter);
                Assert.Equal(Binding.ProfileId.ToString("D"), Assert.Single(request.Headers.GetValues("X-PSF-Director-Profile")));
                if (calls++ == 0) { Assert.Equal(HttpMethod.Get, request.Method); return Response(View()); }
                Assert.Equal(HttpMethod.Post, request.Method);
                var data = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                Assert.Equal(sample.SampleId.ToString("D"), data["sample_id"]!.GetValue<string>());
                Assert.Null(data["api_key"]);
                return Response(new { sample_id = sample.SampleId, profile_id = Binding.ProfileId, stored = false, weather = View() });
            }));
        Assert.Equal(Binding.RigId, (await client.ReadAsync(null, default)).RigId);
        Assert.Equal(Binding.RigId, (await client.ReadAsync(sample, default)).RigId);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("wrong-rig")]
    [InlineData("long-lease")]
    [InlineData("missing-decision")]
    [InlineData("unknown-with-lease")]
    [InlineData("clear-without-forecast")]
    public async Task ClientRejectsMalformedOrUnscopedCloudDecisions(string fault)
    {
        var root = JsonNode.Parse(JsonSerializer.Serialize(View(), CoordinatorProgramContract.Options))!;
        switch (fault)
        {
            case "wrong-rig": root["rig_id"] = Guid.NewGuid().ToString("D"); break;
            case "long-lease": root["forecast_conditions"]!["valid_for_ms"] = 30001; break;
            case "missing-decision": root.AsObject().Remove("reported_cloud"); break;
            case "unknown-with-lease": root["forecast_conditions"]!["state"] = "unknown"; break;
            case "clear-without-forecast": root["forecast"] = null; break;
        }
        using var client = new CoordinatorWeatherClient(Endpoint, Binding, _ => ValueTask.FromResult<string?>(null), new Handler(_ => Task.FromResult(Response(root))));
        Assert.Equal(CoordinatorIntakeFailure.InvalidAcknowledgement,
            (await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ReadAsync(null, default))).Failure);
    }

    [Theory]
    [InlineData("wrong-sample")]
    [InlineData("wrong-profile")]
    [InlineData("missing-stored")]
    public async Task TelemetryMustAcknowledgeTheExactSampleAndProfile(string fault)
    {
        var sample = View().Reported!.Sample;
        var data = JsonNode.Parse(JsonSerializer.Serialize(new { sample_id = sample.SampleId, profile_id = sample.ProfileId, stored = true, weather = View() }, CoordinatorProgramContract.Options))!;
        if (fault == "wrong-sample") data["sample_id"] = Guid.NewGuid().ToString("D");
        if (fault == "wrong-profile") data["profile_id"] = Guid.NewGuid().ToString("D");
        if (fault == "missing-stored") data.AsObject().Remove("stored");
        using var client = new CoordinatorWeatherClient(Endpoint, Binding, _ => ValueTask.FromResult<string?>(null), new Handler(_ => Task.FromResult(Response(data))));
        Assert.Equal(CoordinatorIntakeFailure.InvalidAcknowledgement,
            (await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ReadAsync(sample, default))).Failure);
    }

    private static HttpResponseMessage Response(object data) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { success = true, data, error = (string?)null, status = "ready" }, CoordinatorProgramContract.Options), Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task ForecastWeatherFieldsAndTheCentralDewRuleArePreserved()
    {
        var view = View();
        view = view with
        {
            DewLimitCelsius = 2,
            Forecast = view.Forecast! with { Hour = view.Forecast.Hour! with { TemperatureCelsius = 7, DewPointCelsius = 6, HumidityPercent = 93 } },
            ForecastConditions = new("dewy", "dew_limit_reached", 30000)
        };
        using var client = new CoordinatorWeatherClient(Endpoint, Binding, _ => ValueTask.FromResult<string?>(null), new Handler(_ => Task.FromResult(Response(view))));
        var result = await client.ReadAsync(null, default);
        Assert.Equal(7, result.Forecast!.Hour!.TemperatureCelsius);
        Assert.Equal(6, result.Forecast.Hour.DewPointCelsius);
        Assert.Equal(93, result.Forecast.Hour.HumidityPercent);
        var clock = new Clock(); var cache = new CloudWeatherCache(clock);
        cache.Store(Binding.ProfileId, Endpoint, Binding, result, clock.GetTimestamp(), clock.GetUtcNow());
        var held = cache.Read(Binding.ProfileId, Endpoint, Binding, false, true);
        Assert.False(held.Clear);
        Assert.Contains("dew-point limit", held.Reason);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request); }
    private sealed class Clock : TimeProvider
    {
        internal long Ms, WallMs;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds((long)Now + WallMs);
    }
}
