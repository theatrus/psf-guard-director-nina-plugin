using System.IO;
using System.Text.Json;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Camera;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.Telescope;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

internal sealed record NinaIssuedItem(SequenceItem Item, NinaOperationFence Fence);

// Transient native items only: no MEF export, serialization or cloned authority.
internal sealed class NinaPreparationItems(IProfileService profiles, ICameraMediator camera, IFilterWheelMediator wheel,
    NinaEquipmentSnapshot equipment, TimeProvider clock, ITelescopeMediator? telescope = null)
{
    internal NinaIssuedItem Create(PreparationNext next, DirectorProgram program, NinaEquipmentBinding local,
        Func<CancellationToken, Task> revalidateAtDispatch)
    {
        ArgumentNullException.ThrowIfNull(revalidateAtDispatch);
        if (next is not PreparationNext.Run run) throw new InvalidOperationException("Only a newly issued operation can create a native item.");
        var command = run.Command;
        var binding = program.Bindings.Single(b => b.GoalId == command.GoalId);
        var recipe = program.Recipes.Single(r => r.Id == binding.RecipeId);
        if (command.TargetId != binding.TargetId || command.RecipeId != recipe.Id
            || !program.Targets.Any(t => t.Id == binding.TargetId) || program.Configuration.RigId != local.RigId
            || program.Assignment.RigId != local.RigId || program.Assignment.ConfigurationId != program.Configuration.Id)
            throw new InvalidDataException("Native operation does not match the opened program.");
        SequenceItem? item = null;
        var fence = new NinaOperationFence(command, async token =>
        {
            await revalidateAtDispatch(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (JsonSerializer.Serialize(equipment.Read(local)) != JsonSerializer.Serialize(program.Configuration))
                throw new InvalidOperationException("Native equipment changed before operation dispatch.");
            if (command.Operation is PreparationOperation.Unpark)
            {
                var mount = telescope!.GetInfo();
                if (!mount.Connected || mount.DeviceId != local.TelescopeDeviceId || mount.Slewing)
                    throw new InvalidOperationException("The bound telescope is unavailable or already moving.");
            }
            if (item is SetReadoutMode mode && mode.Mode != recipe.ReadoutMode)
                throw new InvalidOperationException("Native readout settings changed after issue.");
            if (item is SwitchFilter filter)
            {
                var slot = local.Filters.Single(f => f.Id == recipe.FilterId).Position!.Value;
                if (filter.ComboBoxText is not null || filter.XfilterDefinition != slot.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    || filter.Xfilter != slot)
                    throw new InvalidOperationException("Native filter settings changed after issue.");
            }
        }, clock, () =>
        {
            if (JsonSerializer.Serialize(equipment.Read(local)) != JsonSerializer.Serialize(program.Configuration))
                throw new InvalidOperationException("Native equipment changed while the operation ran.");
            if (command.Operation is PreparationOperation.SetReadoutMode && camera.GetInfo().ReadoutModeForNormalImages != recipe.ReadoutMode)
                throw new InvalidOperationException("NINA did not report the requested normal-image readout mode.");
            if (command.Operation is PreparationOperation.Unpark)
            {
                var mount = telescope!.GetInfo();
                if (!mount.Connected || mount.DeviceId != local.TelescopeDeviceId || mount.AtPark || mount.Slewing)
                    throw new InvalidOperationException("NINA did not report the bound telescope unparked and idle.");
            }
            if (command.Operation is PreparationOperation.SwitchFilter && local.FilterWheelDeviceId is not null)
            {
                var expected = local.Filters.Single(f => f.Id == recipe.FilterId);
                var info = wheel.GetInfo();
                if (info.IsMoving || info.SelectedFilter is not { } selected || selected.Position != expected.Position || selected.Name != expected.ExpectedName)
                    throw new InvalidOperationException("NINA did not report the requested settled filter.");
            }
        });
        item = command.Operation switch
        {
            PreparationOperation.Unpark when telescope is not null && local.TelescopeDeviceId is not null =>
                new IssuedUnpark(telescope, fence) { Name = "Director unpark" },
            PreparationOperation.SwitchFilter filter when filter.FilterId == recipe.FilterId =>
                CreateFilter(fence, local.Filters.Single(f => f.Id == filter.FilterId)),
            PreparationOperation.SetReadoutMode readout when readout.Mode == recipe.ReadoutMode && program.Configuration.ReadoutModes.Contains(readout.Mode) =>
                new IssuedReadout(camera, fence) { Mode = readout.Mode, Name = "Director readout" },
            _ => throw new NotSupportedException("This native adapter does not implement the issued operation or recipe.")
        };
        return new(item, fence);
    }

    private SequenceItem CreateFilter(NinaOperationFence fence, NinaFilterBinding filter) => filter.Position is { } slot
        ? new IssuedFilter(profiles, wheel, fence) { Xfilter = slot, Name = "Director filter" }
        : new IssuedFixedFilter(fence) { Name = "Director fixed filter" };

    private sealed class IssuedFilter(IProfileService profiles, IFilterWheelMediator wheel, NinaOperationFence fence) : SwitchFilter(profiles, wheel)
    {
        public override int Attempts { get => 1; set => RequireSingleAttempt(value); }
        public override object Clone() => throw new NotSupportedException("Issued Director operations cannot be cloned.");
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) =>
            fence.ExecuteAsync(() => base.Execute(progress, token), token);
    }

    private sealed class IssuedReadout(ICameraMediator camera, NinaOperationFence fence) : SetReadoutMode(camera)
    {
        public override int Attempts { get => 1; set => RequireSingleAttempt(value); }
        public override object Clone() => throw new NotSupportedException("Issued Director operations cannot be cloned.");
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) =>
            fence.ExecuteAsync(() => base.Execute(progress, token), token);
    }

    private sealed class IssuedUnpark(ITelescopeMediator telescope, NinaOperationFence fence) : UnparkScope(telescope)
    {
        public override int Attempts { get => 1; set => RequireSingleAttempt(value); }
        public override object Clone() => throw new NotSupportedException("Issued Director operations cannot be cloned.");
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) =>
            fence.ExecuteAsync(() => base.Execute(progress, token), token);
    }

    private sealed class IssuedFixedFilter(NinaOperationFence fence) : SequenceItem
    {
        public override int Attempts { get => 1; set => RequireSingleAttempt(value); }
        public override object Clone() => throw new NotSupportedException("Issued Director operations cannot be cloned.");
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) =>
            fence.ExecuteAsync(() => Task.CompletedTask, token);
    }

    private static void RequireSingleAttempt(int attempts)
    {
        if (attempts != 1) throw new InvalidOperationException("The Rust ledger owns Director retries.");
    }
}

