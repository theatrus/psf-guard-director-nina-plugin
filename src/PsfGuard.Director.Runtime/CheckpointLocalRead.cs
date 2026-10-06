namespace PsfGuard.Director.Runtime;

internal static class CheckpointLocalRead
{
    // IPC reads already have a bounded protocol deadline. Canceling one midway
    // invalidates the pipe, so stopping delivery must drain its current read.
    internal static async Task<T> ReadAsync<T>(Func<Task<T>> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var value = await read().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return value;
    }
}
