# Reference data for the golden tests

These frozen files pin detection, change detection, configuration defaults and report
formats. They are test input, not configuration for the application.

| File | Meaning |
| --- | --- |
| `config-defaults.json` | Effective configuration defaults |
| `detection-golden.json` | Normalisation, similarity, detection and deduplication cases |
| `change-detection-golden.json` | Change-detection ratios; the crops are described by formula |
| `reports-baseline/` | events.json, events.csv, segments.json and summary.json of one analysis run |

The files are not regenerated; a deliberate behaviour change updates them by hand.
