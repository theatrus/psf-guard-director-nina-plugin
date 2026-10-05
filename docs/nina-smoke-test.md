# Real N.I.N.A. smoke test

## Bounded Native Recovery

2026-10-04: packaged Director, pinned runtime 0.10.0 / IPC 10, NINA 3.3.0.1065,
ASCOM OmniSim and private PSF Guard (main `212b4a6`) passed these native gates:

- `-PublicAcquisition -LocalTargetScheduling -AutomaticWorkloads -NativeImaging
  -RecoveryScenario focus-once`: one failed native autofocus is retried, then
  normal multi-target acquisition saves three frames and delivers the original
  capture/preparation receipts. A new run cannot reset the recorded night.
  Evidence: `artifacts/nina-smoke-91a92d03e2c5450f86a8fd08d181bc5e/probe/8c100a91df3544ef9762d36c09def44c/result.json`.
  Repeated after the per-rig journal and stopped-state guard review:
  `artifacts/nina-smoke-89070a422f5741c58e03cff4c859c26d/probe/911f76a7f792497aaee5dbfa7cc3960d/result.json`.
- The same options with `-RecoveryScenario focus-always`: initial autofocus and
  exactly one allowed retry fail; no science exposure is saved, the mount parks,
  and local acquisition ownership is released.
  Evidence: `artifacts/nina-smoke-2a288b60149a4ab786cf35e3332cadfb/probe/395618129c4049d4800d4d7ef96b0a69/result.json`.

These use test-only focus results, real NINA actions and simulated equipment.
They do not prove optical autofocus quality or roof/weather resumption.
The regression suite also covers cancellation, unknown/timeout outcomes,
unsettled equipment, safety loss during cooldown and durable stop readback.
All 840 plugin tests, development packaging, whitespace verification and
`git diff --check` passed. Hosted CI is not counted as a local test result.

### Documentation screenshots

Launch an idle, unpaired profile without executing any instructions:

```powershell
./tools/start-nina-smoke.ps1 -NinaDirectory '<reviewed nightly directory>' `
  -PluginZip ./artifacts/PSFGuardDirector-0.1.0.0-dev.zip `
  -AscomSequence -DocumentationOnly
```

Open Sequencer, then Advanced Sequencer. The checked-in documentation fixture
shows the real plugin's Session and Instructions tabs; acquisition remains off.
The unedited window captures in `docs/images/advanced-sequencer-*.png` were taken
in NINA #65 with this fixture. They contain no pairing credentials or real-rig
data. Separate render checks cover all tab fields at 640 and 1000 pixels.

## Live and Deferred Check-In

2026-10-02: the packaged plugin, runtime 0.9.0 / IPC 9, native NINA
3.3.0.1064, ASCOM OmniSim and an isolated PSF Guard server passed:

- `-PublicAcquisition -LocalTargetScheduling -DeferredCheckIn`: three captures
  across two targets remain local with end check-in disabled. Explicit saved-run
  delivery acknowledges six events, repeated delivery sends zero new events,
  and the native Director Check In instruction succeeds. The original permit
  still cannot launch again.
  Evidence: `artifacts/nina-smoke-df62881bd09f40219642e86f3b366b29/probe/4ed5bbba7cc04f409b7a47edd961ddcb/result.json`.
- `-PublicAcquisition -LocalTargetScheduling -AutomaticWorkloads -OfflineWorkloadRelease`:
  disconnect after admission, finish and park locally, then restore the server.
  Saved-run check-in delivers the original ledger, confirms one historical
  workload release and lets intake advance to waiting for quality assessment.
  Repeating check-in does not repeat captures or release a newer workload.
  Evidence: `artifacts/nina-smoke-59f76292fe6542bb9703f8343207d342/probe/1e45ac2dbada45a4bee005f22feb3b73/result.json`.
- `-PublicAcquisition -LocalTargetScheduling -MoonAvoidance`: the ordinary
  live/offline path still captures three images across two targets, honors the
  Moon gate, waits parked and later delivers all six receipts without replay.
  Evidence: `artifacts/nina-smoke-84f379be265049a59dcd1c92d56cccc8/probe/afe8d288e2c24c3791d92a0e3d7a6f19/result.json`.

All 771 unit tests passed. Development packaging completed with zero warnings
or errors; format verification, `git diff --check` and the preview-manifest guard passed. Native
Session screenshots at 640/1000 px and settings at 360 px were visually reviewed.
The twelve selectors bind correctly and fit their bounds. Review made initial
backlog delivery count against the session time limit, removed queued progress
callbacks that could overwrite a terminal status, and added malformed release
acknowledgement tests. End-of-session delivery drains bounded pages rather than
stopping after one batch.

These tests cover capture evidence and clean terminal release, not grade pulls,
timing/recovery journal delivery, automatic night restart or production equipment.

## Configurable Abort

2026-10-02: the packaged plugin, runtime 0.9.0 / IPC 9, native NINA
3.3.0.1064, ASCOM OmniSim and an isolated PSF Guard server passed:

- `-PublicAcquisition -PublicUnsafe -AbortWithoutPark`: abort a 30-second
  exposure offline, stop slewing/tracking without parking, release local
  ownership and remain stopped after safety recovery. Check the final status.
  Evidence: `artifacts/nina-smoke-14f233d427f547c7bb2e777b38558fa6/probe/8d82d09cbbea49078ef4391f66bb7b80/result.json`.
- `-PublicAcquisition -PublicUnsafe`: the same interruption with the default
  park policy, including final parked status and no automatic restart.
  Evidence: `artifacts/nina-smoke-ffd25907d9f94c009adb1d655fb70a30/probe/cef5955904004c02a7418af1f485dc69/result.json`.
- `-PublicAcquisition -EnclosureClosure`: shutter closure still overrides
  parking and stops acquisition/motion while offline and weather remains Safe.
  Evidence: `artifacts/nina-smoke-c2bdd456d45043aba9147c937ca42ecb/probe/3c82acb132f542adab430a492b73dd76/result.json`.

All 751 tests passed. `build-release.ps1` now completes development/release
packaging with zero build warnings; `tools/test-preview-manifest.ps1` accepts
the current template and rejects four invalid variants. Full format verification
and `git diff --check` passed. Native screenshots at 640/1000 px were visually
reviewed; all eleven policy selectors have valid bindings and fit their bounds.
The new stop-only test exposed the old mock's incorrect tracking-return
semantics. NINA returns the resulting state (`false` for tracking-off); the
implementation and mocks now match that contract. Review also added fallback
stopping after any park failure and replacement-device checks between commands.
The final package reran all three native scenarios after adding a shared
park-failure latch: terminal cleanup cannot retry a failed planned-wait park.

