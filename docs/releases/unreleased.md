# Unreleased

- Optionally retry known-completed autofocus and guide-start failures during
  Director-owned target setup. Configure cooldown, a shared session retry budget
  and total recovery time; exhaustion uses the existing park/stop choice. Unsafe
  weather and roof closure stop without retry unless weather holds are enabled.
- Opt-in weather holds now cover startup, workload waits and interrupted live
  exposures. Resume requires stable Safe/Open evidence, a confirmed idle camera,
  and settled capture outcomes. Interrupted attempts remain spent; closing roofs
  block parking. Night end continues to the following sequence steps without
  restarting acquisition. Unknown saves, interrupted custom hooks and
  crash-recovered operations still require reconciliation.
- Add real NINA Advanced Sequencer setup screenshots to the README and guide.

- Report the current operation, monotonic elapsed time, goal, wait/queue state
  and J2000 mount pointing to PSF Guard. Completed preparation receipts share
  the live, deferred and manual Check In paths with independent durable cursors.
  Replayed history never replaces the live status snapshot. Requires a PSF Guard
  server with the Director operations endpoint.
