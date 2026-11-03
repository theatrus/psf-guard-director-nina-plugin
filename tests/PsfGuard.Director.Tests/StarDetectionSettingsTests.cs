using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using NINA.Core.Enum;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class StarDetectionSettingsTests
{
    private static readonly CoordinatorBinding Binding = CoordinatorProgramTests.Binding;
    private const string Token = "psfdrc_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string HocusFocusDetector = "NINA.Joko.Plugins.HocusFocus.StarDetection.HocusFocusStarDetection";
    private static readonly Guid DirectorPluginId = new("03a1d13e-67eb-4e24-a407-82bce7e576a5");

    // The Hocus Focus block of a real C925 profile, with the user name in the
    // save paths replaced.
    private static Dictionary<string, object?> HocusFocusBlock() => new()
    {
        ["IntermediateSavePath"] = @"C:\Users\observer\AppData\Local\NINA\HocusFocusIntermediate",
        ["NoiseReductionRadius"] = 4,
        ["AutoFocusTimeoutSeconds"] = 1800,
        ["Simple_FocusRange"] = "WideRange",
        ["StructureLayers"] = 6,
        ["BrightnessSensitivity"] = 1.2000000000000002,
        ["Simple_PixelScale"] = "LongFocalLength",
        ["MinStarBoundingBoxSize"] = 6,
        ["Save"] = false,
        ["SavePath"] = @"C:\Users\observer\Documents\N.I.N.A\HocusFocus",
        ["KeepFramesForReview"] = true,
        ["UseAdvanced"] = true,
        ["LastInFocusHfrPixels"] = 5.214123060870589,
        ["LastInFocusHfrMeasuredAtUtc"] = 639228231542427906L,
    };

    private static IProfile Profile(NINA.Profile.PluginSettings plugins, double focalLength = 2350, double pixelSize = 3.76,
        IReadOnlyDictionary<string, string>? behaviors = null)
    {
        var image = new Mock<IImageSettings>();
        image.SetupGet(s => s.StarSensitivity).Returns(StarSensitivityEnum.High);
        image.SetupGet(s => s.NoiseReduction).Returns(NoiseReductionEnum.None);
        image.SetupGet(s => s.AutoStretchFactor).Returns(0.2);
        image.SetupGet(s => s.BlackClipping).Returns(-2.8);
        var focuser = new Mock<IFocuserSettings>();
        focuser.SetupGet(s => s.AutoFocusInnerCropRatio).Returns(1);
        focuser.SetupGet(s => s.AutoFocusOuterCropRatio).Returns(1);
        focuser.SetupGet(s => s.AutoFocusUseBrightestStars).Returns(30);
        var telescope = new Mock<ITelescopeSettings>();
        telescope.SetupGet(s => s.FocalLength).Returns(focalLength);
        var camera = new Mock<ICameraSettings>();
        camera.SetupGet(s => s.PixelSize).Returns(pixelSize);
        var application = new Mock<IApplicationSettings>();
        application.SetupGet(s => s.SelectedPluggableBehaviorsLookup).Returns(behaviors
            ?? new Dictionary<string, string> { [StarDetectionProfileReader.StarDetectionBehavior] = HocusFocusDetector });
        var profile = new Mock<IProfile>();
        profile.SetupGet(p => p.Id).Returns(Binding.ProfileId);
        profile.SetupGet(p => p.Name).Returns("C925");
        profile.SetupGet(p => p.ImageSettings).Returns(image.Object);
        profile.SetupGet(p => p.FocuserSettings).Returns(focuser.Object);
        profile.SetupGet(p => p.TelescopeSettings).Returns(telescope.Object);
        profile.SetupGet(p => p.CameraSettings).Returns(camera.Object);
        profile.SetupGet(p => p.ApplicationSettings).Returns(application.Object);
        profile.SetupGet(p => p.PluginSettings).Returns(plugins);
        return profile.Object;
    }

    private static NINA.Profile.PluginSettings C925Plugins()
    {
        var plugins = new NINA.Profile.PluginSettings();
        foreach (var (key, value) in HocusFocusBlock())
        {
            switch (value)
            {
                case int number: plugins.SetValue(HocusFocusSettings.PluginId, key, number); break;
                case long number: plugins.SetValue(HocusFocusSettings.PluginId, key, number); break;
                case double number: plugins.SetValue(HocusFocusSettings.PluginId, key, number); break;
                case bool flag: plugins.SetValue(HocusFocusSettings.PluginId, key, flag); break;
                case string text: plugins.SetValue(HocusFocusSettings.PluginId, key, text); break;
            }
        }
        // Director's own block holds the server URL. A Hocus Focus key name in
        // another plugin's block must not be read.
        plugins.SetValue(DirectorPluginId, "CoordinatorUrl", "https://observer.example/");
        plugins.SetValue(DirectorPluginId, "NoiseReductionRadius", 99);
        return plugins;
    }

    private static StarDetectionSettingsReport Report(IReadOnlyDictionary<string, object>? hocusFocus = null) => new()
    {
        ProfileId = Binding.ProfileId,
        ProfileName = "C925",
        CapturedAt = new DateTimeOffset(2026, 11, 2, 21, 0, 0, TimeSpan.Zero),
        NinaVersion = "3.2.0.9001",
        HocusFocusVersion = null,
        Settings = new StarDetectionSettings
        {
            StarDetector = "hocus_focus",
            StarSensitivity = "high",
            NoiseReduction = "none",
            AutoStretchFactor = 0.2,
            BlackClipping = -2.8,
            AutofocusInnerCropRatio = 1,
            AutofocusOuterCropRatio = 1,
            AutofocusBrightestStars = 30,
            FocalLengthMm = 2350,
            PixelSizeUm = null,
            HocusFocus = hocusFocus ?? HocusFocusSettings.Select(HocusFocusBlock().TryGetValue),
        },
    };

    [Fact]
    public void ReadsTheStarDetectionSettingsOfAC925Profile()
    {
        var report = StarDetectionProfileReader.Read(Profile(C925Plugins()), "3.2.0.9001", "2.4.0.0",
            new DateTimeOffset(2026, 11, 2, 13, 0, 0, TimeSpan.FromHours(-8)));

        Assert.Equal(Binding.ProfileId, report.ProfileId);
        Assert.Equal("C925", report.ProfileName);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 21, 0, 0, TimeSpan.Zero), report.CapturedAt);
        Assert.Equal(TimeSpan.Zero, report.CapturedAt.Offset);
        Assert.Equal("3.2.0.9001", report.NinaVersion);
        Assert.Equal("2.4.0.0", report.HocusFocusVersion);
        var settings = report.Settings;
        Assert.Equal("hocus_focus", settings.StarDetector);
        Assert.Equal("high", settings.StarSensitivity);
        Assert.Equal("none", settings.NoiseReduction);
        Assert.Equal(0.2, settings.AutoStretchFactor);
        Assert.Equal(-2.8, settings.BlackClipping);
        Assert.Equal(1, settings.AutofocusInnerCropRatio);
        Assert.Equal(1, settings.AutofocusOuterCropRatio);
        Assert.Equal(30, settings.AutofocusBrightestStars);
        Assert.Equal(2350, settings.FocalLengthMm);
        Assert.Equal(3.76, settings.PixelSizeUm);
        Assert.Equal(new Dictionary<string, object>
        {
            ["BrightnessSensitivity"] = 1.2000000000000002,
            ["MinStarBoundingBoxSize"] = 6L,
            ["NoiseReductionRadius"] = 4L,
            ["Simple_FocusRange"] = "WideRange",
            ["Simple_PixelScale"] = "LongFocalLength",
            ["StructureLayers"] = 6L,
            ["UseAdvanced"] = true,
        }, report.Settings.HocusFocus);
    }

    [Fact]
    public void ReportsTheBuiltInDetectorAndLeavesOutMissingOptics()
    {
        var report = StarDetectionProfileReader.Read(Profile(new NINA.Profile.PluginSettings(), double.NaN, 0, new Dictionary<string, string>()),
            "3.2.0.9001", null, DateTimeOffset.UtcNow);

        Assert.Equal("nina", report.Settings.StarDetector);
        Assert.Null(report.Settings.FocalLengthMm);
        Assert.Null(report.Settings.PixelSizeUm);
        Assert.Null(report.HocusFocusVersion);
        Assert.Empty(report.Settings.HocusFocus);
        var settings = JsonNode.Parse(JsonSerializer.Serialize(report, StarDetectionJson.Options))!["settings"]!.AsObject();
        Assert.False(settings.ContainsKey("focal_length_mm"));
        Assert.False(settings.ContainsKey("pixel_size_um"));
    }

    [Fact]
    public void SendingIsAPerProfileSwitchThatDefaultsOn()
    {
        var plugins = new NINA.Profile.PluginSettings();
        plugins.SetValue(DirectorPluginId, "CoordinatorUrl", "https://psf-guard.example/");
        var profile = new Mock<IProfile>();
        profile.SetupGet(p => p.Id).Returns(Binding.ProfileId);
        profile.SetupGet(p => p.PluginSettings).Returns(plugins);
        var profiles = new Mock<IProfileService>();
        profiles.SetupGet(p => p.ActiveProfile).Returns(profile.Object);
        var plugin = new DirectorPlugin(profiles.Object);

        Assert.True(plugin.SendStarDetectionSettings);
        plugin.SendStarDetectionSettings = false;

        Assert.False(plugin.SendStarDetectionSettings);
        Assert.True(plugins.TryGetValue(DirectorPluginId, DirectorPlugin.SendStarDetectionSettingsKey, out bool stored));
        Assert.False(stored);
        Assert.True(plugins.TryGetValue(DirectorPluginId, "CoordinatorUrl", out string url));
        Assert.Equal("https://psf-guard.example/", url);
        plugin.SendStarDetectionSettings = true;
        Assert.True(plugin.SendStarDetectionSettings);
    }

    [Fact]
    public void HocusFocusSelectionKeepsOnlyAllowlistedKeys()
    {
        var selected = HocusFocusSettings.Select(HocusFocusBlock().TryGetValue);

        Assert.Equal(new[] { "BrightnessSensitivity", "MinStarBoundingBoxSize", "NoiseReductionRadius", "Simple_FocusRange",
            "Simple_PixelScale", "StructureLayers", "UseAdvanced" }, selected.Keys);
        var everyKey = HocusFocusSettings.Keys.ToDictionary(key => key, object? (_) => 1);
        Assert.Equal(HocusFocusSettings.Keys.Order(StringComparer.Ordinal), HocusFocusSettings.Select(everyKey.TryGetValue).Keys);
        Assert.Equal(35, HocusFocusSettings.Keys.Count);
    }

    [Fact]
    public void HocusFocusSelectionLeavesOutValuesTheServerWouldRefuse()
    {
        var block = new Dictionary<string, object?>
        {
            ["MinHFR"] = double.NaN,
            ["MaxDistortion"] = null,
            ["OptimizedSettingsJson"] = new string('x', HocusFocusSettings.MaxValueChars + 1),
            ["StructureLayers"] = 6,
        };

        Assert.Equal(new[] { "StructureLayers" }, HocusFocusSettings.Select(block.TryGetValue).Keys);
    }

    [Theory]
    [InlineData(null, "nina")]
    [InlineData(" ", "nina")]
    [InlineData(HocusFocusDetector, "hocus_focus")]
    [InlineData("Other.Plugin.StarDetection", "Other.Plugin.StarDetection")]
    public void DetectorNamesMatchTheServer(string? selected, string expected) =>
        Assert.Equal(expected, StarDetectionSettings.DetectorName(selected));

    [Fact]
    public void HocusFocusValuesKeepTheirKindAndDropNonFiniteNumbers()
    {
        Assert.Equal(3L, HocusFocusSettings.Normalize((short)3));
        Assert.Equal(0.5, HocusFocusSettings.Normalize(0.5f));
        Assert.Null(HocusFocusSettings.Normalize(double.NaN));
        Assert.Null(HocusFocusSettings.Normalize(double.PositiveInfinity));
        Assert.Equal("text", HocusFocusSettings.Normalize("text"));
        Assert.Equal(false, HocusFocusSettings.Normalize(false));
    }

    [Fact]
    public void ReportCarriesNoPathsHistoryOrServerAddress()
    {
        var report = StarDetectionProfileReader.Read(Profile(C925Plugins()), "3.2.0.9001", null, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(report, StarDetectionJson.Options);

        Assert.DoesNotContain("observer", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SavePath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("LastInFocusHfr", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoFocusTimeoutSeconds", json, StringComparison.Ordinal);
        Assert.DoesNotContain("psfdrc_", json, StringComparison.Ordinal);
        Assert.Equal(4, JsonNode.Parse(json)!["settings"]!["hocus_focus"]!["NoiseReductionRadius"]!.GetValue<long>());
    }

    [Fact]
    public void ReportUsesTheServersProfileSettingsShape()
    {
        var root = JsonNode.Parse(JsonSerializer.Serialize(Report(), StarDetectionJson.Options))!.AsObject();

        Assert.Equal(new[] { "protocol_version", "profile_id", "profile_name", "captured_at", "nina_version", "hocus_focus_version", "settings" },
            root.Select(p => p.Key));
        Assert.Equal(1, root["protocol_version"]!.GetValue<int>());
        Assert.Equal(Binding.ProfileId.ToString("D"), root["profile_id"]!.GetValue<string>());
        Assert.Equal("2026-11-02T21:00:00+00:00", root["captured_at"]!.GetValue<string>());
        Assert.Null(root["hocus_focus_version"]);
        var settings = root["settings"]!.AsObject();
        Assert.Equal(new[] { "star_detector", "star_sensitivity", "noise_reduction", "auto_stretch_factor", "black_clipping",
            "autofocus_inner_crop_ratio", "autofocus_outer_crop_ratio", "autofocus_brightest_stars", "focal_length_mm", "hocus_focus" },
            settings.Select(p => p.Key));
        Assert.Equal("hocus_focus", settings["star_detector"]!.GetValue<string>());
        Assert.Equal("high", settings["star_sensitivity"]!.GetValue<string>());
        Assert.Equal(30, settings["autofocus_brightest_stars"]!.GetValue<int>());
        Assert.Equal(2350, settings["focal_length_mm"]!.GetValue<double>());
        var hocusFocus = root["settings"]!["hocus_focus"]!;
        Assert.Equal(4, hocusFocus["NoiseReductionRadius"]!.GetValue<long>());
        Assert.True(hocusFocus["UseAdvanced"]!.GetValue<bool>());
    }

    [Fact]
    public void FingerprintIgnoresTheReadTimeOnly()
    {
        var report = Report();

        Assert.Equal(StarDetectionSettingsReporter.Fingerprint(report),
            StarDetectionSettingsReporter.Fingerprint(report with { CapturedAt = report.CapturedAt.AddHours(1) }));
        Assert.NotEqual(StarDetectionSettingsReporter.Fingerprint(report), StarDetectionSettingsReporter.Fingerprint(report with
        {
            Settings = report.Settings with { StarSensitivity = "highest" }
        }));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static readonly string Ack = $$"""{"success":true,"data":{"profile_id":"{{Binding.ProfileId:D}}","stored":true},"error":null,"status":"ready"}""";
    private static CoordinatorStarDetectionClient Client(Handler handler) =>
        new(CoordinatorProgramTests.Endpoint, Binding, _ => ValueTask.FromResult<string?>(Token), handler);

    [Fact]
    public async Task ClientPostsTheReportToThePairedRig()
    {
        string? body = null;
        using var client = Client(new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/prefix/api/director/v1/rigs/{Binding.RigId:D}/star-detection-settings", request.RequestUri!.AbsolutePath);
            Assert.Equal(Binding.ProfileId.ToString("D"), request.Headers.GetValues("X-PSF-Director-Profile").Single());
            Assert.Equal(Token, request.Headers.Authorization!.Parameter);
            body = await request.Content!.ReadAsStringAsync(token);
            return Response(Ack);
        }));

        await client.SendAsync(Report(), CancellationToken.None);

        Assert.Equal(JsonSerializer.Serialize(Report(), StarDetectionJson.Options), body);
        Assert.DoesNotContain(Token, body!, StringComparison.Ordinal);
        // The server takes the catalog from the rig's pairing, not the body.
        Assert.DoesNotContain("catalog_id", body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientRefusesAnotherProfilesSettingsWithoutSending()
    {
        using var client = Client(new Handler((_, _) => throw new InvalidOperationException("Must not send.")));

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(Report() with { ProfileId = Guid.NewGuid() }, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, CoordinatorIntakeFailure.NotFound, true, false)]
    [InlineData(HttpStatusCode.BadRequest, CoordinatorIntakeFailure.UnexpectedStatus, false, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CoordinatorIntakeFailure.Busy, false, false)]
    [InlineData(HttpStatusCode.Unauthorized, CoordinatorIntakeFailure.AuthenticationRequired, false, false)]
    [InlineData(HttpStatusCode.Forbidden, CoordinatorIntakeFailure.Forbidden, false, false)]
    public async Task ClientReportsRefusals(HttpStatusCode status, CoordinatorIntakeFailure failure, bool missingRoute, bool refused)
    {
        using var client = Client(new Handler((_, _) => Task.FromResult(Response("""{"success":false}""", status))));

        var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.SendAsync(Report(), CancellationToken.None));
        Assert.Equal(failure, error.Failure);
        Assert.Equal(missingRoute, CoordinatorStarDetectionClient.IsMissingRoute(error));
        Assert.Equal(refused, CoordinatorStarDetectionClient.IsRefused(error));
    }

    [Theory]
    [InlineData("""{"success":false,"data":null,"error":"refused","status":"ready"}""")]
    [InlineData("""{"success":true,"data":{},"error":null,"status":"pending"}""")]
    [InlineData("""{"success":true,"data":{},"error":null}""")]
    [InlineData("""{"success":true,"data":{},"error":null,"status":"ready","status":"ready"}""")]
    public async Task ClientFailsClosedOnMalformedAcknowledgements(string json)
    {
        using var client = Client(new Handler((_, _) => Task.FromResult(Response(json))));

        Assert.Equal(CoordinatorIntakeFailure.InvalidAcknowledgement,
            (await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.SendAsync(Report(), CancellationToken.None))).Failure);
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 2, 21, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ReporterFixture
    {
        private int requests;
        private int captures;
        public ReporterFixture() : this(StarDetectionReporterTiming.Default, null) { }
        public ReporterFixture(StarDetectionReporterTiming timing, TimeProvider? time)
        {
            Capture = () => Current;
            Reporter = new(() => { Interlocked.Increment(ref captures); return Capture(); }, (capture, _) =>
            {
                var count = Interlocked.Increment(ref requests);
                LastDestination = capture.DestinationKey;
                var failure = Failure;
                OnRequest?.Invoke(count);
                return failure is null ? Task.CompletedTask : Task.FromException(new CoordinatorIntakeException(failure.Value));
            }, CoordinatorStarDetectionClient.IsMissingRoute, CoordinatorStarDetectionClient.IsRefused,
            line => { lock (Log) Log.Add(line); }, timing, time ?? Clock);
        }
        public StarDetectionSettingsReporter Reporter { get; }
        public FakeClock Clock { get; } = new();
        public StarDetectionSettingsCapture? Current { get; set; }
        public Func<StarDetectionSettingsCapture?> Capture { get; set; }
        public CoordinatorIntakeFailure? Failure { get; set; }
        public Action<int>? OnRequest { get; set; }
        public string? LastDestination { get; private set; }
        public List<string> Log { get; } = [];
        public int Requests => Volatile.Read(ref requests);
        public int Captures => Volatile.Read(ref captures);
    }

    private static StarDetectionSettingsCapture Capture(string destination = "rig-a") => new(destination, Report());

    [Fact]
    public async Task ReporterSendsOnlyWhenTheSettingsChange()
    {
        var fixture = new ReporterFixture { Current = Capture() };
        var report = fixture.Current.Report;

        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));
        fixture.Current = fixture.Current with { Report = report with { CapturedAt = report.CapturedAt.AddMinutes(1) } };
        Assert.Equal(StarDetectionSendResult.Unchanged, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(1, fixture.Requests);

        fixture.Current = fixture.Current with
        {
            Report = report with
            {
                Settings = report.Settings with
                {
                    HocusFocus = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["NoiseReductionRadius"] = 3L }
                }
            }
        };
        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task ReporterSendsTheSameSettingsToANewPairing()
    {
        var fixture = new ReporterFixture { Current = Capture() };
        await fixture.Reporter.SendIfChangedAsync(default);

        fixture.Current = Capture("rig-b");

        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal("rig-b", fixture.LastDestination);
    }

    [Fact]
    public async Task ReporterSendsWhileOn()
    {
        var fixture = new ReporterFixture { Current = Capture() };

        Assert.True(fixture.Reporter.Enabled);
        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(1, fixture.Requests);
    }

    [Fact]
    public async Task ReporterReadsAndSendsNothingWhileOff()
    {
        var fixture = new ReporterFixture { Current = Capture() };
        fixture.Reporter.Enabled = false;

        Assert.Equal(StarDetectionSendResult.Disabled, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(0, fixture.Captures);
        Assert.Equal(0, fixture.Requests);
    }

    [Fact]
    public async Task TurningSendingBackOnSendsTheSameSettingsOnce()
    {
        var fixture = new ReporterFixture { Current = Capture() };
        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));

        fixture.Reporter.Enabled = false;
        Assert.Equal(StarDetectionSendResult.Disabled, await fixture.Reporter.SendIfChangedAsync(default));
        fixture.Reporter.Enabled = true;

        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(StarDetectionSendResult.Unchanged, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task TurningSendingBackOnEndsTheMissingRouteHold()
    {
        var fixture = new ReporterFixture { Current = Capture(), Failure = CoordinatorIntakeFailure.NotFound };
        Assert.Equal(StarDetectionSendResult.Unsupported, await fixture.Reporter.SendIfChangedAsync(default));

        fixture.Reporter.Enabled = false;
        fixture.Reporter.Enabled = true;
        fixture.Failure = null;

        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task BackgroundWorkerWaitsWhileOffAndSendsOnceTurnedOn()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new ReporterFixture(StarDetectionReporterTiming.Default with
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            SettleDelay = TimeSpan.FromMilliseconds(10)
        }, TimeProvider.System)
        { Current = Capture() };
        fixture.OnRequest = _ => sent.TrySetResult();
        fixture.Reporter.Enabled = false;

        fixture.Reporter.Start();
        fixture.Reporter.RequestSend();
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, fixture.Captures);
        Assert.Equal(0, fixture.Requests);

        fixture.Reporter.Enabled = true;
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Reporter.DisposeAsync();

        Assert.Equal(1, fixture.Requests);
    }

    [Fact]
    public async Task ReporterDoesNothingWithoutAPairing()
    {
        var fixture = new ReporterFixture();

        Assert.Equal(StarDetectionSendResult.NotPaired, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(0, fixture.Requests);
    }

    [Fact]
    public async Task ReporterRetriesAfterAFailedSend()
    {
        var fixture = new ReporterFixture { Current = Capture(), Failure = CoordinatorIntakeFailure.Transport };

        Assert.Equal(StarDetectionSendResult.Failed, await fixture.Reporter.SendIfChangedAsync(default));
        fixture.Failure = null;
        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task ReporterTreatsAReadFailureAsAFailedSend()
    {
        var fixture = new ReporterFixture { Capture = () => throw new InvalidOperationException("profile changed") };

        Assert.Equal(StarDetectionSendResult.Failed, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Contains(fixture.Log, line => line.Contains("profile changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReporterWaitsBeforeAskingAServerWithoutTheRouteAgain()
    {
        var fixture = new ReporterFixture { Current = Capture(), Failure = CoordinatorIntakeFailure.NotFound };

        Assert.Equal(StarDetectionSendResult.Unsupported, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(StarDetectionSendResult.Unsupported, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(1, fixture.Requests);

        fixture.Clock.Now += TimeSpan.FromHours(7);
        fixture.Failure = null;
        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task ReporterSendsRefusedSettingsAgainOnlyOnceTheyChange()
    {
        var fixture = new ReporterFixture { Current = Capture(), Failure = CoordinatorIntakeFailure.UnexpectedStatus };
        var report = fixture.Current.Report;

        Assert.Equal(StarDetectionSendResult.Refused, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(StarDetectionSendResult.Refused, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(1, fixture.Requests);

        fixture.Failure = null;
        fixture.Current = fixture.Current with { Report = report with { ProfileName = "C925 fixed" } };
        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task TurningSendingBackOnResendsRefusedSettings()
    {
        var fixture = new ReporterFixture { Current = Capture(), Failure = CoordinatorIntakeFailure.UnexpectedStatus };
        Assert.Equal(StarDetectionSendResult.Refused, await fixture.Reporter.SendIfChangedAsync(default));

        fixture.Reporter.Enabled = false;
        fixture.Reporter.Enabled = true;
        fixture.Failure = null;

        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public void RetryDelayDoublesUpToTheLimit()
    {
        var first = TimeSpan.FromSeconds(30);
        var max = TimeSpan.FromMinutes(30);

        Assert.Equal(first, StarDetectionSettingsReporter.RetryDelay(1, first, max));
        Assert.Equal(TimeSpan.FromMinutes(1), StarDetectionSettingsReporter.RetryDelay(2, first, max));
        Assert.Equal(TimeSpan.FromMinutes(16), StarDetectionSettingsReporter.RetryDelay(6, first, max));
        Assert.Equal(max, StarDetectionSettingsReporter.RetryDelay(7, first, max));
        Assert.Equal(max, StarDetectionSettingsReporter.RetryDelay(500, first, max));
    }

    [Fact]
    public async Task BackgroundWorkerSendsAtStartAndRetriesAFailure()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new ReporterFixture(new StarDetectionReporterTiming(TimeSpan.FromMinutes(10), TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromHours(6), TimeSpan.FromSeconds(10)), TimeProvider.System)
        { Current = Capture(), Failure = CoordinatorIntakeFailure.ServerUnavailable };
        fixture.OnRequest = count => { if (count == 1) fixture.Failure = null; else sent.TrySetResult(); };

        fixture.Reporter.Start();
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Reporter.DisposeAsync();

        Assert.Equal(2, fixture.Requests);
        Assert.Contains(fixture.Log, line => line.StartsWith("Sent star-detection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackgroundWorkerChecksAgainOnRequest()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new ReporterFixture(StarDetectionReporterTiming.Default with { SettleDelay = TimeSpan.FromMilliseconds(10) }, TimeProvider.System)
        { Current = Capture() };
        fixture.OnRequest = count => (count == 1 ? first : second).TrySetResult();

        fixture.Reporter.Start();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Current = fixture.Current with { Report = fixture.Current.Report with { ProfileName = "C925 renamed" } };
        fixture.Reporter.RequestSend();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Reporter.DisposeAsync();

        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task DisposingTheReporterStopsAnInFlightSendWithoutThrowing()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reporter = new StarDetectionSettingsReporter(() => Capture(), async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, _ => false, _ => false, _ => { }, StarDetectionReporterTiming.Default with { SettleDelay = TimeSpan.Zero }, TimeProvider.System);

        reporter.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await reporter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
