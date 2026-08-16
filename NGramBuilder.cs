using System;
using System.Collections.Generic;
using System.Linq;

// Generic n-gram counting over any sequence of string "words" — extracted from Program.Main
// so the same logic can be reused for both the main prose token stream and, per-Unit, for a
// parallel path-segment n-gram namespace (see Program.PrintPathNGrams).
public static class NGramBuilder
{
    // Slides windows of each requested size over `words`, incrementing per-ngram counts
    // (joined with `separator`) and per-size total-token counts. Mutates `counts` and
    // `totalTokensPerN` in place so multiple calls (e.g. once per line, or once per Unit)
    // can accumulate into the same dictionaries.
    public static void CollectNGrams(
        IReadOnlyList<string> words,
        HashSet<int> sizes,
        Dictionary<int, Dictionary<string, int>> counts,
        Dictionary<int, int> totalTokensPerN,
        string separator = " ")
    {
        if (words.Count == 0) return;

        int maxN = sizes.Count > 0 ? sizes.Max() : 0;
        for (int n = 1; n <= maxN; n++)
        {
            if (!sizes.Contains(n)) continue;
            if (words.Count < n) continue;

            if (!counts.ContainsKey(n)) counts[n] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!totalTokensPerN.ContainsKey(n)) totalTokensPerN[n] = 0;

            var tokenCountForLine = Math.Max(0, words.Count - n + 1);
            totalTokensPerN[n] += tokenCountForLine;

            for (int i = 0; i <= words.Count - n; i++)
            {
                var ngram = n == 1 ? words[i] : string.Join(separator, Enumerable.Range(0, n).Select(k => words[i + k]));
                counts[n][ngram] = counts[n].TryGetValue(ngram, out var c) ? c + 1 : 1;
            }
        }
    }
}
