# Real N.I.N.A. smoke test

Use the official N.I.N.A. 3.3 nightly #58 (`3.3.0.1058`) in a separate application
directory. Do not install it over an existing N.I.N.A. installation. The official
[nightly bundle](https://f002.backblazeb2.com/file/ninasetup/Nightlies/3.3.0.1058/NINASetupBundle_3.3.0.1058.zip)
contains a WiX bundle with an embedded MSI. Extract the bundle, then use an MSI
administrative extraction (`msiexec /a ... /qn TARGETDIR=...`) to unpack the app.
The test launcher takes the resulting directory containing `NINA.exe`.
Check the [official download page](https://nighttime-imaging.eu/download/) before
a new test campaign. On 2026-09-25 its latest nightly was still #58. If that
changes, update the NuGet, host-version, and source-contract pins together;
do not silently run a newer host against an unreviewed API contract.

Build the plugin package, then launch from PowerShell 7.4 or later:

```powershell
./build-package.ps1
./tools/start-nina-smoke.ps1 -NinaDirectory C:/test/NINA `
    -PluginZip ./artifacts/PSFGuardDirector-0.1.0.0-dev.zip
```

The command prints the process ID and a new `artifacts/nina-smoke-<guid>` path.
It never reuses an existing profile directory. The test-only .NET startup hook
sets N.I.N.A.'s public `CoreUtil.APPLICATIONTEMPPATH` field before application
startup. Profiles, logs, plugin storage, and caches based on that field go under
the new path. A per-launch ownership marker must match before the hook redirects
the field. The launcher stops the test process if isolation is not confirmed.
It loads a private copy of the hook so the running app does not lock build output.

This is data-directory isolation, not an OS sandbox. N.I.N.A. can still discover
hardware and make its normal network requests. Windows jump lists and .NET
per-executable user settings are outside the redirected root. Use an extracted
app in a test-only directory, and never connect real devices. A fresh default
profile starts with no device selected. By default the launcher installs only Director;
it does not copy TS, Sync, Chatstronomy, or any existing profile.

## Runtime host checks

1. Open Plugins and confirm Director appears with no loading errors.
2. Confirm Runtime is Stopped, Acquisition is Not armed, and Start is enabled.
3. Start the runtime. Confirm Ready and one child process under the test root.
4. Stop it. Confirm Stopped and that the child process exited.
5. Restart, terminate only that test child, and wait for the heartbeat. Confirm
   a visible fault, an exception in the isolated N.I.N.A. log, and no automatic
   restart. Click Start and confirm recovery to Ready.
6. While Ready, add a fresh profile under Options and load it. Confirm Director
   shows the new profile, stops its old child, and does not automatically start.
7. Start under the second profile, then exit N.I.N.A. Confirm the child exits.
   N.I.N.A. may ask about an unsaved sequence even with no acquisition configured;
   explicitly choose whether to save or discard this test-only sequence.
8. In another fresh test instance, start Director and terminate only the test
   N.I.N.A. process. Confirm its runtime exits without separately killing it.

Do not publish complete N.I.N.A. logs without review: discovery logs can include
local device names, network addresses, and filesystem paths.

## Runtime host evidence

Tested on 2026-09-25 with the official nightly above, Director host commit
`0bc4abc1c84c5d06e00d737667e6ce582b342008`, and the pinned Rust runtime in
`runtime.lock.json`:

- Real plugin discovery and MEF initialization succeeded without TS installed.
- Settings loaded inside N.I.N.A., with working Start/Stop enablement.
- Start reached Ready; Stop removed the child process.
- Forced child termination changed Ready to Faulted on the next heartbeat
  (about five seconds), logged the failure, and allowed explicit recovery.
- Changing profiles stopped the runtime and updated the profile label.
- Starting under the second profile succeeded.
- Normal N.I.N.A. shutdown removed both the application and runtime processes.
- A second launch with the final helper passed isolation checks. Rebuilding
  succeeded while it ran. After Ready, terminating only N.I.N.A. also caused
  the runtime to exit without intervention.

No hardware was connected and no images were acquired in those host checks. They validate
the real runtime host, not the full-stack acquisition acceptance gate. That
still requires the native N.I.N.A. acquisition adapter, simulated devices, and
an isolated PSF Guard server built from the corresponding changes.

## ASCOM capture sequence

Install the ASCOM Platform with its OmniSim camera, telescope, and filter wheel.
Do not run this alongside another client using the same simulator: its device
state is shared even though N.I.N.A.'s profile is isolated. Close other test
instances before starting another run.

```powershell
./build-package.ps1
./tools/start-nina-smoke.ps1 -NinaDirectory C:/test/NINA `
    -PluginZip ./artifacts/PSFGuardDirector-0.1.0.0-dev.zip -AscomSequence
```

This opt-in mode builds a test-only plugin and verifies that the bundle's plugin
and runtime DLLs match the build. It creates a fresh simulator-only profile and
starts the supplied advanced sequence through N.I.N.A.'s command-line interface.
It refuses non-OmniSim camera/mount/filter selections, other configured devices,
an unconfirmed isolation root, or an image destination outside the test root.
Do not interact with equipment/profile controls while the probe is running.

The sequence starts the verified sidecar, connects the three simulators,
unparks, and slews a small offset from the simulated position. It supplies a
fixture assignment with three prioritized one-exposure filter goals to Rust.
Rust selects the next goal; the host prepares its filter, obtains a fresh Rust
decision at the adapter's dispatch callback, and captures through `NinaCaptureAdapter`.
It waits for correlated save receipts, reloads each FITS through N.I.N.A., checks
`PGCAPID` and nonblank pixel data, and compares the durable journal with the
returned evidence. Cleanup parks and disconnects the simulators and stops the
sidecar, including after a capture failure. Cleanup failures fail the test.
Each successful save increments pending work and consumes one fixture attempt;
it does not mark the image accepted. The final Rust result must be
`wait: pending_assessment`, not `complete`.

Inspect `<test-root>/probe/<run-id>/result.json` for `passed: true`, three
captures, and no errors. Images live under `<test-root>/images`; journals are
under the run's `journal` directory. The launcher returns after startup, not
after completion: an absent result is **not** a pass. The N.I.N.A. log records
each probe step. The result also records every planner snapshot and evaluation,
including revalidation after filter preparation. Close the isolated app after
inspecting the result.

The test assembly and profile/sequence fixtures are excluded from the plugin
ZIP by its explicit file allowlist. CI compiles the probe and tests its guard
and fixture contracts; it does not run a desktop ASCOM sequence.

### Scope

This is Rust-selected native capture with a local fixture assignment, not a
production autonomous Director session. The dispatch callback checks simulator
context and asks the real core again, but there is no PSF Guard assignment,
durable sidecar ledger, or resume/retry path. The host applies saved-image
evidence to pending counts; selection remains in Rust. The fixture hard-codes
simulated-safe conditions and unrestricted eligibility, so it cannot validate
physical sky visibility or hardware safety. No TS, Sync, or Chatstronomy plugin
is installed in this profile.

Remaining full-stack coverage includes the server assignment/feedback loop,
core-authorized dispatch and replanning, autofocus/plate solving/guiding,
safety and meridian/horizon boundaries, crash recovery, and coexistence with
Sync and Chatstronomy. A simulator sequence alone does not satisfy those gates.

### Local capture evidence

On 2026-09-25, nightly #58 with ASCOM Platform 7.1.3.4851 and OmniSim driver
version 0.5 completed two native capture sequences. The final run included the
FITS readback checks and reported `passed: true`, three captures, and no errors.
All three 800x600 images retained their journal's `PGCAPID` and had nonconstant
pixels. Total adapter times were 1521, 1520, and 1382 ms for the one-second
Red, Green, and Blue exposures. These include exposure/download and save, not
the preceding filter changes or slews. The mount parked, all three simulators
disconnected, and the owned sidecar exited. The image was also visible in
N.I.N.A.'s Imaging view with populated star/HFR history.

The original transport-only probe passed 81 automated tests. A follow-on real
nightly run with the typed planner bridge passed three Rust-selected captures,
including six acquisition decisions (selection and revalidation per filter) and
one terminal `wait: pending_assessment`. All FITS readbacks and journals matched;
cleanup completed without errors. The extended automated suite passed 102 tests,
covering real-core outcomes, preparation-induced reselection, malformed and
uncorrelated replies, deadlines, cancellation, and lifecycle/concurrency behavior.
The simulator desktop run is local evidence, not a hosted CI result.

The first registry preview was rechecked on 2026-09-25 with runtime source
`3f67369e76fad0525a1194dbd42ff25b82d921c5` (IPC 2) and the same nightly and
simulators. All three Rust-selected RGB captures passed FITS readback and capture
identity checks; the final decision was `wait: pending_assessment`. The probe
reported no errors, parked and disconnected the simulators, and stopped its
sidecar. The installed plugin's Start/Stop controls also reached Ready/Stopped
without arming acquisition or leaving a child process. Both buttons fit at the
default 800-pixel NINA window width. The automated plugin suite passed all 148
tests, followed by ten ledger stress runs covering 500 rapid restarts. The
runtime now waits briefly for transient storage ownership contention; a live
owner still prevents a second sidecar from acquiring the same directory.
This remains fixture-driven preview evidence, not
the server-assignment or production acquisition acceptance gate.
