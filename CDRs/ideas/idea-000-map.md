# Idea Map: Text Analysis Features for ngc

An index of feature ideas gathered from surveying what "real" corpus-linguistics
and text-analysis tools (AntConc, Sketch Engine, CQPweb, spaCy, NLTK, gensim,
BERTopic, etc.) offer, and how they relate to what `ngc` already does. Each
idea has its own file with more detail; this file is the map/summary.

| # | Idea | Summary |
|---|------|---------|
| [001](idea-001-frequency-collocation-baseline.md) | Frequency / Collocation Baseline | What `ngc` already does. Frequency, keyness, and collocation stats over n-grams — PPM, PMI, TF-IDF, z-scores, percentile filtering. Standard corpus-linguistics measures, already implemented well; the reference point the other ideas extend. |
| [002](idea-002-keyness-analysis.md) | Keyness Analysis | Compares your corpus against a reference/background corpus (or another corpus) and ranks words by how over/under-represented they are, using log-likelihood, chi-square, or %DIFF — not just raw frequency. Core feature of AntConc, Sketch Engine, CQPweb. |
| [003](idea-003-kwic-concordancing.md) | KWIC / Concordancing | Shows every occurrence of a word/phrase in context (N words left/right), sortable by context. The single most-used feature in daily corpus-linguistics work. Cheap to add — reuses existing tokenization/line pipeline, no external deps. |
| [004](idea-004-alternate-association-measures.md) | Alternate Association Measures | PMI overweights rare pairs. Offers log-Dice (Sketch Engine's default), log-likelihood, t-score, and MI3 as alternate/complementary collocation-strength measures, computed from data `ngc` already collects. |
| [005](idea-005-pos-tagging-lemmatization.md) | POS Tagging & Lemmatization | Reduces inflected forms ("run/runs/running/ran") to one lemma before counting, and enables POS-based filtering (e.g. "only adjective-noun bigrams"). Probably the single highest-leverage missing feature; requires spaCy/Stanza/NLTK or a lightweight in-process stemmer. |
| [006](idea-006-named-entity-recognition.md) | Named Entity Recognition | Labels spans as PERSON/ORG/DATE/etc. so top-n-gram lists aren't dominated by proper nouns. Natural sibling of idea-005, rides along on the same tagger dependency. |
| [007](idea-007-dispersion-measures.md) | Dispersion Measures | Measures how evenly a term is spread across files/corpus (Juilland's D, DP), distinguishing "used everywhere a little" from "used a ton in one file." Cheap addition — reuses `ngc`'s existing per-file count data. |
| [008](idea-008-readability-lexical-diversity.md) | Readability / Lexical Diversity | Whole-document/corpus summary stats: type-token ratio, MTLD, Flesch-Kincaid. A different zoom level — one number per corpus/file rather than per-phrase. Common in dataset-characterization ("data-prep") use cases. |
| [009](idea-009-topic-modeling-clustering.md) | Topic Modeling / Clustering | Discovers latent themes across documents (LDA, NMF, or embedding-based clustering like BERTopic), rather than just ranking phrases. The natural "next tier up" from frequency counting; a bigger lift requiring external ML dependencies. |
| [010](idea-010-corpus-vs-corpus-diff.md) | Corpus-vs-Corpus Diff Mode | Generalizes keyness analysis (idea-002) to two arbitrary user-supplied document sets — "what's new/gone between version A and version B." Reuses `ngc`'s existing per-doc infrastructure almost entirely as-is. |
| [011](idea-011-export-formats.md) | Concordance/Collocate Export Formats | CSV/JSON export of result rows, instead of/alongside the console report. Low conceptual complexity, high practical value — the plumbing that lets every other idea's output actually get used downstream (spreadsheets, notebooks). |
| [999](idea-999-reference-repos.md) | Reference: Open-Source Repos | Not a feature idea — a lookup table of every GitHub org/repo + license referenced across ideas 001–011 (spaCy, Stanza, NLTK, scikit-learn, gensim, CoreNLP, BERTopic), plus the closed-source tools named for context. |

## Suggested priority (cheapest/highest-value first)
1. **003** KWIC/concordancing — huge usability jump, no new dependencies.
2. **010** Corpus-vs-corpus diff — generalizes existing per-doc infra.
3. **007** Dispersion score — same per-file data already collected, just a new formula.
4. **011** Export formats — mechanical, unlocks downstream use of everything else.
5. **004** Alternate association measures — new formulas over existing PMI data.
6. **002** Keyness analysis (vs. external reference corpus) — needs a reference corpus source.
7. **008** Readability/lexical diversity — new but self-contained summary stats.
8. **005 / 006** POS tagging, lemmatization, NER — biggest win for linguists specifically,
   but requires an external tagger dependency and a design discussion first.
9. **009** Topic modeling/clustering — biggest lift, its own design discussion.

## Licensing / porting strategy
The plan for ideas that draw on existing open-source tools is to **port the
algorithm into our own C# code** ("port-n-morph"), not to shell out to Python
at runtime — no subprocess dependency, no cross-ecosystem version skew, stays
a single self-contained CLI. This constrains which projects are safe to port
*from*:

- **Permissive (safe to port from): MIT, Apache 2.0, BSD.** Minimal
  obligations (keep the notice), no requirement to open-source our code, no
  restrictions on commercial use. **spaCy** (MIT), **Stanza** (Apache 2.0),
  **NLTK** (Apache 2.0), and **scikit-learn** (BSD) are our named porting
  sources for POS/lemma/NER (005/006) and NMF (009).
- **Copyleft (avoid as a porting source): GPL, and — for a rewritten/ported
  derivative specifically — LGPL too.** **Stanford CoreNLP is GPL**: a
  derivative port of its code would inherit GPL obligations, so it's
  reference-only, never a fork source. **gensim is LGPL**: its
  linking-exception protection doesn't cleanly cover "rewrote the algorithm
  in another language and shipped it as our own source," so treat it the
  same way — implement LDA from the original published algorithm/paper
  (public academic knowledge, not gensim's IP) rather than porting gensim's
  source.
- **Pretrained-model ideas (005/006, and 009's embedding upgrade path)**
  consume a model as a vendored artifact via **ONNX + Microsoft.ML.OnnxRuntime**
  (in-process, no Python at runtime), with our own C# tokenizer-alignment and
  inference glue — the actual code we ship is original, only the trained
  weights are sourced externally (per-model license still needs checking).
- **Pure-formula ideas (002, 004, 007, 008, 003, 010, 011)** have **no
  licensing exposure at all** — they're standard published statistical
  formulas or original presentation/plumbing code, not derived from any
  single tool's codebase.