## Enclosure Clearance

2026-10-02: the public Session uses independent native enclosure clearance for
acquisition and shutdown. The simulator fixture explicitly selects Open air;
the `-PublicAcquisition -EnclosureClosure` variant configures only the ASCOM
OmniSim dome, opens it, then closes it during a 30-second exposure with the
server offline and the weather monitor still Safe. It verifies camera abort,
slew-stop/tracking-off, no park and no restart after reopening. The test fixture
then opens the simulator for its own final cleanup; Director never opens it.

Final production code, runtime 0.9.0 / IPC 9, NINA 3.3.0.1064 and isolated local
PSF Guard passed these native tests:

- Enclosure closure:
  `artifacts/nina-smoke-180d202ee1ae49e6923fdf907da894ed/probe/661b381debd8415aa981bbf2967ea73a/result.json`.
- Weather Unsafe with independent open-air clearance, park and no restart:
  `artifacts/nina-smoke-88bc993b0c984c688f563f594cb0f227/probe/6b6947a72ee54851b0d850064d1cc36a/result.json`.
- Offline multi-target/Moon avoidance, three saves, six batch receipts and
  server replay refusal:
  `artifacts/nina-smoke-a0376354c9ae41f4934c34cf7eb0eeab/probe/765310a08bf8489c9fe3e66b03be507f/result.json`.

The package built without warnings and all 742 tests passed. Full `dotnet format
--verify-no-changes --no-restore` and `git diff --check` passed. Native template
checks cover ten policy selectors, bounds and nonblank screenshots at 640/1000
px; both widths were visually reviewed. All test instances closed normally.
Review added explicit mount-stop cleanup and refusal of cached registration
evidence. One intermediate multi-target probe failed because its second session
omitted the new policy; the replay fixture now copies that policy and passed.

These tests prove the local enclosure gate, not persistent observing-night
admission, cloud classification or automatic recovery probes.

## Recovery Client Adoption

2026-10-02: runtime 0.9.0 / IPC 9 / recovery contract 1 is pinned from
PSF Guard `59f9ed9cb088297e8f77f762ddfe3df929a55bf0`, successful build
36977340197. The public NINA Session still launches with recovery disabled;
this validates existing acquisition, not native quality-pause/recovery support.
The official download page still lists nightly #64 (`3.3.0.1064`).

The package built without warnings. All 712 tests passed, including real-sidecar
quality hold/probe completion, stop/park persistence across restart, replay
without issuance, malformed replies and interrupted submission. Whitespace and
diff checks passed. Two full-stack runs used native NINA #64, ASCOM OmniSim,
the packaged plugin and an isolated local PSF Guard server:

- Offline multi-target priority, Moon avoidance, three saved images, six batch
  acknowledgements and replay refusal:
  `artifacts/nina-smoke-0a7bb85dfa0243baa0c562682d6fffc8/probe/c356ff4284044a42864f46deeee8d5a6/result.json`.
- Unsafe exposure cancellation, park and no restart after returning Safe:
  `artifacts/nina-smoke-b7b5dc5233af4b148a4fbc90b20c9984/probe/17963407d9974eb0bfb7f5b27f01349d/result.json`.

Both runs verified native editor rendering, equipment review and live status.
The isolated instances closed and revoked their test pairings. Review tightened
snapshot monotonicity, exact enum decoding and one-shot issuance validation.
Native commissioning, independent roof clearance and recovery dispatch remain
the next implementation and simulator gate.

## Moon Avoidance Test

Run `run-server-plan-smoke.ps1 -PublicAcquisition -LocalTargetScheduling
-MoonAvoidance` with the isolated server and package arguments below. The
fixture adds a fourth goal with priority 100 and an always-blocked Moon-down
rule. The public container must ignore that goal, acquire the other three
exposures across two targets during a server outage, then enter its native
wait hooks parked with `moon_avoidance`. The probe cancels the wait, restores
the server, verifies exactly three pending images and no credit for the
blocked goal, and refuses replay. The connected automatic-workload variant
uses enabled fractional Moon settings to exercise v2 capability admission.

2026-10-01 validation: NINA 3.3.0.1064 nightly, ASCOM OmniSim, bundled runtime
0.8.0 / engine 0.3.0 / IPC 8 and an isolated PSF Guard server. Both passed:

- Offline lunar-blocked priority and parked wait:
  `artifacts/nina-smoke-3050d3783ec5403a855d5bbc62d1cead/probe/3975fc2c18e24826b6ac485573a3c463/result.json`.
- Connected v2 automatic intake, release and pending-assessment wait:
  `artifacts/nina-smoke-ec12e6ca28e04f42a2aae3aaea860e52/probe/e71c2ed748254e8299a5a028e2595fd0/result.json`.

The full plugin suite passed 681 tests. Review also corrected stale release
descriptions; the second native run used that rebuilt committed package.

## Local target scheduling test

Run `run-server-plan-smoke.ps1 -PublicAcquisition -LocalTargetScheduling` with
the isolated server and plugin package arguments below. The fixture activates
two projects/targets with three goals. The second target has higher priority.
The public Session must select it first, slew through NINA's inherited-coordinate
instruction, save one image, close its target hooks and slew to the other target
while the server is stopped. It saves the other two images locally, parks,
then the probe restores the server and delivers six events without duplicate
credit. Replay of the consumed allocation is refused.

Add `-AutomaticWorkloads` for the connected variant: it commissions both active
projects, requests `local_sequence_v1`, retries the same grant, releases after
all captures/park/check-in, then waits for pending assessment without recapturing.
Both variants verify target order from the saved capture journals and the exact
nine hook boundaries. UI rendering checks four tabs at 640/1000 px with nine
labeled switches. No TS plugin is installed or needed.

2026-10-01 evidence on NINA 3.3.0.1064 / ASCOM OmniSim / runtime 0.7.0 / IPC 8:

- Offline target switch:
  `artifacts/nina-smoke-e3044959c41a443fadd28cd1ef03c6af/probe/321a8b087f0441a993b81b3a2b8933ed/result.json`.
- Multi-target automatic intake/release:
  `artifacts/nina-smoke-4987c2a7c0704d63aee495c083720fc3/probe/880554a1675f4bf391168426494ac077/result.json`.
- Prepared-target unsafe cancellation, park and no restart after safe recovery:
  `artifacts/nina-smoke-67d5437c2e7a4f529cde11604a2dcb27/probe/fb178c4799134650aff983c9f53bb531/result.json`.

