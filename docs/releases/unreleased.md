# Unreleased

- Director uploads the lights a Director Session captures to the paired PSF Guard database once N.I.N.A. saves them. Turn on **Image uploads** for the database under Rig setup › Plugins in PSF Guard. Director keeps frames in a queue on disk while PSF Guard is unreachable, and does nothing with a PSF Guard version that cannot take them. Turn off **Upload captured frames to PSF Guard** in Director's plugin settings to stop uploads. The settings show whether PSF Guard takes uploads and how many frames are queued or refused.
