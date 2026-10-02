# Unreleased

- Require an explicit enclosure-clearance policy before acquisition. Existing
  sequences start unconfigured. Fully-open enclosure evidence is independent of
  weather safety: closure or lost clearance aborts acquisition, requests mount
  stop/tracking-off and blocks parking. Reopening does not restart the session.

- Add opt-in local multi-target scheduling with shared-core priorities, native
  target setup hooks, and background check-ins that let a running workload
  switch targets while the server is offline. Old prepared-target sessions
  remain supported; automatic equipment-operation defaults are still pending.

- Add opt-in automatic commissioned workload intake to Director Session, with
  durable request retries, clean parked terminal check-in, and a bounded wait
  for eligible work or quality assessment. Successors cannot refill spent
  attempt budgets. The prepared-target and restart/recovery limits still apply.

- Add a Report equipment button to Director Session. It sends connected device
  capabilities and the session's constraint fingerprint for operator review,
  without enabling acquisition or overwriting the rig's manual setup.

- Add an experimental Director Session to the Advanced Sequencer, with
  seven TS-style instruction slots, native triggers and conditions, grouped
  local-policy settings, and a status view. Configuration can be saved and
  cloned without carrying credentials or launch authority.
- Allow opt-in, bounded acquisition of one already prepared target using an
  operator-issued allocation and online one-shot launch. Native safety, tracking,
  geometry and capture/save checks remain local; offline continuation retains
  durable receipts for batch check-in. Shutdown parks only with valid enclosure
  clearance and reports final status when connected. Restart/resume remains
  unavailable. See [prepared-target setup](../native-capture.md#public-prepared-target-mode-experimental).
