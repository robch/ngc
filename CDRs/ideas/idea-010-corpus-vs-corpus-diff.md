# Idea 010: Corpus-vs-Corpus Diff Mode

## The question it answers
"What's new, gone, or changed between version A and version B of my docset?"

## What it is
A diff/comparison mode between two document sets: run keyness analysis (see
idea-002) side-by-side between corpus A and corpus B, rather than one corpus
against a static external reference corpus. Answers questions like "what
phrases appeared in the new version of these docs that weren't there before"
or "what dropped out."

## Why it matters
This is a very natural generalization of keyness analysis (idea-002) to two
*arbitrary*, user-supplied document sets instead of one set vs. a fixed
external reference corpus — useful for tracking documentation changes,
comparing two log periods, before/after a change, two competing datasets, etc.

## Possible ngc extension
`ngc` already has `--files` with per-document tracking (`DocumentNames`,
`PerDocNGramCounts`). This idea would extend that to accept two *labeled
groups* of files/globs (e.g. `--files-a ... --files-b ...`) and report
log-likelihood/%DIFF (from idea-002) per n-gram between the two aggregated
groups, reusing the existing per-doc counting infrastructure almost entirely
as-is — mostly a CLI/aggregation-layer feature rather than a new stats engine.
