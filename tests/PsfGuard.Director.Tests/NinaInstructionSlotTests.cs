using Moq;
using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaInstructionSlotTests
{
    private static readonly IProgress<ApplicationStatus> Progress = new Progress<ApplicationStatus>();

    [Fact]
    public void SevenNativeSlotsCloneIndependentlyAndKeepExecutionSettings()
    {
        var slots = new NinaInstructionSlots();
        foreach (var slot in Enum.GetValues<NinaInstructionSlot>())
        {
            var nested = new SequentialContainer { Attempts = 3, ErrorBehavior = InstructionErrorBehavior.SkipInstructionSetOnError };
            nested.Add(new ProbeItem { Status = SequenceEntityStatus.DISABLED });
            slots[slot].Add(nested);
        }
        var clone = slots.Clone();
        Assert.Equal(7, Enum.GetValues<NinaInstructionSlot>().Length);
        foreach (var slot in Enum.GetValues<NinaInstructionSlot>())
        {
            Assert.NotSame(slots[slot], clone[slot]);
            Assert.Null(clone[slot].Parent);
            var nested = Assert.IsType<SequentialContainer>(Assert.Single(clone[slot].Items));
            Assert.NotSame(slots[slot].Items[0], nested);
            Assert.Same(clone[slot], nested.Parent);
            Assert.Equal(3, nested.Attempts);
            Assert.Equal(InstructionErrorBehavior.SkipInstructionSetOnError, nested.ErrorBehavior);
            Assert.Equal(SequenceEntityStatus.DISABLED, Assert.Single(nested.Items).Status);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => slots.CreateInvocation((NinaInstructionSlot)100));
    }

    [Fact]
    public void NativeSequenceConfigurationRoundTripsWithoutRuntimeParents()
    {
        var slots = new NinaInstructionSlots();
        slots.BeforeNewTarget.Add(new WaitForTimeSpan { Time = 23, Attempts = 2 });
        var settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto,
            PreserveReferencesHandling = PreserveReferencesHandling.Objects
        };
        var json = JsonConvert.SerializeObject(slots, settings);
        var restored = JsonConvert.DeserializeObject<NinaInstructionSlots>(json, settings)!;
        var instruction = Assert.IsType<WaitForTimeSpan>(Assert.Single(restored.BeforeNewTarget.Items));
        Assert.Equal(23, instruction.Time);
        Assert.Equal(2, instruction.Attempts);
        Assert.Same(restored.BeforeNewTarget, instruction.Parent);
        Assert.Null(restored.BeforeNewTarget.Parent);
        Assert.Equal("Before New Target Instructions", restored.BeforeNewTarget.Name);
        Assert.Empty(restored.AfterTargetComplete.Items);
        Assert.DoesNotContain("running", json, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<NinaInstructionSlots>("{}", settings));
    }

    [Fact]
    public async Task NativeInheritedTriggersSurroundInstructionWithNormalLifecycle()
    {
        var events = new List<string>();
        var configured = new ProbeItem
        {
            OnInitialize = () => events.Add("initialize"),
            OnTeardown = () => events.Add("teardown"),
            Action = (_, _) => { events.Add("instruction"); return Task.CompletedTask; }
        };
        var slots = new NinaInstructionSlots();
        slots.BeforeNewTarget.Add(configured);
        var parent = new SequentialContainer();
        parent.Add(Hook(() => events.Add("before"), () => events.Add("after")));
        var result = await slots.CreateInvocation(NinaInstructionSlot.BeforeNewTarget).RunAsync(parent, Progress, default);
        Assert.True(result.Completed);
        Assert.Equal(new[] { "initialize", "before", "instruction", "after", "teardown" }, events);
        Assert.Equal(SequenceEntityStatus.CREATED, configured.Status);
        Assert.Same(slots.BeforeNewTarget, configured.Parent);
        Assert.Empty(parent.Items);
    }

    [Fact]
    public async Task HookInstructionsResolveTheIssuedNativeTargetThroughTheirParent()
    {
        var profile = new Mock<IProfile>();
        var profiles = new Mock<IProfileService>();
        var astrometry = new Mock<IAstrometrySettings>();
        profile.SetupGet(x => x.Id).Returns(Guid.NewGuid());
        profile.SetupGet(x => x.AstrometrySettings).Returns(astrometry.Object);
        profiles.SetupGet(x => x.ActiveProfile).Returns(profile.Object);
        var now = DateTime.Now;
        var nighttime = new NighttimeData(now, NighttimeCalculator.GetReferenceDate(now), default, null, null, null, null, null, null);
        try
        {
            var target = new NinaTargetContainer(profiles.Object, profile.Object.Id,
                new DirectorTarget("target", "M31", 648000000, 72000000, null), nighttime, TimeProvider.System);
            var slots = new NinaInstructionSlots();
            slots.BeforeNewTarget.Add(new ProbeItem
            {
                Action = (item, _) =>
                {
                    var context = item.Parent;
                    while (context is not null && context is not IDeepSkyObjectContainer) context = context.Parent;
                    Assert.Same(target, context);
                    Assert.Equal(180, ItemUtility.RetrieveContextCoordinates(item.Parent).Coordinates.RADegrees);
                    Assert.Equal(20, ItemUtility.RetrieveContextCoordinates(item.Parent).Coordinates.Dec);
                    return Task.CompletedTask;
                }
            });
            Assert.True((await slots.CreateInvocation(NinaInstructionSlot.BeforeNewTarget).RunAsync(target, Progress, default)).Completed);
            target.ValidateContext();
        }
        finally { nighttime.Ticker.Stop(); }
    }

    [Fact]
    public void CloningPreservesDisabledNativeTriggersAndConditions()
    {
        var slots = new NinaInstructionSlots();
        var trigger = new FailureTrigger { Status = SequenceEntityStatus.DISABLED };
        slots.BeforeWait.Add(trigger);
        var condition = new Mock<ISequenceCondition>();
        condition.SetupProperty(x => x.Status, SequenceEntityStatus.DISABLED);
        condition.Setup(x => x.Clone()).Returns(() =>
        {
            var copied = new Mock<ISequenceCondition>();
            copied.SetupProperty(x => x.Status, SequenceEntityStatus.CREATED);
            return copied.Object;
        });
        slots.BeforeWait.Add(condition.Object);
        var clone = slots.Clone();
        Assert.Equal(SequenceEntityStatus.DISABLED, Assert.Single(clone.BeforeWait.Triggers).Status);
        Assert.NotSame(trigger, Assert.Single(clone.BeforeWait.Triggers));
        Assert.Equal(SequenceEntityStatus.DISABLED, Assert.Single(clone.BeforeWait.Conditions).Status);
    }

    [Fact]
    public async Task ParentConditionSkipsHookWithoutReportingCompletion()
    {
        var slots = new NinaInstructionSlots();
        slots.AfterEachExposure.Add(new ProbeItem { Action = (_, _) => throw new Exception("must not run") });
        var parent = new SequentialContainer();
        var condition = new Mock<ISequenceCondition>();
        condition.Setup(x => x.RunCheck(It.IsAny<ISequenceItem>(), It.IsAny<ISequenceItem>())).Returns(false);
        parent.Add(condition.Object);
        var result = await slots.CreateInvocation(NinaInstructionSlot.AfterEachExposure).RunAsync(parent, Progress, default);
        Assert.False(result.Completed);
        Assert.Contains(result.Issues, issue => issue.Status == SequenceEntityStatus.SKIPPED);
    }

    [Fact]
    public async Task NativeSwallowedItemAndTriggerFailuresAreNotSuccessfulEvidence()
    {
        var slots = new NinaInstructionSlots();
        slots.AfterEachTarget.Add(new ProbeItem { Action = (_, _) => throw new InvalidOperationException("native failure") });
        var result = await slots.CreateInvocation(NinaInstructionSlot.AfterEachTarget).RunAsync(new SequentialContainer(), Progress, default);
        Assert.False(result.Completed);
        Assert.Contains(result.Issues, issue => issue.Status == SequenceEntityStatus.FAILED);

        slots = new NinaInstructionSlots();
        slots.AfterWait.Add(new ProbeItem());
        var parent = new SequentialContainer();
        parent.Add(new FailureTrigger());
        result = await slots.CreateInvocation(NinaInstructionSlot.AfterWait).RunAsync(parent, Progress, default);
        Assert.False(result.Completed);
        Assert.Contains(result.Issues, issue => issue.Name == "failure hook" && issue.Status == SequenceEntityStatus.FAILED);
    }

    [Fact]
    public async Task NativeFinishedTriggerWithNestedFailedInstructionIsNotSuccessfulEvidence()
    {
        var slots = new NinaInstructionSlots();
        slots.AfterWait.Add(new ProbeItem());
        var parent = new SequentialContainer();
        var trigger = new NestedFailureTrigger();
        parent.Add(trigger);
        var result = await slots.CreateInvocation(NinaInstructionSlot.AfterWait).RunAsync(parent, Progress, default);
        Assert.Equal(SequenceEntityStatus.FINISHED, trigger.Status);
        Assert.False(result.Completed);
        Assert.Contains(result.Issues, issue => issue.Name == "nested failure" && issue.Status == SequenceEntityStatus.FAILED);
    }

    [Fact]
    public async Task TriggerOwnedRunnerLifecycleIsNotInitializedOrTornDownTwice()
    {
        var events = new List<string>();
        var slots = new NinaInstructionSlots();
        slots.BeforeWait.Add(new ProbeItem());
        slots.BeforeWait.Add(new LifecycleTrigger(events));
        Assert.True((await slots.CreateInvocation(NinaInstructionSlot.BeforeWait)
            .RunAsync(new SequentialContainer(), Progress, default)).Completed);
        Assert.Equal(new[] { "runner initialize", "runner teardown" }, events);
    }

    [Fact]
    public async Task InvocationIsOneUseAndSlotsRejectConcurrentExecutionButRecoverAfterCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardown = 0;
        var slots = new NinaInstructionSlots();
        slots.BeforeWait.Add(new ProbeItem
        {
            Action = async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); },
            OnTeardown = () => teardown++
        });
        using var cancellation = new CancellationTokenSource();
        var parent = new SequentialContainer();
        var invocation = slots.CreateInvocation(NinaInstructionSlot.BeforeWait);
        var pending = invocation.RunAsync(parent, Progress, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => invocation.RunAsync(parent, Progress, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => slots.CreateInvocation(NinaInstructionSlot.AfterWait).RunAsync(parent, Progress, default));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, teardown);
        Assert.True((await slots.CreateInvocation(NinaInstructionSlot.AfterWait).RunAsync(parent, Progress, default)).Completed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => invocation.RunAsync(parent, Progress, default));
    }

    [Fact]
    public async Task CleanupContinuesAfterFailureAndRetainsPrimaryError()
    {
        var cleaned = false;
        var slots = new NinaInstructionSlots();
        slots.BeforeWait.Add(new ProbeItem { OnTeardown = () => throw new InvalidOperationException("cleanup") });
        slots.BeforeWait.Add(new ProbeItem
        {
            OnInitialize = () => throw new InvalidOperationException("initialize"),
            OnTeardown = () => cleaned = true
        });
        var error = await Assert.ThrowsAsync<AggregateException>(() => slots.CreateInvocation(NinaInstructionSlot.BeforeWait)
            .RunAsync(new SequentialContainer(), Progress, default));
        Assert.True(cleaned);
        Assert.Equal(new[] { "initialize", "cleanup" }, error.InnerExceptions.Select(e => e.Message));
        Assert.True((await slots.CreateInvocation(NinaInstructionSlot.AfterWait).RunAsync(new SequentialContainer(), Progress, default)).Completed);
    }

    [Fact]
    public async Task DisabledInstructionsStayDisabledAndConfigurationEditsDoNotChangeCreatedInvocation()
    {
        var calls = 0;
        var slots = new NinaInstructionSlots();
        slots.AfterTargetComplete.Add(new ProbeItem { Status = SequenceEntityStatus.DISABLED, Action = (_, _) => throw new Exception("disabled") });
        var invocation = slots.CreateInvocation(NinaInstructionSlot.AfterTargetComplete);
        slots.AfterTargetComplete.Add(new ProbeItem { Action = (_, _) => { calls++; return Task.CompletedTask; } });
        Assert.True((await invocation.RunAsync(new SequentialContainer(), Progress, default)).Completed);
        Assert.Equal(0, calls);
        Assert.True((await slots.CreateInvocation(NinaInstructionSlot.AfterTargetComplete).RunAsync(new SequentialContainer(), Progress, default)).Completed);
        Assert.Equal(1, calls);
    }

    private static ISequenceTrigger Hook(Action before, Action after)
    {
        var hook = new Mock<ISequenceTrigger>();
        hook.Setup(x => x.ShouldTrigger(It.IsAny<ISequenceItem>(), It.IsAny<ISequenceItem>())).Returns(true);
        hook.Setup(x => x.ShouldTriggerAfter(It.IsAny<ISequenceItem>(), It.IsAny<ISequenceItem>())).Returns(true);
        var calls = 0;
        hook.Setup(x => x.Run(It.IsAny<ISequenceContainer>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
            .Callback(() => { if (calls++ == 0) before(); else after(); }).Returns(Task.CompletedTask);
        return hook.Object;
    }

    private sealed class FailureTrigger : SequenceTrigger
    {
        internal FailureTrigger() => Name = "failure hook";
        public override object Clone() => new FailureTrigger();
        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) => true;
        public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) => throw new InvalidOperationException("hook failed");
    }

    private sealed class NestedFailureTrigger : SequenceTrigger
    {
        internal NestedFailureTrigger()
        {
            var nested = new SequentialContainer();
            nested.Add(new ProbeItem { Name = "nested failure", Action = (_, _) => throw new InvalidOperationException("nested") });
            TriggerRunner.Add(nested);
        }
        public override object Clone() => new NestedFailureTrigger();
        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) => nextItem is ProbeItem;
        public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) => TriggerRunner.Run(progress, token);
    }

    private sealed class LifecycleTrigger : SequenceTrigger
    {
        private readonly List<string> events;
        internal LifecycleTrigger(List<string> events)
        {
            this.events = events;
            TriggerRunner.Add(new ProbeItem
            {
                OnInitialize = () => events.Add("runner initialize"),
                OnTeardown = () => events.Add("runner teardown")
            });
        }
        public override object Clone() => new LifecycleTrigger(events);
        public override void Initialize() { foreach (var item in TriggerRunner.Items) item.Initialize(); }
        public override void Teardown() { foreach (var item in TriggerRunner.Items) item.Teardown(); }
        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) => true;
        public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) => TriggerRunner.Run(progress, token);
    }

    private sealed class ProbeItem : SequenceItem
    {
        internal Func<ISequenceItem, CancellationToken, Task> Action { get; init; } = (_, _) => Task.CompletedTask;
        internal Action OnInitialize { get; init; } = () => { };
        internal Action OnTeardown { get; init; } = () => { };
        public override object Clone() => new ProbeItem { Name = Name, Action = Action, OnInitialize = OnInitialize, OnTeardown = OnTeardown };
        public override void Initialize() => OnInitialize();
        public override void Teardown() => OnTeardown();
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) => Action(this, token);
    }
}
