# Idea 001: Frequency / Collocation Baseline (what ngc already does)

## The question it answers
"What words/phrases matter here, and how much?"

## What it is
Frequency, keyness, and collocation stats over n-grams. This is the tool-family
`ngc` already lives in: PPM (frequency-per-million), PMI, TF-IDF, z-scores, and
percentile filtering are all standard corpus-linguistics measures. `ngc` isn't
doing anything unusual here — it's doing this well, with a clean tokenization
pipeline (path/quote-aware "units"), stopword and trim-word filtering, and
per-document vs. per-corpus aggregation.

## Status
Already implemented in `ngc` (NGramBuilder, StopWords, TrimWords, Program.cs
CommandOptions: Ppm/Z/Pmi/Tfidf/Percentile filters). Recorded here as the
baseline/reference point for how the other ideas below relate to and extend it.

## Related tools in the wild
AntConc, Sketch Engine, Wordsmith Tools, CQPweb — all built around this same
core frequency/collocation engine, with the other idea files below layered on
top of it.
