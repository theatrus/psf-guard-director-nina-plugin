namespace PsfGuard.Director.Plugin;

// Keep progress ordered with completion; queued callbacks can overwrite a final status.
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
