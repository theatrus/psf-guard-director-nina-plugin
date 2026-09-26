# Native capture adapter

This is the internal acquisition building block for Director's session
container. It is not exported to N.I.N.A.'s sequencer or MEF and has no settings
button. It does not select goals, retry exposures, or replace the Rust planner.

## N.I.N.A. contract

The adapter targets `3.3.0.1058-nightly`, source commit
`516039556050dde4c0820a486fbc75217e3804f3` from the published NuGet metadata.
It uses public interfaces only:

- `IImagingMediator.CaptureImage` for exposure and download.
- `IExposureData.ToImageData`, `IImagingMediator.PrepareImage`, and image history
  for N.I.N.A.'s normal preparation and display.
- `IImageSaveMediator.Enqueue`, `ImageSaved`, and `ImageSaveFailed` for saving.

Enqueue only admits work to the save queue. It does not confirm a file on disk.
The adapter subscribes before enqueue and waits for a matching final receipt.
Both the N.I.N.A. image ID and Director's `PGCAPID` metadata value must match.
An unrelated image cannot complete the attempt. A save failure propagates to the
caller rather than becoming a successful exposure.

The native save worker reads the active profile when dequeuing. Director wraps
`IImageData` to retain the original destination, filename pattern, format, and
compression settings while keeping N.I.N.A.'s queue, events, custom patterns, and
writer. Each write receives a fresh settings copy, including native retries.
The capture supports FITS and XISF lights; it does not silently produce TIFF or
RAW output that PSF Guard cannot catalog. The final path comes from the save
receipt, not a filename prediction or the event's profile-dependent file type.

The caller must prepare filters, focus, pointing, and other operations before
asking the Rust core again. Capture does not request a filter change. The
mandatory dispatch callback must reject stale authorization. Profile and camera
identity are checked before and after that callback and before enqueue. The
adapter rejects concurrent calls rather than queuing another stale decision.

These checks are not sufficient to enable autonomous dispatch. A session must
own equipment against competing controllers, bind cancellation to local safety
and profile/session lifetime, and enforce eligibility at the actual hardware
boundary. N.I.N.A.'s imaging mediator can itself wait behind another capture;
calling the adapter is not proof that the shutter started at that instant.

## Capture evidence

Before dispatch, the adapter reserves
`<journal-root>/<profile-guid>/<capture-guid>.json`. The caller supplies an
absolute Director-owned journal root, not a server-provided path. Existing
capture IDs cannot be reused, including failed or uncertain attempts. JSON is
bounded to 64 KiB, flushed to disk, and atomically renamed in the same directory.
An unwritable journal prevents dispatch. A failed final journal write does not
return a successful result.

Schemas 1 and 2 are experimental host evidence, not the final IPC/event contract. They
records rig/configuration, profile, assignment/revision, goal, camera, target,
requested exposure, destination, UTC timestamps, and observed state:

| State | Meaning |
| --- | --- |
| Reserved | The attempt identity is durably claimed. |
| Capturing | Dispatch is intended; a crash here does not prove whether hardware ran. |
| CaptureUncertain | Capture returned no usable result or was interrupted after dispatch. Hardware may have exposed; recovery is required. |
| Downloaded | N.I.N.A. returned a frame and image ID. |
| SaveQueued | Save admission is being attempted or completion is still unconfirmed. |
| Saved | A matching final file receipt was observed and recorded. This is not a grade. |
| Failed | An operation or explicit save failure was observed. No retry is authorized. |
| Interrupted | Cancellation occurred before save admission. A capture may have occurred. |
| SaveUncertain | Cancellation, timeout, or an unconfirmed error followed save admission. A file may still appear. |

The adapter preserves downloaded image identity even when cancellation arrives
before processing. It writes the stable Director capture GUID into FITS/XISF
metadata as `PGCAPID`, alongside normal N.I.N.A. target metadata. Input RA is
explicitly in degrees; the N.I.N.A. coordinate object converts it to hours.

Elapsed times use a monotonic clock. Capture/download time excludes preceding
authorization/journal work; processing/save time starts after download; total
attempt time includes host overhead. These intervals are nested, not additive.
Exposure and download are not separately timed yet because `CaptureImage`
returns them as one operation. No timing estimator or second C# scheduler is
introduced here.

### Bound native recipes

The internal `NinaProgramCapture` adapter maps a validated Rust capture binding
to native exposure seconds, binning, gain, offset, and ICRS target metadata.
It requires a matching newly created reservation with a GUID capture ID.
Existing, recovery, saved, or mismatched attempts cannot enter this path.
A binding lookup alone is never authorization. The caller must still provide
fresh dispatch validation and own the equipment and session lifecycle.

