using System.IO;
using System.Windows.Input;
using NINA.Core.Enum;
using NINA.Sequencer.Utility;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Sequencer;

public sealed partial class DirectorSessionContainer
{
    private DirectorSessionDefaults? defaults;
    private bool isToolboxTemplate;
    private AsyncCommand saveDefaultsCommand = null!, loadDefaultsCommand = null!;
    public ICommand SaveDefaultsCommand => saveDefaultsCommand;
    public ICommand LoadDefaultsCommand => loadDefaultsCommand;
    public string DefaultsStatus { get; private set; } = "This session saves its own settings. Profile defaults do not change saved sequences.";

    internal DirectorSessionContainer(DirectorSessionDefaults defaults) : this()
    {
        this.defaults = defaults;
        try { Options = defaults.Read(); }
        catch (InvalidDataException error) { SetDefaultsStatus($"Could not load profile defaults: {error.Message}"); }
    }

    private void InitializeDefaultsCommands()
    {
        saveDefaultsCommand = new(() => { SaveDefaults(); return Task.CompletedTask; }, CanUseDefaults, error => SetDefaultsStatus(error.Message));
        loadDefaultsCommand = new(() => { LoadDefaults(); return Task.CompletedTask; }, CanUseDefaults, error => SetDefaultsStatus(error.Message));
    }

    private bool CanUseDefaults()
    {
        lock (executionLock) return defaults is not null && !AcquisitionLease.IsActive && !reportingEquipment
            && execution is not { IsCompleted: false } && Status != SequenceEntityStatus.RUNNING
            && ItemUtility.GetRootContainer(Parent)?.Status != SequenceEntityStatus.RUNNING;
    }

    internal void SaveDefaults()
    {
        lock (executionLock)
        {
            if (!CanUseDefaults()) throw new InvalidOperationException("Stop the sequence before saving profile defaults.");
            defaults!.Save(Options);
            SetDefaultsStatus($"Defaults saved for {defaults.ProfileName}. Instructions and triggers stay in this sequence.");
        }
    }

    internal void LoadDefaults()
    {
        lock (executionLock)
        {
            if (!CanUseDefaults()) throw new InvalidOperationException("Stop the sequence before loading profile defaults.");
            var replacement = defaults!.Read();
            replacement.EnableAcquisition = Options.EnableAcquisition;
            Options = replacement;
            RaisePropertyChanged(nameof(Options));
            SettingsChanged(this, new(nameof(Options)));
            SetDefaultsStatus($"Loaded defaults for {defaults.ProfileName}. Instructions, triggers and Enable acquisition were kept.");
        }
    }

    private void SetDefaultsStatus(string status)
    {
        DefaultsStatus = status;
        RaisePropertyChanged(nameof(DefaultsStatus));
    }
}
