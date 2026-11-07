using System.ComponentModel;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

public sealed partial class DirectorPlugin
{
    private CancellationTokenSource? equipmentPolling;
    public string EquipmentReportStatus { get; private set; } = "No equipment report sent in this N.I.N.A. session.";

    internal void SetEquipmentSettingsVisible(bool visible)
    {
        if (!visible)
        {
            var previous = equipmentPolling;
            equipmentPolling = null;
            previous?.Cancel();
            return;
        }
        if (!initialized || equipmentPolling is not null) return;
        equipmentPolling = new();
        _ = PollEquipmentAsync(equipmentPolling);
    }

    private async Task PollEquipmentAsync(CancellationTokenSource cancellation)
    {
        using (cancellation)
        {
            var token = cancellation.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var profile = profiles.ActiveProfile.Id;
                    var endpoint = ConnectionModel.ResolvedEndpoint;
                    var generation = Interlocked.Read(ref profileGeneration);
                    string status;
                    try
                    {
                        var pairing = endpoint is null ? null : ConnectionModel.ReadPairing();
                        var report = pairing is null ? null : DirectorEquipmentReport.Find(endpoint!, pairing);
                        if (report is null)
                            status = "No equipment report sent for this pairing in this N.I.N.A. session.";
                        else
                        {
                            ValueTask<string?> ReadCredential(CancellationToken ct)
                            {
                                ct.ThrowIfCancellationRequested();
                                var current = CurrentContext() ? ConnectionModel.ReadPairing() : null;
                                return ValueTask.FromResult(current?.Binding == report.Binding && current.ClientId == report.ClientId
                                    ? current.Token : null);
                            }

                            var revision = await report.CheckAcceptanceAsync(ReadCredential, ConnectionModel.AllowInsecureHttp, token);
                            var current = CurrentContext() ? ConnectionModel.ReadPairing() : null;
                            if (current is null || DirectorEquipmentReport.Find(endpoint!, current)?.Id != report.Id)
                                continue;
                            status = revision is { } value ? $"Equipment report accepted as rig configuration revision {value}."
                                : "Equipment report received. Awaiting acceptance in PSF Guard.";
                        }
                    }
                    catch (CoordinatorIntakeException error)
                    { status = $"Could not check equipment acceptance ({error.Failure})."; }
                    catch (Exception) when (!token.IsCancellationRequested)
                    { status = "Could not check equipment acceptance. Check the coordinator pairing."; }
                    if (CurrentContext() && status != EquipmentReportStatus)
                    {
                        EquipmentReportStatus = status;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EquipmentReportStatus)));
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5), token);

                    bool CurrentContext() => !token.IsCancellationRequested && generation == Interlocked.Read(ref profileGeneration)
                        && profiles.ActiveProfile.Id == profile && ConnectionModel.ResolvedEndpoint == endpoint;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }
}
