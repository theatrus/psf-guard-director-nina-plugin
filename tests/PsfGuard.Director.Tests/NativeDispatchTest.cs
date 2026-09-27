namespace PsfGuard.Director.Tests;

// Only mock-equipment tests may bypass the production one-use dispatch guard.
internal static class NativeDispatchTest
{
    internal static Task<Action> Allow(CancellationToken _) => Task.FromResult<Action>(() => { });

    internal static Func<CancellationToken, Task<Action>> After(Func<CancellationToken, Task>? check) => async token =>
    {
        if (check is not null) await check(token);
        return () => { };
    };
}
