# Idea 003: KWIC / Concordancing (Keyword-In-Context)

## The question it answers
"Show me the word/phrase in context."

## What it is
KWIC (Keyword-In-Context) / concordancing is the ability to click or query an
n-gram and see every occurrence of it in the corpus with N words of context on
each side, typically sortable by left-context or right-context. Every serious
corpus tool has this: AntConc, Sketch Engine, CQPweb, and NLTK's
`concordance()` method all support it.

## Why it matters
This is arguably THE single most-used feature linguists actually touch daily.
It's the natural next step after a frequency/n-gram tool finds something
interesting: `ngc` can tell you "X Y" occurs 40 times, but right now can't show
you the 40 actual lines/passages where that happens. Frequency counting tells
you *what* is common; concordancing tells you *how* it's used.

## Possible ngc extension
A `--show-context N` flag: given a target n-gram (or the top-K results already
being shown), re-scan the tokenized lines and print each occurrence with N
tokens of context on either side. This reuses the existing `Tokenizer`
line-processing pipeline directly — no new tokenization logic needed, just
line/position tracking through to output. Likely the cheapest big win of all
these ideas since it needs no external NLP dependencies.

## Licensing / porting notes
No licensing exposure at all: KWIC/concordancing is a simple presentation
pattern (context window around a match), not tied to any tool's codebase.
Pure original C# implementation on top of `ngc`'s existing tokenizer — no
porting-source question applies here.