Both multi-target variants were rerun with the committed plugin package and
PSF Guard rebased on main through `ebfd93f8`. Both passed. The server's Director
suite passed 97 tests with one existing ignored benchmark; server build and
library clippy with warnings denied also passed.

The serial plugin suite passes 675 tests, including local selection after slow
setup, one-shot/unknown setup results, outstanding mode stability and atomic
background status updates. These tests do not establish automatic equipment
policies, learned duration estimates, active-grant quality correction or
offline cold-start/recovery authorization.

## Automatic workload test

Build a schema-18 PSF Guard server and the development plugin package, then run
the command below with `-PublicAcquisition -AutomaticWorkloads`. The isolated
operator commissions one exact reviewed client/profile/configuration and active
project, but does not POST allocation admission. The paired client persists and
retries its request UUID; the real public Session then acquires three FITS
frames, acknowledges all six reservation/save events, parks, and seals the
workload. The next request waits for pending quality assessment without issuing
duplicate work. The probe cancels that wait and verifies replay refusal and the
seven-slot hook/save boundaries. This variant is connected; use the separate
public outage and Unsafe variants below for those regressions.

The 2026-10-01 run passed on NINA `3.3.0.1064`, ASCOM OmniSim and runtime
0.7.0 / IPC 8. Evidence:
`artifacts/nina-smoke-cbe57faf5bac406b9190296622b6d77c/probe/f0ad8754c4e64883bb38317ef51621b8/result.json`.
It reports `automatic_workload_verified: true`, six acknowledged events, three
correlated saved files, native equipment review and live status. The full serial
plugin suite passed 662 tests. This proves automatic intake/clean release for
one prepared target, not multi-target execution, grade feedback or crash recovery.
The manual public offline regression also passed with the same final package:
`artifacts/nina-smoke-4666d02721da4e43a04abf22ef4e03bf/probe/361ff629cd34499c89101d41cd73e2ad/result.json`.
The separate Unsafe regression passed with native exposure abort, park and no
restart after recovery:
`artifacts/nina-smoke-7c71d07f32824db2ad90a361cb18c9a6/probe/19c7d9d9df534be499856e6869be6c52/result.json`.

## Server-plan outage test

The September 30 local-evidence increment passed on NINA `3.3.0.1064` with
runtime 0.7.0 / IPC 8. It read dated IERS rows from NINA's cache, ran all three
filtered captures and offline/restart/check-in assertions, then interrupted a
running native Wait through the safety simulator. Returning to Safe did not
revive the canceled owner. Final evidence:
`artifacts/nina-smoke-2dcb4e2122be49fb919e26ebb799c36e/probe/2d10555875864a6c9d03d04f4acb81aa/result.json`.
The package build, formatting, diff check and all 612 plugin tests passed locally.
No public acquisition gate was removed.

Build PSF Guard with immutable first-allocation admission (meta schema 15) and
build this plugin's development ZIP, then run:

```powershell
./tools/run-server-plan-smoke.ps1 -PsfGuardExe C:/test/psf-guard-cli.exe `
    -NinaDirectory C:/test/NINA `
    -PluginZip ./artifacts/PSFGuardDirector-0.1.0.0-dev.zip
```

This requires Python's standard `sqlite3` module to seed a profile in the new
empty catalog, plus the existing OmniSim drivers and current daily IERS rows in
NINA's local astronomy cache. The cache is read-only; missing rows fail the test.
The script creates a unique
temporary server directory, registry, meta database and catalog, verifies the
loopback listener belongs to its process, and starts an isolated NINA profile.
It does not use any existing PSF Guard registry or NINA profile.

The test-only operator client reports actual native equipment and filter labels,
saves framing/plan drafts, and applies activation for three one-frame goals.
The real paired preview client pulls and caches the returned program twice.
The operator then admits the reviewed first allocation for that paired client,
retries admission, and the allocation client validates/caches the issued snapshot.
The harness stops the server; the probe verifies a connection failure, validates
the persisted allocation, captures three FITS frames and restarts its durable
sidecar entirely offline. After the server restarts, it checks unchanged program
identity, delivers the capture
receipts, resumes its cursor and replays duplicates. A fresh program must show
exactly one pending image per goal, while the issued allocation remains unchanged.
Paired status reports must be visible in the
server's live rig inventory before and after the outage. Cleanup revokes the
test credential, removes it from the vault, parks/disconnects the simulators,
and closes the owned processes. Results and server logs remain for inspection.

The non-secret fixture flags `ActivateSimulatorPlan` and `ExerciseOutage` are
explicit opt-ins. Never supply them against a live server. This proves the
server-plan/native-capture/check-in path, not a production session container:
safety uses NINA's built-in simulator and Earth orientation uses its local cache.
Pointing uses the simulator's current coordinates, and no slew, autofocus, guiding,
flip or unattended safety recovery is claimed. A program preview remains
inspection data. The server-issued allocation is exercised by the guarded test
probe, not the public session container; production ownership/recovery gates
remain unfinished.

The allocation run passed on September 30 with the same nightly/runtime and the
new local server. Evidence:
`artifacts/nina-smoke-41b2ad183d094c89812ca07605dce3b8/probe/b183a26338e34405b553743705ed206a/result.json`.
The ledger's assignment ID is the issued `allocation-...`, not the preview ID.
The full plugin suite passed 625 tests with
`dotnet test --configuration Release --no-build -- xUnit.MaxParallelThreads=1 xUnit.ParallelizeTestCollections=false`.
Parallel runs showed intermittent existing sidecar timeout/cleanup-lock failures;
the serial run and native test passed. No public acquisition gate was removed.

On 2026-09-30 the combined test passed on nightly #64 (`3.3.0.1064`) with
runtime 0.7.0 / IPC 8 and the PSF Guard handoff correction. It saved three
correlated FITS images, completed seven native preparation operations and six
inherited exposure hooks, retained progress across a sidecar restart during
the server outage, and acknowledged all six reservation/save events after
reconnect. A sender restart delivered no new events; duplicate replay did not
increase pending credit. Both paired live-status reports appeared in the rig
inventory, and cleanup reported no errors. Evidence:
`artifacts/nina-smoke-7261dfdeeefc410f9ea6000620b3a350/probe/171d2afc78294858b3de51be0e3d6c89/result.json`.
All 575 plugin tests passed. No production acquisition release was enabled.

## Paired capture receipt smoke

