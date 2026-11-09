using NINA.Core.Enum;
using NINA.Sequencer;
using NINA.Sequencer.Container;
using NINA.Sequencer.Utility;

namespace PsfGuard.Director.Plugin.Acquisition;

// NINA may swallow an instruction's exception and then cancel its parent.
// Observe the public failure event so that the original cause survives both.
internal sealed class NinaSequenceFailure : IDisposable
{
    private readonly ISequenceContainer block;
    private readonly ISequenceRootContainer? root;
    private readonly object gate = new();
    private FailureDetail? failure;
    private sealed record FailureDetail(ISequenceEntity Entity, List<ISequenceEntity> Parents, Exception Error);
    internal Exception? Failure
    {
        get { lock (gate) return failure?.Entity.Status == SequenceEntityStatus.FINISHED ? null : failure?.Error; }
    }

    internal NinaSequenceFailure(ISequenceContainer block)
    {
        this.block = block;
        root = ItemUtility.GetRootContainer(block);
        if (root is not null) root.FailureEvent += OnFailure;
    }

    private Task OnFailure(object sender, SequenceEntityFailureEventArgs args)
    {
        // Ignore unrelated instructions in a parallel branch of the sequence.
        var parents = new List<ISequenceEntity>();
        for (ISequenceEntity? item = args.Entity; item is not null; item = item.Parent)
        {
            if (!ReferenceEquals(item, args.Entity)) parents.Add(item);
            if (ReferenceEquals(item, block))
            {
                var name = string.IsNullOrWhiteSpace(args.Entity.Name) ? args.Entity.GetType().Name : args.Entity.Name;
                lock (gate)
                {
                    // Keep the instruction's error when its parent then fails or
                    // cancels. A successful retry must not mask a later failure.
                    if (failure is { } previous && previous.Entity.Status != SequenceEntityStatus.FINISHED
                        && (args.Exception is OperationCanceledException || previous.Parents.Any(parent => ReferenceEquals(parent, args.Entity))))
                        return Task.CompletedTask;
                    failure = new(args.Entity, parents, new InvalidOperationException($"{name}: {args.Exception.Message}", args.Exception));
                }
                break;
            }
        }
        return Task.CompletedTask;
    }

    public void Dispose() { if (root is not null) root.FailureEvent -= OnFailure; }
}
