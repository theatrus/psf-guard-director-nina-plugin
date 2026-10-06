using System.Collections.Immutable;

namespace PsfGuard.Director.Runtime;

public sealed record QualityObservation(bool IsNew, int SamplesSeen, QualityReference? Reference, QualityAssessment? Assessment);

// Persisted evidence, never an acquisition permit. The core owns stability and
// classification; the host collects only the bounded initial cohort.
public sealed class QualityReferenceStore(string root, string nightId, RuntimeController runtime)
{
    private sealed record State(int Version, string NightId, QualityFrameContext Context,
        int SamplesSeen, ImmutableArray<QualityFrame> Samples, QualityReference? Reference,
        string LastCaptureId, ulong LastObservedAtMs);

    public async Task<QualityObservation> ObserveAsync(QualityFrame frame, ulong nowMs, CancellationToken token)
    {
        LedgerContract.CheckId(nightId);
        var file = new CoordinatorStateFile(root, new { nightId, frame.Context }, "quality-reference");
        using var lease = file.Lock();
        var state = file.Read<State>();
        if (state is not null && (state.Version != 1 || state.NightId != nightId || state.Context != frame.Context
            || state.SamplesSeen is < 1 or > 16 || state.Samples.IsDefault || state.Samples.Length > 5
            || state.Samples.Any(f => f.Context != frame.Context)
            || state.Reference is { } r && (r.Approved || r.Frame.Context != frame.Context || r.InitialGroup is not { Length: 5 })))
            throw new InvalidDataException("The stored quality reference is invalid; acquisition cannot replace it.");
        if (state is not null && (state.LastCaptureId == frame.CaptureId || frame.ObservedAtMs <= state.LastObservedAtMs))
            return new(false, state.SamplesSeen, state.Reference, null);

        state ??= new(1, nightId, frame.Context, 0, [], null, "", 0);
        QualityAssessment? assessment = null;
        if (state.Reference is { } reference)
        {
            assessment = (await runtime.ClassifyQualityAsync(new(), reference, frame, nowMs, token).ConfigureAwait(false)).Value
                ?? throw new InvalidDataException("The stored quality reference could not be validated.");
        }
        else if (state.SamplesSeen < 16)
        {
            var samples = state.Samples.Add(frame);
            if (samples.Length > 5) samples = samples.RemoveAt(0);
            var built = await runtime.BuildQualityReferenceAsync(new(), Guid.NewGuid().ToString("D"), samples, token).ConfigureAwait(false);
            if (built.Error is not (null or RecoveryError.InsufficientSamples or RecoveryError.UnstableBaseline))
                throw new InvalidDataException($"Quality baseline refused: {built.Error}.");
            state = state with { SamplesSeen = state.SamplesSeen + 1, Samples = samples, Reference = built.Value };
        }
        state = state with { LastCaptureId = frame.CaptureId, LastObservedAtMs = frame.ObservedAtMs };
        file.Write(state);
        return new(true, state.SamplesSeen, state.Reference, assessment);
    }
}
