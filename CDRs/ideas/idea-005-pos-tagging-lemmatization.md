# Idea 005: Part-of-Speech Tagging & Lemmatization

## The question it answers
"Are 'run', 'runs', 'running', and 'ran' really four different things?"

## What it is
Lemmatization reduces inflected word forms to a single dictionary/base form
(lemma) before counting, so frequency stats reflect the underlying word rather
than its surface inflection. Part-of-speech (POS) tagging labels each token
with its grammatical category (noun, verb, adjective, etc.), enabling queries
like "show me only adjective-noun bigrams" or excluding function-word
categories more precisely than a static stopword list can.

Reference tools: **spaCy**, **NLTK**, **Stanford CoreNLP / Stanza** — all
provide POS tagging and lemmatization as core, well-established pipeline
stages that basically every serious corpus/NLP tool runs before frequency
analysis.

## Why it matters / biggest gap
This is probably the single highest-leverage feature category missing from
`ngc`. Right now every inflected form is counted as a wholly separate n-gram,
which both fragments frequency counts across a lemma's forms and denies users
any POS-based filtering (e.g. "just show me noun phrases").

## Possible ngc extension
Two tiers of effort, both built as **in-process C# ports**, not subprocess/
shell-outs to Python:
- **Cheap partial substitute**: port the Porter/Snowball stemming algorithm
  directly into C# — it's a pure rule-based suffix-stripping algorithm with no
  trained weights, trivial to port, imperfect but reduces surface-form
  fragmentation immediately with zero dependencies.
- **Full solution**: consume a pretrained spaCy or Stanza model exported to
  ONNX format via `Microsoft.ML.OnnxRuntime` (NuGet), with our own C#
  tokenizer-alignment and inference glue written around it — fully in-process,
  no Python runtime at request time. The model file is vendored as a data
  artifact (converted once, offline); the actual tagging/lemmatizing logic we
  ship is our own C# code calling into ONNX Runtime.

## Licensing / porting notes
Port from **spaCy (MIT)** — [explosion/spaCy](https://github.com/explosion/spaCy)
— or **Stanza (Apache 2.0)** — [stanfordnlp/stanza](https://github.com/stanfordnlp/stanza)
— both permissive, safe to port code/architecture from directly. Explicitly
**avoid Stanford CoreNLP** — [stanfordnlp/CoreNLP](https://github.com/stanfordnlp/CoreNLP),
GPL-licensed (copyleft) — as a porting source: a derivative port of its code
would inherit GPL obligations. CoreNLP is fine to use only as an
inference-only reference for comparison/validation, never as a source to fork.
See [idea-999-reference-repos.md](idea-999-reference-repos.md) for the full
repo/license lookup table.
