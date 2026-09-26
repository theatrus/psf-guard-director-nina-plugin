using System.Windows.Input;

namespace PsfGuard.Director.Plugin;

internal sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> reportError) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && canExecute();
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true;
        Refresh();
        try { await execute(); }
        catch (Exception error) { reportError(error); }
        finally { running = false; Refresh(); }
    }
    internal void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
