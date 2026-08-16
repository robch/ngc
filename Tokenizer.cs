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
public enum UnitKind { Path, Quoted }

// An atomic unit detected in a line: a path (word/word/word...) or a quoted span ("...").
public class Unit
{
    public UnitKind Kind = UnitKind.Path;
    public string RawText = string.Empty;      // original text, e.g. "src/whatever/services/file.cs"
    public string DisplayToken = string.Empty; // how it appears as ONE slot in prose n-grams
    public List<string> InteriorSegments = new List<string>(); // Path: split on / or \ ; Quoted: split into words
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

// Turns a raw line of text into a stream of Tokens (Layer 1 + Layer 2 of the tokenization
// pipeline). Layer 1 detects atomic "units" (paths, quoted spans) before any character
// filtering happens, since the existing char-filter step would otherwise destroy the very
// delimiters (/, \, ") needed to detect them. Layer 2 applies the existing alnum+hyphen
// word-splitting rule, but only to the plain-text gaps between units.
public static class Tokenizer
{
    // A path-like run: two or more /- or \-joined segments, each made of word chars, dots,
    // or hyphens (so "src/whatever/services/file.cs" matches, but a lone "-" or "/" doesn't).
    private static readonly Regex PathPattern = new Regex(@"[\w.-]+(?:[/\\][\w.-]+)+", RegexOptions.Compiled);

    // A double-quoted span, or a single-quoted span. For single quotes, require the opening
    // ' to NOT be immediately preceded by a letter/digit, and the closing ' to NOT be
    // immediately followed by a letter/digit — this distinguishes a genuine quoted span
    // ('quoted word') from a possessive/contraction apostrophe (Android's, doesn't), which
    // always has a letter directly on at least one side. Without this, a real single-quote
    // pair and a stray possessive/contraction elsewhere on the same line could otherwise be
    // greedily matched together as one bogus "quoted span" spanning both (see ngc-feedback.md
    // #4). Kept simple (no escape-sequence handling) — good enough for prose/markdown/log-style
    // text, which is what ngc targets.
    private static readonly Regex QuotedPattern = new Regex("\"[^\"]*\"|(?<![A-Za-z0-9])'[^']*'(?![A-Za-z0-9])", RegexOptions.Compiled);

    public static List<Token> Tokenize(string line)
    {
        // Strip a leading markdown bullet marker ("- " or "* " at line start, optionally
        // after leading whitespace) so it isn't tokenized as a literal hyphen/asterisk "word".
        // This is purely a display/tokenization concern, not a content filter.
        var trimmed = line.TrimStart();
        int leadingWs = line.Length - trimmed.Length;
        if ((trimmed.StartsWith("- ") || trimmed.StartsWith("* ")))
            line = line.Substring(0, leadingWs) + "  " + trimmed.Substring(2);

        var segments = DetectUnits(line);
        var tokens = new List<Token>();

        foreach (var (plainText, unit) in segments)
        {
            if (unit != null)
            {
                tokens.Add(Token.FromUnit(unit));
            }
            else if (plainText != null)
            {
                foreach (var w in SplitPlainWords(plainText))
                    tokens.Add(Token.FromWord(w));
            }
        }

        return tokens;
    }

    // Layer 1: scan the raw line for quoted spans first (so a path-looking string INSIDE
    // quotes doesn't get separately matched as a path unit), then path spans in the
    // remaining text. Returns the line as an ordered list of (plainText, null) and
    // (null, Unit) segments, preserving original order.
    internal static List<(string? plainText, Unit? unit)> DetectUnits(string line)
    {
        // Collect raw spans (start, length, kind) from both patterns, quotes first so they
        // win ties/overlaps, then remove any path-span matches that fall inside a quoted span.
        var spans = new List<(int start, int len, UnitKind kind)>();

        foreach (Match m in QuotedPattern.Matches(line))
            spans.Add((m.Index, m.Length, UnitKind.Quoted));

        foreach (Match m in PathPattern.Matches(line))
        {
            bool insideQuote = spans.Any(s => s.kind == UnitKind.Quoted && m.Index >= s.start && m.Index + m.Length <= s.start + s.len);
            if (!insideQuote)
                spans.Add((m.Index, m.Length, UnitKind.Path));
        }

        spans.Sort((a, b) => a.start.CompareTo(b.start));

        var result = new List<(string? plainText, Unit? unit)>();
        int pos = 0;
        foreach (var (start, len, kind) in spans)
        {
            if (start < pos) continue; // skip any accidental overlap remnants
            if (start > pos)
                result.Add((line.Substring(pos, start - pos), null));

            var raw = line.Substring(start, len);
            var unit = kind == UnitKind.Quoted ? BuildQuotedUnit(raw) : BuildPathUnit(raw);
            result.Add((null, unit));

            pos = start + len;
        }
        if (pos < line.Length)
            result.Add((line.Substring(pos), null));

        return result;
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

    private static Unit BuildQuotedUnit(string raw)
    {
        var inner = raw.Length >= 2 ? raw.Substring(1, raw.Length - 2) : raw;
        var interior = SplitPlainWords(inner).ToList();
        return new Unit
        {
            Kind = UnitKind.Quoted,
            RawText = raw,
            DisplayToken = raw, // keep quotes in the display token so it reads as "a unit"
            InteriorSegments = interior,
        };
    }

    // Layer 2: the alnum+hyphen+apostrophe word-splitting rule, scoped to a plain-text
    // substring instead of the whole line. Apostrophe is kept glued to its word (not treated
    // as a hard split character) so that possessives ("Android's") and contractions ("don't",
    // "isn't") stay as ONE token instead of fragmenting into a real word plus a meaningless
    // leftover "s"/"t" token — see ngc-feedback.md #4 for the root-cause writeup. This does
    // NOT affect quoted-span detection (Tokenizer.QuotedPattern, Layer 1): a lone apostrophe
    // here is only reached once it's already known NOT to be part of a matched quote pair.
    internal static string[] SplitPlainWords(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '\'') sb.Append(ch); else sb.Append(' ');
        }
        return sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
