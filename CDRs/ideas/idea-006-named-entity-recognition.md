# Idea 006: Named Entity Recognition (NER)

## The question it answers
"Are my top phrases actually interesting, or just a list of people/company names?"

## What it is
Named entity recognition identifies and labels spans of text as belonging to
categories like PERSON, ORG (organization), DATE, GPE (place), etc. Reference
tools: **spaCy**, **Stanza** (same libraries that provide POS tagging/
lemmatization in idea-005 — this is typically a sibling pipeline stage in the
same tools).

## Why it matters
Without NER, top-n-gram lists in real-world corpora (logs, emails, articles,
transcripts) are very often dominated by proper nouns — a person's name, a
company, a product — which crowd out more analytically interesting content
phrases. Being able to exclude, separately bucket, or specifically surface
named entities gives users control over whether they're studying "vocabulary
and phrasing" or "who/what is being talked about."

## Possible ngc extension
Once a tagger dependency exists (see idea-005), NER labels would ride along
for free from the same pipeline. Could support `--exclude-entities PERSON,ORG`
or `--show-entities-only` flags, filtering n-grams whose tokens overlap a
detected entity span. Depends on idea-005's tagging infrastructure decision.

## Licensing / porting notes
Same porting source guidance as idea-005: use **spaCy (MIT)**
([explosion/spaCy](https://github.com/explosion/spaCy)) or **Stanza
(Apache 2.0)** ([stanfordnlp/stanza](https://github.com/stanfordnlp/stanza))
models/architectures, consumed in-process via ONNX Runtime, not subprocess
calls. Avoid **Stanford CoreNLP (GPL)**
([stanfordnlp/CoreNLP](https://github.com/stanfordnlp/CoreNLP)) as a porting
source for the same copyleft reason noted in idea-005 — CoreNLP's NER models
are a common reference point in NLP literature, but the actual code/
architecture we port should come from an MIT/Apache-licensed project instead.
See [idea-999-reference-repos.md](idea-999-reference-repos.md) for the full
repo/license lookup table.
