# Idea 002: Keyness Analysis (Corpus vs. Reference Corpus)

## The question it answers
"What's distinctive about THIS corpus vs. a reference corpus?"

## What it is
Keyness analysis compares your document set against a large reference/background
corpus (e.g. the British National Corpus, or simply "everything else" you have
lying around) and ranks words/phrases by how over- or under-represented they
are — not just how frequent they are in isolation. Standard statistical
measures for this: log-likelihood ratio (G2), chi-square, and %DIFF.

## Why it's different from what ngc has
`ngc`'s TF-IDF is document-vs-corpus (how distinctive is this term to this one
file, relative to the rest of the current document set). Keyness is
corpus-vs-reference-corpus — comparing two whole document sets against each
other, or one set against an external/background corpus. Related, but a
genuinely different comparison axis.

## Reference tools
AntConc, Sketch Engine, Wordsmith Tools, CQPweb all have keyness analysis as a
headline feature.

## Possible ngc extension
Generalizes into idea-010 (corpus-vs-corpus diff mode): run `ngc` over two
`--files` sets and report log-likelihood/%DIFF per n-gram between them, reusing
the existing per-doc counting infrastructure (PerDocNGramCounts).

## Licensing / porting notes
No licensing exposure: log-likelihood, chi-square, and %DIFF are all standard
published statistical formulas, not tied to any specific tool's codebase
(AntConc/Sketch Engine/CQPweb all implement the same public formulas
independently). A reference corpus dataset (e.g. a public frequency list),
if used, would need its own license check separately from the formula itself.