The adapter rereads the full native configuration before creating the intent
and after the dispatch callback. At the latter boundary it verifies the prepared
filter slot/name, a stationary wheel, and `ReadoutModeForNormalImages`. NINA's
LIGHT exposure uses that setting, not the currently active `ReadoutMode`.
Preparation must set it through `SetReadoutModeForNormalImages`; capture does
not insert filter, readout, autofocus, or other preparation operations.
Supported gain and offset require explicit values. Native `-1` sentinels are
used only for controls declared unsupported by the validated configuration.

Bound captures write schema 2 journals with ledger, target, recipe, stable
filter, and requested readout identities. Schema 1 journals remain readable.
An unspecified position angle stays null in the journal and uses NINA's NaN
metadata sentinel, not a fabricated zero-degree rotation. The host journal is
still separate from the Rust ledger; the production container must coordinate
outcomes and recovery. No automatic retry or ledger refund is introduced.

Tests connect a real Rust prepared reservation and binding to mocked native
capture/save mediators, then record the saved outcome in Rust. They also cover
configuration drift, readout selection, wheel state, unsupported controls,
fixed filters, and old journals. These are not the full NINA/server simulator
gate. They do not prove that a camera driver applied a requested setting, nor
remove the imaging mediator's internal queue delay or competing-controller risk.

## Planner evaluation

`RuntimeController.EvaluateAsync` sends an immutable `PlannerRequest` through
the bundled Rust sidecar. Its nested records and immutable arrays represent the
shared core's assignment, goals, eligibility intervals, transit coverage, and
local state. C# does not select priorities or implement window policy.

Requests use contract 2 with exact unsigned integers, including revision and
Unix-millisecond fields. Serialization and the 256 KiB core request limit are
checked before entering the session queue. The assignment rig must match the
negotiated runtime rig. Evaluations and heartbeats share one serialized pipe and
strictly increasing request IDs. Each admitted exchange has a five-second
deadline; shutdown and profile/runtime replacement cancel in-flight evaluation.

Replies must match the IPC session/request, engine/contract versions, and
assignment identity/revision. The decoder rejects unknown, duplicate, missing,
or incorrectly typed response fields, unknown actions/errors, and acquisition
of a goal absent from or ambiguous in the supplied snapshot. A valid core error
has no decision and leaves the session usable. Malformed transport/replies,
timeout, or cancellation after admission invalidate the session; no old decision
is replayed. Bad caller input and cancellation before queue admission do not
tear down another request.

A `PlannerEvaluation` is a recommendation for its snapshot, not a durable or
reusable authorization token. Production dispatch still needs a session-owned
state generation, durable attempt/event accounting, recovery, and independent
local safety enforcement at the actual equipment boundary. The simulator probe
now uses the durable bound program path below: Rust rechecks the boundary when
reserving after preparation. Its dispatch callback checks the local fixture and
reserved evidence, not a new stateless recommendation. This does not solve the
imaging mediator's possible internal queue delay.

## Durable ledger host

Runtime 0.4.0 / IPC 5 provides opt-in persistence through
`RuntimeController(pluginDirectory, storageDirectory)`. The caller must supply
an existing absolute, private, local Director-owned directory, scoped to its
rig/profile and allocation. Do not accept this path from a server assignment.
The sidecar exclusively owns the directory and its SQLite ledger until exit.
The ordinary settings preview remains stateless.

`OpenLedgerAsync` binds an immutable allocation to that ledger. `ReserveAsync`
asks Rust to project durable progress and select work atomically. Results
distinguish a new reservation, an existing attempt, recovery of another unresolved
attempt, and a non-acquisition planner decision. No result is a hardware permit.
An existing or recovery result must never cause an automatic capture retry.
Stateless `EvaluateAsync` is refused once a ledger is open.

`RecordAsync` accepts saved, failed, or uncertain observations. Saved means an
image exists, not that grading accepted it. `FindAttemptAsync` recovers evidence
by capture ID; `ReadEventsAsync` returns up to 64 events with a stable ledger
identity and next cursor. The host validates allocation/rig/configuration,
versions, capture/goal identity, strict fields, and contiguous event sequences.
It preserves unsigned 64-bit revisions and durations exactly. A returned storage
error is typed and carries no successful value. The host does not change retry,
credit, or recovery policy in C#.