`-CoordinatorFixture <absolute-json-path>` extends `-AscomSequence` with a
disposable loopback PSF Guard server. The JSON contains only `Endpoint`,
`CoordinatorInstanceId`, `CatalogId` and `RigId`; never put a code or token in
it. Bind an empty test database to that rig first. The probe issues a code using
trusted local operator access, exchanges it through the real plugin client,
and stores/reads the credential in the Windows vault for its isolated profile.
It uses the paired rig identity in its fixture assignment. After native captures
and a sidecar restart it checks in the six reservation/save events, recreates
the sender to verify cursor recovery, and replays from a separate cursor to
verify duplicate acknowledgements. Cleanup revokes the test client and removes
its credential. This opt-in probe must never point at a live server/catalog.

On 2026-09-27, this path passed with NINA nightly #59, OmniSim camera/mount/wheel,
the bundled runtime 0.7.0 / IPC 8, and an isolated PSF Guard pairing build.
Three FITS captures, seven preparation operations and six inherited trigger
calls passed; all six capture ledger events were acknowledged, cursor restart
and duplicate replay succeeded, and cleanup reported no errors. Evidence:
`artifacts/nina-smoke-e60b4c8bf97d4fa6922cb47a360cb037/probe/8707ef9c7d6644599f3e1846693cb785/result.json`.
This is fixture-driven acquisition plus real paired receipt delivery, not
server-issued acquisition authorization, preparation telemetry or a production
session container. The isolated NINA instance was closed normally afterwards.

## Host setup

