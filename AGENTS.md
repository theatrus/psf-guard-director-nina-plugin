# Director contributor guide

- Keep N.I.N.A. types in `src/PsfGuard.Director.Plugin` and the planner in the
  shared Rust crate in PSF Guard. The runtime library must remain testable
  without loading N.I.N.A.
- Do not change or replace PSF Guard Sync. Use a distinct plugin identity,
  settings namespace, and experimental release channel.
- Use pinned N.I.N.A. and runtime versions. Verify runtime hashes and IPC,
  engine, and contract versions before accepting decisions.
- A failed or stale runtime session cannot authorize equipment dispatch.
- Keep credentials out of process arguments, environment variables, logs,
  and generated configuration files.
- Never overwrite live catalogs or operate real equipment in tests. Use
  copied catalogs, an isolated PSF Guard registry, and N.I.N.A. simulated devices.
- Each PR needs regression tests, code review, and exact validation evidence.
  Full-stack validation requires the real plugin, bundled runtime, native N.I.N.A. adapter,
  and an isolated PSF Guard build; console tests alone do not satisfy that gate.
- Director must run without TS installed. Keep planning/feedback policy in
  Rust and reuse supported N.I.N.A. operations rather than TS internals.
- Preserve Chatstronomy's existing TS integration. Publish explicit Director
  state and command context, not spoofed TS events or private TS container types.
- Use the existing authenticated `gh` CLI for repository and PR operations.
