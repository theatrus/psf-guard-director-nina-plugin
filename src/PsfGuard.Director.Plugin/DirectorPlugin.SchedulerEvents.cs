namespace PsfGuard.Director.Plugin;

public sealed partial class DirectorPlugin
{
    public bool EmitTargetSchedulerEvents
    {
        get => settings.GetValueBoolean(DirectorSchedulerEvents.SettingKey, false);
        set
        {
            if (value == EmitTargetSchedulerEvents) return;
            settings.SetValueBoolean(DirectorSchedulerEvents.SettingKey, value);
            PropertyChanged?.Invoke(this, new(nameof(EmitTargetSchedulerEvents)));
        }
    }
}
