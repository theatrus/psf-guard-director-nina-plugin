using NINA.Core.Model;
using NINA.Sequencer.Container;
using PsfGuard.Director.Plugin.Sequencer;

namespace PsfGuard.Director.Plugin.Acquisition;

internal sealed record NinaHookReceipt(NinaInstructionSlot Slot, double ElapsedMs, bool Completed);

// Boundary bookkeeping only. No goal selection, safety authority or completion
// inference lives here: the session owner supplies those from the shared core.
internal sealed class NinaSessionHooks
{
    private readonly NinaInstructionSlots slots;
    private readonly ISequenceContainer parent;
    private readonly TimeSpan timeout;
    private readonly TimeProvider clock;
    private readonly List<NinaHookReceipt> receipts = [];
    private readonly HashSet<string> captures = new(StringComparer.Ordinal);
    private readonly HashSet<string> completedTargets = new(StringComparer.Ordinal);
    private ISequenceContainer? target;
    private string? targetId;
    private int entered;
    private bool faulted, finished;

    internal NinaSessionHooks(DirectorSessionContainer session, TimeProvider clock)
    {
        if (session.Options.ValidateSettings().Count != 0) throw new InvalidOperationException("Invalid Director session settings.");
        NinaHookAdmission.Validate(session.InstructionBlocks, session);
        slots = session.Slots.Clone();
        parent = session;
        timeout = TimeSpan.FromSeconds(session.Options.HookTimeoutSeconds);
        this.clock = clock;
    }

    internal IReadOnlyList<NinaHookReceipt> Receipts => receipts.AsReadOnly();

    internal Task SelectTargetAsync(string id, ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) =>
        BoundaryAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentNullException.ThrowIfNull(context);
            if (completedTargets.Contains(id)) throw new InvalidOperationException("A completed target needs a new core assignment before re-entry.");
            if (targetId == id)
            {
                if (!ReferenceEquals(context, target)) throw new InvalidOperationException("Target context changed within a target visit.");
                return;
            }
            await LeaveTargetAsync(progress, token).ConfigureAwait(false);
            if (!ReferenceEquals(context.Parent, parent)) throw new InvalidOperationException("Target context is not attached to this Director session.");
            target = context;
            targetId = id;
            await RunAsync(NinaInstructionSlot.BeforeNewTarget, context, progress, token).ConfigureAwait(false);
        }, token);

    internal Task ExposureSavedAsync(string captureId, IProgress<ApplicationStatus> progress, CancellationToken token) =>
        BoundaryAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
            if (target is null || !captures.Add(captureId)) throw new InvalidOperationException("Exposure hook requires a new saved capture and an active target.");
            await RunAsync(NinaInstructionSlot.AfterEachExposure, target, progress, token).ConfigureAwait(false);
        }, token);

    internal Task TargetCompletedAsync(IProgress<ApplicationStatus> progress, CancellationToken token) =>
        BoundaryAsync(async () =>
        {
            if (target is null || targetId is null || !completedTargets.Add(targetId))
                throw new InvalidOperationException("Target completion requires a new core-confirmed completion.");
            await RunAsync(NinaInstructionSlot.AfterTargetComplete, target, progress, token).ConfigureAwait(false);
            await LeaveTargetAsync(progress, token).ConfigureAwait(false);
        }, token);

    internal Task WaitAsync(Func<CancellationToken, Task> wait, IProgress<ApplicationStatus> progress, CancellationToken token) =>
        BoundaryAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(wait);
            await LeaveTargetAsync(progress, token).ConfigureAwait(false);
            await RunAsync(NinaInstructionSlot.BeforeWait, parent, progress, token).ConfigureAwait(false);
            await wait(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await RunAsync(NinaInstructionSlot.AfterWait, parent, progress, token).ConfigureAwait(false);
        }, token);

    internal Task FinishAsync(IProgress<ApplicationStatus> progress, CancellationToken token) =>
        BoundaryAsync(async () =>
        {
            await LeaveTargetAsync(progress, token).ConfigureAwait(false);
            finished = true;
        }, token);

    private async Task LeaveTargetAsync(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        if (target is null) return;
        await RunAsync(NinaInstructionSlot.AfterNewTarget, target, progress, token).ConfigureAwait(false);
        await RunAsync(NinaInstructionSlot.AfterEachTarget, target, progress, token).ConfigureAwait(false);
        target = null;
        targetId = null;
    }

    private async Task BoundaryAsync(Func<Task> action, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref entered, 1, 0) != 0) throw new InvalidOperationException("Director session boundaries cannot overlap.");
        try
        {
            if (faulted || finished) throw new InvalidOperationException("This Director hook session has ended.");
            token.ThrowIfCancellationRequested();
            await action().ConfigureAwait(false);
        }
        catch { faulted = true; throw; }
        finally { Volatile.Write(ref entered, 0); }
    }

    private async Task RunAsync(NinaInstructionSlot slot, ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        if (!ReferenceEquals(context, parent) && !ReferenceEquals(context.Parent, parent))
            throw new InvalidOperationException("Director target context was detached from its session.");
        NinaHookAdmission.Validate([slots[slot]], context);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var started = clock.GetTimestamp();
        var completed = false;
        try
        {
            var result = await slots.CreateInvocation(slot).RunAsync(context, progress, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (!result.Completed) throw new InvalidOperationException($"Director {slot} instructions did not complete.");
            completed = true;
        }
        finally { receipts.Add(new(slot, clock.GetElapsedTime(started).TotalMilliseconds, completed)); }
    }
}
