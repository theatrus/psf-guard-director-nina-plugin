using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Windows;
using System.Windows.Input;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

[Export(typeof(IPluginManifest))]
public sealed class DirectorPlugin : PluginBase, INotifyPropertyChanged
{
    private static readonly Guid PluginId = new("03a1d13e-67eb-4e24-a407-82bce7e576a5");
    private readonly IProfileService profiles;
    private readonly PluginOptionsAccessor settings;
    private readonly RuntimeController runtime;
    private readonly AsyncCommand start;
    private readonly AsyncCommand stop;
    private bool initialized;
    private int profileTransitions;
    private long profileGeneration;
    private string? commandError;

    [ImportingConstructor]
    public DirectorPlugin(IProfileService profileService)
    {
        profiles = profileService;
        settings = new PluginOptionsAccessor(profiles, PluginId);
        runtime = new RuntimeController(Path.GetDirectoryName(typeof(DirectorPlugin).Assembly.Location)!);
        start = new AsyncCommand(StartRuntimeAsync,
            () => initialized && Volatile.Read(ref profileTransitions) == 0 && runtime.Status.State is RuntimeState.Stopped or RuntimeState.Faulted,
            ReportError);
        stop = new AsyncCommand(runtime.StopAsync,
            () => initialized && runtime.Status.State is RuntimeState.Starting or RuntimeState.Ready or RuntimeState.Faulted,
            ReportError);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand StartRuntimeCommand => start;
    public ICommand StopRuntimeCommand => stop;
    public string RuntimeStatus => commandError ?? runtime.Status.Message;
    public string ProfileName => profiles.ActiveProfile.Name;
    public string EngineVersion => RuntimeContract.EngineVersion;
    public string AcquisitionStatus => "Not armed";

    public override async Task Initialize()
    {
        await base.Initialize();
        profiles.ProfileChanged += ProfileChanged;
        runtime.StateChanged += RuntimeStateChanged;
        initialized = true;
        Refresh();
    }

    public override async Task Teardown()
    {
        initialized = false;
        profiles.ProfileChanged -= ProfileChanged;
        runtime.StateChanged -= RuntimeStateChanged;
        try { await runtime.DisposeAsync(); }
        catch (Exception error) { Logger.Error(error); }
        finally { await base.Teardown(); }
    }

    private async Task StartRuntimeAsync()
    {
        commandError = null;
        var generation = Interlocked.Read(ref profileGeneration);
        var rigId = settings.GetValueString("RigId", "");
        if (!Guid.TryParse(rigId, out _))
        {
            rigId = Guid.NewGuid().ToString("D");
            settings.SetValueString("RigId", rigId);
        }
        await runtime.StartAsync(rigId);
        if (generation != Interlocked.Read(ref profileGeneration) || !initialized) await runtime.StopAsync();
        Refresh();
    }

    private async void ProfileChanged(object? sender, EventArgs args)
    {
        Interlocked.Increment(ref profileGeneration);
        Interlocked.Increment(ref profileTransitions);
        commandError = null;
        Refresh();
        try { await runtime.StopAsync(); }
        catch (Exception error) { ReportError(error); }
        finally { Interlocked.Decrement(ref profileTransitions); Refresh(); }
    }

    private void RuntimeStateChanged(object? sender, EventArgs args)
    {
        var status = runtime.Status;
        Logger.Info($"PSF Guard Director runtime: {status.State}");
        if (status.Error is not null) Logger.Error(status.Error);
        Refresh();
    }

    private void ReportError(Exception error)
    {
        Logger.Error(error);
        commandError = "Director operation failed. See the NINA log.";
        Refresh();
    }

    private void Refresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(Refresh);
            return;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RuntimeStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProfileName)));
        start.Refresh();
        stop.Refresh();
    }
}
