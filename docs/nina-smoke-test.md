# Real N.I.N.A. smoke test

Use the official N.I.N.A. 3.3 nightly #58 (`3.3.0.1058`) in a separate application
directory. Do not install it over an existing N.I.N.A. installation. The official
[nightly bundle](https://f002.backblazeb2.com/file/ninasetup/Nightlies/3.3.0.1058/NINASetupBundle_3.3.0.1058.zip)
contains a WiX bundle with an embedded MSI. Extract the bundle, then use an MSI
administrative extraction (`msiexec /a ... /qn TARGETDIR=...`) to unpack the app.
The test launcher takes the resulting directory containing `NINA.exe`.

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
profile starts with no device selected. The launcher installs only Director;
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

## Local evidence

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

No hardware was connected and no images were acquired. These checks validate
the real runtime host, not the full-stack acquisition acceptance gate. That
still requires the native N.I.N.A. acquisition adapter, simulated devices, and
an isolated PSF Guard server built from the corresponding changes.
