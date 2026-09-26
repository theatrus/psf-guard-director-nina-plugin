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

Before exporting a Director sequencer item, wire core evaluation and a durable
event handoff to the sidecar, implement recovery, and run the simulated-equipment
gate with an isolated PSF Guard server. Verify native save-event consumers,
including Sync, across profile changes. Keep Chatstronomy's TS integration intact
and add explicit Director context rather than impersonating TS events.
