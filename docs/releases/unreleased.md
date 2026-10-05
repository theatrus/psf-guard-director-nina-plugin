# Unreleased

- Report the current operation, monotonic elapsed time, goal, wait/queue state
  and J2000 mount pointing to PSF Guard. Completed preparation receipts share
  the live, deferred and manual Check In paths with independent durable cursors.
  Replayed history never replaces the live status snapshot. Requires a PSF Guard
  server with the Director operations endpoint.
