using PsfGuard.Director.Plugin.Sequencer;

namespace PsfGuard.Director.Plugin;

public sealed partial class DirectorPlugin
{
    private DirectorDefaultsEditor? defaultsEditor;
    private Guid? defaultsProfile;
    public DirectorDefaultsEditor SessionDefaults
    {
        get
        {
            if (defaultsEditor is null || defaultsProfile != profiles.ActiveProfile.Id || (!defaultsEditor.HasChanges && defaultsEditor.IsOutOfDate))
            {
                defaultsEditor = new(new DirectorSessionDefaults(profiles));
                defaultsProfile = profiles.ActiveProfile.Id;
            }
            return defaultsEditor;
        }
    }
}
