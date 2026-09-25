# PSF Guard Director for N.I.N.A.

An experimental, goal-driven acquisition plugin for N.I.N.A. 3.3. Director will
execute PSF Guard observing assignments through Target Scheduler while keeping
equipment safety and operator control local.

Director is separate from PSF Guard Sync. It uses the same Rust planning core
as PSF Guard through a bundled, versioned sidecar; it does not reimplement the
planner in C#.

The architecture and acceptance gates live in
[PSF Guard's Director design](https://github.com/theatrus/psf-guard/blob/codex/director-sidecar/docs/design/director.md).
This repository is not a stable plugin release and has no acquisition support yet.

## License

Apache-2.0. Copyright 2026 Yann Ramin (@theatrus).
