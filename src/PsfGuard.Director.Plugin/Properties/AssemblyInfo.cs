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
[assembly: AssemblyMetadata("FeaturedImageURL", "https://raw.githubusercontent.com/theatrus/psf-guard-director-nina-plugin/main/assets/psf-guard-director.png")]
[assembly: AssemblyMetadata("ShortDescription", "Beta PSF Guard Director with local target scheduling and calibration.")]
[assembly: AssemblyMetadata("LongDescription", "Experimental Director selects targets and filters from active PSF Guard plans. Reports equipment and requests work automatically, and recovers interrupted sessions using current image counts while preserving images and history. Shows sequence and trigger activity in NINA and PSF Guard. Supports native centering, rotation, autofocus, guiding, dithering and meridian flips, plus custom sequence instructions. Optional panel flats and darks use active-plan settings; flat replacement requests sync from PSF Guard without deleting original files. Requires a current PSF Guard server, plus acquisition, safety and enclosure settings. Image uploads are optional and off by default. Real-sky full-night acceptance remains open. Do not use for unattended imaging. Director is separate from PSF Guard Sync and does not replace or change Sync.")]
[assembly: AssemblyMetadata("Tags", "psf-guard,director,beta,experimental")]
