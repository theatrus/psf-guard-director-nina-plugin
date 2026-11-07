# Unreleased

## Equipment

- **Run templates left at the camera default:** **Report equipment** now includes
  N.I.N.A.'s default gain and offset. PSF Guard uses them for exposure templates
  set to `-1`, which were otherwise left out of the rig's work. Recipes stay
  explicit, so a `-1` never reaches the camera. Update PSF Guard so it can use
  these values, click **Report equipment**, then accept the report and allow
  work again. An older PSF Guard still gets the report, without the defaults.
