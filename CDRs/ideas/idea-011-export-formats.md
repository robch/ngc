# Idea 011: Concordance/Collocate Export Formats (CSV/JSON)

## The question it answers
"Can I get this data into a spreadsheet or a notebook?"

## What it is
Structured export of n-gram/collocate/concordance results as CSV or JSON,
instead of (or alongside) the human-readable console report `ngc` currently
produces. Standard expectation across virtually all serious corpus tools
(AntConc, Sketch Engine, etc. all support tabular export) since downstream
analysis routinely moves into a spreadsheet or a Python/R notebook.

## Why it matters
Right now (needs verification against the current codebase) `ngc` appears to
be console-report-only. Even a very good console report has a ceiling: once
someone wants to sort/filter/chart/join the data against something else, they
need it in a structured file format. This is a low-conceptual-complexity but
high-practical-value gap — it's "plumbing," not a new statistic, but it's the
plumbing that lets every other idea in this set actually get used downstream.

## Possible ngc extension
A `--format csv` / `--format json` flag (default remains the current console
report) that serializes the same result rows (n-gram, count, ppm, z, pmi,
tfidf, etc. — whichever columns are currently enabled via the existing
`--show-*` flags) to stdout or a file. Should be a relatively mechanical
addition since the underlying result rows already exist before being
formatted for console display.