Use the official N.I.N.A. 3.3 nightly #64 in a separate application
directory. Do not install it over an existing N.I.N.A. installation. The official
[nightly bundle](https://f002.backblazeb2.com/file/ninasetup/Nightlies/3.3.0.1058/NINASetupBundle_3.3.0.1058.zip)
contains a WiX bundle with an embedded MSI. Extract the bundle, then use an MSI
administrative extraction (`msiexec /a ... /qn TARGETDIR=...`) to unpack the app.
The test launcher takes the resulting directory containing `NINA.exe`.
Check the [official download page](https://nighttime-imaging.eu/download/) before
a new test campaign. On 2026-09-30 its latest nightly was #64 (`3.3.0.1064`).
The launcher allows #58, #59, #64 and #65, while the plugin keeps #58
as its minimum API baseline. The #59 SOFA, NOVAS and JPLEPH files match the pinned
test dependencies exactly. Review newer host contracts before adding them to
this allowlist; a minimum-version declaration alone is not compatibility evidence.

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
It refuses non-OmniSim camera/mount/filter selections, a safety monitor other
than the built-in NINA simulator, other configured devices,
an unconfirmed isolation root, or an image destination outside the test root.
Do not interact with equipment/profile controls while the probe is running.

The sequence starts the verified sidecar, connects the three ASCOM simulators
and NINA's built-in safety simulator, and
parks the mount to establish the unpark test precondition. It supplies an
immutable fixture program with three prioritized one-exposure filter goals to
Rust's durable geometry ledger. The isolated profile has an explicit synthetic
site (35 degrees north, 120 degrees west, 1000 m). The fixture reads dated
Earth-orientation rows from NINA's cache and supplies native horizon vertices,
altitude limits and meridian policy. A continuously checked native safety
interlock cancels the session on stale/unsafe evidence. The test still supplies
its own three-minute conditions horizon, not a production forecast or permit.
Rust selects goals and issues unpark and filter/readout preparation
commands. The probe runs matching native NINA items inside a transient native
sequential container. Each item allows one dispatch, revalidates after inherited
before-triggers, and verifies the resulting unparked mount, readout, or settled filter state.
The probe requires both a finished item and a successful completion receipt.
Reported monotonic durations cover dispatch validation and the operation, not
the preceding inherited triggers.
Rust rechecks the remaining preparation after native before-hooks, and rechecks
each reserved capture after native before-exposure hooks. The session-bound
callbacks cannot be reused or revived by restarting the sidecar. Rust also
rechecks the boundary when reserving each prepared capture. The host obtains
its saved binding and runs a `NinaExposureItem` through nested native sequential
containers. Its parent has test-only exposure hooks; the probe requires one
inherited before-hook before boundary validation and one after-hook after the
correlated save. Capture still uses `NinaProgramCapture` and `NinaCaptureAdapter`.
It waits for correlated save receipts, reloads each FITS through N.I.N.A., checks
`PGCAPID` and nonblank pixel data, and compares the durable journal with the
returned evidence. Cleanup parks and disconnects the simulators and stops the
sidecar, including after a capture failure. Cleanup failures fail the test.
Each successful save is recorded in Rust; the C# probe no longer edits pending
counts or attempt budgets. A save is not an accepted grade. The final Rust result
must be `wait: pending_assessment`, not `complete`. The probe then restarts the
sidecar, reopens the same immutable program, and checks ledger identity and
pending progress survived without another capture.

Inspect `<test-root>/probe/<run-id>/result.json` for `passed: true`, three
captures, and no errors. Images live under `<test-root>/images`; journals are
under the run's `journal` directory. The launcher returns after startup, not
after completion: an absent result is **not** a pass. The N.I.N.A. log records
each probe step. The result requires seven preparation receipts (one unpark and
six filter/readout operations) and records ledger decisions,
six exposure-hook records, the ledger identity, equipment/constraint snapshots,
and saved captures. The
Rust ledger is in the run's `state` directory. Close the isolated app after
inspecting the result.

The test assembly and profile/sequence fixtures are excluded from the plugin
ZIP by its explicit file allowlist. CI compiles the probe and tests its guard
and fixture contracts; it does not run a desktop ASCOM sequence.

### Scope

On 2026-09-27 the isolated sequence passed on official nightly #59 with the
IPC 8 / runtime 0.7.0 artifact pinned from PSF Guard #518. It recorded three
verified FITS captures, seven successful preparation receipts, six inherited
exposure-hook events and no errors. Restarting the sidecar retained ledger
identity and `wait: pending_assessment` rather than acquiring extra frames.
Same-path horizon edit detection, simulator park/disconnect, sidecar shutdown
and normal N.I.N.A. close also succeeded. The combined plugin passed 494
automated tests, including the native instruction-slot tests; this desktop
fixture does not yet invoke all seven configured instruction slots.
The final run includes the one-use synchronous dispatch guard after journal and
progress work, with final prepared-equipment and native-setting checks. Evidence:
`artifacts/nina-smoke-e93231424a95409f977e8ced5b0f592c/probe/54967cb98af14d18baccfd136a1508b7/result.json`.

This is durable Rust-geometry native capture with a local fixture, not a
production autonomous Director session. The dispatch callback checks simulator
context, unchanged horizon/configuration, the fixture deadline, and the reserved
attempt through the shared-core post-hook checks. There is no
PSF Guard assignment or automatic crash/resume path. The fixture hard-codes
simulated-safe conditions, synthetic EOP and broad authorized intervals. Rust
still applies its physical visibility calculation, but this does not validate
a real site's orientation source or hardware safety. The fixture uses the simulator's
initial pointing; it does not slew or validate plate solving. No TS, Sync, or Chatstronomy plugin
is installed in this profile. Native preparation items run through NINA's normal
container strategy. Automated tests exercise inherited triggers and conditions;
the desktop fixture exercises test-only inherited before/after exposure hooks.
It is not a production Director session container and does not execute real
autofocus, dither, or guiding hooks. In particular, NINA can
swallow item failures or return after cancellation; a returned task alone must
never be recorded as a successful equipment operation.

Remaining full-stack coverage includes the server assignment/feedback loop,
core-authorized dispatch and replanning, autofocus/plate solving/guiding,
safety and meridian/horizon boundaries, crash recovery, and coexistence with
Sync and Chatstronomy. A simulator sequence alone does not satisfy those gates.

### Local capture evidence

On 2026-09-26, the IPC 7 geometry/dispatch development build passed nightly
#58/OmniSim with full native horizon export and post-hook Rust feasibility
checks. Seven preparation operations, three RGB FITS captures with identity and
pixel readback, six target-aware hooks, ledger restart and horizon edit checks
passed. The final and post-restart decisions were both `wait: pending_assessment`.
Evidence is in
`artifacts/nina-smoke-f2b0254a5a8a45cb827b78b2b29c3936/probe/32dfee9e90df4bcba889bd072de8dd8d/result.json`.
This final run includes duplicate-wrapper rejection; all 447 automated tests
also passed. An earlier successful run before that additional guard is retained
under `artifacts/nina-smoke-725ee0cf41c2496ba0d76aacf39a72b8/`.
The image view was inspected after the simulators disconnected. The first run
refused all acquisition because the old profile's implicit 0/0 site placed the
parked target below its minimum altitude. Setting the fixture's explicit
synthetic site corrected the test without relaxing geometry. No server loop,
real pointing accuracy or production Earth-orientation source was tested.

On 2026-09-26, commit `1177c3b` repeated the nightly #58/OmniSim probe with
the merged PSF Guard #475 runtime (`b7c2f89`) and its pinned license notices.
Seven preparation operations, three RGB FITS captures, six target-aware hooks,
ledger restart, and horizon edit checks passed without errors. The completed
image view was inspected and the isolated host closed. All 376 automated tests,
runtime-fetch corruption/missing-file regressions, and package allowlist checks
passed. Evidence is in
`artifacts/nina-smoke-ad028f9749284a2fb66328c5276b2c50/probe/c5ecaad5fe2e4e45a69c9c1a50875360/result.json`.
This checks runtime adoption, not whole-exposure sky visibility or a server loop.

On 2026-09-26, commit `be633bc` passed nightly #58/OmniSim with native target
containers around all preparation and exposure items. The six inherited exposure
hooks resolved the exact J2000 coordinates and absent position angle through
NINA's target-context API. Seven preparation receipts, three saved RGB FITS
images, ledger restart, horizon edits, and cleanup passed without errors. All
376 automated tests passed. Evidence is in
`artifacts/nina-smoke-21c86cb0786d458b8093f47b0328c748/probe/2d70db2ffbeb4d14a319e0b8d712d865/result.json`.
The completed image view and disconnected simulators were inspected before
closing the isolated host. This remains a local fixture, not server-authorized
acquisition or proof of real autofocus/guiding behavior.

On 2026-09-26, commit `7f6aac8` passed nightly #58/OmniSim with reserved captures
running as native `IExposureItem` instances. Each of three captures inherited
one before-hook before final validation and one after-hook after its correlated
save. Seven preparation receipts, FITS readback, ledger restart, horizon edits,
and cleanup also passed. All 358 automated plugin tests passed. Evidence is in
`artifacts/nina-smoke-1d42a70fff59462d8aa0763b2b9322c9/probe/c1f4baab32cb4337b4e6fe100dacff05/result.json`.
The completed image view and disconnected devices were inspected, then the
isolated NINA instance was closed. These were test-only hooks, not autofocus
or guiding operations.

On 2026-09-26, commit `2d46f0a` passed nightly #58/OmniSim with a bound mount
and Rust-issued unpark. The fixture began parked, executed one native unpark
and six native filter/readout operations, saved three RGB images, and retained
pending-assessment state after restarting the sidecar. FITS readback, capture
IDs, horizon edit detection, and cleanup all passed. Evidence is in
`artifacts/nina-smoke-58983a4d3d9547cd9cdd7a711332a969/probe/c46e89519ca54caebd7a6a895ccf749a/result.json`.
The completed image view and disconnected equipment were inspected before
closing the isolated NINA instance. This run did not exercise slew or guiding.

On 2026-09-26, plugin commit `5e79f86` passed the same nightly #58/OmniSim
fixture with one-use native preparation items inside transient sequential
containers. Six operation receipts and all three RGB FITS captures passed;
the restarted ledger remained pending assessment. Cleanup completed without
errors. Evidence is in
`artifacts/nina-smoke-049cae0efc384c7bba1746c9e972265f/probe/3062a6f2a75646fa845a80678a458c2a/result.json`.
All 331 automated plugin tests also passed. The isolated NINA process was closed
after inspecting its completed image view and disconnected equipment state.

On 2026-09-26, nightly #58 with the same OmniSim devices passed the durable
program probe at plugin commit `2abde35`. Rust issued six filter/readout
operations and three GUID capture reservations. All three RGB FITS files passed
pixel/identity readback, matched schema 2 journals, and were recorded saved in
the Rust ledger. A sidecar restart retained the same ledger identity and returned
`wait: pending_assessment` without reacquiring. The same-path horizon edit check
also passed. Cleanup parked/disconnected all three devices; closing NINA left
no NINA or Director runtime process. The image and three-exposure HFR history
were visible in the real Imaging view.

The first run exposed a native API mismatch: numeric `SwitchFilter.ComboBoxText`
is sanitized into a symbol (`0` becomes `_0`). It failed validation, captured
nothing, and cleaned up. The corrected run uses typed `Xfilter` constants;
three new regression cases cover slots 0, 1, and 2 through NINA's real item.
The full automated suite passed 316 tests. Neither this host run nor those
tests establish production container, physical visibility, or server-loop parity.

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
`37d6d6ff46c4faa54681feb95af1ca749e8785fd` (runtime 0.2.1 / IPC 3) and the same nightly and
simulators. All three Rust-selected RGB captures passed FITS readback and capture
identity checks; the final decision was `wait: pending_assessment`. The probe
reported no errors, parked and disconnected the simulators, and stopped its
sidecar. The installed plugin's Start/Stop controls also reached Ready/Stopped
without arming acquisition or leaving a child process. Both buttons fit at the
default 800-pixel NINA window width. The automated plugin suite passed all 149
tests, followed by ten ledger/lifecycle stress runs covering 500 rapid restarts. The
runtime now waits briefly for transient storage ownership contention; a live
owner still prevents a second sidecar from acquiring the same directory.
Graceful shutdown acknowledges the final reply before waiting for child exit;
both the Rust delayed-reader and plugin disconnect regressions fail with the
old behavior and pass with the new versioned handshake.
This remains fixture-driven preview evidence, not
the server-assignment or production acquisition acceptance gate.

### Public prepared-target acceptance (2026-09-30)

The actual public Director Session ran in NINA 3.3.0.1064 with ASCOM OmniSim,
the pinned runtime 0.7.0 / IPC 8 and an isolated schema-16 PSF Guard server.
No TS plugin was installed. Operator admission, vault pairing, one-shot launch,
Rust selection/budgets, native unpark with verified sidereal tracking,
filter/readout preparation, three correlated FITS saves, session hook cadence,
server outage and six-event batch check-in passed. A fresh container could not
launch the consumed allocation again. Evidence:
`artifacts/nina-smoke-26e8d445090842318d8829bd332cbef1/probe/dc9645187dc84d9b911b10f92edd0976/result.json`.

A separate public run used 30-second exposures and made the native safety
monitor unsafe after the camera began exposing. The public owner, without the
probe canceling its lifetime, aborted exposure, parked, released ownership and
stayed stopped after monitor recovery. Evidence:
`artifacts/nina-smoke-3188d92612854001b43afacb8981da5a/probe/6f8c11d82e4445fa81e5906389524f32/result.json`.

All 641 plugin tests passed in serial test-collection mode. The package build,
format check and diff whitespace check passed. Review fixes cover tracking
confirmation, launch/acknowledgement validation, clock-initialization cleanup,
interruption locking, complete teardown attempts and bounded terminal status.

Run these with `run-server-plan-smoke.ps1 -PublicAcquisition`, then with
`-PublicAcquisition -PublicUnsafe`. Each invocation creates fresh disposable
profiles, catalog, registry, pairing and allocation. The test closes only its
owned NINA/server processes and retains evidence below `artifacts/`.

This validates one prepared target, not automatic centering, focus, guiding,
dithering, meridian flips, multi-target scheduling, resume, successor grants,
grade feedback or coexistence with every third-party plugin. A claimed launch
cannot be refunded after a failure. Preserve journals for reconciliation.

### Reviewed equipment handoff (2026-10-01)

The public simulator path now reports connected equipment through the actual
Director Session entry point while acquisition is disabled. It verifies the
button's command guard, exact paired profile, and absence of an active
configuration before review. Operator acceptance preserves manual optics,
then activation, allocation and one-shot native acquisition proceed normally.
The exported NINA template renders the new native-style button at 640 and 1000
pixels; service-free instances cannot run it.

NINA 3.3.0.1064, ASCOM OmniSim, runtime 0.7.0 / IPC 8 and an isolated schema-17
PSF Guard server passed the complete public path: three FITS saves, server
outage, six-event check-in and replay refusal. Evidence:
`artifacts/nina-smoke-8a29c4867441426fa5448c05f06dae75/probe/a691ca4fc9e54ff3ad0a8f5758a01fba/result.json`.
The separate unsafe run passed exposure abort, park, ownership release and
no automatic restart after safety recovery:
`artifacts/nina-smoke-b288369ce2de4011a7d64fa02d75d663/probe/029180ed7d8d43efa92979f4d18835ca/result.json`.
Both runs verified explicit equipment review, and all 652 automated plugin
tests passed in serial test-collection mode. This adds setup evidence, not an
operator review UI, acquisition-ready automatic scheduling, or recovery grants.

### Session editor and hook boundary check (2026-09-30)

NINA 3.3 nightly #64 loaded the exported Director Session template and rendered
all four tabs at 640 and 1000 pixels. The probe resolves NINA's actual MEF-loaded
type rather than the test plugin's separate assembly-load-context copy. It
checks all 25 policy editors, their selected values, bounds and nonblank output.
Screenshots are retained beside `result.json` as `session-{width}-{tab}.png`.

The isolated server-plan run captured three FITS images, ran eight session-slot
boundaries, survived server outage and sidecar restart, and replayed capture
receipts without duplicate credit. The terminal pending-assessment state did
not fire After Target Complete. All 587 plugin tests passed; the package build,
format check and diff whitespace check passed. This validates configuration UI,
hook bookkeeping and the internal simulator path, not production arming or
automatic equipment-policy execution.

## Observing preference execution

2026-10-02: runtime 0.10.0 / IPC 10, packaged plugin and native NINA 3.3.0.1064
passed the public multi-target session test against isolated local PSF Guard
with ASCOM OmniSim camera, mount and filter wheel. Rig importance-only weights
and project importance reversed the legacy priority order: two captures on the
preferred project, then one on the other target, through normal native hooks.
The server was stopped during acquisition. On reconnection, all six capture
events were acknowledged; live status, saved-run batch replay and refusal of a
second allocation launch passed. No real equipment or catalog was used.

Run `tools/run-server-plan-smoke.ps1` with `-PublicAcquisition
-LocalTargetScheduling -ObservingPreferences`. Evidence:
`artifacts/nina-smoke-f1714f67bfab4179af5e5b0183b66a2f/probe/a0f7c5c19aa940d0b067815034543300/result.json`.

The same scenario also passed with `-AutomaticWorkloads -OfflineWorkloadRelease`:
v3 intake issued weighted work, the rig captured and parked offline, and batch
delivery released the original workload before entering bounded assessment wait.
Evidence: `artifacts/nina-smoke-4c8709f089284387a5ce18230c533326/probe/7dab6f23c8ea4efc9a1751bf85a6e010/result.json`.

The `-PublicAcquisition -PublicUnsafe` regression passed with the same package:
unsafe weather aborted the active exposure, parked the simulated mount and
remained stopped after recovery. Evidence:
`artifacts/nina-smoke-87f8b9fbbaf341ba8e56c405ccc2e2d5/probe/fa26e827b9c64da2bbcb4a174a40e5ef/result.json`.
All 775 plugin tests, package build, format and runtime-fetch regressions passed.

## Ranked priority handoff

2026-10-03: the packaged plugin, runtime 0.10.0 / IPC 10 and native NINA
3.3.0.1064 passed against isolated local PSF Guard with ASCOM OmniSim devices.
No normal NINA profile, real equipment or live catalog was used.

`-PublicAcquisition -LocalTargetScheduling -ProjectOrder` verified that a rig
order replaces its site order, clearing it inherits the site order, and that
site order replaces the global order. The final inherited order executed
offline as A, A, B despite conflicting legacy objective priorities. All six
capture events reconciled after reconnection; repeat launch was refused.
Evidence: `artifacts/nina-smoke-98b0850e96e8487bb272317be136814e/probe/13218b9a3385450ca6e59ffdbac2d424/result.json`.

Adding `-AutomaticWorkloads -PriorityRefresh` changed the global ranking during
the first exposure. NINA finished A, closed the next unused preparation,
parked and released the two-event ledger, then obtained a successor and
captured B followed by the remaining A exposure. Both ledgers were sealed,
pending credit and spent attempts carried forward, and saved-run batch
check-in delivered no duplicate events.
Evidence: `artifacts/nina-smoke-8b2096d4964b472eb9fdc0b5df03965e/probe/8b67b290b2874324a004387a5d837d5e/result.json`.

This verifies ranked-priority replacement for the same goals and configuration,
not arbitrary goal/grade changes, seamless tracking between grants, or
unattended production readiness. Manual and deferred sessions keep their
held order until they obtain another allocation.

The unchanged-priority automatic regression also passed with `-ProjectOrder
-AutomaticWorkloads -OfflineWorkloadRelease`: one ledger, no progress-driven
handoff, offline completion and park, then batch release on reconnection.
Evidence: `artifacts/nina-smoke-052ea89f4a484855a91a023ff2253993/probe/331b23cff8fb4862901f9a232fb654f9/result.json`.

`-PublicAcquisition -PublicUnsafe` passed exposure abort, park and no automatic
restart after safety recovered with the same package. Evidence:
`artifacts/nina-smoke-986c585ff568423cacffd0609a6bfdd8/probe/9b83802eac5441aa93ae7598fe484304/result.json`.
All 777 plugin tests passed. Package build, changed-file format verification,
meta allocation tests and meta Clippy passed. Native template renders at 640
and 1000 pixels remain beside each run's result.

## Constraint changes during slow setup

2026-10-03: native NINA 3.3.0.1064, ASCOM OmniSim and runtime 0.10.0 / IPC 10
ran the packaged plugin against a fresh isolated PSF Guard main build.
`-PublicAcquisition -ConstraintChange horizon`, `site` and `meridian` each
changed local constraints while a 45-second native setup hook was running and
the server was offline. Each canceled the hook before it finished, saved no
exposure, parked, released local ownership and stayed stopped after restoring
the original settings. The horizon case retained the file path, size and
timestamp; only contents changed.

Evidence:
- Horizon: `artifacts/nina-smoke-38e53ac0f19b4b37951186f99e486dbd/probe/3831d430e62a4f8491a84bdcc3408767/result.json`.
- Site: `artifacts/nina-smoke-3dcc48ca190f4c25acd17603597acc77/probe/c5b0c8b799a44296821dd4b91496f2e1/result.json`.
- Meridian, including cleared terminal operation status: `artifacts/nina-smoke-204a5c8890ad4a63b699d7759dfb7e7f/probe/76d3b70109484cbfaf24341b7283677f/result.json`.

All 788 plugin tests passed, including unreadable/locked/deleted files, profile
round trips, fixed-horizon changes, cancellation and concurrent unchanged
dispatch refreshes. Package build, changed-file format and whitespace checks
passed. Native status renders at 640 and 1000 pixels retain the review-required
stop reason without a running operation. This does not commission changed
constraints automatically or implement recovery grants.

The live ranked-priority regression passed with this guard enabled:
`-PublicAcquisition -LocalTargetScheduling -AutomaticWorkloads -ProjectOrder
-PriorityRefresh` completed A, B, A across two sealed ledgers without a false
constraint stop, duplicate capture or replenished attempt budget. Evidence:
`artifacts/nina-smoke-8a231fd69ce8464c99bcac1e49c4b0e8/probe/ffa73810158b424b9034dfe0542b761c/result.json`.

`-PublicAcquisition -PublicUnsafe` also passed with the final package: abort
the active exposure, park and remain stopped after safety recovery. Evidence:
`artifacts/nina-smoke-556e22db895444cab9b1ed04c3bb4119/probe/9e7dac1ca5dc4208a756c6531a1b5b48/result.json`.
## Native imaging flow validation (2026-10-03)

The native-flow increment was tested with NINA 3.3.0.1064, pinned API
3.3.0.1058-nightly, bundled runtime 0.10.0 / IPC 10, ASCOM OmniSim camera,
telescope, filter wheel and focuser, NINA Direct Guider, and an isolated local
PSF Guard server. No real profile or live catalog was modified.

Use `run-server-plan-smoke.ps1` with `-PublicAcquisition -LocalTargetScheduling
-NativeImaging`. Add `-AutomaticWorkloads` for commissioned intake/release; omit
it for a server outage during local acquisition and deferred reconnect delivery.
The test changes targets, executes native Center and Autofocus, starts guiding,
switches filters/readout, dithers the simulated mount, saves three correlated
FITS files and parks. Native autofocus/meridian/restore-guiding triggers remain
attached during execution and are removed before terminal release. The probe
renders all six Session tabs at 640 and 1000 pixels, including NINA's altitude
chart and the populated action history.

With automatic workloads, add `-NativeImagingFailure center` or
`-NativeImagingFailure autofocus` to verify that injected failure saves no
exposure, completes parking, releases local ownership and does not restart.
Autofocus's native AbortOnError also cancels the outer NINA sequence; the probe
waits for actual shutdown independently before checking that result.

**Optical evidence is synthetic.** The test-only probe substitutes solver/focus
results while using the real NINA actions, sequencing and ASCOM device mediators.
This proves dispatch, hook and failure orchestration, not optical accuracy or
autofocus quality. The base test does not force a flip; use the additional case
below. Rotation and real-sky acceptance remain open. The probe and its Moq dependency
are never included in the published plugin ZIP.

Meridian regression tests use the pinned native trigger, inherited sequence
runner and a controlled flip VM result. They cover upcoming instruction timing,
the pre-meridian pause, unavailable mounts, failed workflow steps, cancellation,
blocking dispatch after failure, Activity entries, and exclusion of runtime
defaults from saved/cloned sequences. These tests do not claim a physical flip.

## Forced native meridian workflow

Run `run-server-plan-smoke.ps1` with `-PublicAcquisition -LocalTargetScheduling
-AutomaticWorkloads -NativeImaging -ForceNativeFlip`. Add
`-NativeImagingFailure meridian` for a failed mount-command result.

The fixture places its target east of the meridian, arms after two saved frames,
and gives NINA's native trigger a bounded deadline through the actual crossing.
NINA's real flip VM pauses/resumes tracking, calls the ASCOM mount flip, runs
autofocus, settles, and raises the native before/after events. The success case
requires a live device pier-side change followed by a third saved exposure.
The failure case requires a failed native workflow step, exactly two saved
frames, no third exposure, parked shutdown and released local ownership.
Both cases record ordered save/flip events and workflow steps in `result.json`.

The flip deadline, optical focus/solve results, status display and window
presentation are test substitutes; the failure case also returns false at the
mount-command boundary. Mount motion and the flip workflow are real NINA operations
against ASCOM OmniSim. Recenter is disabled: this is not a sky-solving test.
Direct Guider does not exercise a PHD2 stop/reacquire cycle. Native rotation,
real-sky recentering, external plugin callbacks and full-night acceptance remain
separate gates. The test-only helpers are not shipped in the plugin package.

Validated on 2026-10-04 with NINA 3.3.0.1064, bundled runtime 0.10.0 and
an isolated PSF Guard server. Both success and injected mount-failure runs passed.
The pinned NINA VM reports an overall successful result/event even when its
`Flip` step returns false. Director's workflow-step check catches this and
prevents the next exposure; do not replace it with a boolean/event-only check.
Local evidence:

- Success: `artifacts/nina-smoke-f353cf1360cc4ca3b42548c4b20e1153/probe/bec80c8443aa4a69a84ad591d25fb41a/result.json`.
- Failure: `artifacts/nina-smoke-abdc212581824960baf0f55ba8ef320e/probe/77b8e8d961a2453ab9d747132d73bc82/result.json`.

## PHD2 recovery, recentering and rotation

Add `-Phd2Executable C:/test/PHD2/phd2.exe` to the successful forced-flip
command above. Use an extracted PHD2 2.6.14 executable. The helper allocates
an unused instance in the 2000-2999 range, with its own registry profile,
loopback port and log directory. It verifies the **Simulator** camera and
**On Camera** guide output before NINA connects. Existing PHD2 instances and
equipment profiles are not reused. The helper shuts down its child and removes
its owned test registry profile; failure logs remain in the server artifacts.

This scenario additionally requires ASCOM OmniSim Rotator. The server's rig
optics declare a rotator and the framing plan requests 30 degrees. NINA's real
Center and Rotate action moves the device from a synthetic zero to that angle.
All three capture intents must retain the requested angle.

The real PHD2 process calibrates and guides its simulated stars. NINA's flip VM
stops guiding, crosses the meridian, flips, runs autofocus, captures a recenter
snapshot, selects a new guide star, restarts guiding and settles. The test
requires each native workflow step and the next saved exposure. An external
test-only ASTAP-shaped process supplies the recenter solution. The guard also
requires NINA's centering solver to consume a successful parsed solution;
the VM's best-effort `Recenter.Finished` flag alone is insufficient.

This remains synthetic optical evidence: neither an actual plate-solving
algorithm nor a focus curve is validated. The rotator's optical zero is
synthetic, and PHD2's simulated guide mount is not physically coupled to OmniSim's
pier-side change. Calibration parity across a physical flip, real-sky focus and
recenter accuracy, clouds and third-party equipment/plugin combinations remain
field acceptance work. No test helper is included in the plugin ZIP.

The campaign uses NINA nightly #65 (`3.3.0.1065`) from the official download
page, the pinned #58 API and runtime 0.10.0, and PSF Guard main `a035e56`.
The native flip, centering and rotation contracts were checked against upstream
NINA source before admitting #65 to the launcher. Download SHA-256 values:

- NINA #65 bundle: `af3ca920f0888b46ef9c68ebce4040e6100f0109ac442be652f781a2305a1bf6`.
- PHD2 2.6.14 installer: `d7a21f67de32b901c6d173da9a27d765a0d98874798f898df6ca51cd5453b5ff`.

Run `tools/run-simulator-matrix.ps1` with `-PsfGuardExe`, `-NinaDirectory`
and `-PluginZip` to repeat the remaining existing automated host scenarios.
It runs serially because ASCOM simulators share device state, stops at the first
failure, and retains a JSON report with each case's evidence path. The cases
cover offline native acquisition/workload release, both unsafe shutdown choices,
enclosure closure, horizon/site/meridian changes, Moon wait, ranked-priority
refresh, deferred check-in, and centering/autofocus/flip failures.

All 13 matrix cases passed on 2026-10-04. Local report:
`artifacts/matrix-06150df6662a4d63ac7c66f260141be9.json`.
The report links each isolated host result and server log directory. The full
unit suite also passed (827 tests), along with locked package build, formatter
verification and PowerShell parser checks. Session templates were rendered at
640 and 1000 pixels across all six tabs; the narrow sky and activity views were
also visually inspected.

The combined PHD2/rotator/flip repeat passed with the final harness:
`artifacts/nina-smoke-21dc98a6347e475b840c53476aa23cd2/probe/c7a5d23dbc1f40d48b16e8bceb0a42ae/result.json`.
It records 30 degrees, one parsed recenter solution, `pierWest` to `pierEast`,
all eight native workflow steps finished, and `Saved 3` after the flip events.

## Operation telemetry increment

The telemetry harness also reads the real plugin's current operation, elapsed
time, goal, connected mount pointing and freshness lease from the local server
before forcing an outage. After the run, settings and sequencer Check In replay
preparation history independently of the six capture events. It verifies that
repeated replay delivers no acknowledged events and leaves live status unchanged.
Deferred mode must create neither cursor before the explicit check-in.

The native automatic/offline workload case passed on NINA 3.3.0.1065 with the
bundled runtime 0.10.0 / IPC 10 and a freshly built isolated PSF Guard server:
`artifacts/nina-smoke-5261e57f09e44af2997b455420f511a9/probe/3108cf6ec81d4cfd8a86a9c85733a5cc/result.json`.
This is simulator evidence with synthetic optical results, not real-sky focus,
solve or unattended-night certification.

The separate native/deferred case also passed:
`artifacts/nina-smoke-fa814c287fc84eedad4b3c0149077a1f/probe/7abb9bf24a3440478c25e4ddf83027df/result.json`.
The plugin unit suite passed 830 tests.

After keeping priority refresh on its existing target/periodic cadence, the
live ranked-priority handoff passed too, including two-ledger duplicate replay:
`artifacts/nina-smoke-fce69738d83f418cb7d4f10d16639db5/probe/1908bf1cc0ec4322b3feb41363fae6b1/result.json`.