internal sealed class NinaOperationFence(PreparationCommand command, Func<CancellationToken, Task> revalidate, TimeProvider clock,
    Action? verifyCompletion = null)
{
    private int invoked;
    private PreparationCompletion? completion;
    internal PreparationCompletion? Completion => Volatile.Read(ref completion);

    internal async Task ExecuteAsync(Func<Task> nativeAction, CancellationToken token)
    {
        if (Interlocked.Exchange(ref invoked, 1) != 0)
            throw new InvalidOperationException("This issued Director operation has already entered dispatch.");
        var started = clock.GetTimestamp();
        var dispatched = false;
        PreparationOutcome outcome = new PreparationOutcome.Failed("dispatch_not_entered");
        try
        {
            token.ThrowIfCancellationRequested();
            await revalidate(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            dispatched = true;
            await nativeAction().ConfigureAwait(false);
            verifyCompletion?.Invoke();
            token.ThrowIfCancellationRequested();
            outcome = new PreparationOutcome.Succeeded();
        }
        catch (Exception error)
        {
            var reason = error is OperationCanceledException ? "native_operation_canceled" : "native_operation_failed";
            outcome = dispatched ? new PreparationOutcome.Uncertain(reason) : new PreparationOutcome.Failed(reason);
            throw;
        }
        finally
        {
            var observed = new PreparationCompletion(command.PreparationId, command.Ordinal,
                checked((ulong)clock.GetUtcNow().ToUnixTimeMilliseconds()),
                checked((ulong)Math.Ceiling(clock.GetElapsedTime(started).TotalMilliseconds)), outcome);
            Volatile.Write(ref completion, observed);
        }
    }
}
