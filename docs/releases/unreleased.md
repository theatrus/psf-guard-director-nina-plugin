# Unreleased

- Optionally retry known-completed autofocus and guide-start failures during
  Director-owned target setup. Configure cooldown, a shared session retry budget
  and total recovery time; exhaustion uses the existing park/stop choice. Unsafe
  weather, roof closure and uncertain operations still stop without retry.
- Add real NINA Advanced Sequencer setup screenshots to the README and guide.

- Report the current operation, monotonic elapsed time, goal, wait/queue state
  and J2000 mount pointing to PSF Guard. Completed preparation receipts share
  the live, deferred and manual Check In paths with independent durable cursors.
  Replayed history never replaces the live status snapshot. Requires a PSF Guard
  server with the Director operations endpoint.
