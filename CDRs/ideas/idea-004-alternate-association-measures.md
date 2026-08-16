# Idea 004: Alternate Association Measures (log-Dice, log-likelihood, t-score, MI3)

## The question it answers
"Is PMI the best way to measure 'these words go together'?"

## What it is
PMI (pointwise mutual information) is one of many statistical association
measures used to score collocations, and it has a well-known weakness: it
overweights rare pairs (two words that each occur once, together once, get an
inflated score). Corpus linguistics has several alternatives, each with
different tradeoffs:
- **log-Dice** — Sketch Engine's default; more robust to rare-pair inflation.
- **log-likelihood (G2)** — good general-purpose significance measure, also
  used for keyness (see idea-002).
- **t-score** — favors frequent, well-attested collocations over rare flukes.
- **MI3** (cubed mutual information) — a common tweak to plain PMI/MI that
  reduces the low-frequency bias somewhat while keeping the "mutual
  information" flavor.

## Why it matters
Different measures surface different kinds of collocations. PMI-only tools
tend to over-report rare/noisy pairs as "significant." Offering log-Dice
and/or t-score as alternates (or default) alongside PMI would let users choose
the measure matching their use case (rare-but-real pair discovery vs. robust,
well-attested collocation ranking).

## Possible ngc extension
Add `--show-logdice` / `--show-tscore` alongside the existing `--show-pmi`,
computed from the same unigram + n-gram count dictionaries already collected
for PMI (see NGramBuilder / needPmiCollection in Program.cs). Purely a new
formula over existing data — no new collection pass needed.

## Licensing / porting notes
No licensing exposure at all: log-Dice, log-likelihood, t-score, and MI3 are
all standard published statistical formulas (academic/public-domain math),
not tied to any single tool's codebase. Nothing to "port" here beyond
implementing the formula directly from its published definition — safe
regardless of which tool (Sketch Engine, etc.) popularized it.
