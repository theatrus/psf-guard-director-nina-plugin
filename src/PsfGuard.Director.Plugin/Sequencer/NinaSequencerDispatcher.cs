using System.Windows;

namespace PsfGuard.Director.Plugin.Sequencer;

internal static class NinaSequencerDispatcher
{
    // The editor may be watching collections and lifecycle callbacks while
    // Director runs acquisition on a worker.
    internal static T Invoke<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is not null && !dispatcher.CheckAccess() ? dispatcher.Invoke(action) : action();
    }

    internal static void Invoke(Action action) => Invoke(() => { action(); return true; });
}
