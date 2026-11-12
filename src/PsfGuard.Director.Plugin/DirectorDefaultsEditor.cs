using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using PsfGuard.Director.Plugin.Sequencer;

namespace PsfGuard.Director.Plugin;

public sealed class DirectorDefaultsEditor : INotifyPropertyChanged
{
    private readonly DirectorSessionDefaults store;
    private readonly Guid profileId;
    private string snapshot;
    internal bool HasChanges { get; private set; }
    internal bool IsOutOfDate => snapshot != store.Snapshot;
    internal DirectorDefaultsEditor(DirectorSessionDefaults store)
    {
        this.store = store;
        profileId = store.ProfileId;
        ProfileName = store.ProfileName;
        snapshot = store.Snapshot;
        SaveCommand = new AsyncCommand(() => { Save(); return Task.CompletedTask; },
            () => store.ProfileId == profileId, error => SetStatus(error.Message));
        try { Options = store.Read(); }
        catch (InvalidDataException error) { Options = new(); Status = error.Message; }
        Options.PropertyChanged += Edited;
        ReloadCommand = new AsyncCommand(() => { Reload(); return Task.CompletedTask; },
            () => store.ProfileId == profileId, error => SetStatus(error.Message));
    }

    public DirectorSessionOptions Options { get; private set; }
    public string ProfileName { get; }
    public string Status { get; private set; } = "Changes apply to new sessions after you save defaults. Existing sequences keep their settings.";
    public ICommand SaveCommand { get; }
    public ICommand ReloadCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Save()
    {
        if (store.ProfileId != profileId) throw new InvalidOperationException("The N.I.N.A. profile changed. Reopen Director settings before saving defaults.");
        if (IsOutOfDate) throw new InvalidOperationException("Profile defaults changed in another editor. Reload saved defaults before making further changes.");
        store.Save(Options);
        snapshot = store.Snapshot;
        HasChanges = false;
        SetStatus($"Defaults saved for {ProfileName}. Existing sequences keep their settings.");
    }

    private void Reload()
    {
        if (store.ProfileId != profileId) throw new InvalidOperationException("The N.I.N.A. profile changed. Reopen Director settings.");
        var replacement = store.Read();
        Options.PropertyChanged -= Edited;
        Options = replacement;
        Options.PropertyChanged += Edited;
        snapshot = store.Snapshot;
        HasChanges = false;
        PropertyChanged?.Invoke(this, new(nameof(Options)));
        SetStatus($"Loaded saved defaults for {ProfileName}.");
    }

    private void Edited(object? sender, PropertyChangedEventArgs e)
    {
        HasChanges = true;
        SetStatus("Unsaved changes. Save profile defaults to use them in new sessions.");
    }

    private void SetStatus(string status)
    {
        Status = status;
        PropertyChanged?.Invoke(this, new(nameof(Status)));
    }
}
