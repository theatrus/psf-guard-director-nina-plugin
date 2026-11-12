using System.IO;
using Newtonsoft.Json;
using NINA.Profile;
using NINA.Profile.Interfaces;

namespace PsfGuard.Director.Plugin.Sequencer;

// A template, not a live policy source. Every session owns its copied settings.
internal sealed class DirectorSessionDefaults(IProfileService profiles)
{
    internal const string SettingKey = "SessionDefaults";
    private readonly PluginOptionsAccessor settings = new(profiles, new("03a1d13e-67eb-4e24-a407-82bce7e576a5"));
    internal Guid ProfileId => profiles.ActiveProfile.Id;
    internal string ProfileName => profiles.ActiveProfile.Name;
    internal string Snapshot => settings.GetValueString(SettingKey, "");

    internal DirectorSessionOptions Read()
    {
        var json = Snapshot;
        if (string.IsNullOrWhiteSpace(json)) return Prepare(new());
        try
        {
            var options = JsonConvert.DeserializeObject<DirectorSessionOptions>(json)
                ?? throw new InvalidDataException("Profile defaults are empty.");
            Validate(options);
            return Prepare(options);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Director profile defaults could not be read. Save new defaults in the plugin settings.", error);
        }
    }

    internal void Save(DirectorSessionOptions options)
    {
        Validate(options);
        settings.SetValueString(SettingKey, JsonConvert.SerializeObject(Prepare(options.Clone())));
    }

    private static void Validate(DirectorSessionOptions options)
    {
        var issues = options.ValidateSettings();
        if (issues.Count != 0) throw new InvalidDataException(string.Join(" ", issues));
    }

    private static DirectorSessionOptions Prepare(DirectorSessionOptions options)
    {
        // Defaults must never arm a newly added session or revive an old run.
        options.EnableAcquisition = false;
        options.AutomaticWorkloads = options.LocalTargetScheduling = true;
        options.AllowSettledRestart = false;
        return options;
    }
}
