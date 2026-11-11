# Unreleased

- Already-reported run history stays reported when the bundled runtime is
  upgraded. Director migrates the records from older releases without reopening
  each ledger or resending its events. Startup shows unsent image-record progress;
  detailed historical run counts remain in the log.
