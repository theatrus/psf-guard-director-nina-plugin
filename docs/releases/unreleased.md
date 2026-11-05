# Unreleased

- Director explicitly starts sidereal tracking before slewing and centering, including when your mount is already unparked. It confirms tracking before continuing and shows a clear error if the mount refuses or does not respond.
- Director shows its current action and elapsed time in N.I.N.A.'s status bar, with target and filter context during capture. Startup steps report their durations and saved images are checked once per start.
- Choose **Recalibrate before guiding** to reset guider calibration for each target and retry, or use your own instructions in **Before New Target**. Guiding and exposure failures keep their original error visible through shutdown and retry, with clearer messages about images and targets.
- Share NINA weather telemetry through the existing PSF Guard pairing. Sessions
  can opt in to holds between exposures using NINA cloud cover, the site's
  central forecast, or both. Forecasts reuse the site's cloud and dew limits,
  including centrally configured paid providers. Missing or stale enabled
  sources hold the session; physical safety and roof controls remain independent.
