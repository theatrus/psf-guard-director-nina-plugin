# Unreleased

- Optional settled-night restart preserves the original night deadline and retry
  budgets, checks fresh native safety and idle equipment, and requests new work.
  It defaults off and never clears a terminal stop or resumes uncertain captures
  or hooks. Session cancellation now keeps recovery storage alive through shutdown
  so the stopped night is recorded before the sidecar exits.

- Image-quality screening is off by default. Optional monitoring, stop-and-park,
  or bounded cloud probes use a frozen initial reference and keep its unknown-
  quality warning visible. Probes are not saved as science images or credited
  toward goals. Slew failures still stop; focus/guide retries remain opt-in and
  have a configurable failure limit.
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
