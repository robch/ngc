using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// What kind of atomic "unit" a token represents. Units are spans of the raw line that are
// treated as a single opaque slot for prose n-gram purposes (so they never fragment into
// fake phrases), while still being decomposable into their own "interior" words/segments
// for unigram counting and for a separate, parallel n-gram namespace (see NGramBuilder /
// Program.PrintPathNGrams).
//
// Path stays its own shape-based kind (detected by pattern, not by delimiter pair). Paired
// covers every delimiter-pair span — the built-in defaults (quotes, backtick code-spans)
// AND any user-supplied --pair-chars — since they're all structurally identical: "protect
// everything between this open char and the matching close char as one atomic slot."
public enum UnitKind { Path, Paired }

// An atomic unit detected in a line: a path (word/word/word...) or a paired-delimiter span
// (quotes, backtick code-spans, or a user-defined --pair-chars pair).
public class Unit
{
    public UnitKind Kind = UnitKind.Path;
    public string RawText = string.Empty;      // original text, e.g. "src/whatever/services/file.cs"
    public string DisplayToken = string.Empty; // how it appears as ONE slot in prose n-grams
    public List<string> InteriorSegments = new List<string>(); // Path: split on / or \ ; Paired: split into words
}

// One element of the per-line token stream: either a plain prose word, or a placeholder
// referencing a Unit. Prose n-gram building treats both uniformly via Display; only Units
// get the extra "explode into interior words for unigrams" and "build a parallel segment
// n-gram" treatment.
public class Token
{
    public string? Word;
    public Unit? Unit;

    public bool IsUnit => Unit != null;
    public string Display => IsUnit ? Unit!.DisplayToken : Word!;

    public static Token FromWord(string word) => new Token { Word = word };
    public static Token FromUnit(Unit unit) => new Token { Unit = unit };
}

// Turns a raw line of text into a stream of Tokens across four explicit, independently
// user-controllable layers (see ngc-feedback.md #4 for the full design writeup this
// generalizes):
//   Layer 1 — pair-span detection (Units: quotes/backtick-code-spans/user --pair-chars,
//             plus the separate shape-based Path detector) — protected atomic spans, immune
//             to layers 2-3.
//   Layer 2 — keep-symbols word-splitting: characters in the keep-set glue to their word;
//             everything else (any char NOT in the keep-set) is a hard boundary and is
//             replaced with a space before splitting on whitespace.
//   Layer 3 — trim-symbols: strip leading/trailing trim-set characters from each resulting
//             plain word (edge-only, never mid-token). Empty by default in THIS keep-set-
//             based architecture, since Layer 2 already can't leave a non-keep character
//             stuck to a word's edge (it always splits there already) — this layer only
//             starts doing real work once a user opts extra punctuation INTO the Layer-2
//             keep-set (via --keep-symbols) and wants it excluded from just the EDGES
//             (e.g. keep interior dots in "U.S." but still drop a trailing sentence-final
//             dot) — see --help for a worked example.
//   Layer 4 — stop-words/trim-words (whole-phrase filtering) — unchanged, lives in
//             Program.cs's PassStopwordFilter/PassTrimFilter, downstream of tokenization.
public static class Tokenizer
{
    // A path-like run: two or more /- or \-joined segments, each made of word chars, dots,
    // or hyphens (so "src/whatever/services/file.cs" matches, but a lone "-" or "/" doesn't).
    private static readonly Regex PathPattern = new Regex(@"[\w.-]+(?:[/\\][\w.-]+)+", RegexOptions.Compiled);

    public static List<Token> Tokenize(string line, List<(char open, char close)> pairChars, HashSet<char> keepChars, HashSet<char> trimChars)
    {
        // Strip a leading markdown bullet marker ("- " or "* " at line start, optionally
        // after leading whitespace) so it isn't tokenized as a literal hyphen/asterisk "word".
        // This is purely a display/tokenization concern, not a content filter.
        var trimmed = line.TrimStart();
        int leadingWs = line.Length - trimmed.Length;
        if ((trimmed.StartsWith("- ") || trimmed.StartsWith("* ")))
            line = line.Substring(0, leadingWs) + "  " + trimmed.Substring(2);

        var segments = DetectUnits(line, pairChars);
        var tokens = new List<Token>();

        foreach (var (plainText, unit) in segments)
        {
            if (unit != null)
            {
                tokens.Add(Token.FromUnit(unit));
            }
            else if (plainText != null)
            {
                foreach (var w in SplitPlainWords(plainText, keepChars))
                {
                    var trimmedWord = TrimWordEdges(w, trimChars);
                    if (trimmedWord.Length > 0)
                        tokens.Add(Token.FromWord(trimmedWord));
                }
            }
        }

        return tokens;
    }

