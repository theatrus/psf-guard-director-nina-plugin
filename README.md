# PSF Guard Director for N.I.N.A.

An experimental, goal-driven acquisition plugin for N.I.N.A. 3.3. Director will
execute PSF Guard observing assignments through supported N.I.N.A. sequencing
and equipment APIs while keeping safety and operator control local. Target
Scheduler is inspiration and an optional data source, not a runtime dependency.

Director is separate from PSF Guard Sync. It uses the same Rust planning core
as PSF Guard through a bundled, versioned sidecar; it does not reimplement the
planner in C#.

Director will expose explicit session/target state to Chatstronomy while
preserving Chatstronomy's existing TS integration. Neither plugin is a required
dependency of Director, and chat commands remain subject to local permissions
and safe execution boundaries.

The architecture and acceptance gates live in
[PSF Guard's Director design](https://github.com/theatrus/psf-guard/blob/codex/director-ledger-ipc/docs/design/director.md).
This repository is not a stable plugin release and has no acquisition support yet.

## Runtime preview

The initial plugin targets N.I.N.A. 3.3 nightly #58 (`3.3.0.1058-nightly`) and
.NET 10 on Windows x64. It starts only on explicit request. Profile changes
and N.I.N.A. shutdown stop the child process; it never starts equipment work.
Runtime state changes and faults are logged through N.I.N.A.

The settings page shows the current profile, planner version, runtime state,
and acquisition state. Start/Stop control the local planner process only.
There is no PSF Guard pairing, target execution, or Chatstronomy adapter yet.

## Development

Use .NET SDK 10 and the existing authenticated `gh` CLI:

```powershell
./fetch-runtime.ps1
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet format --verify-no-changes --no-restore
./build-package.ps1
```

`runtime.lock.json` pins a reviewed PSF Guard commit, successful CI run,
artifact identity, executable SHA-256, and all wire versions. Fetching never
builds Rust locally or silently takes a newer artifact. The same pin is embedded
in the runtime-host assembly and checked before execution. The executable stays
read-locked for the process lifetime. CI artifact pins are temporary development
inputs; expiry requires a reviewed update. Stable distribution will use durable,
signed release artifacts, not expiring CI links.

The package includes only the two Director assemblies, pinned sidecar, runtime
lock, and license. It does not bundle N.I.N.A. assemblies, TS, or Sync. The build
does not install anything into a live N.I.N.A. profile or publish to a registry.

Tests exercise the real pinned child and adversarial pipe peers. They cover
startup/shutdown, crash, checksum verification, bounded framing, malformed and
stale replies, timeouts, cancellation before/after dispatch, and profile-scoped
runtime replacement. These are not the full-stack acceptance test: that still
requires N.I.N.A. with simulated equipment, Director's native execution adapter,
and an isolated PSF Guard instance built from the corresponding changes.

The WPF render test loads the compiled settings template and N.I.N.A.'s button
style, checks command enablement/contrast/bounds at 360 and 640 pixels, and
writes stopped/ready/fault screenshots into `artifacts/`. This validates the
settings view in isolation, not plugin discovery or a real N.I.N.A. session.

For a real nightly session with fresh test profiles and plugin storage, use the
[N.I.N.A. smoke-test procedure](docs/nina-smoke-test.md). The test-only startup
hook is not part of the plugin bundle.

The [native capture adapter](docs/native-capture.md) implements journaled capture,
processing, and correlated save completion behind an internal interface. It is
not yet exposed as a production sequencer action and cannot be started from
the plugin settings. The runtime library now exposes typed planning evaluation;
the test-only ASCOM sequence exercises Rust-selected capture and pending-image
feedback. It still uses a local fixture assignment, not server authorization.

The runtime host also exposes the sidecar's durable capture ledger through IPC 2.
Storage is opt-in for callers with an existing private state directory. It
supports reservations, observed outcomes, lookup, and bounded event replay;
restarts retain unresolved attempts and saved-image pending credit. The native
capture adapter and settings preview do not yet use this ledger. See the
[ledger integration boundary](docs/native-capture.md#durable-ledger-host).

## License

Apache-2.0. Copyright 2026 Yann Ramin (@theatrus).
