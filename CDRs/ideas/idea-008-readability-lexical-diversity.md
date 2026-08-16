# Idea 008: Readability / Lexical Diversity Metrics

## The question it answers
"How complex or varied is the language in this document set, overall?"

## What it is
A family of whole-document/whole-corpus summary statistics, distinct from
per-n-gram stats:
- **Type-Token Ratio (TTR)** — ratio of unique words to total words, a crude
  lexical-diversity measure (sensitive to document length).
- **MTLD (Measure of Textual Lexical Diversity)** — a length-robust
  alternative to TTR, standard in corpus linguistics.
- **Flesch-Kincaid** (and similar) — readability grade-level scores based on
  sentence length and syllable counts.

## Why it matters
Common in corpus-linguistics-adjacent "data-prep" contexts — e.g.
characterizing a dataset before using it for training/fine-tuning, or
comparing the complexity/diversity of two document sets at a glance, without
digging into individual n-grams at all. This is a different zoom level than
everything else in this idea set: a single summary number per corpus/file
rather than a per-phrase statistic.

## Possible ngc extension
A `--show-corpus-stats` (or similar) summary section reporting TTR/MTLD per
file and for the whole corpus. Requires sentence/syllable-boundary detection
for Flesch-Kincaid specifically (syllable counting is the fiddly part); TTR
and MTLD only need the token stream `ngc` already produces.

## Licensing / porting notes
No licensing exposure: TTR, MTLD, and Flesch-Kincaid are all standard
published formulas (academic/public-domain), independent of any specific
tool's codebase. Straight implementation from the published definitions.
