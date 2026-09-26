using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Model;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

// Internal, transient item. The session owns reservation, authorization and
// receipt delivery; native sequencing owns inherited triggers and conditions.
internal sealed class NinaExposureItem : SequenceItem, IExposureItem
{
    private readonly NinaProgramCapture capture;
    private readonly LedgerReservation reservation;
    private readonly CaptureBinding binding;
    private readonly NinaEquipmentBinding local;
    private readonly Func<CancellationToken, Task> revalidate;
    private readonly CaptureIntent intent;
    private int entered;
    private CaptureEvidence? evidence;
    private Exception? executionError;

    internal NinaExposureItem(NinaProgramCapture capture, LedgerReservation reservation, CaptureBinding binding,
        NinaEquipmentBinding local, Func<CancellationToken, Task> revalidate)
    {
        ArgumentNullException.ThrowIfNull(revalidate);
        this.capture = capture;
        this.reservation = reservation;
        this.binding = binding;
        this.local = local;
        this.revalidate = revalidate;
        intent = capture.CreateIntent(reservation, binding, local);
        ExposureTime = intent.ExposureSeconds;
        Gain = intent.Gain;
        Offset = intent.Offset;
        Binning = new(intent.BinX, intent.BinY);
        ImageType = CaptureSequence.ImageTypes.LIGHT;
        Name = "Director exposure";
    }

    public double ExposureTime { get; set; }
    public int Gain { get; set; }
    public int Offset { get; set; }
    public string ImageType { get; set; }
    public BinningMode Binning { get; set; }
    internal CaptureEvidence? Evidence => Volatile.Read(ref evidence);
    internal Exception? ExecutionError => Volatile.Read(ref executionError);

    public override int Attempts
    {
        get => 1;
        set { if (value != 1) throw new InvalidOperationException("The Rust ledger owns Director retries."); }
    }
    public override object Clone() => throw new NotSupportedException("Reserved Director exposures cannot be cloned.");
    public override TimeSpan GetEstimatedDuration() => TimeSpan.FromSeconds(intent.ExposureSeconds);

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        if (Interlocked.Exchange(ref entered, 1) != 0)
            throw new InvalidOperationException("This reserved Director exposure has already entered execution.");
        try
        {
            CheckSettings();
            var saved = await capture.CaptureAsync(reservation, binding, local, async cancellation =>
            {
                await revalidate(cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                CheckSettings();
            }, progress, token).ConfigureAwait(false);
            Volatile.Write(ref evidence, saved);
        }
        catch (Exception error)
        {
            Volatile.Write(ref executionError, error);
            throw;
        }
    }

    private void CheckSettings()
    {
        if (ExposureTime != intent.ExposureSeconds || Gain != intent.Gain || Offset != intent.Offset
            || ImageType != CaptureSequence.ImageTypes.LIGHT || Binning is null
            || Binning.X != intent.BinX || Binning.Y != intent.BinY)
            throw new InvalidOperationException("Native exposure settings changed after reservation.");
    }
}
