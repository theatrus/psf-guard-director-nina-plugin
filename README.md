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
[PSF Guard's Director design](https://github.com/theatrus/psf-guard/blob/main/docs/design/director.md).
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
./tools/fetch-nina-test-dependencies.ps1
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet format --verify-no-changes --no-restore
./build-package.ps1
```

`runtime.lock.json` pins a reviewed PSF Guard commit, successful CI run,
artifact identity, executable and license-notice SHA-256 hashes, and all wire versions. Fetching never
builds Rust locally or silently takes a newer artifact. The same pin is embedded
in the runtime-host assembly and checked before execution. The executable stays
read-locked for the process lifetime. CI artifact pins are temporary development
inputs; expiry requires a reviewed update. Stable distribution will use durable,
signed release artifacts, not expiring CI links.

The package includes only the two Director assemblies, pinned sidecar, runtime
lock, license, and the sidecar's required third-party notices. Missing or changed
notices invalidate the fetch cache. It does not bundle N.I.N.A. assemblies, TS, or Sync. The build
does not install anything into a live N.I.N.A. profile or publish to a registry.

Tests exercise the real pinned child and adversarial pipe peers. They cover
startup/shutdown, crash, checksum verification, bounded framing, malformed and
stale replies, timeouts, cancellation before/after dispatch, and profile-scoped
runtime replacement. These are not the full-stack acceptance test: that still
requires N.I.N.A. with simulated equipment, Director's native execution adapter,
and an isolated PSF Guard instance built from the corresponding changes.

The WPF render test loads the compiled settings template and N.I.N.A.'s button
style, checks command enablement/contrast/bounds at 240, 280, 360 and 640 pixels, and
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

The runtime host also exposes the sidecar's durable capture and preparation
ledgers through IPC 6 (runtime 0.5.0). It can request read-only, ledger-backed
planning, discover interrupted work, report native-operation receipts, and
reserve a prepared capture after a fresh shared-core boundary check.
Graceful shutdown reads the final reply and closes the pipe before waiting for
child exit, so Windows cannot discard an unread reply during teardown.
Storage is opt-in for callers with an existing private state directory. It
supports reservations, observed outcomes, lookup, and bounded event replay;
restarts retain unresolved attempts, preparation operations, and saved-image pending credit. The native
capture adapter and settings preview do not yet use this ledger. See the
[ledger integration boundary](docs/native-capture.md#durable-ledger-host).

The typed program API binds immutable targets, exposure recipes, and equipment
capabilities to durable execution. It rejects changed capture evidence and
preparation settings while keeping selection policy in Rust. These APIs are
development groundwork, not an acquisition-ready NINA container or an update
to the published runtime preview.

The typed geometry API opens a separate geometry-bound ledger with the complete
site, Earth-orientation validity, horizon, altitude limits, and meridian policy.
Each evaluation, preparation boundary, and final reservation requires fresh
constraints. The shared Rust core computes whole-operation visibility and
preserves its original binding across restarts; the C# client never computes
replacement windows. Old program-only calls cannot bypass geometry mode.
This development pin depends on PSF Guard #483 and must not merge before that
runtime change. NINA horizon export, production dispatch, and the full server
acceptance gate still need to be connected. The published preview is unchanged.

## Preview releases

Run `./build-release.ps1` after the full test suite passes. It builds the same
seven-file bundle, checks its assembly version against the registry template,
and writes a versioned ZIP and manifest with the ZIP's exact SHA-256 under
`artifacts/`. It does not publish or install anything.

Publish those generated artifacts with the authenticated `gh` CLI as a GitHub
prerelease using the reported tag. Never replace assets on a published release.
Copy the generated manifest, not its template, into the theatr.us registry at
`manifests/p/PSF Guard Director/3.3.0.1058/manifest.json`. Verify the published
archive checksum and the live registry response before calling the release done.
The registry uses a single feed and rejects non-Release channels, so the manifest
omits `Channel`. The description and GitHub prerelease label explicitly identify
Director as experimental; feed placement does not make it stable. It requires
N.I.N.A. 3.3 nightly #58 or newer.
This runtime preview is not an acquisition controller and does not change Sync.

## License

Apache-2.0. Copyright 2026 Yann Ramin (@theatrus).
