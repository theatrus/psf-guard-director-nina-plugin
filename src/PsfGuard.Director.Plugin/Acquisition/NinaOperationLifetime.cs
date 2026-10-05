namespace PsfGuard.Director.Plugin.Acquisition;

// A weather interruption cancels one generation of native work, not the night.
// Replaced tokens stay canceled; no pending operation can inherit a fresh token.
internal sealed class NinaOperationLifetime : IDisposable
{
    private readonly List<CancellationTokenSource> generations = [];
    internal CancellationToken Token => generations[^1].Token;
    internal NinaOperationLifetime(CancellationToken owner, CancellationToken safety, CancellationToken enclosure) => Renew(owner, safety, enclosure);
    internal void Renew(CancellationToken owner, CancellationToken safety, CancellationToken enclosure) =>
        generations.Add(CancellationTokenSource.CreateLinkedTokenSource(owner, safety, enclosure));
    public void Dispose() { foreach (var generation in generations) generation.Dispose(); }
}
