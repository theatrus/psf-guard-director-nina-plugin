# Unreleased

- Add an experimental Director Session to the Advanced Sequencer, with
  seven TS-style instruction slots, native triggers and conditions, grouped
  local-policy settings, and a status view. Configuration can be saved and
  cloned without carrying credentials or launch authority.
- Allow opt-in, bounded acquisition of one already prepared target using an
  operator-issued allocation and online one-shot launch. Native safety, tracking,
  geometry and capture/save checks remain local; offline continuation retains
  durable receipts for batch check-in. Shutdown parks the mount and reports final
  status when connected. Automatic multi-target work and restart/resume remain
  unavailable. See [prepared-target setup](../native-capture.md#public-prepared-target-mode-experimental).
