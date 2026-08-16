# Idea 007: Dispersion Measures

## The question it answers
"Is this term used everywhere, or is it just one file screaming really loudly?"

## What it is
Dispersion measures how *evenly* a term is spread across the corpus/files,
as distinct from how often it occurs in total. A term appearing 100 times in
one file and zero times elsewhere is very different, corpus-linguistically,
from one occurring evenly ~1x across 100 files — raw frequency alone can't
tell these apart. Standard measures: **Juilland's D** and **DP (Deviation of
Proportions)**.

## Why it matters / relation to what ngc has
`ngc` already has per-file TF-IDF (via `--per-file`, `PerDocNGramCounts`),
which is related but answers a different question (distinctiveness of a term
to one document vs. the rest). Dispersion is a distinct, well-established
statistic in its own right, and — importantly — reuses the exact same
per-file count data `ngc` already collects. This makes it a very cheap
addition relative to its analytical value.

## Possible ngc extension
Add `--show-dispersion` computing Juilland's D or DP per n-gram from the
existing `PerDocNGramCounts` / `PerDocTotalTokensPerN` dictionaries — just a
new formula over data already being gathered, no new collection pass.

## Licensing / porting notes
No licensing exposure: Juilland's D and DP are standard published corpus-
linguistics formulas, not tied to any specific tool's codebase. Straight
implementation from the published formula, no porting-source concerns.
