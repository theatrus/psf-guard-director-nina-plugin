# Director development

Use .NET SDK 10 on Windows x64, the .NET 8 desktop runtime for tests, and the
authenticated `gh` CLI. The plugin targets .NET 8 and NINA's 3.2.0.9001 API;
the same package runs in NINA 3.2 and 3.3.
NINA types belong in the plugin project; scheduling belongs in the shared Rust
core in PSF Guard. Read [the contributor guide](../AGENTS.md) before changing code.

## Build and test

```powershell
./fetch-runtime.ps1
./tools/fetch-nina-test-dependencies.ps1
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet format --verify-no-changes --no-restore
./tools/test-runtime-fetch.ps1
./build-package.ps1
```

`runtime.lock.json` pins a reviewed PSF Guard commit, successful CI run, artifact
identity, executable and license-notice hashes, and wire versions. Fetching does
not build Rust locally or silently select a newer artifact. The host verifies
the pin before starting the sidecar. Expired CI artifacts need a reviewed pin
update; installed packages contain the runtime and do not fetch it at startup.

The bundle contains the two Director assemblies, sidecar, runtime lock, licenses
and third-party notices. Three pinned .NET 8-compatible JSON assemblies preserve
strict wire validation on both hosts. It does not contain NINA, Target Scheduler,
Sync or the test-only simulator plugin. Building does not install or publish anything.

Managed tests exercise the pinned runtime and malformed pipe peers. Native
validation also needs the actual NINA plugin, simulated equipment and an
isolated PSF Guard server. Follow the [smoke-test procedure](nina-smoke-test.md).
Use private profiles and catalogs; never run these tests on real equipment.

### Collaboration Validation

The saved-evidence increment passed 956 Release tests, formatter verification,
and the locked development package build on 2026-10-07. Native public Session
acquisition passed on NINA 3.3.0.1065 with automatic workloads, and on
3.2.0.9001 with deferred capture/preparation check-in, each against an isolated
PSF Guard build with ASCOM simulators and bundled runtime 0.14.0 / IPC 14.
Each run saved three frames and acknowledged six capture events without
replaying hardware. The nightly files preserved `PGCAPID` and measured pixel
`PGHFR`; the unmeasured guider RMS was correctly omitted. Unit tests verify
recorded guider-scale conversion in both FITS and XISF metadata.

Local evidence paths:

- Nightly: `artifacts/nina-smoke-6c4e714d957c4004a450a8acbb0bc919/probe/b1f29fc8f20b421ebbc368ba4feed4ab/result.json`.
- Stable: `artifacts/nina-smoke-38bd58aab42f4e1383a69c7fedb919a6/probe/549c6ead861e424d82a397abae105dce/result.json`.

The separate PSF Guard browser test uses a local M31 assignment, public
import/activation APIs, a real TS schema, delayed image arrival, receipt-gated
review and offline report replay. Its pixel solutions and remote receiver are
test fixtures, not optical or live Starfront credit validation. The native
NINA runs use visible simulator targets, not the M31 assignment. This does not
claim a single live Starfront-to-TS/Sync-to-Director scientific-credit run.

On 2026-10-07 the standalone authentication increment passed 954 Release tests,
the locked development package build, and formatter verification. Tests cover
separate credentials, missing-credential recovery, uncertain enrollment,
browser approval and malformed or ambiguous replies. The real NINA 3.3.0.1058
host also completed public Director Session acquisition with the pinned
runtime 0.14.0, ASCOM simulators and an isolated PSF Guard server: three frames,
multiple targets, native centering/focus, session hooks, live status and saved
check-in replay. Local evidence is
`artifacts/nina-smoke-8cb6587f1aa44c5fb971677a62d03329/probe/aac2bb5d937643ffbafd7aa751a6736f/result.json`.
That acquisition regression uses local Director allocations. Standalone
collaboration registration does not yet import, admit or run remote workloads.

## Release

Run `./build-release.ps1` after validation. It checks the assembly version against
the registry template and produces a versioned ZIP and manifest containing its
SHA-256 in `artifacts/`. It does not publish them.

Publish with the authenticated `gh` CLI. Keep the package version, assembly
metadata, registry template and release notes aligned. Never replace an asset
on an existing release. The current release scripts produce GitHub prereleases;
changing that policy is separate from editing the README.

Copy the generated manifest into the
[theatr.us registry](https://github.com/theatrus/nina-plugins-registry) at
`manifests/p/PSF Guard Director/3.2.0.9001/manifest.json`. The registry has one feed;
manifests omit `Channel`. Verify both the published archive checksum and live
registry response before calling a release complete.

## README images

`docs/images/advanced-sequencer-*.png` are full NINA 3.3 nightly #65 screenshots
from an isolated, unarmed simulator profile. Their settings are test choices,
not defaults. `director-status.png` is a native WPF status render from the cloud
stop test run `nina-smoke-fdbf61b7855b415e96a4903b9d2893c6`, recorded in the
[cloud acceptance tests](nina-smoke-test.md#native-quality-screening-and-probes).
It displays real session status with synthetic quality evidence. Do not present
simulator results as proof of real-sky cloud accuracy.