All ledger calls share the heartbeat queue and five-second exchange deadline.
Malformed replies, disconnect, timeout, or cancellation after admission invalidate
the session. A lost reply may follow a committed write: reconnect and inspect
the same ledger/capture identity; do not replay equipment work or create a new
capture ID to bypass uncertainty. Caller errors and cancellation before admission
do not invalidate another request.

Tests exercise real Rust sidecars with isolated ledgers, abrupt process exit,
exclusive ownership, uncertain-to-saved recovery, duplicate observations,
pending credit, allocation mismatch, event pagination, and adversarial replies.
This is a host API, not a completed native acquisition integration. The adapter
still uses its separate host journal. Before connecting it, the shared core
needs reservation-aware operation/dispatch revalidation and session fencing;
opening a ledger and then using stateless evaluation is not a valid substitute.

N.I.N.A. remains the only execution backend in scope. The production Director
container must preserve TS-style options and normal sequence triggers, conditions,
and cancellation while the shared core owns operation policy. These host tests
do not establish native container or TS behavioral parity.

### Preparation and recovery host

`OpenProgramAsync` opens a program-bound ledger with the full immutable
allocation, target/recipe bindings, and equipment capability snapshot. It cannot
adopt an existing unbound ledger. `BeginProgramPreparationAsync` supplies local
observations and estimates, not replacement settings or progress counters.
`AdvanceProgramPreparationAsync` and `ReserveProgramPreparedAsync` require the
complete refreshed configuration at each boundary. Rust validates capability
ranges, resolves bindings, and owns scheduling and preparation policy.

`FindCaptureBindingAsync` retrieves the saved target, recipe, configuration, and
attempt. The host checks every field against the opened immutable program,
including array contents and exact integer coordinates/timings. Preparation
commands, recovery records, and preparation events are also checked against
their program binding. An unknown capture returns an explicit null binding.
No lookup, existing reservation, or recovery command authorizes redispatch.
Malformed replies invalidate the session; a typed `InvalidProgram` or changed
configuration error does not. Opening errors leave room for an exact retry.

The original unbound APIs below remain available for isolated legacy tests;
the runtime rejects their begin/advance/reservation commands on bound ledgers.
The production container must use the bound APIs, a matching sidecar bundle,
and fresh native dispatch checks. This host does not yet export actual NINA
capabilities through a production container or connect the native capture journal to this durable ledger.

### Native equipment snapshot

The internal `NinaEquipmentSnapshot` reader copies capabilities from the supported
camera and filter-wheel mediators and the active profile's filter definitions.
The local binding supplies explicit stable filter IDs and wheel slots. Names
detect edited mappings; they do not establish remote identity. A wheel-free
profile requires an explicit single fixed filter.

The snapshot contains binning pairs, readout indices, gain and offset ranges or
discrete values, and conservative whole-millisecond exposure bounds. Unsupported
controls are explicit. Missing, ambiguous, disconnected, subframe, or
unrepresentable configurations fail instead of acquiring with invented defaults.
NINA's ASCOM adapter can optimistically advertise writable gain before it has
ever attempted a write. Its exact absent-control state (unreadable gain, empty
gain list, and both limits `-1`) exports an unavailable control, never a guessed
range or a write to probe support. Other inconsistent ranges still fail.
The configuration digest includes profile/device identity, driver versions,
readout order, filter mappings, capture options, and the caller's constraint
revision. Raw device paths are not exported. Capability sets are copied into
immutable arrays and bounded to 256 entries.

Two copied reads detect changes during export. This is not a hardware lock or
permission to dispatch; the production executor must refresh the snapshot at
each operation boundary. A configured driver identity does not attest a physical
camera serial number. The site/horizon reader supplies the constraint revision,
including same-path horizon changes, as described below.

Regression tests validate a native snapshot with the real Rust program validator.
The isolated ASCOM probe records the connected simulators' capabilities and
checks that their identity survives three captures. It binds those capabilities
to a durable fixture program and checks saved progress after a sidecar restart.
It is not a production session or server allocation.

### Native constraint snapshot

`NinaConstraintSnapshot.Refresh` exports the active profile's site coordinates,
native flip settings, the explicit rig before/after meridian exclusion, minimum
altitude, and complete horizon breakpoints. Native flip/pause behavior remains
separate from hard rig exclusions. This adapter exports data, not observing
windows or a second scheduler. Rust visibility calculation and composition with
project preferences still need implementation.

The caller must persist an explicit `RequiredFile` or `FixedMinimum` declaration.
An empty NINA file path does not disable a previously required horizon. The
internal API rejects a missing, cleared, changed, malformed, or oversized file.
Fixed-minimum mode requires both the NINA path and loaded horizon to be empty.
There is no production settings UI or persisted declaration store yet.