    // Layer 1: scan the raw line for paired-delimiter spans first (so a path-looking string
    // INSIDE a paired span doesn't get separately matched as a path unit), then path spans in
    // the remaining text. Returns the line as an ordered list of (plainText, null) and
    // (null, Unit) segments, preserving original order.
    internal static List<(string? plainText, Unit? unit)> DetectUnits(string line, List<(char open, char close)> pairChars)
    {
        // Collect raw spans (start, length, kind) from paired-delimiter patterns first (in
        // the order pairChars lists them — defaults first, unless reset via --no-pair-chars),
        // then path spans, dropping any path match that falls inside an already-claimed
        // paired span.
        var spans = new List<(int start, int len, UnitKind kind, int suffixLen)>();

        foreach (var pair in pairChars)
        {
            var regex = BuildPairRegex(pair.open, pair.close);
            foreach (Match m in regex.Matches(line))
            {
                bool overlaps = spans.Any(s => m.Index < s.start + s.len && m.Index + m.Length > s.start);
                if (!overlaps)
                    spans.Add((m.Index, m.Length, UnitKind.Paired, 0));
            }
        }

        foreach (Match m in PathPattern.Matches(line))
        {
            bool insidePaired = spans.Any(s => s.kind == UnitKind.Paired && m.Index >= s.start && m.Index + m.Length <= s.start + s.len);
            if (!insidePaired)
                spans.Add((m.Index, m.Length, UnitKind.Path, 0));
        }

        spans.Sort((a, b) => a.start.CompareTo(b.start));

        // Post-pass: a paired span (most commonly a backtick code-span, e.g. `RemoteApi`)
        // immediately followed by an apostrophe-led suffix with no whitespace in between
        // (`RemoteApi`'s, `cycod`'ll) is glued onto the span itself — same "possessive/
        // contraction stays glued" spirit as SplitPlainWords' apostrophe handling below,
        // extended to cover the case where the word right before the apostrophe was itself
        // swallowed whole into a Unit rather than left as a plain word (see ngc-feedback.md
        // #4's "residual edge case" — this closes it). Only applies to Paired spans; a Path
        // span isn't a "word" a possessive would sensibly attach to. suffixLen tracks how
        // much of the extended span is "glued possessive suffix" vs. "the delimited core",
        // so BuildPairedUnit can still strip only the real open/close delimiters, not the
        // suffix, when computing DisplayToken/InteriorSegments.
        for (int idx = 0; idx < spans.Count; idx++)
        {
            var (start, len, kind, _) = spans[idx];
            if (kind != UnitKind.Paired) continue;
            int suffixStart = start + len;
            if (suffixStart >= line.Length || line[suffixStart] != '\'') continue;
            int end = suffixStart + 1;
            while (end < line.Length && char.IsLetter(line[end])) end++;
            if (end == suffixStart + 1) continue; // bare trailing "'" with no letters after it — leave alone
            spans[idx] = (start, end - start, kind, end - suffixStart);
        }

        var result = new List<(string? plainText, Unit? unit)>();
        int pos = 0;
        foreach (var (start, len, kind, suffixLen) in spans)
        {
            if (start < pos) continue; // skip any accidental overlap remnants
            if (start > pos)
                result.Add((line.Substring(pos, start - pos), null));

            var raw = line.Substring(start, len);
            var unit = kind == UnitKind.Paired ? BuildPairedUnit(raw, suffixLen, GlobalKeepCharsForInterior) : BuildPathUnit(raw);
            result.Add((null, unit));

            pos = start + len;
        }
        if (pos < line.Length)
            result.Add((line.Substring(pos), null));

        return result;
    }

