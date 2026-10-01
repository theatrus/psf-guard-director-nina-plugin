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
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.3.0.1058")]
[assembly: AssemblyMetadata("ShortDescription", "Experimental PSF Guard Director with bounded prepared-target acquisition.")]
[assembly: AssemblyMetadata("LongDescription", "Experimental Director for operator-issued, single prepared-target allocations. Requires NINA safety monitoring and sequence-owned centering, focus, guiding, dithering and meridian flips. Automatic scheduling and restart/resume are not yet available.")]
[assembly: AssemblyMetadata("Tags", "psf-guard,director,experimental")]