Refresh bounds file size and point count, then locks the file against writes
and renames while calling NINA's public `ChangeHorizon` API. This controlled
reload synchronizes NINA's model with the exact exported bytes, including edits
that preserve the path, size, and modification timestamp. Call it only at a safe
refresh/check-in boundary, not for each UI repaint. It emits NINA's normal
`HorizonChanged` event. Do not recursively call Refresh from that event.

Standard files contain azimuth/altitude pairs; `.hpts` files contain JSON
altitude/azimuth pairs. Format selection matches the pinned NINA version,
including its case-sensitive extension check. Breakpoints use degrees with
azimuth north=0, east=90. Curves use linear interpolation, NINA's nearest-endpoint
completion when 0/360 are missing, and modulo-360 query wrap. Explicit unequal
0/360 endpoint values are retained. Duplicate azimuths use the last value as
NINA does. Every breakpoint is retained; export never samples a coarse grid.
The adapter checks knots, segment midpoints, wrap, and extrema against the
fresh native model. Invalid lines that NINA might skip are rejected by Director.

Profile/location/horizon events increment a local invalidation generation.
Profile changes and unexpected events during refresh reject the snapshot;
Dispose removes subscriptions. Generation alone does not detect disk edits:
Refresh must reread the file at each controlled boundary. The content digest,
profile, site, horizon, minimum altitude, rig exclusions, and native flip settings
feed a versioned constraint revision used by the equipment identity. Paths stay
local. This is not a hardware permit, filesystem watcher, or local safety monitor.

The isolated ASCOM probe reads a fixture horizon through the real profile
service and verifies a same-path edit reaches both NINA and the configuration
identity. It still uses unrestricted fixture eligibility for capture; this
test does not prove horizon enforcement during acquisition or server parity.

`EvaluateLedgerAsync` requests a read-only decision using durable progress,
without reserving an exposure. `FindUnresolvedAttemptAsync` and
`FindActivePreparationAsync` discover interrupted work after a lost reply or
host restart; callers do not need to have remembered its ID in memory.

`BeginPreparationAsync` binds the resolved target/recipe/equipment context and
blocking estimates. Rust decides native operation order. `AdvancePreparationAsync`
returns a newly issued `Run`, existing `InFlight`, `ReadyToReserve`, or a planner
decision. Only `Run` represents a newly issued operation, and it still requires
independent local safety, profile, configuration, target/recipe, and native
sequence-lifecycle validation at dispatch. A recovered record's `Pending`
command is evidence, never permission to repeat an operation.

`CompletePreparationAsync` records the operation ID/ordinal, outcome, wall-clock
end, and measured monotonic elapsed duration. It verifies the returned receipt
exactly, including 64-bit timings. Outer hook durations already include nested
work; do not add child durations again. The host never advances counters or
chooses the next operation itself. Failed and uncertain receipts stop progression.

`ReservePreparedAsync` asks Rust to refresh the final boundary and atomically
link the preparation to a capture reservation. Existing reservations remain
non-dispatchable. `ClosePreparationAsync` cannot discard a pending or uncertain
operation; closing an already captured preparation returns its captured evidence
unchanged. It does not reset or refund that capture.

`ReadPreparationEventsAsync` returns at most 32 events. Keep this cursor separate
from `ReadEventsAsync`'s capture cursor. The decoder checks strict fields,
allocation/rig/configuration/engine identity, contiguous sequences, native
receipt identities, record lifecycle consistency, and known goals. New typed
domain errors preserve the connection; malformed replies invalidate it.
IPC 3 hosts and IPC 4 runtimes cannot negotiate a session.

The plugin tests use the pinned CI-built runtime and terminate it after issue,
then reopen the same ledger and verify no redispatch, idempotent receipts,
prepared reservation, pending-image accounting, and separate event cursors.
These tests do not execute equipment. Authoritative recipe/configuration binding,
the native N.I.N.A. container, server allocation/check-in, and the full-stack
simulator gate remain required. The published 0.1.0.0 preview remains unchanged.

## Native preparation lifecycle

`NinaPreparationItems` creates internal, transient native unpark, filter, and readout
items from a newly issued Rust `PreparationNext.Run`. It rejects recovered
in-flight operations, mismatched recipe bindings, cloning, and native retries.
Resetting a NINA item cannot reset its one-use dispatch fence. Fixed-filter
configurations use a checked no-op rather than moving a wheel.

