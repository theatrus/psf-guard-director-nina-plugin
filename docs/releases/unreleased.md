# Unreleased

- Saved-run check-in skips unchanged runs whose records were already sent, including empty failed runs, without starting a runtime for each one. Existing history is checked once after updating. Changed records, failed deliveries and outstanding completion reports still get checked; images and history stay on disk. Plugin settings now show when saved records are up to date and how many unchanged runs were skipped.
