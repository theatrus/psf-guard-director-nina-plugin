using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PsfGuard.Director.Tests")]
[assembly: InternalsVisibleTo("PsfGuard.Director.SimulatorProbe")]

[assembly: ComVisible(false)]
[assembly: Guid("03a1d13e-67eb-4e24-a407-82bce7e576a5")]
[assembly: AssemblyMetadata("License", "Apache-2.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://github.com/theatrus/psf-guard-director-nina-plugin/blob/main/LICENSE")]
[assembly: AssemblyMetadata("Repository", "https://github.com/theatrus/psf-guard-director-nina-plugin")]
[assembly: AssemblyMetadata("Homepage", "https://psf-guard.com")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]
[assembly: AssemblyMetadata("ShortDescription", "Experimental PSF Guard Director with bounded local target scheduling.")]
[assembly: AssemblyMetadata("LongDescription", "Experimental Director with shared-core local target scheduling, ranked priorities and Moon avoidance inside bounded allocations. Supports pairing, automatic workload requests, live or deferred check-ins, native NINA centering, rotation, autofocus, guiding, dithering and meridian flips, plus custom sequence hooks. Requires explicit acquisition, safety and enclosure policies. Settled-night restart is opt-in; uncertain-work resume and real-sky full-night acceptance remain unavailable. Do not use for unattended imaging. Director is separate from PSF Guard Sync and does not replace or change Sync.")]
[assembly: AssemblyMetadata("Tags", "psf-guard,director,experimental")]