Unpark requires an explicit local telescope binding and a connected matching
native mediator. The configuration fingerprint includes the bound mount identity,
driver version, and public capabilities, but not changing park/tracking/position
observations. Legacy camera-only fingerprints remain unchanged. Refresh rejects
changed profile selection or disconnection. The native `UnparkScope` item retains
NINA's dome/shutter gate and unpark events. Success requires the requested mount
to report unparked and idle; false returns or contradictory post-state remain
uncertain and cannot advance the ledger. No mount is inferred from its name.

The native container still owns condition checks and inherited before/after
triggers. Inside `Execute`, after before-triggers have run, the item calls the
owner's current-boundary validator and compares fresh equipment configuration
and mutable item settings with the immutable program. Completion also requires
NINA to report the requested normal-image readout or settled filter. A failure
after entering the native operation is uncertain, even if NINA swallows the
exception. Skipped or invalid items may have no receipt. Owners must require
both `FINISHED` and a matching successful receipt; a returned task is not proof
of success. The original receipt is retained if a reset attempts redispatch.

Operation durations use a monotonic clock from dispatch validation through the
native result check. They exclude preceding inherited triggers. They are not
whole-target timing observations. This helper does not persist authority or
authorize recovery, and its callback is not yet a production server/core permit.
Native trigger exceptions can be swallowed by NINA; a production session must
observe hook failures and enforce safety independently. Slew/center, autofocus,
guiding, dither, and the complete session container remain unfinished.

Regression tests run the native container strategy with inherited triggers,
condition-based skipping, cancellation, altered settings, driver failures,
no-op readout, unsettled filters, reset, clone, and concurrent-entry cases.

## Native exposure lifecycle

`NinaExposureItem` is an internal transient `SequenceItem` implementing NINA's
`IExposureItem`. It exposes the reserved LIGHT recipe to exposure-aware native
triggers (including autofocus, guiding, and dither), while dispatch still uses
`NinaProgramCapture` and the correlated-save adapter. Native containers own
inherited before/after triggers and conditions. The item performs final recipe
and boundary validation after before-triggers; an inherited hook cannot silently
change its exposure, gain, offset, binning, or image type. Estimated duration is
the immutable exposure duration, matching the native exposure-item convention;
it is not a whole-operation timing estimate or a visibility authorization.

Each item can enter execution once. Native retry, clone, reset, and concurrent
entry cannot repeat a reservation. It waits for the correlated save before
returning, so after-exposure hooks observe saved evidence. A saved receipt stays
available even if a later reset or hook fails. The owner must deliver that
evidence to the ledger and decide whether execution may continue separately.
Errors remain available even when NINA catches them. Missing evidence is never
proof that no exposure happened; skipped, canceled, abandoned, and uncertain
operations still need the session's reconciliation rules.

This is not a public acquisition container. Target context, production
authorization after slow hooks, hook-failure propagation, timing of inherited
operations, and the complete TS-style session options remain required. Tests
exercise the real native strategy and confirm native RestoreGuiding recognizes
the item as a LIGHT exposure; they do not prove every native/third-party trigger.
The ASCOM probe also checks inherited before/after hooks in the real nightly host.

## Recovery and validation limits

Save waiting has a bounded deadline and honors cancellation. A receipt already
observed wins a simultaneous cancellation, whether it reports success or failure.
An error, cancellation, or missing result from the capture call is not proof
that the camera did not expose. It records `CaptureUncertain`, with no automatic
retry. Revalidation failures before dispatch remain distinct from this outcome.
After cancellation/timeout, handlers detach and an unconfirmed save remains
uncertain. N.I.N.A. may still write the queued image. A later recovery component
must reconcile `PGCAPID`, the recorded destination, and catalog evidence before
authorizing replacement work. This adapter does not claim exactly-once capture,
perform recovery scans, or automatically refund an attempt.

Tests use mocked public N.I.N.A. mediators and the real header serializers.
They cover admission versus completion, identity correlation, save failures,
stalled queues, cancellation races, profile changes, settings snapshots, journal
failures, duplicate IDs, and FITS/XISF capture-ID preservation. The separate
[ASCOM smoke sequence](nina-smoke-test.md#ascom-capture-sequence) exercises this
adapter inside the real nightly host with simulator camera, mount, and filter
wheel. Neither suite proves the full-stack planning/server acceptance gate.

Before exporting a production Director sequencer item, connect the evaluation
bridge to durable session state and an event handoff, implement recovery, and run the simulated-equipment
gate with an isolated PSF Guard server. Verify native save-event consumers,
including Sync, across profile changes. Keep Chatstronomy's TS integration intact
and add explicit Director context rather than impersonating TS events.
