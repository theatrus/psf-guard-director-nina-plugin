using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

/// A distinct remote agent per connection, never the PSF Guard pairing token.
internal sealed class CollaborationConnection : INotifyPropertyChanged, IDisposable
{
    private sealed record Binding(Guid Id, string Server, string Name, bool Loopback, string? Agent, string State);
    private sealed record Saved(Binding Current, Binding[] History);
    private readonly Func<Guid> profile;
    private readonly Func<bool> idle;
    private readonly Func<string> read;
    private readonly Action<string> write;
    private readonly Func<Uri, Guid, Guid, CollaborationCredential?> credential;
    private readonly Action<Uri, Guid, Guid, CollaborationCredential> store;
    private readonly Action<Uri, Guid, Guid> forget;
    private readonly Func<Uri, bool, CollaborationAuthClient> client;
    private readonly AsyncCommand pair, signin, poll, disconnect, validate, replace, open;
    private Saved saved = Fresh();
    private CancellationTokenSource lifetime = new();
    private CollaborationLogin? login;
    private long expires;
    private long nextPoll;
    private int generation;
    private bool busy;
    private bool disposed;
    private string code = "";
    private string status = "Not connected";
    internal CollaborationConnection(Func<Guid> profile, Func<bool> idle, Func<string> read, Action<string> write,
        Func<Uri, Guid, Guid, CollaborationCredential?>? credential = null,
        Action<Uri, Guid, Guid, CollaborationCredential>? store = null,
        Action<Uri, Guid, Guid>? forget = null, Func<Uri, bool, CollaborationAuthClient>? client = null)
    {
        this.profile = profile; this.idle = idle; this.read = read; this.write = write;
        this.credential = credential ?? CollaborationCredentialStore.Read;
        this.store = store ?? CollaborationCredentialStore.Store; this.forget = forget ?? CollaborationCredentialStore.Forget;
        this.client = client ?? ((uri, loopback) => new(uri, loopback));
        pair = new(() => RunAsync("pair"), () => IsEditable && Endpoint is not null && !string.IsNullOrWhiteSpace(code), Failed);
        signin = new(() => RunAsync("signin"), () => IsEditable && Endpoint is not null, Failed);
        poll = new(() => RunAsync("poll"), () => CanOperate && login is not null, Failed);
        validate = new(() => RunAsync("validate"), () => CanOperate && saved.Current.Agent is not null && saved.Current.State != "disabled", Failed);
        disconnect = new(() => RunAsync("disconnect"), () => CanOperate && saved.Current.State != "disabled", Failed);
        replace = new(() => { var history = saved.History.Append(saved.Current).TakeLast(32).ToArray(); saved = Fresh() with { History = history }; Persist(); code = ""; login = null; status = "New connection; previous agent retained"; Changed(); return Task.CompletedTask; }, () => CanOperate && login is null, Failed);
        open = new(() => { if (login is not null) Process.Start(new ProcessStartInfo(login.Url.AbsoluteUri) { UseShellExecute = true }); return Task.CompletedTask; }, () => CanOperate && login is not null, Failed);
    }
    private static Saved Fresh() => new(new(Guid.NewGuid(), "", "NINA", false, null, "new"), []);
    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand PairCommand => pair;
    public ICommand SigninCommand => signin;
    public ICommand PollCommand => poll;
    public ICommand OpenSigninCommand => open;
    public ICommand DisconnectCommand => disconnect;
    public ICommand ValidateCommand => validate;
    public ICommand NewConnectionCommand => replace;
    public bool IsBusy => busy;
    private bool CanOperate => !disposed && !busy && idle();
    public bool IsEditable => CanOperate && saved.Current.Agent is null && saved.Current.State == "new" && login is null;
    public string ServerUrl { get => saved.Current.Server; set { if (!IsEditable) return; saved = saved with { Current = saved.Current with { Server = value.Trim(), Loopback = false } }; Persist(); Changed(); } }
    public string RigName { get => saved.Current.Name; set { if (!IsEditable) return; saved = saved with { Current = saved.Current with { Name = value } }; Persist(); Changed(); } }
    public bool AllowLoopbackHttp { get => saved.Current.Loopback; set { if (!IsEditable) return; saved = saved with { Current = saved.Current with { Loopback = value } }; Persist(); Changed(); } }
    public string PairingCode { get => code; set { code = value; Changed(); } }
    public string Status => status;
    private Uri? Endpoint
    {
        get { try { return CollaborationAuthClient.Normalize(new Uri(saved.Current.Server, UriKind.Absolute), saved.Current.Loopback); } catch (Exception) { return null; } }
    }
    private void Persist() => write(JsonSerializer.Serialize(saved));
    internal void Reload()
    {
        try
        {
            var text = read();
            saved = string.IsNullOrEmpty(text) ? Fresh() : JsonSerializer.Deserialize<Saved>(text) ?? throw new InvalidOperationException();
            if (saved.Current.Id == Guid.Empty || saved.History.Length > 32 || saved.Current.Agent is { } agent && !CollaborationAuthClient.ValidAgent(agent)) throw new InvalidOperationException();
            status = saved.Current.State switch
            {
                "unknown" => "Registration outcome unknown; check the server before creating a new connection",
                "disabled" => "Disconnected; original agent retained",
                _ => saved.Current.Agent is null ? "Not connected" : Endpoint is { } endpoint && credential(endpoint, profile(), saved.Current.Id) is { } c && c.AgentId == saved.Current.Agent
                    ? "Registered as " + saved.Current.Agent : "Credential missing; original agent retained. Create a new connection to register another agent.",
            };
        }
        catch (Exception) { status = "Collaboration settings or credential unavailable; original data retained"; saved = saved with { Current = saved.Current with { State = "unknown" } }; }
        Changed();
    }
    private async Task RunAsync(string action)
    {
        var endpoint = Endpoint ?? throw new InvalidOperationException();
        var original = saved.Current;
        var originalProfile = profile();
        var originalGeneration = generation;
        var token = lifetime.Token;
        var pairingCode = code; code = "";
        busy = true; status = "Contacting collaboration server"; Changed();
        bool Current() => !disposed && originalGeneration == generation && originalProfile == profile();
        void State(string value) { if (!Current()) throw new OperationCanceledException(); saved = saved with { Current = saved.Current with { State = value } }; Persist(); }
        void Enrolled(CollaborationCredential result)
        {
            if (!Current()) throw new OperationCanceledException();
            // Persist the identity first: a storage error must never permit an
            // automatic second enrollment or relabel work with another agent.
            saved = saved with { Current = saved.Current with { Agent = result.AgentId, State = "registered" } }; Persist();
            store(endpoint, originalProfile, original.Id, result);
            status = "Registered as " + result.AgentId;
        }
        try
        {
            using var remote = client(endpoint, original.Loopback);
            switch (action)
            {
                case "pair":
                    if (!(await remote.DiscoverAsync(token)).Pairing) { status = "Server does not advertise pairing"; break; }
                    State("unknown"); Enrolled(await remote.PairAsync(pairingCode, original.Name, token)); break;
                case "signin":
                    if (!(await remote.DiscoverAsync(token)).Signin) { status = "Server does not advertise browser sign-in"; break; }
                    var started = await remote.LoginAsync(token);
                    if (!Current()) break;
                    login = started; expires = Environment.TickCount64 + (long)started.Lifetime.TotalMilliseconds; nextPoll = Environment.TickCount64 + 2000;
                    status = "Open sign-in, then check approval"; break;
                case "poll":
                    if (Environment.TickCount64 < nextPoll) { status = "Wait before checking approval again"; break; }
                    if (login is null || Environment.TickCount64 >= expires) { login = null; status = "Sign-in expired; start again"; break; }
                    nextPoll = Environment.TickCount64 + 2000;
                    var result = await remote.PollAsync(login.Code, token);
                    if (!Current()) break;
                    if (result.State == "pending") { status = "Waiting for browser approval"; break; }
                    login = null;
                    if (result.State != "done") { status = "Sign-in expired or claimed; start again"; break; }
                    State("unknown"); Enrolled(await remote.EnrollAsync(result.Token!, original.Name, token)); break;
                case "validate":
                    var existing = credential(endpoint, originalProfile, original.Id);
                    if (existing is null || existing.AgentId != original.Agent) { status = "Credential missing; original agent retained"; break; }
                    await remote.ValidateAsync(existing, token); if (Current()) status = "Registered as " + original.Agent; break;
                case "disconnect":
                    State("disabled"); login = null; forget(endpoint, originalProfile, original.Id); status = "Disconnected; original agent retained"; break;
            }
        }
        catch (CoordinatorIntakeException error) when (Current())
        {
            if (action == "pair" && error.Failure is CoordinatorIntakeFailure.AuthenticationRequired or CoordinatorIntakeFailure.NotReady) State("new");
            if (action == "poll" && error.Failure == CoordinatorIntakeFailure.Busy) nextPoll = Environment.TickCount64 + 5000;
            status = error.Failure switch
            {
                CoordinatorIntakeFailure.AuthenticationRequired => "Credential or pairing code rejected; original identity retained",
                CoordinatorIntakeFailure.Busy => "Server busy; wait before retrying",
                _ => "Collaboration request failed; original identity and registration outcome retained",
            };
        }
        catch (OperationCanceledException) { }
        finally { if (Current()) { busy = false; Changed(); } }
    }
    private void Failed(Exception error) { status = "Collaboration operation failed; original settings retained"; Changed(); }
    internal void ProfileChanged()
    {
        generation++; lifetime.Cancel(); lifetime.Dispose(); lifetime = new(); busy = false; login = null; code = ""; Reload();
    }
    internal void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        pair.Refresh(); signin.Refresh(); poll.Refresh(); disconnect.Refresh(); validate.Refresh(); replace.Refresh(); open.Refresh();
    }
    public void Dispose() { disposed = true; generation++; lifetime.Cancel(); lifetime.Dispose(); login = null; code = ""; }
}
