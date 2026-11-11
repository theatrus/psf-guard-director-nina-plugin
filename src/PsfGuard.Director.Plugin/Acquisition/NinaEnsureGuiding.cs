using NINA.Core.Model;
using NINA.Equipment.Equipment.MyGuider.PHD2;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Guider;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;

namespace PsfGuard.Director.Plugin.Acquisition;

// PHD2's cached AppState can lag behind stop, lost-lock and dither events.
// Ask the connected instance before deciding to omit the native start action.
internal sealed class NinaEnsureGuiding : SequenceItem, IValidatable
{
    private readonly StartGuiding start;
    private readonly IProfileService profiles;
    private readonly IProfile profile;
    private readonly string guiderId;
    private readonly IGuiderMediator guider;
    private readonly Func<IGuider, CancellationToken, Task<bool>> isGuiding;

    internal NinaEnsureGuiding(StartGuiding start, IProfileService profiles, IGuiderMediator guider,
        Func<IGuider, CancellationToken, Task<bool>>? isGuiding = null)
    {
        this.start = start;
        this.profiles = profiles;
        profile = profiles.ActiveProfile;
        guiderId = profile.GuiderSettings.GuiderName;
        this.guider = guider;
        this.isGuiding = isGuiding ?? ReadGuidingAsync;
        Name = "Check guiding";
        Attempts = 1;
        ErrorBehavior = InstructionErrorBehavior.AbortOnError;
    }

    public IList<string> Issues { get; set; } = new List<string>();

    public bool Validate()
    {
        var issues = new List<string>();
        if (!start.Validate() || start.Issues.Count != 0) issues.AddRange(start.Issues);
        try { BoundDevice(); }
        catch (InvalidOperationException error) { issues.Add(error.Message); }
        Issues = issues;
        return issues.Count == 0;
    }

    private NinaEnsureGuiding(NinaEnsureGuiding source) : this((StartGuiding)source.start.Clone(), source.profiles, source.guider, source.isGuiding)
    {
        profile = source.profile;
        guiderId = source.guiderId;
    }

    public override object Clone() => new NinaEnsureGuiding(this);

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var device = BoundDevice();
        progress.Report(new ApplicationStatus { Status = "Checking guiding" });
        var guiding = await isGuiding(device, token);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(BoundDevice(), device))
            throw new InvalidOperationException("The connected guider changed while checking guiding.");
        if (guiding) return;
        if (!start.Validate() || start.Issues.Count != 0)
            throw new InvalidOperationException(string.Join("; ", start.Issues));
        progress.Report(new ApplicationStatus { Status = "Starting guiding" });
        await start.Execute(progress, token);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(BoundDevice(), device))
            throw new InvalidOperationException("The connected guider changed while starting guiding.");
    }

    private IGuider BoundDevice()
    {
        if (!ReferenceEquals(profiles.ActiveProfile, profile) || profile.GuiderSettings.GuiderName != guiderId
            || guider.GetInfo() is not { Connected: true } info || info.DeviceId != guiderId
            || guider.GetDevice() is not IGuider device || !device.Connected || device.Id != guiderId)
            throw new InvalidOperationException("Connect the configured guider before checking guiding.");
        return device;
    }

    private static Task<bool> ReadGuidingAsync(IGuider device, CancellationToken token) => device is PHD2Guider phd2
        ? ReadPHD2GuidingAsync(() => phd2.SendMessage(new Phd2GetAppState(), 3000), token)
        // Other guiders expose different state vocabularies and may do extra
        // work in StartGuiding. Keep their supported native behavior.
        : Task.FromResult(false);

    internal static async Task<bool> ReadPHD2GuidingAsync(Func<Task<GenericPhdMethodResponse>> readState, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        GenericPhdMethodResponse response;
        try { response = await readState().WaitAsync(TimeSpan.FromSeconds(3), token); }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw new InvalidOperationException("Could not check PHD2 guiding. Check the guider connection.", error); }
        token.ThrowIfCancellationRequested();
        if (response is not { error: null, result: string state } || state is not
            ("Guiding" or "Stopped" or "Selected" or "Calibrating" or "LostLock" or "Paused" or "Looping"))
            throw new InvalidOperationException("PHD2 did not return a valid guiding state. Check the guider connection.");
        return state == PhdAppState.GUIDING;
    }
}
