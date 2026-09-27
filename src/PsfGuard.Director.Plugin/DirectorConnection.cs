using System.ComponentModel;
using System.Net;
using System.Windows.Input;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

internal sealed class DirectorConnection : INotifyPropertyChanged, IDisposable
{
    private readonly Func<Guid> profile;
    private readonly Func<bool> idle;
    private readonly Func<string> readUrl;
    private readonly Action<string> writeUrl;
    private readonly Func<string> readHttpConsent;
    private readonly Action<string> writeHttpConsent;
    private readonly Func<Uri, Guid, CoordinatorPairing?> read;
    private readonly Action<Uri, CoordinatorPairing> store;
    private readonly Action<Uri, Guid> forget;
    private readonly Func<Uri, string, Guid, bool, CancellationToken, Task<CoordinatorPairing>> exchange;
    private readonly AsyncCommand pair;
    private readonly AsyncCommand reset;
    private CancellationTokenSource lifetime = new();
    private long generation;
    private string code = "";
    private string status = "Not paired";
    private bool busy;
    private string httpConsent = "";

    internal DirectorConnection(Func<Guid> profile, Func<bool> idle, Func<string> readUrl, Action<string> writeUrl,
        Func<Uri, Guid, CoordinatorPairing?>? read = null, Action<Uri, CoordinatorPairing>? store = null,
        Action<Uri, Guid>? forget = null,
        Func<Uri, string, Guid, bool, CancellationToken, Task<CoordinatorPairing>>? exchange = null,
        Func<string>? readHttpConsent = null, Action<string>? writeHttpConsent = null)
    {
        this.profile = profile; this.idle = idle; this.readUrl = readUrl; this.writeUrl = writeUrl;
        this.readHttpConsent = readHttpConsent ?? (() => httpConsent);
        this.writeHttpConsent = writeHttpConsent ?? (value => httpConsent = value);
        this.read = read ?? DirectorCredentialStore.Read;
        this.store = store ?? DirectorCredentialStore.Store;
        this.forget = forget ?? DirectorCredentialStore.Forget;
        this.exchange = exchange ?? ExchangeAsync;
        pair = new(PairAsync, () => IsEditable && Endpoint() is not null && CoordinatorPairingClient.ValidCode(code.Trim()), ReportError);
        reset = new(ResetAsync, () => IsEditable && Endpoint() is not null, ReportError);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string ServerUrl
    {
        get => readUrl();
        set { if (!IsEditable) return; writeUrl(value.Trim()); Reload(); }
    }
    public string PairingCode
    {
        get => code;
        set { code = value; Changed(); }
    }
    public bool AllowInsecureHttp
    {
        get => ParseEndpoint(true) is { Scheme: "http" } endpoint && readHttpConsent() == endpoint.AbsoluteUri;
        set
        {
            if (!IsEditable) return;
            writeHttpConsent(value && ParseEndpoint(true) is { Scheme: "http" } endpoint ? endpoint.AbsoluteUri : "");
            Reload();
        }
    }
    public bool IsEditable => !busy && idle() && !lifetime.IsCancellationRequested;
    internal bool IsBusy => busy;
    public string PairingStatus => status;
    public ICommand PairCommand => pair;
    public ICommand ResetPairingCommand => reset;

    internal CoordinatorPairing? ReadPairing()
    {
        var endpoint = Endpoint();
        return endpoint is null ? null : read(endpoint, profile());
    }

    private Uri? Endpoint() => ParseEndpoint(AllowInsecureHttp);
    private Uri? ParseEndpoint(bool allowHttp)
    {
        var value = ServerUrl.Trim();
        if (value.Length == 0) return null;
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate("http://" + value, UriKind.Absolute, out var address)) return null;
            value = (IPAddress.TryParse(address.Host.Trim('[', ']'), out _) || address.IsLoopback ? "http://" : "https://") + value;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        try { return CoordinatorTransport.Normalize(uri, allowHttp); }
        catch (ArgumentException) { return null; }
    }

    internal void Reload()
    {
        try
        {
            var pairing = ReadPairing();
            status = pairing is null ? "Not paired" : $"Paired to rig {pairing.Binding.RigId:D}";
        }
        catch (Exception) { status = "Credential unavailable. Reset pairing and use a new code."; }
        Changed();
    }

    internal void ProfileChanged()
    {
        generation++;
        lifetime.Cancel();
        lifetime.Dispose();
        lifetime = new();
        code = "";
        Reload();
    }

    private async Task PairAsync()
    {
        var endpoint = Endpoint() ?? throw new InvalidOperationException();
        var selectedProfile = profile();
        var selectedGeneration = generation;
        var selectedCode = code.Trim();
        var token = lifetime.Token;
        code = "";
        busy = true;
        status = "Pairing...";
        Changed();
        try
        {
            var pairing = await exchange(endpoint, selectedCode, selectedProfile, AllowInsecureHttp, token);
            token.ThrowIfCancellationRequested();
            if (generation != selectedGeneration || selectedProfile != profile()) return;
            if (pairing.Binding.ProfileId != selectedProfile) throw new InvalidOperationException();
            store(endpoint, pairing);
            status = $"Paired to rig {pairing.Binding.RigId:D}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == selectedGeneration)
                status = error is CoordinatorIntakeException failure ? $"Pairing failed: {failure.Failure}. Use a new code."
                    : "Pairing could not be saved. Use a new code.";
        }
        finally { busy = false; Changed(); }
    }

    private Task ResetAsync()
    {
        forget(Endpoint() ?? throw new InvalidOperationException(), profile());
        code = "";
        Reload();
        return Task.CompletedTask;
    }

    private void ReportError(Exception _) { status = "Pairing operation failed. Check Windows Credential Manager."; Changed(); }
    internal void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        pair.Refresh();
        reset.Refresh();
    }
    private static async Task<CoordinatorPairing> ExchangeAsync(Uri endpoint, string code, Guid profile, bool allowHttp, CancellationToken token)
    {
        using var client = new CoordinatorPairingClient(endpoint, allowHttp);
        return await client.PairAsync(code, profile, token);
    }
    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
