# Director development

Use .NET SDK 10 on Windows x64 and the authenticated `gh` CLI.
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

The bundle contains the two Director assemblies, sidecar, runtime lock, license
and third-party notices. It does not contain NINA, Target Scheduler, Sync or the
test-only simulator plugin. Building does not install or publish anything.

Managed tests exercise the pinned runtime and malformed pipe peers. Native
validation also needs the actual NINA plugin, simulated equipment and an
isolated PSF Guard server. Follow the [smoke-test procedure](nina-smoke-test.md).
Use private profiles and catalogs; never run these tests on real equipment.

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
`manifests/p/PSF Guard Director/3.3.0.1058/manifest.json`. The registry has one feed;
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
