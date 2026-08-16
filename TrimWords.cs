using System;
using System.Collections.Generic;

// A small, conservative default set of pure grammatical-glue words used to filter out
// n-grams whose FIRST OR LAST token is one of these — i.e. phrases that don't have real
// content anchoring at least one of their edges (e.g. "for the", "the same", "its own" all
// get dropped: each has a trim word sitting right at an edge with nothing but glue there).
//
// This is a DIFFERENT concept from StopWords: a stopword-phrase test asks "is every word in
// this phrase content-free?" (coarse, all-or-nothing). A trim-word test asks "does this
// phrase have real content anchoring EACH edge?" (stricter, position-sensitive).
//
// Critically, TrimWords is NOT simply "StopWords minus a few exceptions" that happens to be
// smaller — it deliberately EXCLUDES several categories of word that are stopword-light in
// the coarse sense, but routinely ARE the signal when they sit at a phrase boundary:
//   - negation: "not", "no"            (e.g. "not yet resolved", "does not decide")
//   - do-support: "do", "does", "did"  (e.g. "does not decide" needs "does" anchoring it)
//   - modals: "can/could/will/would/shall/should/may/might/must" (hypotheticals are signal)
//   - wh-words: "which/who/whom/whose/what/when/where/why/how" (open questions are signal)
// None of those are in TrimWords.Default, even though all of them ARE in StopWords.Default.
// See CommandOptions.EffectiveTrimWords / --trim-words / --no-trim-words.
public static class TrimWords
{
    public static readonly HashSet<string> Default = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the",
        "-",
        "and", "or", "but", "so", "nor",
        "of", "to", "in", "on", "at", "by", "with", "from", "as", "into", "onto", "than",
        "is", "are", "was", "were", "be", "been", "being",
        "this", "that", "these", "those", "it", "its",
    };
}
