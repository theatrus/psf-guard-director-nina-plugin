using System.IO;
using System.Text.Json;
using NINA.Profile.Interfaces;

namespace PsfGuard.Director.SimulatorProbe;

internal sealed record Phd2Fixture(string Executable, int Instance, int Port)
{
    internal static Phd2Fixture? Read(string root)
    {
        var path = Path.Combine(root, "coordinator-fixture.json");
        if (!File.Exists(path)) return null;
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        return fixture.RootElement.TryGetProperty("Phd2", out var value) ? value.Deserialize<Phd2Fixture>() : null;
    }

    internal void Configure(IProfileService profiles)
    {
        if (Instance is < 2000 or >= 3000 || Port != 4400 + Instance - 1 || !File.Exists(Executable))
            throw new InvalidDataException("Invalid isolated PHD2 simulator binding.");
        var settings = profiles.ActiveProfile.GuiderSettings;
        settings.GuiderName = "PHD2_Single";
        settings.PHD2ServerUrl = "127.0.0.1";
        settings.PHD2ServerPort = Port;
        settings.PHD2InstanceNumber = Instance;
        settings.PHD2Path = Executable;
        settings.PHD2ProfileId = 1;
        settings.SettlePixels = 2;
        settings.SettleTime = 1;
        settings.SettleTimeout = 90;
        settings.AutoRetryStartGuiding = false;
    }

    internal bool Matches(IGuiderSettings settings) => Instance is >= 2000 and < 3000
        && Port == 4400 + Instance - 1 && settings.GuiderName == "PHD2_Single"
        && settings.PHD2ServerUrl == "127.0.0.1" && settings.PHD2ServerPort == Port
        && settings.PHD2InstanceNumber == Instance && settings.PHD2Path == Executable
        && settings.PHD2ProfileId == 1;
}
