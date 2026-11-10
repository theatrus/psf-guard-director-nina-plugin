namespace PsfGuard.Director.Runtime;

/// <summary>A valid runtime refusal, not a broken connection or permission to retry equipment.</summary>
public sealed class RecoveryBlockedException(RecoveryError code) : InvalidOperationException(MessageFor(code))
{
    public RecoveryError Code { get; } = code;

    private static string MessageFor(RecoveryError code) => code switch
    {
        RecoveryError.AcquisitionBlocked => "Imaging is paused or stopped, or the last safety check is too old. Director must check the session before continuing.",
        RecoveryError.NotAdmitted => "The observing session has not finished starting.",
        RecoveryError.WrongScope => "The saved observing session belongs to different equipment settings.",
        RecoveryError.ClockReversed => "The computer clock moved backwards during the observing session.",
        RecoveryError.Busy or RecoveryError.Unavailable => "Director could not read the saved observing session. It will keep the saved image records.",
        _ => $"Director could not confirm that imaging may continue ({code})."
    };
}
