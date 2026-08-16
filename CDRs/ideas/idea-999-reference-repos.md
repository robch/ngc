# Reference: Open-Source Repos Named Across These Ideas

A single lookup table for every open-source repo referenced by org/name across
idea-001 through idea-011, with license and the idea file(s) that mention it.
Closed-source/proprietary tools (AntConc, Sketch Engine, Wordsmith Tools) are
listed separately at the bottom for completeness — they have no GitHub repo.

## Open-source repos

| Tool | GitHub | License | Referenced in | Porting stance |
|---|---|---|---|---|
| spaCy | [explosion/spaCy](https://github.com/explosion/spaCy) | MIT | [005](idea-005-pos-tagging-lemmatization.md), [006](idea-006-named-entity-recognition.md) | ✅ safe to port from |
| Stanza | [stanfordnlp/stanza](https://github.com/stanfordnlp/stanza) | Apache 2.0 | [005](idea-005-pos-tagging-lemmatization.md), [006](idea-006-named-entity-recognition.md) | ✅ safe to port from |
| NLTK | [nltk/nltk](https://github.com/nltk/nltk) | Apache 2.0 | [001](idea-001-frequency-collocation-baseline.md), [003](idea-003-kwic-concordancing.md) | ✅ safe to port from |
| scikit-learn | [scikit-learn/scikit-learn](https://github.com/scikit-learn/scikit-learn) | BSD-3-Clause | [009](idea-009-topic-modeling-clustering.md) | ✅ safe to port from |
| gensim | [piskvorky/gensim](https://github.com/piskvorky/gensim) | LGPL-2.1 | [009](idea-009-topic-modeling-clustering.md) | ⚠️ avoid as a porting source — implement LDA from the original published algorithm/paper instead |
| Stanford CoreNLP | [stanfordnlp/CoreNLP](https://github.com/stanfordnlp/CoreNLP) | GPL-2/3 | [005](idea-005-pos-tagging-lemmatization.md), [006](idea-006-named-entity-recognition.md) | ❌ avoid as a porting source — reference/comparison only |
| BERTopic | [MaartenGr/BERTopic](https://github.com/MaartenGr/BERTopic) | MIT | [009](idea-009-topic-modeling-clustering.md) | ✅ safe to port from |

Note: Stanza and CoreNLP are both Stanford NLP projects but are separate,
independently-licensed codebases living under the same `stanfordnlp` GitHub
org — Stanza is the newer neural rewrite (Apache 2.0, portable), CoreNLP is
the older Java toolkit (GPL, not portable). Easy to conflate by name; don't.

## Closed-source / proprietary (no GitHub repo — listed for context only)

| Tool | Status | Referenced in |
|---|---|---|
| AntConc | Free proprietary freeware (Laurence Anthony) — no public source | [002](idea-002-keyness-analysis.md), [003](idea-003-kwic-concordancing.md) |
| Sketch Engine | Commercial/paid, closed source | [002](idea-002-keyness-analysis.md), [004](idea-004-alternate-association-measures.md) |
| Wordsmith Tools | Commercial, closed source | [002](idea-002-keyness-analysis.md) |

## Open-source linguist-facing tool (not a code-porting source, listed for completeness)

| Tool | Repo | License |
|---|---|---|
| CQPweb | Primarily hosted on SourceForge; GitHub mirrors exist (e.g. search `cqpweb`) but no single canonical GitHub org/repo has been confirmed here — needs pinning down if we ever want to reference its code directly. | GPL-ish (CWB stack licensing varies by component) |
