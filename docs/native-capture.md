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

Schema 1 is experimental host evidence, not the final IPC/event contract. It
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
asks Rust again after filter preparation and immediately before the adapter's
capture call, and rejects a changed recommendation. This does not solve the
imaging mediator's possible internal queue delay.

## Durable ledger host

Runtime 0.2 / IPC 2 adds opt-in persistence through
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