    // BuildPairedUnit needs the current keep-chars set to tokenize its OWN interior (for
    // unigram counting), but DetectUnits' signature is a public entry point already relied
    // on elsewhere — rather than thread keepChars through every call, stash it here just for
    // the duration of a single Tokenize() call. Not thread-safe for concurrent Tokenize calls
    // from multiple threads at once, which ngc never does (single-threaded line-by-line scan).
    [ThreadStatic] private static HashSet<char>? _keepCharsForInterior;
    private static HashSet<char> GlobalKeepCharsForInterior => _keepCharsForInterior ?? new HashSet<char> { '-', '\'' };

    // Builds a regex matching a delimiter-pair span. When open == close (quotes, backtick
    // code-spans, or any other single-char symmetric pair a user configures), a genuine
    // adjacency exclusion is applied ONLY for the apostrophe character specifically — an
    // opening/closing ' immediately touching a letter/digit is almost certainly a
    // possessive/contraction, not a real quoted span (see ngc-feedback.md #4). No other
    // symmetric pair char (backtick, or a user's own choice) needs this special-casing.
    private static Regex BuildPairRegex(char open, char close)
    {
        string o = Regex.Escape(open.ToString());
        string c = Regex.Escape(close.ToString());
        if (open == close)
        {
            string inner = $"[^{o}]*";
            if (open == '\'')
                return new Regex($"(?<![A-Za-z0-9]){o}{inner}{c}(?![A-Za-z0-9])", RegexOptions.Compiled);
            return new Regex($"{o}{inner}{c}", RegexOptions.Compiled);
        }
        return new Regex($"{o}[^{c}]*{c}", RegexOptions.Compiled);
    }

    private static Unit BuildPathUnit(string raw)
    {
        var interior = raw.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        return new Unit
        {
            Kind = UnitKind.Path,
            RawText = raw,
            DisplayToken = raw,
            InteriorSegments = interior,
        };
    }

    private static Unit BuildPairedUnit(string raw, int suffixLen, HashSet<char> keepChars)
    {
        // raw = [open][core][close][possessive-suffix?]. Strip only the real open/close
        // delimiters (the first and last char of the NON-suffix portion) when computing the
        // interior word-list; the glued suffix (if any) is appended back as its own trailing
        // word so it still counts for unigram purposes, e.g. `RemoteApi`'s -> interior
        // ["RemoteApi", "'s"] (matching what a plain "RemoteApi's" would have produced).
        int coreLen = raw.Length - suffixLen;
        var core = coreLen >= 2 ? raw.Substring(1, coreLen - 2) : raw.Substring(0, coreLen);
        var interior = SplitPlainWords(core, keepChars).ToList();
        if (suffixLen > 0)
            interior.Add(raw.Substring(coreLen)); // e.g. "'s" — glued, kept as its own word
        return new Unit
        {
            Kind = UnitKind.Paired,
            RawText = raw,
            DisplayToken = raw, // keep the delimiters (+ any glued suffix) in the display token
            InteriorSegments = interior,
        };
    }

    // Layer 2: word-splitting scoped to a plain-text substring (the gaps between Layer-1
    // Units). A character glues to its word if it's a letter, digit, or in `keepChars`;
    // everything else is a hard boundary. Default keepChars = {'-', '\''} — hyphenated
    // compounds ("re-run", "multi-device") and possessives/contractions ("Android's",
    // "don't", "isn't") stay as ONE token instead of fragmenting (see ngc-feedback.md #4 for
    // the apostrophe root-cause writeup). Configurable via --keep-symbols/--no-keep-symbols.
    internal static string[] SplitPlainWords(string text, HashSet<char> keepChars)
    {
        _keepCharsForInterior = keepChars;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || keepChars.Contains(ch)) sb.Append(ch); else sb.Append(' ');
        }
        return sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    }

    // Layer 3: strip leading/trailing trimChars characters from an already-formed plain
    // word — edge-only, never mid-token, and only ever applied AFTER Layer 2 has already
    // produced a word (never applied to a Unit's DisplayToken, which is protected verbatim).
    // Empty trimChars (the default) makes this a no-op; see the class-level doc comment
    // above for why that's the sensible default in this keep-set-based architecture, and
    // when a non-empty trim-set actually starts doing useful work.
    internal static string TrimWordEdges(string word, HashSet<char> trimChars)
    {
        if (trimChars.Count == 0 || word.Length == 0) return word;
        int start = 0, end = word.Length;
        while (start < end && trimChars.Contains(word[start])) start++;
        while (end > start && trimChars.Contains(word[end - 1])) end--;
        return word.Substring(start, end - start);
    }
}
