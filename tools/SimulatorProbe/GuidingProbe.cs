using System.IO;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyGuider.PHD2;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem.Guider;
using NINA.Sequencer.Trigger.Guider;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;

namespace PsfGuard.Director.SimulatorProbe;

internal sealed record GuidingProbe(int StartEvents, int ChecksWhileGuiding, int Dithers)
{
    internal static async Task<GuidingProbe> VerifyAsync(Phd2Fixture fixture, IProfileService profiles,
        INinaActionFactory factory, IGuiderMediator guider, IFocuserMediator focuser, IRotatorMediator rotator,
        IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        if (!fixture.Matches(profiles.ActiveProfile.GuiderSettings) || guider.GetDevice() is not PHD2Guider)
            throw new InvalidOperationException("Guiding event test requires the isolated PHD2 simulator.");
        var starts = 0;
        Task Started(object sender, EventArgs args) { Interlocked.Increment(ref starts); return Task.CompletedTask; }
        guider.GuidingStarted += Started;
        try
        {
            await NinaGuiderStop.ConfirmAsync(profiles, guider, token);
            var start = new NinaEnsureGuiding(factory.GetItem<StartGuiding>(), profiles, guider);
            await start.Execute(progress, token);
            if (starts != 1) throw new InvalidDataException("A stopped guider did not produce exactly one native start event.");
            var options = new DirectorSessionOptions
            {
                Guiding = DirectorOperationOwner.Director,
                SlewCenter = DirectorOperationOwner.Sequence,
                Focus = DirectorOperationOwner.Sequence,
                Dither = DirectorOperationOwner.Sequence,
                MeridianFlip = DirectorOperationOwner.Sequence
            };
            using var native = new NinaNativeImaging(factory, profiles, guider, focuser, rotator, options);
            var container = new DirectorSessionContainer();
            native.Install(container);
            var trigger = container.GetTriggersSnapshot().OfType<RestoreGuiding>().Single();
            async Task Check(int expected)
            {
                await trigger.Run(container, progress, token);
                native.CheckTriggers();
                if (trigger.Status != SequenceEntityStatus.FINISHED || starts != expected)
                    throw new InvalidDataException("Checking guiding emitted an unexpected start event or failed.");
            }
            await Check(1);
            if (!await guider.Dither(token)) throw new InvalidDataException("PHD2 simulator dither failed.");
            await Check(1);
            await NinaGuiderStop.ConfirmAsync(profiles, guider, token);
            await Check(2);
            return new(starts, 2, 1);
        }
        finally
        {
            guider.GuidingStarted -= Started;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await NinaGuiderStop.ConfirmAsync(profiles, guider, cleanup.Token);
        }
    }
}
