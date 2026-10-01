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
The Coordinator section pairs with a Director code, stores the resulting
credential in Windows Credential Manager, and can reset local pairing. It never
displays an API key. A configured coordinator must be paired before starting its
rig's runtime. Pairing is not permission to acquire.
The read-only coordinator client validates and caches program previews against
an explicit database/rig/profile binding. A separate capture checkpoint client
delivers durable ledger evidence in bounded batches and resumes after restart.
Neither is yet a production background session or target executor; see
[coordinator intake](docs/native-capture.md#read-only-coordinator-intake).
Separate allocation intake now accepts an operator-issued first allocation for
the exact paired client, persists it for explicit offline use, and refuses changed
or expired grants. It cannot admit or renew a grant itself; see
[issued allocations](docs/native-capture.md#issued-allocation-intake).
There is no Chatstronomy adapter yet.

The Advanced Sequencer now offers **Director Session**, a configuration preview
with seven TS-style instruction slots, native trigger/condition editors, grouped
local-policy fields, and a status view. It saves and clones configuration, not
credentials or acquisition authority. Running it reports an explicit readiness
error until production admission, safety and orientation sources are wired.
See [session configuration](docs/native-capture.md#session-configuration-preview)
for what is implemented and what remains gated.

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
feedback. The optional server-plan smoke activates and pulls an actual PSF Guard
program, captures during a server outage and verifies restart, batch check-in
and duplicate replay. Both probes use NINA's native safety simulator and dated,
read-only Earth-orientation cache, not production acquisition authorization.

The runtime host also exposes the sidecar's durable capture and preparation
ledgers through IPC 8 (runtime 0.7.0). It can request read-only, ledger-backed
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
development groundwork, not an acquisition-ready NINA container.

The typed geometry API opens a separate geometry-bound ledger with the complete
site, Earth-orientation validity, horizon, altitude limits, and meridian policy.
Each evaluation, preparation boundary, and final reservation requires fresh
constraints. The shared Rust core computes whole-operation visibility and
preserves its original binding across restarts; the C# client never computes
replacement windows. Old program-only calls cannot bypass geometry mode.
The internal native geometry exporter now joins NINA's complete
horizon/site snapshot with the matching equipment fingerprint for this client.
The local orientation reader and continuous safety interlock are tested in the
native probe. Production dispatch, immutable allocation admission, and the full
server acceptance gate still need to be connected.

The two dispatch-check APIs recheck an issued preparation command or reserved
capture after native before-hooks. They preserve the original command, attempt
budget and capture evidence; a successful check cannot authorize replay after
recovery. Replies include the core's exact evaluation time and inclusive latest
start. Native dispatch accounts for monotonic validation/IPC time (rounded up)
and rechecks wall-clock validity after native evidence, so a late successful
reply cannot start equipment. The runtime pin uses the reviewed and merged head
of PSF Guard PR #518, built by the three-platform Director CI. A changed runtime head requires a
reviewed pin update. The isolated native simulator sequence uses session-bound post-hook
checks; production container adoption and full-stack acceptance remain separate
work.

## Preview releases

Version 0.1.0.1 updates the bundled runtime to 0.6.0 / IPC 7 and includes the
internal durable-ledger, geometry, and post-hook dispatch clients. Its public
settings still control only Start/Stop and runtime status. It does not add
pairing, server assignments, or a production acquisition container. See the
[preview release notes](docs/releases/0.1.0.1.md).

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
