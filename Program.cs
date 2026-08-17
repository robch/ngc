using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

enum SortDirection { Asc, Desc }
enum OutputMode { Default, Enhanced, Both, Detailed, Minimal }
enum SortBy { Count, Ppm }

class TextFilter
{
    public enum TypeEnum { Contains, NotContains, StartsWith, EndsWith, NotStartsWith, NotEndsWith }
    public TypeEnum Type;
    public string Pattern = string.Empty;
    public Regex? CompiledRegex = null; // For efficient regex matching when pattern contains special chars
}

class FrequencyFilter
{
    public int? Min; // inclusive
    public int? Max; // inclusive
    public bool Outside;
}

class PpmFilter
{
    public double? Min; public double? Max; public bool Outside;
}

class ZFilter
{
    public double? Min; public double? Max; public bool Outside;
}

class PmiFilter
{
    public double? Min; public double? Max; public bool Outside;
}

class PercentileFilter
{
    public double? Min; public double? Max; public bool Outside;
}

class TfidfFilter
{
    public double? Min; public double? Max; public bool Outside;
}

class CommandOptions
{
    public List<int> NGramSizes = new List<int>();
    // "Big" report-selector flags (see the anyBigReportFlagUsed logic in ParseArgs' post-
    // processing step): whether to show the per-n-gram-size report (--show-ngrams/
    // --show-n-grams) and/or the combined-across-sizes report (--show-merged-ngrams/
    // --show-merged-n-grams). ShowNGrams defaults to true (today's baseline: n-grams show
    // unless something says otherwise), but once ANY big --show-X flag is passed on the
    // command line, nothing is implicit any more — ParseArgs flips ShowNGrams to false in
    // that case unless --show-ngrams was ALSO explicitly passed (or --show-pmi was, since
    // PMI is a column on the n-grams report, not its own report, and has nothing to attach
    // to otherwise).
    public bool ShowMergedNGrams = false;
    public bool ShowNGrams = true;
    // Tracks whether --show-ngrams/--hide-ngrams/--show-n-grams/--hide-n-grams was passed
    // literally, so the "any big --show-X report flag suppresses n-grams unless asked for"
    // rule below (see ParseArgs' post-processing step) doesn't clobber an explicit choice.
    public bool ShowNGramsExplicit = false;
    public List<TextFilter> TextFilters = new List<TextFilter>();
    public List<FrequencyFilter> FrequencyFilters = new List<FrequencyFilter>();
    public List<PpmFilter> PpmFilters = new List<PpmFilter>();
    public List<ZFilter> ZFilters = new List<ZFilter>();
    public List<PmiFilter> PmiFilters = new List<PmiFilter>();
    public List<PercentileFilter> PercentileFilters = new List<PercentileFilter>();
    public List<TfidfFilter> TfidfFilters = new List<TfidfFilter>();
    public SortDirection Sort = SortDirection.Asc;
    public SortBy SortBy = SortBy.Count;
    public OutputMode Mode = OutputMode.Default;
    public int Limit = int.MaxValue;
    public bool LimitIsPercentage = false;
    public double LimitPercentage = 0;
    public int BottomLimit = 0;
    public bool BottomLimitIsPercentage = false;
    public double BottomLimitPercentage = 0;
    public int MaxItems = 200;
    public bool MaxItemsSetExplicitly = false;
    public bool TopFilesSetExplicitly = false;
    public bool MaxItemsPerFileSetExplicitly = false;
    public bool MinimalOutput = false;
    public bool StatsOnly = false; // Only show statistics, not full phrase lists
    public List<double> UniquePercentiles = new List<double>();
    // Case sensitivity for all text filters (--contains/--starts/--ends/etc. and their
    // --remove-* siblings): case-SENSITIVE by default (matches ngc's own documented use
    // cases, e.g. `--contains "class [A-Z]"` / `--contains "new [A-Z]"` for finding
    // capitalized identifiers — those examples only make sense if case is respected).
    // --ignore-case opts into case-insensitive matching for prose-style searches.
    public bool IgnoreCase = false;
    public List<string> ExcludeFiles = new List<string>();

    // --show-x / --hide-x granular flags (presets below are just bundles of these)
    // Report sections:
    public bool ShowInput = true;
    public bool ShowSectionHeader = true;
    public bool ShowSummary = true;
    public bool ShowFreqStats = true;
    public bool ShowPpmStats = false;
    public bool ShowColumnHeader = true;
    public bool ShowPhrases = true;
    public bool ShowTfidfPhrases = true;
    // ShowMergedNGrams / ShowNGrams already declared above.
    // Per-item columns:
    public bool ShowCount = true;
    public bool ShowPpm = false;
    public bool ShowZ = false;
    // New reports (implemented in later steps; parsed now so flags are accepted):
    public bool ShowPdf = false;
    public bool ShowCdf = false;
    public bool ShowPmi = false;
    public bool ShowTfidf = false;
    // Separate namespace/report: n-grams built from the interior segments of path-like
    // units (e.g. "src/whatever" from "src/whatever/services"), distinct from prose n-grams.
    public bool ShowPathNGrams = false;
    // Idea-007 (CDRs/ideas/idea-007-dispersion-measures.md): dispersion measures (Juilland's
    // D and DP) per n-gram, computed from the existing PerDocNGramCounts/PerDocTotalTokensPerN
    // dictionaries — how EVENLY a term is spread across --files documents, as distinct from how
    // often it occurs in total. Requires --files (document boundaries).
    public bool ShowDispersion = false;
    // Idea-004 (CDRs/ideas/idea-004-alternate-association-measures.md): alternate collocation-
    // strength measures alongside PMI, computed from the same unigram+n-gram count dictionaries
    // already collected for PMI. log-Dice (Sketch Engine's default; robust to rare-pair PMI
    // inflation), t-score (favors frequent, well-attested collocations), and MI3 (cubed mutual
    // information; reduces low-frequency bias vs. plain PMI/MI).
    public bool ShowLogDice = false;
    public bool ShowTScore = false;
    public bool ShowMi3 = false;
    // Idea-008 (CDRs/ideas/idea-008-readability-lexical-diversity.md): whole-document/corpus
    // summary stats — a different zoom level than everything else (one number per corpus/file,
    // not per-phrase). TTR and MTLD only need the unigram token stream ngc already produces.
    public bool ShowReadability = false;
    // Tracking flags (not user-facing) for the "--show-cdf/--show-pdf implies --hide-phrases
    // unless something says otherwise" rule below in ParseArgs' post-processing step.
    public bool ShowPhrasesExplicit = false; // true iff --show-phrases or --hide-phrases was passed literally
    public bool MorePresetUsed = false;      // true iff --more/--more++/--more+++ was passed
    // Stopword filtering: on by default. An n-gram is dropped only if EVERY word in it is a
    // stopword (mixed phrases like "the guard" are kept). Starting basis is either
    // StopWords.Default or empty, decided by a pre-scan for --no-stop-words (see ParseArgs);
    // --stop-words word1 word2 ... is then always purely ADDITIVE on top of that basis,
    // regardless of where --no-stop-words appears on the command line. An empty set here
    // naturally means "filter off" (no phrase can be all-stopwords against an empty set).
    public HashSet<string> EffectiveStopWords = new HashSet<string>(StopWords.Default, StringComparer.OrdinalIgnoreCase);
    // Trim-word filtering: on by default. An n-gram is dropped if its FIRST OR LAST token is
    // a trim word — a stricter, position-sensitive sibling of stopword filtering (see
    // TrimWords.cs for why this is a genuinely different, smaller set, not just a reuse of
    // StopWords). Same --no-trim-words / --trim-words additive semantics as stopwords above.
    public HashSet<string> EffectiveTrimWords = new HashSet<string>(TrimWords.Default, StringComparer.OrdinalIgnoreCase);
    public bool PerFile = false;

    // --- Tokenizer Layers 1-3 (ngc-feedback.md #4 generalization) ---
    // Layer 1: pair-span detection. Default pairs: double-quote, single-quote (apostrophe-
    // adjacency-aware, see Tokenizer.BuildPairRegex), and backtick (markdown code-spans —
    // new default, closes the "`RemoteApi`'s" stray-'s edge case from ngc-feedback.md #4).
    // --pair-chars adds more (open,close) pairs on top of this basis; --no-pair-chars resets
    // the basis to empty first (same order-independent pre-scan pattern as --no-stop-words).
    public List<(char open, char close)> EffectivePairChars = new List<(char open, char close)>
    {
        ('"', '"'), ('\'', '\''), ('`', '`'),
    };
    // Layer 2: keep-symbols. A character glues to its word if it's a letter/digit or in this
    // set; everything else is a hard split boundary. Default: hyphen (compounds like
    // "re-run") + apostrophe (possessives/contractions like "Android's", "don't" — the
    // ngc-feedback.md #4 fix). --keep-symbols adds more; --no-keep-symbols resets to empty.
    public HashSet<char> EffectiveKeepSymbols = new HashSet<char> { '-', '\'' };
    // Layer 3: trim-symbols. Strip leading/trailing characters in this set from each already-
    // formed plain word (edge-only). Empty by default — Layer 2 already can't leave a
    // non-keep character stuck to a word's edge, so this only starts doing real work once a
    // user opts extra punctuation into Layer 2's keep-set via --keep-symbols and wants it
    // excluded from just the edges. --trim-symbols adds; --no-trim-symbols resets (also a
    // no-op against the already-empty default, kept for symmetry/discoverability).
    public HashSet<char> EffectiveTrimSymbols = new HashSet<char>();
    // --files glob1 [glob2 ...] — when non-empty, read these files/globs as
    // separate documents instead of reading stdin as one blob. Relative globs
    // (including "../" parent-traversal, e.g. "../other-repo/**/*.cs") are
    // supported — see LooksLikeFileGlob's ".." handling in ParseArgs.
    public List<string> FileGlobs = new List<string>();
    // --line-contains / --remove-line-contains PATTERN — a genuinely separate filter
    // stage from --contains/etc. above: applied to whole SOURCE LINES, BEFORE
    // tokenization, deciding which lines even enter the n-gram token pool at all.
    // --contains and friends filter the already-built n-gram PHRASE results after the
    // fact; this filters the raw corpus lines beforehand. Reuses TextFilter's
    // Contains/NotContains matching machinery (see PassTextFilters), just against a
    // different string (the whole line) at a different pipeline stage.
    public List<TextFilter> LineFilters = new List<TextFilter>();
    // --top-files N (with --top-files 0 meaning unlimited) — caps how many of
    // the matched --files documents get a full breakdown table under
    // `--show-tfidf --per-file`, after ranking documents by an aggregate per-file
    // distinctiveness score (see TopFilesBy). Mirrors the existing --max-items
    // rank+cap+trailing-notice convention, applied one level up (files, not rows).
    // Scales with the --less/--more verbosity ladder (ngc-feedback.md #4):
    // --less--=5, --less=10, (default)=20, --more=50, --more++=100, --more+++=0.
    public int TopFiles = 20;
    // How to aggregate a single file's many per-ngram TF-IDF scores into one ranking
    // score for --top-files: "max" (that file's single highest-scoring ngram — cheap,
    // but one outlier term can dominate), "sum" (rewards breadth of distinctiveness),
    // or "avg-top5" (default — average of the file's top 5 ngram scores; robust,
    // rewards several distinctive terms rather than one fluke).
    public string TopFilesBy = "avg-top5";
    // --max-items-per-file N (0 = unlimited) — caps ROWS shown within EACH file's own
    // breakdown table under `--show-tfidf --per-file`, independently of the global
    // --max-items cap (which, before this flag existed, was the only knob controlling
    // per-file row counts too — see ngc-feedback.md #3). Small default so `--per-file` is
    // usable out of the box across many files without needing to hand-tune the global cap
    // down first. Scales with the --less/--more verbosity ladder (ngc-feedback.md #6):
    // --less--=5, --less=10, (default)=15, --more=30, --more++=60, --more+++=0.
    public int MaxItemsPerFile = 15;
}

class Program
{
    // Static properties to maintain state for percentile filtering
    public static CommandOptions CurrentOptions { get; set; } = new CommandOptions();
    public static bool PercentilesAreSorted { get; set; } = false;

    // Per-document data, populated when --files is used (one entry per matched
    // file; when reading stdin, a single "<stdin>" pseudo-document). Kept as
    // static state so later features (TF-IDF, --per-file reports) can consume
    // it without re-plumbing through every method signature.
    public static List<string> DocumentNames { get; set; } = new List<string>();
    public static Dictionary<string, Dictionary<int, Dictionary<string, int>>> PerDocNGramCounts { get; set; } = new Dictionary<string, Dictionary<int, Dictionary<string, int>>>();
    public static Dictionary<string, Dictionary<int, int>> PerDocTotalTokensPerN { get; set; } = new Dictionary<string, Dictionary<int, int>>();
    public static Dictionary<string, Dictionary<int, Dictionary<string, int>>> PerDocPathSegmentNGramCounts { get; set; } = new Dictionary<string, Dictionary<int, Dictionary<string, int>>>();
    public static Dictionary<string, Dictionary<int, int>> PerDocPathSegmentTotalTokensPerN { get; set; } = new Dictionary<string, Dictionary<int, int>>();
    // Idea-008: per-document ordered word-token sequence (display words, in original order),
    // needed for TTR/MTLD — those measures are sensitive to token ORDER (MTLD walks the
    // sequence factor-by-factor), unlike the bag-of-counts dictionaries above which lose order.
    public static Dictionary<string, List<string>> PerDocWordSequence { get; set; } = new Dictionary<string, List<string>>();
        
    // Helper method to detect if a pattern contains regex special characters and compile it
    private static bool TryCompileRegex(string pattern, out Regex regex, bool ignoreCase = false)
    {
        regex = null!;
        // Check for common regex metacharacters. `^`/`$` (anchors) are included even though
        // they're single-purpose regex-only chars with no literal-substring meaning here —
        // omitting them was a real bug (ngc-feedback.md): a pattern like "^payload" has none
        // of the OTHER metachars below, so it used to be (wrongly) treated as a literal
        // substring search for the 8-character string "^payload" (caret included), which of
        // course never appears anywhere, silently producing a confident "0 results" instead
        // of the anchored match the user asked for.
        if (pattern.IndexOfAny(new[] { '|', '*', '+', '?', '[', ']', '(', ')', '{', '}', '\\', '^', '$' }) < 0)
            return false; // Not a regex pattern
            
        try
        {
            var opts = RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            regex = new Regex(pattern, opts);
            return true;
        }
        catch (ArgumentException)
        {
            // If regex compilation fails, it's not a valid regex
            return false;
        }
    }

    static void Main(string[] args)
    {
        // Fix for garbled Unicode output (ngc-feedback.md #5): ngc's own --help text embeds
        // real Unicode glyphs (⚠️, →, ≠, etc.), and file content read via --files is UTF-8.
        // Without explicitly setting the console's output encoding, many consoles/capture
        // pipes default to a legacy codepage and silently replace those glyphs with '?' at
        // print time. Set this before any output is written.
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        var options = ParseArgs(args);
        
        // Store options for access by static methods
        CurrentOptions = options;
        PercentilesAreSorted = false;

        // Read input: either --files (one or more documents, each processed
        // separately so future features like TF-IDF have document boundaries),
        // or stdin as a single document (unchanged/default behavior).
        var documents = new List<(string Name, string Content)>();
        if (options.FileGlobs.Count > 0)
        {
            var matchedFiles = FileHelpers.FilesFromGlobs(options.FileGlobs).Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (matchedFiles.Count == 0)
            {
                Console.WriteLine($"## Pattern: {string.Join(" ", options.FileGlobs)}\n\n - No files found\n");
                Environment.Exit(1);
                return;
            }
            foreach (var file in matchedFiles)
            {
                try
                {
                    var content = file == "-"
                        ? Console.In.ReadToEnd()
                        : File.ReadAllText(file, Encoding.UTF8);
                    documents.Add((file, content));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: could not read '{file}': {ex.Message}");
                }
            }
        }
        else
        {
            // No --files given: we only read stdin, and only if it's actually piped/redirected.
            // Without this check, running ngc with no --files and no piped input (e.g. a typo'd
            // command from an interactive terminal) hangs forever waiting on a console read that
            // will never come. Fail fast instead.
            if (!Console.IsInputRedirected)
            {
                Console.Error.WriteLine("No input provided: pass --files <glob> [<glob> ...], or pipe text in via stdin.");
                Console.Error.WriteLine("Run with --help to see available options.");
                Environment.Exit(1);
                return;
            }
            documents.Add(("<stdin>", Console.In.ReadToEnd()));
        }

        // PMI needs unigram counts (for expected-frequency chain rule), regardless of whether
        // the user asked to see 1-grams. We "force collect" size 1 (and size n-1, for n>=3,
        // in case someone wants pmi on trigrams+) without adding it to the sizes we *print*.
        bool needPmiCollection = options.ShowPmi || options.PmiFilters.Count > 0 ||
            options.ShowLogDice || options.ShowTScore || options.ShowMi3;
        var collectSizes = new HashSet<int>(options.NGramSizes);
        if (needPmiCollection)
        {
            collectSizes.Add(1);
            foreach (var n in options.NGramSizes) if (n >= 2) collectSizes.Add(n - 1);
        }

        var totalTokensPerN = new Dictionary<int, int>();
        foreach (int n in collectSizes) totalTokensPerN[n] = 0;

        var nGramCounts = new Dictionary<int, Dictionary<string, int>>();
        foreach (int n in collectSizes) nGramCounts[n] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Path-segment n-grams: a separate namespace/dictionary set for the interior
        // segments of path-like units (e.g. "src/whatever/services" -> segments "src",
        // "whatever", "services"). These are structurally different from prose n-grams
        // (segment adjacency reflects directory nesting, not English word order) so they
        // are never merged into nGramCounts. See --show-path-ngrams / PrintPathNGrams.
        var pathSegmentNGramCounts = new Dictionary<int, Dictionary<string, int>>();
        foreach (int n in collectSizes) pathSegmentNGramCounts[n] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pathSegmentTotalTokensPerN = new Dictionary<int, int>();
        foreach (int n in collectSizes) pathSegmentTotalTokensPerN[n] = 0;

        // Per-document n-gram data, kept for future document-boundary features
        // (e.g. TF-IDF): one dictionary-of-dictionaries per matched document.
        var perDocNGramCounts = new Dictionary<string, Dictionary<int, Dictionary<string, int>>>();
        var perDocTotalTokensPerN = new Dictionary<string, Dictionary<int, int>>();
        var perDocPathSegmentNGramCounts = new Dictionary<string, Dictionary<int, Dictionary<string, int>>>();
        var perDocPathSegmentTotalTokensPerN = new Dictionary<string, Dictionary<int, int>>();
        var perDocWordSequence = new Dictionary<string, List<string>>();

        // Input statistics tracking (aggregate across all documents)
        int totalChars = 0;
        int totalLines = 0;
        int totalWords = 0;

        foreach (var (docName, content) in documents)
        {
            totalChars += content.Length;
            var docLines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            totalLines += docLines.Length;

            var docNGramCounts = new Dictionary<int, Dictionary<string, int>>();
            foreach (int n in collectSizes) docNGramCounts[n] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var docTotalTokensPerN = new Dictionary<int, int>();
            foreach (int n in collectSizes) docTotalTokensPerN[n] = 0;

            var docPathSegmentNGramCounts = new Dictionary<int, Dictionary<string, int>>();
            foreach (int n in collectSizes) docPathSegmentNGramCounts[n] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var docPathSegmentTotalTokensPerN = new Dictionary<int, int>();
            foreach (int n in collectSizes) docPathSegmentTotalTokensPerN[n] = 0;

            // Idea-008: ordered word sequence for this document, used by TTR/MTLD. Only
            // populated when actually needed (--show-readability), to avoid holding onto a
            // full token-order list for every document on every run.
            var docWordSequence = options.ShowReadability ? new List<string>() : null;

            foreach (var line in docLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!PassLineFilters(line, options.LineFilters, options.IgnoreCase)) continue;

                // Layer 1+2+3: detect atomic units (paths, quoted/backtick/user-defined
                // paired spans) first, then split the remaining plain text into words using
                // the configurable keep-symbols rule, then trim configurable edge characters
                // off each resulting word. See ngc-feedback.md #4 for the full design.
                var tokens = Tokenizer.Tokenize(line, options.EffectivePairChars, options.EffectiveKeepSymbols, options.EffectiveTrimSymbols);

                // Layer 3a: prose n-grams. Each token contributes exactly one slot via
                // Display — a Unit (e.g. a path) never fragments into multiple slots, so it
                // can never bridge into a fake multi-word phrase with its interior segments.
                var displayWords = tokens.Select(t => t.Display).ToList();
                totalWords += displayWords.Count;
                NGramBuilder.CollectNGrams(displayWords, collectSizes, nGramCounts, totalTokensPerN);
                NGramBuilder.CollectNGrams(displayWords, collectSizes, docNGramCounts, docTotalTokensPerN);
                docWordSequence?.AddRange(displayWords);

                // Layer 3b: unigram word-frequency feed. A Unit's own Display token is NOT
                // counted as a unigram (it's not really "a word"); instead each of its
                // interior segments/words is counted individually, same as ordinary prose
                // words, so overall word-frequency stats stay accurate.
                if (collectSizes.Contains(1))
                {
                    foreach (var t in tokens)
                    {
                        if (!t.IsUnit) continue;
                        foreach (var interiorWord in t.Unit!.InteriorSegments)
                        {
                            if (nGramCounts[1].TryGetValue(interiorWord, out var c1)) nGramCounts[1][interiorWord] = c1 + 1; else nGramCounts[1][interiorWord] = 1;
                            if (docNGramCounts[1].TryGetValue(interiorWord, out var c2)) docNGramCounts[1][interiorWord] = c2 + 1; else docNGramCounts[1][interiorWord] = 1;
                            totalTokensPerN[1]++;
                            docTotalTokensPerN[1]++;
                        }
                    }
                }

                // Layer 4: path-segment n-grams, a separate namespace scoped to each
                // Path unit's own interior segments (e.g. "src/whatever", "whatever/services").
                foreach (var t in tokens)
                {
                    if (!t.IsUnit || t.Unit!.Kind != UnitKind.Path) continue;
                    NGramBuilder.CollectNGrams(t.Unit.InteriorSegments, collectSizes, pathSegmentNGramCounts, pathSegmentTotalTokensPerN, "/");
                    NGramBuilder.CollectNGrams(t.Unit.InteriorSegments, collectSizes, docPathSegmentNGramCounts, docPathSegmentTotalTokensPerN, "/");
                }
            }

            perDocNGramCounts[docName] = docNGramCounts;
            perDocTotalTokensPerN[docName] = docTotalTokensPerN;
            perDocPathSegmentNGramCounts[docName] = docPathSegmentNGramCounts;
            perDocPathSegmentTotalTokensPerN[docName] = docPathSegmentTotalTokensPerN;
            if (docWordSequence != null) perDocWordSequence[docName] = docWordSequence;
        }

        // Make per-document data available for later features (TF-IDF etc.)
        Program.DocumentNames = documents.Select(d => d.Name).ToList();
        Program.PerDocNGramCounts = perDocNGramCounts;
        Program.PerDocTotalTokensPerN = perDocTotalTokensPerN;
        Program.PerDocPathSegmentNGramCounts = perDocPathSegmentNGramCounts;
        Program.PerDocPathSegmentTotalTokensPerN = perDocPathSegmentTotalTokensPerN;
        Program.PerDocWordSequence = perDocWordSequence;


        // Load exclude files into text filters
        foreach (var f in options.ExcludeFiles)
        {
            if (File.Exists(f))
            {
                var linesIn = File.ReadAllLines(f);
                foreach (var l in linesIn)
                {
                    var t = l.Trim();
                    if (t.Length == 0) continue;
                    var filter = new TextFilter { Type = TextFilter.TypeEnum.NotContains, Pattern = t };
                    if (TryCompileRegex(t, out Regex regex, options.IgnoreCase))
                        filter.CompiledRegex = regex;
                    options.TextFilters.Add(filter);
                }
            }
        }

        // Display input statistics
        if (options.ShowInput)
        {
            Console.WriteLine($"Chars: {totalChars}\nLines: {totalLines}\nWords: {totalWords}");
            // Single blank line after input stats
            Console.WriteLine();
        }

        // Track pre-filter statistics
        var preFilterStats = new Dictionary<int, (int uniqueCount, int[] frequencies, double[] ppmValues)>();
        foreach (var n in options.NGramSizes)
        {
            var frequencies = nGramCounts[n].Values.ToArray();
            Array.Sort(frequencies);
            
            double[] freqPpmValues = new double[frequencies.Length];
            if (frequencies.Length > 0 && totalTokensPerN[n] > 0)
            {
                for (int i = 0; i < frequencies.Length; i++)
                {
                    freqPpmValues[i] = (double)frequencies[i] / totalTokensPerN[n] * 1_000_000.0;
                }
                Array.Sort(freqPpmValues);
            }
            
            preFilterStats[n] = (nGramCounts[n].Count, frequencies, freqPpmValues);
        }

        // Precompute stats if needed
        var ppmValues = new Dictionary<int, Dictionary<string, double>>();
        var zValues = new Dictionary<int, Dictionary<string, double>>();

        bool needPpm = options.PpmFilters.Count > 0 || options.Mode == OutputMode.Enhanced || options.Mode == OutputMode.Detailed || options.ShowPpm || options.ShowPpmStats;
        bool needZ = options.ZFilters.Count > 0 || options.Mode == OutputMode.Detailed || options.ShowZ;

        foreach (var n in options.NGramSizes)
        {
            if (needPpm)
            {
                ppmValues[n] = new Dictionary<string, double>();
                var total = Math.Max(1, totalTokensPerN[n]);
                foreach (var kv in nGramCounts[n]) ppmValues[n][kv.Key] = (double)kv.Value / total * 1_000_000.0;
            }
            if (needZ)
            {
                zValues[n] = new Dictionary<string, double>();
                var vals = nGramCounts[n].Values.ToList();
                double mean = vals.Count > 0 ? vals.Average() : 0.0;
                double sd = vals.Count > 0 ? Math.Sqrt(vals.Sum(v => (v - mean) * (v - mean)) / vals.Count) : 0.0;
                foreach (var kv in nGramCounts[n]) zValues[n][kv.Key] = sd == 0 ? 0.0 : ((double)kv.Value - mean) / sd;
            }
        }

        // Precompute PMI (Pointwise Mutual Information) per n-gram, for n >= 2 only.
        // PMI(w1..wn) = log2( P(w1..wn) / (P(w1)*P(w2)*...*P(wn)) )
        // We approximate multi-word PMI via the "chain" comparison of the whole n-gram's
        // observed probability vs. the product of its individual unigram probabilities.
        var pmiValues = new Dictionary<int, Dictionary<string, double>>();
        bool needPmi = options.ShowPmi || options.PmiFilters.Count > 0;
        if (needPmi && nGramCounts.ContainsKey(1))
        {
            double totalUnigramTokens = Math.Max(1, totalTokensPerN.ContainsKey(1) ? totalTokensPerN[1] : 0);
            var unigramProb = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in nGramCounts[1]) unigramProb[kv.Key] = kv.Value / totalUnigramTokens;

            foreach (var n in options.NGramSizes)
            {
                if (n < 2) continue; // PMI meaningless for unigrams
                pmiValues[n] = new Dictionary<string, double>();
                double totalNgramTokens = Math.Max(1, totalTokensPerN.ContainsKey(n) ? totalTokensPerN[n] : 0);
                foreach (var kv in nGramCounts[n])
                {
                    var words = kv.Key.Split(' ');
                    double observedProb = kv.Value / totalNgramTokens;
                    double expectedProb = 1.0;
                    bool haveAllWords = true;
                    foreach (var w in words)
                    {
                        if (unigramProb.TryGetValue(w, out double wp)) expectedProb *= wp;
                        else { haveAllWords = false; break; }
                    }
                    double pmi = 0.0;
                    if (haveAllWords && expectedProb > 0 && observedProb > 0)
                        pmi = Math.Log(observedProb / expectedProb, 2);
                    pmiValues[n][kv.Key] = pmi;
                }
            }
        }

        // Apply filters per n and build output sets
        var outputs = new Dictionary<int, List<(string ngram, int count, double ppm, double z, double pmi)>>();
        foreach (var n in options.NGramSizes)
        {
            var list = new List<(string, int, double, double, double)>();
            foreach (var kv in nGramCounts[n])
            {
                var ngram = kv.Key; var count = kv.Value; var ppm = needPpm ? ppmValues[n][ngram] : 0.0; var z = needZ ? zValues[n][ngram] : 0.0;
                var pmi = (needPmi && n >= 2 && pmiValues.ContainsKey(n)) ? pmiValues[n][ngram] : 0.0;
                if (!PassTextFilters(ngram, options.TextFilters, options.IgnoreCase)) continue;
                if (!PassStopwordFilter(ngram, options)) continue;
                if (!PassTrimFilter(ngram, options)) continue;
                if (!PassFrequencyFilters(count, options.FrequencyFilters)) continue;
                if (!PassPpmFilters(ppm, options.PpmFilters)) continue;
                if (!PassZFilters(z, options.ZFilters)) continue;
                if (n >= 2 && !PassPmiFilters(pmi, options.PmiFilters)) continue;
                list.Add((ngram, count, ppm, z, pmi));
            }
            outputs[n] = list;
        }

        // // Calculate and display statistics for each n-gram size
        // Console.WriteLine("## N-gram Statistics");
        // Console.WriteLine();
        
        // foreach (var n in options.NGramSizes.OrderBy(x => x))
        // {
        //     var preStats = preFilterStats[n];
        //     var postFilterCount = outputs[n].Count;
            
        //     // Calculate percentage of n-grams that passed the filters
        //     double percentRetained = preStats.uniqueCount > 0 
        //         ? (double)postFilterCount / preStats.uniqueCount * 100 
        //         : 0;
            
        //     // Calculate frequency statistics for filtered results
        //     var filteredFrequencies = outputs[n].Select(x => x.count).ToArray();
        //     Array.Sort(filteredFrequencies);
            
        //     int minFreq = filteredFrequencies.Length > 0 ? filteredFrequencies[0] : 0;
        //     int maxFreq = filteredFrequencies.Length > 0 ? filteredFrequencies[filteredFrequencies.Length - 1] : 0;
        //     double avgFreq = filteredFrequencies.Length > 0 ? filteredFrequencies.Average() : 0;
        //     double medianFreq = 0;
        //     int p90Freq = 0;
            
        //     if (filteredFrequencies.Length > 0)
        //     {
        //         // Calculate median (middle value or average of two middle values)
        //         int midIndex = filteredFrequencies.Length / 2;
        //         if (filteredFrequencies.Length % 2 == 0 && filteredFrequencies.Length > 1)
        //         {
        //             medianFreq = (filteredFrequencies[midIndex - 1] + filteredFrequencies[midIndex]) / 2.0;
        //         }
        //         else if (filteredFrequencies.Length > 0)
        //         {
        //             medianFreq = filteredFrequencies[midIndex];
        //         }
                
        //         // Calculate 90th percentile
        //         int p90Index = (int)(filteredFrequencies.Length * 0.9);
        //         if (p90Index >= filteredFrequencies.Length) p90Index = filteredFrequencies.Length - 1;
        //         if (p90Index >= 0) p90Freq = filteredFrequencies[p90Index];
        //     }
            
        //     // Calculate PPM statistics for filtered results
        //     var filteredPpms = outputs[n].Select(x => x.ppm).ToArray();
        //     Array.Sort(filteredPpms);
            
        //     double minPpm = filteredPpms.Length > 0 ? filteredPpms[0] : 0;
        //     double maxPpm = filteredPpms.Length > 0 ? filteredPpms[filteredPpms.Length - 1] : 0;
        //     double avgPpm = filteredPpms.Length > 0 ? filteredPpms.Average() : 0;
        //     double medianPpm = 0;
            
        //     if (filteredPpms.Length > 0)
        //     {
        //         // Calculate median PPM
        //         int midIndex = filteredPpms.Length / 2;
        //         if (filteredPpms.Length % 2 == 0 && filteredPpms.Length > 1)
        //         {
        //             medianPpm = (filteredPpms[midIndex - 1] + filteredPpms[midIndex]) / 2.0;
        //         }
        //         else if (filteredPpms.Length > 0)
        //         {
        //             medianPpm = filteredPpms[midIndex];
        //         }
        //     }
            
        //     // Display n-gram statistics
        //     Console.WriteLine($"{preStats.uniqueCount} (unique) - {preStats.uniqueCount - postFilterCount} (filtered) = {postFilterCount} ({percentRetained,3:F0}%)");
        //     Console.WriteLine($"Freq: {minFreq}..{maxFreq} (range) | {avgFreq} (avg) | {medianFreq} (median) | 90% < {p90Freq}");
            
        //     if (minPpm > 0 || maxPpm > 0)
        //     {
        //         Console.WriteLine($"PPM:  {minPpm}..{maxPpm} (range) | {avgPpm} (avg) | {medianPpm} (median)");
        //     }
            
        //     Console.WriteLine();
        // }
        
        // Prepare merged if requested
        var merged = new List<(string ngram, int count, double ppm, double z, double pmi)>();
        if (options.ShowMergedNGrams || options.Mode == OutputMode.Both)
        {
            var dict = new Dictionary<string, (int count, double ppm, double z, double pmi)>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in options.NGramSizes)
            {
                foreach (var item in outputs[n])
                {
                    if (dict.ContainsKey(item.ngram)) dict[item.ngram] = (dict[item.ngram].count + item.count, dict[item.ngram].ppm, dict[item.ngram].z, dict[item.ngram].pmi);
                    else dict[item.ngram] = (item.count, item.ppm, item.z, item.pmi);
                }
            }
            foreach (var kv in dict) merged.Add((kv.Key, kv.Value.count, kv.Value.ppm, kv.Value.z, kv.Value.pmi));
        }

        // Output according to mode
        if (options.MinimalOutput && options.ShowNGrams)
        {
            // only ngrams lines from merged or per-bucket depending on ShowMergedNGrams/ShowNGrams
            // NOTE: the global --max-items cap must still apply here too (--less/--less-- were
            // previously bypassing it entirely, since ApplyMaxItemsCap was only wired into the
            // non-minimal code paths below — found while re-verifying each preset after the
            // ngc-feedback.md #2 grammar rewrite).
            if (options.ShowMergedNGrams)
            {
                var mergedList = SortAndLimit(merged, options).ToList();
                if (mergedList.Count == 0)
                {
                    PrintNoMatchesNotice();
                }
                else
                {
                    var cappedMergedMinimal = ApplyMaxItemsCap(mergedList, options, options.Sort == SortDirection.Asc ? "ascending" : "descending", out var mergedMinimalNotice);
                    foreach (var it in cappedMergedMinimal) PrintEntry(it, options, "merged");
                    if (mergedMinimalNotice != null) Console.WriteLine(mergedMinimalNotice);
                }
            }
            else
            {
                foreach (var n in options.NGramSizes.OrderBy(x => x))
                {
                    var list = SortAndLimit(outputs[n], options).ToList();
                    if (list.Count == 0)
                    {
                        PrintNoMatchesNotice();
                        continue;
                    }
                    var cappedList = ApplyMaxItemsCap(list, options, options.Sort == SortDirection.Asc ? "ascending" : "descending", out var minimalNotice);
                    foreach (var it in cappedList) PrintEntry(it, options, n.ToString());
                    if (minimalNotice != null) Console.WriteLine(minimalNotice);
                }
            }
            return;
        }

        if (options.ShowNGrams)
        {
            foreach (var n in options.NGramSizes.OrderBy(x => x))
            {
                // Add a blank line before section header (except the first one)
                if (options.ShowSectionHeader)
                {
                    if (n != options.NGramSizes.OrderBy(x => x).First()) {
                        Console.WriteLine();
                    }
                    
                    Console.WriteLine($"## {n}-grams Results");
                    Console.WriteLine();
                }
                
                // Calculate statistics for this n-gram size based on final (post-filter) set
                var preStats = preFilterStats[n];

                // Apply percentile/top/bottom limits and materialize final list for this n
                var finalList = SortAndLimit(outputs[n], options).ToList();
                var postFilterCount = finalList.Count;

                if (postFilterCount == 0)
                {
                    PrintNoMatchesNotice();
                    continue;
                }
                
                // Calculate percentage of n-grams that passed the filters (relative to pre-filter unique count)
                double percentRetained = preStats.uniqueCount > 0 
                    ? (double)postFilterCount / preStats.uniqueCount * 100 
                    : 0;
                
                // Calculate frequency statistics for filtered results
                var filteredFrequencies = finalList.Select(x => x.count).ToArray();
                Array.Sort(filteredFrequencies);
                
                int minFreq = filteredFrequencies.Length > 0 ? filteredFrequencies[0] : 0;
                int maxFreq = filteredFrequencies.Length > 0 ? filteredFrequencies[filteredFrequencies.Length - 1] : 0;
                double avgFreq = filteredFrequencies.Length > 0 ? filteredFrequencies.Average() : 0;
                double medianFreq = 0;
                int p90Freq = 0;
                
                if (filteredFrequencies.Length > 0)
                {
                    // Calculate median (middle value or average of two middle values)
                    int midIndex = filteredFrequencies.Length / 2;
                    if (filteredFrequencies.Length % 2 == 0 && filteredFrequencies.Length > 1)
                    {
                        medianFreq = (filteredFrequencies[midIndex - 1] + filteredFrequencies[midIndex]) / 2.0;
                    }
                    else if (filteredFrequencies.Length > 0)
                    {
                        medianFreq = filteredFrequencies[midIndex];
                    }
                    
                    // Calculate 90th percentile
                    int p90Index = (int)(filteredFrequencies.Length * 0.9);
                    if (p90Index >= filteredFrequencies.Length) p90Index = filteredFrequencies.Length - 1;
                    if (p90Index >= 0) p90Freq = filteredFrequencies[p90Index];
                }
                
                // Calculate PPM statistics for filtered results
                var filteredPpms = finalList.Select(x => x.ppm).ToArray();
                Array.Sort(filteredPpms);
                
                double minPpm = filteredPpms.Length > 0 ? filteredPpms[0] : 0;
                double maxPpm = filteredPpms.Length > 0 ? filteredPpms[filteredPpms.Length - 1] : 0;
                double avgPpm = filteredPpms.Length > 0 ? filteredPpms.Average() : 0;
                double medianPpm = 0;
                
                if (filteredPpms.Length > 0)
                {
                    // Calculate median PPM
                    int midIndex = filteredPpms.Length / 2;
                    if (filteredPpms.Length % 2 == 0 && filteredPpms.Length > 1)
                    {
                        medianPpm = (filteredPpms[midIndex - 1] + filteredPpms[midIndex]) / 2.0;
                    }
                    else if (filteredPpms.Length > 0)
                    {
                        medianPpm = filteredPpms[midIndex];
                    }
                }
                
                // Display n-gram statistics
                if (options.ShowSummary)
                    Console.WriteLine($"Count: {preStats.uniqueCount} (unique), {preStats.uniqueCount - postFilterCount} (filtered), {postFilterCount} ({percentRetained:F1}%)");
                if (options.ShowFreqStats)
                    Console.WriteLine($"Freq: {minFreq}..{maxFreq}, {medianFreq} (median), {avgFreq} (avg), 90% < {p90Freq}");

                if (options.ShowPpmStats && (minPpm > 0 || maxPpm > 0))
                {
                    Console.WriteLine($"PPM: {minPpm:F0}..{maxPpm:F0}, {medianPpm:F0} (median), {avgPpm:F0} (avg)");
                }
                
                if (options.ShowSummary || options.ShowFreqStats || options.ShowPpmStats)
                    Console.WriteLine();

                if (options.ShowPdf)
                {
                    PrintPdfHistogram(preStats.frequencies, options);
                    Console.WriteLine();
                }

                if (options.ShowCdf)
                {
                    PrintCdfLadder(filteredFrequencies, options);
                    Console.WriteLine();
                }
                
                // Add column headers for Enhanced and Detailed modes
                if (options.ShowPhrases) {
                    if (options.ShowColumnHeader) {
                        bool showPmiHeader = options.ShowPmi && n != 1;
                        if (options.ShowZ && showPmiHeader)
                            Console.WriteLine("COUNT   PPM     Z      PMI    PHRASE");
                        else if (options.ShowZ)
                            Console.WriteLine("COUNT   PPM     Z      PHRASE");
                        else if (options.ShowPpm && showPmiHeader)
                            Console.WriteLine("COUNT   PPM     PMI    PHRASE");
                        else if (options.ShowPpm)
                            Console.WriteLine("COUNT   PPM     PHRASE");
                        else if (showPmiHeader)
                            Console.WriteLine("COUNT   PMI    PHRASE");
                    }
                    
                    // Print final (post-filter) results for this n-gram size (capped to avoid dumping enormous lists)
                    var cappedList = ApplyMaxItemsCap(finalList, options, options.Sort == SortDirection.Asc ? "ascending" : "descending", out var maxItemsNotice);
                    foreach (var it in cappedList) PrintEntry(it, options, n.ToString());
                    if (maxItemsNotice != null) Console.WriteLine(maxItemsNotice);
                }
            }
        }
        
        if (options.ShowMergedNGrams)
        {
            if (options.ShowSectionHeader)
            {
                // Add a blank line before merged section
                Console.WriteLine();
                
                Console.WriteLine("## Merged N-grams Results");
                Console.WriteLine();
            }

            // Apply percentile/top/bottom limits to merged results and materialize final list
            var finalMerged = SortAndLimit(merged, options).ToList();

            if (finalMerged.Count == 0)
            {
                PrintNoMatchesNotice();
            }
            else
            {

            // Calculate statistics for merged results based on final (post-filter) set
            var mergedFrequencies = finalMerged.Select(x => x.count).ToArray();
            Array.Sort(mergedFrequencies);

            int minFreq = mergedFrequencies.Length > 0 ? mergedFrequencies[0] : 0;
            int maxFreq = mergedFrequencies.Length > 0 ? mergedFrequencies[mergedFrequencies.Length - 1] : 0;
            double avgFreq = mergedFrequencies.Length > 0 ? mergedFrequencies.Average() : 0;
            double medianFreq = 0;
            int p90Freq = 0;

            if (mergedFrequencies.Length > 0)
            {
                // Calculate median
                int midIndex = mergedFrequencies.Length / 2;
                if (mergedFrequencies.Length % 2 == 0 && mergedFrequencies.Length > 1)
                {
                    medianFreq = (mergedFrequencies[midIndex - 1] + mergedFrequencies[midIndex]) / 2.0;
                }
                else if (mergedFrequencies.Length > 0)
                {
                    medianFreq = mergedFrequencies[midIndex];
                }

                // Calculate 90th percentile
                int p90Index = (int)(mergedFrequencies.Length * 0.9);
                if (p90Index >= mergedFrequencies.Length) p90Index = mergedFrequencies.Length - 1;
                if (p90Index >= 0) p90Freq = mergedFrequencies[p90Index];
            }

            // Calculate PPM statistics for final merged results
            double totalTokens = totalTokensPerN.Values.Sum();
            var mergedPpmArray = finalMerged.Select(x => (double)x.count / Math.Max(1, totalTokens) * 1_000_000.0).ToArray();
            Array.Sort(mergedPpmArray);

            double minPpm = mergedPpmArray.Length > 0 ? mergedPpmArray[0] : 0;
            double maxPpm = mergedPpmArray.Length > 0 ? mergedPpmArray[mergedPpmArray.Length - 1] : 0;
            double avgPpm = mergedPpmArray.Length > 0 ? mergedPpmArray.Average() : 0;
            double medianPpm = 0;

            if (mergedPpmArray.Length > 0)
            {
                // Calculate median PPM
                int midIndex = mergedPpmArray.Length / 2;
                if (mergedPpmArray.Length % 2 == 0 && mergedPpmArray.Length > 1)
                {
                    medianPpm = (mergedPpmArray[midIndex - 1] + mergedPpmArray[midIndex]) / 2.0;
                }
                else if (mergedPpmArray.Length > 0)
                {
                    medianPpm = mergedPpmArray[midIndex];
                }
            }

            // Display merged statistics
            if (options.ShowSummary)
                Console.WriteLine($"Count: {finalMerged.Count} (unique)");
            if (options.ShowFreqStats)
                Console.WriteLine($"Freq: {minFreq}..{maxFreq}, {medianFreq} (median), {avgFreq} (avg), 90% < {p90Freq}");

            if (options.ShowPpmStats && (minPpm > 0 || maxPpm > 0))
            {
                Console.WriteLine($"PPM: {minPpm:F0}..{maxPpm:F0}, {medianPpm:F0} (median), {avgPpm:F0} (avg)");
            }

            if (options.ShowSummary || options.ShowFreqStats || options.ShowPpmStats)
                Console.WriteLine();

            if (options.ShowPdf)
            {
                PrintPdfHistogram(mergedFrequencies, options);
                Console.WriteLine();
            }

            if (options.ShowCdf)
            {
                PrintCdfLadder(mergedFrequencies, options);
                Console.WriteLine();
            }

            // Add column headers for Enhanced and Detailed modes
            if (options.ShowPhrases) {
                if (options.ShowColumnHeader) {
                    if (options.ShowZ)
                        Console.WriteLine("COUNT   PPM     Z      PHRASE");
                    else if (options.ShowPpm)
                        Console.WriteLine("COUNT   PPM     PHRASE");
                }

                // Print final (post-filter) merged results (capped to avoid dumping enormous lists)
                var cappedMerged = ApplyMaxItemsCap(finalMerged, options, options.Sort == SortDirection.Asc ? "ascending" : "descending", out var mergedMaxItemsNotice);
                foreach (var it in cappedMerged)
                {
                    PrintEntry(it, options, "merged");
                }
                if (mergedMaxItemsNotice != null) Console.WriteLine(mergedMaxItemsNotice);
            }
            } // end else (finalMerged.Count != 0)
        }

        if (options.ShowTfidf)
        {
            if (options.ShowSectionHeader) Console.WriteLine();
            PrintTfidf(options);
        }

        if (options.ShowPathNGrams)
        {
            if (options.ShowSectionHeader) Console.WriteLine();
            PrintPathNGrams(pathSegmentNGramCounts, options);
        }

        if (options.ShowDispersion)
        {
            if (options.ShowSectionHeader) Console.WriteLine();
            PrintDispersion(options);
        }

        if (options.ShowLogDice || options.ShowTScore || options.ShowMi3)
        {
            if (options.ShowSectionHeader) Console.WriteLine();
            PrintAltAssociationMeasures(nGramCounts, totalTokensPerN, options);
        }

        if (options.ShowReadability)
        {
            if (options.ShowSectionHeader) Console.WriteLine();
            PrintReadability(options);
        }
    }

    // Prints the path-segment n-gram section: same filter/sort/print machinery as the main
    // prose n-gram sections, but reading from the separate pathSegmentNGramCounts namespace
    // and joining ngram display with "/" instead of a space, since these reflect directory
    // nesting/adjacency rather than English word order.
    static void PrintPathNGrams(Dictionary<int, Dictionary<string, int>> pathSegmentNGramCounts, CommandOptions options)
    {
        foreach (var n in options.NGramSizes.OrderBy(x => x))
        {
            if (!pathSegmentNGramCounts.TryGetValue(n, out var counts) || counts.Count == 0) continue;

            if (options.ShowSectionHeader)
            {
                Console.WriteLine($"## Path-Segment {n}-grams (from path-like units, e.g. \"src/whatever\")");
                Console.WriteLine();
            }

            var list = new List<(string ngram, int count, double ppm, double z, double pmi)>();
            foreach (var kv in counts)
            {
                if (!PassTextFilters(kv.Key, options.TextFilters, options.IgnoreCase)) continue;
                if (!PassFrequencyFilters(kv.Value, options.FrequencyFilters)) continue;
                list.Add((kv.Key, kv.Value, 0.0, 0.0, 0.0));
            }

            var finalList = SortAndLimit(list, options).ToList();
            if (options.ShowSummary)
                Console.WriteLine($"Count: {counts.Count} (unique), {finalList.Count} shown");
            if (options.ShowSummary)
                Console.WriteLine();

            var cappedList = ApplyMaxItemsCap(finalList, options, options.Sort == SortDirection.Asc ? "ascending" : "descending", out var notice);
            foreach (var it in cappedList) PrintEntry(it, options, "path-" + n);
            if (notice != null) Console.WriteLine(notice);
            Console.WriteLine();
        }
    }

    // Idea-007 (CDRs/ideas/idea-007-dispersion-measures.md): Juilland's D and DP (Deviation
    // of Proportions) per n-gram, computed from PerDocNGramCounts/PerDocTotalTokensPerN —
    // data ngc already collects for --show-tfidf. Answers "is this term used everywhere, or
    // is it just one file screaming really loudly?" as distinct from raw frequency.
    //
    // Juilland's D = 1 - (CV / sqrt(n-1)), where CV is the coefficient of variation of the
    // term's per-document RELATIVE frequencies (count-in-doc / doc's total tokens), and n is
    // the number of documents. D ranges 0..1; D=1 means perfectly even dispersion across every
    // document, D近0 means concentrated in very few. DP (Deviation of Proportions, Gries 2008)
    // is a complementary 0..1 measure where LOWER = more even; DP = 0.5 * sum(|obs_i - exp_i|)
    // where obs_i is the term's share of its occurrences in doc i, and exp_i is doc i's share
    // of the corpus's total tokens (i.e. "expected" share if the term were spread proportional
    // to document size).
    static void PrintDispersion(CommandOptions options)
    {
        if (options.FileGlobs.Count == 0)
        {
            Console.WriteLine("## Dispersion (Juilland's D / DP)");
            Console.WriteLine();
            Console.WriteLine("(--show-dispersion requires --files; stdin has no document boundaries)");
            Console.WriteLine();
            return;
        }

        int docCount = Program.DocumentNames.Count;
        if (docCount < 2)
        {
            Console.WriteLine("## Dispersion (Juilland's D / DP)");
            Console.WriteLine();
            Console.WriteLine("(dispersion is meaningless with fewer than 2 documents)");
            Console.WriteLine();
            return;
        }

        // Corpus-wide token totals per document, used for DP's "expected share" term.
        var docTotalTokens = new Dictionary<string, long>();
        long grandTotalTokens = 0;
        foreach (var docName in Program.DocumentNames)
        {
            long docTotal = 0;
            if (Program.PerDocTotalTokensPerN.TryGetValue(docName, out var perN))
                foreach (var kv in perN) docTotal += kv.Value; // summed across n just for a size proxy
            docTotalTokens[docName] = docTotal;
            grandTotalTokens += docTotal;
        }

        foreach (var n in options.NGramSizes.OrderBy(x => x))
        {
            if (options.ShowSectionHeader)
            {
                Console.WriteLine($"## Dispersion: {n}-grams (Juilland's D / DP across {docCount} documents)");
                Console.WriteLine();
            }

            // ngram -> per-document raw count (only docs where it occurs need an entry;
            // absence means 0 for that doc).
            var perNgramDocCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var docName in Program.DocumentNames)
            {
                if (!Program.PerDocNGramCounts.TryGetValue(docName, out var docCounts)) continue;
                if (!docCounts.TryGetValue(n, out var ngramCounts)) continue;
                foreach (var kv in ngramCounts)
                {
                    if (kv.Value <= 0) continue;
                    if (!perNgramDocCounts.TryGetValue(kv.Key, out var byDoc))
                    {
                        byDoc = new Dictionary<string, int>();
                        perNgramDocCounts[kv.Key] = byDoc;
                    }
                    byDoc[docName] = kv.Value;
                }
            }

            var rows = new List<(string ngram, int totalCount, double juillandD, double dp)>();
            foreach (var kv in perNgramDocCounts)
            {
                var ngram = kv.Key;
                if (!PassTextFilters(ngram, options.TextFilters, options.IgnoreCase)) continue;
                if (!PassStopwordFilter(ngram, options)) continue;
                if (!PassTrimFilter(ngram, options)) continue;

                var byDoc = kv.Value;
                int totalCount = byDoc.Values.Sum();
                if (!PassFrequencyFilters(totalCount, options.FrequencyFilters)) continue;

                // Juilland's D: coefficient of variation over per-doc RELATIVE frequencies
                // (count-in-doc / doc's total tokens), across ALL documents (0 for docs the
                // term never appears in — that absence is exactly what should pull D down).
                var relFreqs = new double[docCount];
                for (int i = 0; i < docCount; i++)
                {
                    var docName = Program.DocumentNames[i];
                    int c = byDoc.TryGetValue(docName, out var cc) ? cc : 0;
                    long docTotal = docTotalTokens[docName];
                    relFreqs[i] = docTotal > 0 ? (double)c / docTotal : 0.0;
                }
                double mean = relFreqs.Average();
                double sd = mean > 0
                    ? Math.Sqrt(relFreqs.Sum(v => (v - mean) * (v - mean)) / docCount)
                    : 0.0;
                double cv = mean > 0 ? sd / mean : 0.0;
                double juillandD = mean > 0 ? 1.0 - (cv / Math.Sqrt(docCount - 1)) : 0.0;
                juillandD = Math.Max(0.0, Math.Min(1.0, juillandD));

                // DP (Gries 2008): 0.5 * sum(|obs_i - exp_i|), where obs_i is this term's
                // share of ITS OWN total occurrences that fell in doc i, and exp_i is doc i's
                // share of the corpus's total tokens (the "expected" share under an even split
                // proportional to document size).
                double dp = 0.0;
                if (totalCount > 0 && grandTotalTokens > 0)
                {
                    for (int i = 0; i < docCount; i++)
                    {
                        var docName = Program.DocumentNames[i];
                        int c = byDoc.TryGetValue(docName, out var cc) ? cc : 0;
                        double obs = (double)c / totalCount;
                        double exp = (double)docTotalTokens[docName] / grandTotalTokens;
                        dp += Math.Abs(obs - exp);
                    }
                    dp *= 0.5;
                }

                rows.Add((ngram, totalCount, juillandD, dp));
            }

            // Sort ascending by Juilland's D by default (idea-007's framing: "used everywhere
            // a little" vs "screaming loudly in one file" — the low-D end is the interesting,
            // concentrated-in-one-file tail most users will want to see first), honoring
            // --asc/--desc same as everywhere else.
            var sorted = options.Sort == SortDirection.Asc
                ? rows.OrderBy(r => r.juillandD).ThenBy(r => r.ngram)
                : rows.OrderByDescending(r => r.juillandD).ThenBy(r => r.ngram);
            var limited = sorted.AsEnumerable();
            if (options.Limit < int.MaxValue) limited = limited.Take(options.Limit);
            var limitedList = ApplyMaxItemsCap(limited.ToList(), options, options.Sort == SortDirection.Asc ? "ascending by Juilland's D" : "descending by Juilland's D", out var notice);

            if (options.ShowSummary)
            {
                Console.WriteLine($"Count: {rows.Count} (unique)");
                Console.WriteLine();
            }

            int phraseWidth = limitedList.Count > 0 ? limitedList.Max(r => r.ngram.Length) : 0;
            phraseWidth = Math.Max(phraseWidth, "PHRASE".Length);

            if (options.ShowColumnHeader)
                Console.WriteLine($"COUNT   D       DP      {"PHRASE".PadRight(phraseWidth)}");

            if (options.ShowPhrases)
            {
                foreach (var r in limitedList)
                    Console.WriteLine($"{r.totalCount,-7} {r.juillandD,-7:F2} {r.dp,-7:F2} {r.ngram.PadRight(phraseWidth)}");
            }
            if (notice != null) Console.WriteLine(notice);
            Console.WriteLine();
        }

        Console.WriteLine("Note: D near 1.0 = spread evenly across documents; D near 0.0 = concentrated");
        Console.WriteLine("      in very few documents (\"screaming loudly in one file\"). DP is the");
        Console.WriteLine("      complementary reading — near 0.0 = even, near 1.0 = concentrated.");
        Console.WriteLine();
    }

    // Idea-004 (CDRs/ideas/idea-004-alternate-association-measures.md): log-Dice, t-score,
    // and MI3 as alternates/complements to PMI, computed from the same unigram+n-gram count
    // dictionaries already gathered for PMI (see needPmiCollection in Main). All three are
    // sibling formulas over the exact same inputs PMI already uses — no new collection pass.
    static void PrintAltAssociationMeasures(
        Dictionary<int, Dictionary<string, int>> nGramCounts,
        Dictionary<int, int> totalTokensPerN,
        CommandOptions options)
    {
        if (!nGramCounts.ContainsKey(1))
        {
            Console.WriteLine("## Alternate Association Measures");
            Console.WriteLine();
            Console.WriteLine("(log-Dice/t-score/MI3 need unigram counts internally; none were collected)");
            Console.WriteLine();
            return;
        }

        double totalUnigramTokens = Math.Max(1, totalTokensPerN.TryGetValue(1, out var t1) ? t1 : 0);
        var unigramCounts = nGramCounts[1];

        foreach (var n in options.NGramSizes.OrderBy(x => x))
        {
            if (n < 2) continue; // all three measures are meaningless for unigrams (no co-occurrence)
            if (!nGramCounts.TryGetValue(n, out var counts) || counts.Count == 0) continue;

            if (options.ShowSectionHeader)
            {
                var cols = new List<string>();
                if (options.ShowLogDice) cols.Add("log-Dice");
                if (options.ShowTScore) cols.Add("t-score");
                if (options.ShowMi3) cols.Add("MI3");
                Console.WriteLine($"## Alternate Association Measures: {n}-grams ({string.Join(", ", cols)})");
                Console.WriteLine();
            }

            double totalNgramTokens = Math.Max(1, totalTokensPerN.TryGetValue(n, out var tn) ? tn : 0);

            var rows = new List<(string ngram, int count, double logDice, double tScore, double mi3)>();
            foreach (var kv in counts)
            {
                var ngram = kv.Key;
                int count = kv.Value;
                if (!PassTextFilters(ngram, options.TextFilters, options.IgnoreCase)) continue;
                if (!PassStopwordFilter(ngram, options)) continue;
                if (!PassTrimFilter(ngram, options)) continue;
                if (!PassFrequencyFilters(count, options.FrequencyFilters)) continue;

                var words = ngram.Split(' ');
                bool haveAllWords = true;
                double sumUnigramFreq = 0.0; // for log-Dice's denominator (sum of component frequencies)
                double expectedProbProduct = 1.0; // for t-score/MI3's expected-count term
                foreach (var w in words)
                {
                    if (unigramCounts.TryGetValue(w, out var uc))
                    {
                        sumUnigramFreq += uc;
                        expectedProbProduct *= uc / totalUnigramTokens;
                    }
                    else { haveAllWords = false; break; }
                }

                double logDice = 0.0, tScore = 0.0, mi3 = 0.0;
                if (haveAllWords)
                {
                    // log-Dice: 14 + log2( 2*count / sumUnigramFreq ). The "14 +" offset is
                    // Sketch Engine's own convention purely to keep typical scores positive;
                    // it doesn't change relative ranking, only the display scale.
                    if (sumUnigramFreq > 0 && count > 0)
                        logDice = 14.0 + Math.Log(2.0 * count / sumUnigramFreq, 2);

                    // t-score: (observed - expected) / sqrt(observed), where expected count
                    // is the chance-co-occurrence count implied by the words' own frequencies.
                    double expectedCount = expectedProbProduct * totalNgramTokens;
                    if (count > 0)
                        tScore = (count - expectedCount) / Math.Sqrt(count);

                    // MI3: log2( count^3 / (totalNgramTokens^2 * expectedProbProduct) ) — the
                    // "cubed" variant of plain PMI/MI, which reduces PMI's well-known bias
                    // toward inflating rare pairs while keeping the same "surprise" flavor.
                    if (expectedProbProduct > 0 && count > 0)
                        mi3 = Math.Log(Math.Pow(count, 3) / (totalNgramTokens * totalNgramTokens * expectedProbProduct), 2);
                }

                rows.Add((ngram, count, logDice, tScore, mi3));
            }

            // Sort by whichever measure is enabled (log-Dice > t-score > MI3 priority when
            // more than one is on simultaneously), descending by default like PMI.
            Func<(string ngram, int count, double logDice, double tScore, double mi3), double> sortKey =
                options.ShowLogDice ? (r => r.logDice) :
                options.ShowTScore ? (r => r.tScore) :
                (r => r.mi3);

            var sorted = options.Sort == SortDirection.Asc
                ? rows.OrderBy(sortKey).ThenBy(r => r.ngram)
                : rows.OrderByDescending(sortKey).ThenBy(r => r.ngram);
            var limited = sorted.AsEnumerable();
            if (options.Limit < int.MaxValue) limited = limited.Take(options.Limit);
            var limitedList = ApplyMaxItemsCap(limited.ToList(), options, options.Sort == SortDirection.Asc ? "ascending" : "descending", out var notice);

            if (options.ShowSummary)
            {
                Console.WriteLine($"Count: {rows.Count} (unique)");
                Console.WriteLine();
            }

            int phraseWidth = limitedList.Count > 0 ? limitedList.Max(r => r.ngram.Length) : 0;
            phraseWidth = Math.Max(phraseWidth, "PHRASE".Length);

            if (options.ShowColumnHeader)
            {
                var headerCols = new StringBuilder("COUNT   ");
                if (options.ShowLogDice) headerCols.Append("LOGDICE ");
                if (options.ShowTScore) headerCols.Append("TSCORE  ");
                if (options.ShowMi3) headerCols.Append("MI3     ");
                headerCols.Append(("PHRASE").PadRight(phraseWidth));
                Console.WriteLine(headerCols.ToString());
            }

            if (options.ShowPhrases)
            {
                foreach (var r in limitedList)
                {
                    var sb = new StringBuilder();
                    sb.Append($"{r.count,-7} ");
                    if (options.ShowLogDice) sb.Append($"{r.logDice,-7:F2} ");
                    if (options.ShowTScore) sb.Append($"{r.tScore,-7:F2} ");
                    if (options.ShowMi3) sb.Append($"{r.mi3,-7:F2} ");
                    sb.Append(r.ngram.PadRight(phraseWidth));
                    Console.WriteLine(sb.ToString());
                }
            }
            if (notice != null) Console.WriteLine(notice);
            Console.WriteLine();
        }
    }

    // Idea-008 (CDRs/ideas/idea-008-readability-lexical-diversity.md): whole-document/corpus
    // summary stats — TTR (Type-Token Ratio) and MTLD (Measure of Textual Lexical Diversity).
    // A different zoom level than everything else: one number per document/corpus, not per-
    // phrase. Both only need the ordered unigram token stream ngc already produces (see
    // PerDocWordSequence, populated in Main only when --show-readability is set).
    const double MtldTtrThreshold = 0.72; // standard MTLD factor-completion threshold (McCarthy & Jarvis 2010)

    static void PrintReadability(CommandOptions options)
    {
        Console.WriteLine("## Readability / Lexical Diversity");
        Console.WriteLine();

        if (Program.PerDocWordSequence.Count == 0)
        {
            Console.WriteLine("(no word sequence data collected — this shouldn't happen if --show-readability was set)");
            Console.WriteLine();
            return;
        }

        // Compute one row per document, then sort/cap using the same pipeline every other
        // report uses (--asc/--desc, --top/--bottom, --max-items) — sorted by MTLD, since
        // that's the length-robust, more meaningful of the two measures.
        var rows = new List<(string docName, int tokenCount, int typeCount, double ttr, double mtld)>();
        var aggregateWords = new List<string>();
        foreach (var docName in Program.DocumentNames)
        {
            if (!Program.PerDocWordSequence.TryGetValue(docName, out var words)) continue;
            aggregateWords.AddRange(words);

            int tokenCount = words.Count;
            int typeCount = new HashSet<string>(words, StringComparer.OrdinalIgnoreCase).Count;
            double ttr = tokenCount > 0 ? (double)typeCount / tokenCount : 0.0;
            double mtld = ComputeMtld(words);
            rows.Add((docName, tokenCount, typeCount, ttr, mtld));
        }

        var sorted = options.Sort == SortDirection.Asc
            ? rows.OrderBy(r => r.mtld).ThenBy(r => r.docName)
            : rows.OrderByDescending(r => r.mtld).ThenBy(r => r.docName);
        var limited = sorted.AsEnumerable();
        if (options.Limit < int.MaxValue) limited = limited.Take(options.Limit);
        var limitedList = ApplyMaxItemsCap(limited.ToList(), options, options.Sort == SortDirection.Asc ? "ascending by MTLD" : "descending by MTLD", out var notice);

        if (options.ShowSummary)
        {
            Console.WriteLine($"Count: {rows.Count} document(s)");
            Console.WriteLine();
        }

        if (options.ShowColumnHeader)
            Console.WriteLine("TOKENS  TYPES   TTR     MTLD    DOCUMENT");

        if (options.ShowPhrases)
        {
            foreach (var r in limitedList)
                Console.WriteLine($"{r.tokenCount,-7} {r.typeCount,-7} {r.ttr,-7:F3} {r.mtld,-7:F1} {r.docName}");
        }
        if (notice != null) Console.WriteLine(notice);

        // Whole-corpus aggregate row, only meaningful/distinct when there's more than one doc.
        // Always shown in full (never subject to the per-document --max-items cap above).
        if (Program.DocumentNames.Count > 1)
        {
            int tokenCount = aggregateWords.Count;
            int typeCount = new HashSet<string>(aggregateWords, StringComparer.OrdinalIgnoreCase).Count;
            double ttr = tokenCount > 0 ? (double)typeCount / tokenCount : 0.0;
            double mtld = ComputeMtld(aggregateWords);
            Console.WriteLine();
            Console.WriteLine($"{tokenCount,-7} {typeCount,-7} {ttr,-7:F3} {mtld,-7:F1} (WHOLE CORPUS)");
        }

        Console.WriteLine();
        Console.WriteLine("Note: TTR (unique words / total words) is sensitive to document length —");
        Console.WriteLine("      shorter documents/samples naturally score higher TTR. MTLD corrects");
        Console.WriteLine("      for this by averaging the token-count needed for TTR to drop to");
        Console.WriteLine($"      {MtldTtrThreshold:F2} across repeated forward/backward passes — higher MTLD =");
        Console.WriteLine("      more lexically varied text, independent of sample length.");
        Console.WriteLine();
    }

    // MTLD (McCarthy & Jarvis, 2010): walks the token sequence accumulating a running TTR;
    // whenever that running TTR drops to or below MtldTtrThreshold, a "factor" is completed
    // and the running counters reset. The final (possibly partial) factor is given partial
    // credit proportional to how close its own TTR got to the threshold. MTLD = total tokens
    // / total factor count (full + partial). Computed forward and backward, then averaged,
    // per the original algorithm, to reduce sensitivity to where exactly the text starts/ends.
    static double ComputeMtld(List<string> words)
    {
        if (words.Count < 2) return 0.0;

        double forward = MtldOneDirection(words);
        var reversed = new List<string>(words);
        reversed.Reverse();
        double backward = MtldOneDirection(reversed);
        return (forward + backward) / 2.0;
    }

    static double MtldOneDirection(List<string> words)
    {
        int factorCount = 0;
        int tokenCount = 0;
        var typesInFactor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in words)
        {
            typesInFactor.Add(w);
            tokenCount++;
            double ttr = (double)typesInFactor.Count / tokenCount;
            if (ttr <= MtldTtrThreshold)
            {
                factorCount++;
                tokenCount = 0;
                typesInFactor.Clear();
            }
        }

        if (tokenCount > 0)
        {
            double finalTtr = typesInFactor.Count > 0 ? (double)typesInFactor.Count / tokenCount : 1.0;
            double proportionComplete = finalTtr < 1.0 ? (1.0 - finalTtr) / (1.0 - MtldTtrThreshold) : 0.0;
            proportionComplete = Math.Min(1.0, Math.Max(0.0, proportionComplete));
            double totalFactors = factorCount + proportionComplete;
            return totalFactors > 0 ? (double)words.Count / totalFactors : (double)words.Count;
        }

        return factorCount > 0 ? (double)words.Count / factorCount : (double)words.Count;
    }

    static void PrintTfidf(CommandOptions options)
    {
        if (options.FileGlobs.Count == 0)
        {
            Console.WriteLine("## TF-IDF");
            Console.WriteLine();
            Console.WriteLine("(--show-tfidf requires --files; stdin has no document boundaries)");
            Console.WriteLine();
            return;
        }

        int docCount = Program.DocumentNames.Count;
        if (docCount == 0) return;

        foreach (var n in options.NGramSizes.OrderBy(x => x))
        {
            if (options.ShowSectionHeader)
            {
                Console.WriteLine($"## TF-IDF: {n}-grams ({docCount} documents)");
                Console.WriteLine();
            }

            // Aggregate n-gram -> set/count of documents it appears in (DF), plus per-doc TF for tf-idf.
            var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var perDocTf = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase); // ngram -> docName -> count
            foreach (var docName in Program.DocumentNames)
            {
                if (!Program.PerDocNGramCounts.TryGetValue(docName, out var docCounts)) continue;
                if (!docCounts.TryGetValue(n, out var ngramCounts)) continue;
                foreach (var kv in ngramCounts)
                {
                    if (kv.Value <= 0) continue;
                    df[kv.Key] = df.TryGetValue(kv.Key, out var cur) ? cur + 1 : 1;
                    if (!perDocTf.TryGetValue(kv.Key, out var byDoc))
                    {
                        byDoc = new Dictionary<string, int>();
                        perDocTf[kv.Key] = byDoc;
                    }
                    byDoc[docName] = kv.Value;
                }
            }

            // Compute IDF (smoothed) and TF-IDF (max across docs) + best file per ngram.
            double Idf(int documentFrequency) => Math.Log((double)docCount / (1 + documentFrequency)) + 1;

            var rows = new List<(string ngram, int count, int docFreq, double idf, double tfidfMax, string bestFile)>();
            foreach (var kv in perDocTf)
            {
                var ngram = kv.Key;
                if (!PassTextFilters(ngram, options.TextFilters, options.IgnoreCase)) continue;
                if (!PassStopwordFilter(ngram, options)) continue;
                if (!PassTrimFilter(ngram, options)) continue;
                int totalCount = kv.Value.Values.Sum();
                if (!PassFrequencyFilters(totalCount, options.FrequencyFilters)) continue;

                int documentFrequency = df[ngram];
                double idf = Idf(documentFrequency);
                double bestTfidf = -1; string bestFile = "";
                foreach (var docKv in kv.Value)
                {
                    double tfidf = docKv.Value * idf;
                    if (tfidf > bestTfidf) { bestTfidf = tfidf; bestFile = docKv.Key; }
                }
                rows.Add((ngram, totalCount, documentFrequency, idf, bestTfidf, bestFile));
            }

            if (!options.PerFile)
            {
                var tfidfFiltered = options.TfidfFilters.Count > 0
                    ? rows.Where(r => PassTfidfFilters(r.tfidfMax, options.TfidfFilters)).ToList()
                    : rows;
                var sorted = options.Sort == SortDirection.Asc
                    ? tfidfFiltered.OrderBy(r => r.tfidfMax).ThenBy(r => r.ngram)
                    : tfidfFiltered.OrderByDescending(r => r.tfidfMax).ThenBy(r => r.ngram);
                var limited = sorted.AsEnumerable();
                if (options.Limit < int.MaxValue) limited = limited.Take(options.Limit);
                var limitedList = ApplyMaxItemsCap(limited.ToList(), options, options.Sort == SortDirection.Asc ? "ascending by tfidf" : "descending by tfidf", out var tfidfMaxItemsNotice);

                // Fixed-width phrase column so the (variable-length) BEST-FILE that follows
                // always lines up, and phrases themselves are easy to scan down the column.
                int phraseWidth = limitedList.Count > 0 ? limitedList.Max(r => r.ngram.Length) : 0;
                phraseWidth = Math.Max(phraseWidth, "PHRASE".Length);

                if (options.ShowColumnHeader)
                    Console.WriteLine($"COUNT   DF      IDF     TFIDF   {"PHRASE".PadRight(phraseWidth)}  BEST-FILE");

                if (options.ShowTfidfPhrases)
                {
                    foreach (var r in limitedList)
                        Console.WriteLine($"{r.count,-7} {r.docFreq,-7} {r.idf,-7:F2} {r.tfidfMax,-7:F2} {r.ngram.PadRight(phraseWidth)}  {r.bestFile}");
                }
                if (tfidfMaxItemsNotice != null) Console.WriteLine(tfidfMaxItemsNotice);
                Console.WriteLine();
            }
            else
            {
                // Rank documents by an aggregate per-file distinctiveness score (computed
                // from this same n-gram size's rows) before deciding which ones get a full
                // breakdown table — see CommandOptions.TopFiles/TopFilesBy. Mirrors the
                // existing --max-items rank+cap+trailing-notice convention, one level up
                // (files, not rows).
                var docScores = new Dictionary<string, double>();
                foreach (var docName in Program.DocumentNames)
                {
                    var docTfidfs = rows.Where(r => perDocTf[r.ngram].ContainsKey(docName))
                        .Select(r => perDocTf[r.ngram][docName] * r.idf)
                        .OrderByDescending(v => v)
                        .ToList();
                    double score;
                    if (docTfidfs.Count == 0) score = 0;
                    else if (options.TopFilesBy == "max") score = docTfidfs[0];
                    else if (options.TopFilesBy == "sum") score = docTfidfs.Sum();
                    else score = docTfidfs.Take(5).Average(); // "avg-top5" (default)
                    docScores[docName] = score;
                }

                var rankedDocs = Program.DocumentNames.OrderByDescending(d => docScores[d]).ToList();
                bool topFilesCapped = options.TopFiles < int.MaxValue && rankedDocs.Count > options.TopFiles;
                var docsToShow = topFilesCapped ? rankedDocs.Take(options.TopFiles).ToList() : rankedDocs;

                foreach (var docName in docsToShow)
                {
                    var docRows = rows.Where(r => perDocTf[r.ngram].ContainsKey(docName))
                        .Select(r => (r.ngram, r.count, r.docFreq, r.idf, tfidf: perDocTf[r.ngram][docName] * r.idf))
                        .ToList();

                    var tfidfFilteredDocRows = options.TfidfFilters.Count > 0
                        ? docRows.Where(r => PassTfidfFilters(r.tfidf, options.TfidfFilters)).ToList()
                        : docRows;

                    var sorted = options.Sort == SortDirection.Asc
                        ? tfidfFilteredDocRows.OrderBy(r => r.tfidf).ThenBy(r => r.ngram)
                        : tfidfFilteredDocRows.OrderByDescending(r => r.tfidf).ThenBy(r => r.ngram);
                    var limited = sorted.AsEnumerable();
                    if (options.Limit < int.MaxValue) limited = limited.Take(options.Limit);
                    var limitedDocList = limited.ToList();

                    // Per-file row cap (ngc-feedback.md #3): distinct from the global
                    // --max-items cap below, and applied first — this is what actually keeps
                    // `--per-file` output bounded when many files are selected, rather than
                    // relying on the global cap (which used to apply once, per WHOLE run, not
                    // per file, so N files could each print up to --max-items rows).
                    bool perFileCapped = !HasExplicitLimit(options) && options.MaxItemsPerFile > 0 && limitedDocList.Count > options.MaxItemsPerFile;
                    int totalBeforePerFileCap = limitedDocList.Count;
                    if (perFileCapped) limitedDocList = limitedDocList.Take(options.MaxItemsPerFile).ToList();

                    var afterPerFileCap = ApplyMaxItemsCap(limitedDocList, options, options.Sort == SortDirection.Asc ? "ascending by tfidf" : "descending by tfidf", out var tfidfDocMaxItemsNotice);

                    string headerSuffix = perFileCapped ? $" — showing top {options.MaxItemsPerFile} of {totalBeforePerFileCap}" : "";
                    Console.WriteLine($"### {docName} (rank score: {docScores[docName]:F2}, by {options.TopFilesBy}){headerSuffix}");
                    Console.WriteLine();
                    if (options.ShowColumnHeader)
                        Console.WriteLine("COUNT   DF      IDF     TFIDF   PHRASE");

                    if (options.ShowTfidfPhrases)
                    {
                        foreach (var r in afterPerFileCap)
                            Console.WriteLine($"{r.count,-7} {r.docFreq,-7} {r.idf,-7:F2} {r.tfidf,-7:F2} {r.ngram}");
                    }
                    if (perFileCapped)
                        Console.WriteLine($"[showing top {options.MaxItemsPerFile} of {totalBeforePerFileCap} rows for this file — use `--max-items-per-file N` (0=unlimited) to see more]");
                    if (tfidfDocMaxItemsNotice != null) Console.WriteLine(tfidfDocMaxItemsNotice);
                    Console.WriteLine();
                }

                if (topFilesCapped)
                    Console.WriteLine($"[showing top {options.TopFiles} of {rankedDocs.Count} files, ranked by {options.TopFilesBy} TF-IDF score — use `--top-files 0` for all]");
            }

            // Feedback item #3 (CDRs/ngc-feedback.md Round 3, decision #3): cheap dispersion
            // hint reusing the DF (document frequency) data already computed above for TF-IDF.
            // Terms present in near-every document are likely shared structural/template
            // vocabulary (e.g. repeated section headings), not real content — flag them so the
            // user can suppress them and re-rank, rather than silently letting them dominate
            // every file's "top terms" list. This is a crude DF-ratio proxy, not a true
            // dispersion measure (Juilland's D/DP, see cdrs/ideas/idea-007) — a term appearing
            // once in 90% of docs looks identical here to one appearing 50x in one doc and 1x
            // in nine others. Deliberately simple for now; upgrade later if idea-007 lands.
            PrintDispersionHint(rows, docCount, options);
        }
    }

    const double NearUniversalDispersionThreshold = 0.9; // DF/docCount >= this => "near-universal"
    const int NearUniversalDispersionHintSampleSize = 10; // cap how many example terms we print

    static void PrintDispersionHint(
        List<(string ngram, int count, int docFreq, double idf, double tfidfMax, string bestFile)> rows,
        int docCount,
        CommandOptions options)
    {
        if (docCount < 3) return; // dispersion is a meaningless signal with only 1-2 documents

        var nearUniversal = rows
            .Where(r => (double)r.docFreq / docCount >= NearUniversalDispersionThreshold)
            .OrderByDescending(r => r.docFreq)
            .ThenByDescending(r => r.count)
            .ToList();

        if (nearUniversal.Count == 0) return;

        var sample = nearUniversal.Take(NearUniversalDispersionHintSampleSize).Select(r => r.ngram).ToList();
        string moreSuffix = nearUniversal.Count > sample.Count ? $", +{nearUniversal.Count - sample.Count} more" : "";

        Console.WriteLine($"⚠ {nearUniversal.Count} term(s) appear in ≥{NearUniversalDispersionThreshold * 100:F0}% of your {docCount} documents");
        Console.WriteLine("  (near-universal dispersion) — likely shared structural/template vocabulary");
        Console.WriteLine("  (e.g. repeated section headings), not distinctive content. This is a crude");
        Console.WriteLine("  DF-ratio proxy, not a true dispersion measure — treat as a hint, not a fact.");
        Console.WriteLine($"  Consider suppressing before re-ranking: --stop-words {string.Join(" ", sample)}{moreSuffix}");
        Console.WriteLine();
    }

    static bool HasExplicitLimit(CommandOptions options) => options.Limit < int.MaxValue || options.LimitIsPercentage || options.BottomLimit > 0 || options.BottomLimitIsPercentage;

    // Trims to options.MaxItems (unless the user gave an explicit top:/bottom:), and returns
    // a notice string to print AFTER the list (so it's the last thing visible once the list
    // scrolls off-screen), or null if no trimming occurred.
    static List<T> ApplyMaxItemsCap<T>(List<T> items, CommandOptions options, string sortDescription, out string? notice)
    {
        notice = null;
        if (HasExplicitLimit(options)) return items; // user's explicit top:/bottom: always wins, no cap
        if (options.MaxItems <= 0) return items; // 0 = unlimited
        if (items.Count <= options.MaxItems) return items;

        var trimmed = items.Take(options.MaxItems).ToList();
        notice = $"[showing top {options.MaxItems} of {items.Count} results, sorted {sortDescription} — use `--top N`, `--bottom N`, or `--max-items N` (0=unlimited) to see more]";
        return trimmed;
    }

    static void PrintPdfHistogram(int[] sortedFrequencies, CommandOptions options)
    {
        if (sortedFrequencies.Length == 0)
        {
            Console.WriteLine("PDF: (no data)");
            return;
        }

        // sortedFrequencies is ascending array of per-ngram counts (frequency-of-frequencies input).
        int maxFreq = sortedFrequencies[sortedFrequencies.Length - 1];
        int total = sortedFrequencies.Length;

        // Build log-scale buckets: 1, 2, 3, then doubling ranges (4-7, 8-15, 16-31, ...)
        var buckets = new List<(int lo, int hi, string label)>();
        buckets.Add((1, 1, "1"));
        buckets.Add((2, 2, "2"));
        buckets.Add((3, 3, "3"));
        int lo = 4;
        while (lo <= maxFreq)
        {
            int hi = lo * 2 - 1;
            buckets.Add((lo, hi, hi > lo ? $"{lo}-{hi}" : $"{lo}"));
            lo *= 2;
        }

        if (options.ShowSectionHeader)
        {
            Console.WriteLine("## PDF (frequency-of-frequencies histogram)");
            Console.WriteLine();
        }
        if (options.ShowColumnHeader)
            Console.WriteLine("COUNT     FREQ        %");

        // sortedFrequencies is sorted ascending; walk it once with a pointer per bucket
        int idx = 0;
        int hapaxCount = 0; // freq==1 bucket, tracked separately for the annotation below
        foreach (var b in buckets)
        {
            int count = 0;
            while (idx < sortedFrequencies.Length && sortedFrequencies[idx] <= b.hi)
            {
                if (sortedFrequencies[idx] >= b.lo) count++;
                idx++;
            }
            if (b.lo == 1 && b.hi == 1) hapaxCount = count;
            if (count == 0) continue;
            double pct = total > 0 ? (double)count / total * 100.0 : 0.0;
            Console.WriteLine($"{count,-9} {b.label,-11} {pct:F1}%");
        }

        // Annotate the freq=1 bucket in plain language: it's always the largest bucket in
        // real text (Zipf's law), so a large percentage here is normal — not a sign of rich
        // content. These "hapax legomena" (occur-exactly-once items) are disproportionately
        // proper nouns, quoted examples, and one-off identifiers, not recurring vocabulary.
        if (hapaxCount > 0 && total > 0)
        {
            double hapaxPct = (double)hapaxCount / total * 100.0;
            Console.WriteLine();
            Console.WriteLine($"Note: {hapaxPct:F1}% of unique items occur exactly once (\"hapax legomena\")");
            Console.WriteLine("      — often one-off names/quotes/identifiers, not recurring patterns.");
            Console.WriteLine("      This bucket is always the largest in real text (Zipf's law); a high");
            Console.WriteLine("      percentage here is normal, not evidence of rich unique content.");
        }
    }

    static void PrintCdfLadder(int[] sortedFrequencies, CommandOptions options)
    {
        if (sortedFrequencies.Length == 0)
        {
            Console.WriteLine("CDF: (no data)");
            return;
        }

        // sortedFrequencies must already be sorted ascending.
        int total = sortedFrequencies.Length;
        double[] percentiles = { 0, 10, 25, 50, 75, 90, 95, 99, 100 };

        if (options.ShowSectionHeader)
        {
            Console.WriteLine("## CDF (percentile ladder: frequency value at or below which N% of items fall)");
            Console.WriteLine();
        }
        if (options.ShowColumnHeader)
            Console.WriteLine("PCTILE    FREQ");

        foreach (var p in percentiles)
        {
            int idx = (int)Math.Round((p / 100.0) * (total - 1));
            if (idx < 0) idx = 0;
            if (idx >= total) idx = total - 1;
            int freq = sortedFrequencies[idx];
            Console.WriteLine($"{p,-9:F0} {freq}");
        }
    }

    static IEnumerable<(string ngram, int count, double ppm, double z, double pmi)> SortAndLimit(IEnumerable<(string ngram, int count, double ppm, double z, double pmi)> seq, CommandOptions options)
    {
        // First, materialize the sequence if we need to calculate percentiles
        var items = options.PercentileFilters.Count > 0 ? seq.ToList() : seq;
        
        // Apply percentile filters if any
        if (options.PercentileFilters.Count > 0)
        {
            // Sort items by count to calculate percentiles
            var sortedByCount = items.OrderBy(x => x.count).ToList();
            int totalItems = sortedByCount.Count;
            
            // Skip percentile calculation if no items
            if (totalItems == 0) 
                return Enumerable.Empty<(string ngram, int count, double ppm, double z, double pmi)>();
            
            // Group items by count to handle ties properly
            var countGroups = sortedByCount.GroupBy(x => x.count).ToList();
            int totalGroups = countGroups.Count;
            
            // Create a mapping of item to its percentile, ensuring items with same count get same percentile
            var percentileMap = new Dictionary<string, double>();
            int itemsProcessed = 0;
            
            // Clear any existing percentile boundaries
            options.UniquePercentiles.Clear();
            
            foreach (var group in countGroups)
            {
                // Calculate percentile based on the middle position of this group
                int groupSize = group.Count();
                int groupMiddlePosition = itemsProcessed + (groupSize / 2);
                
                // Calculate percentile for this group (all items in group get same percentile)
                double percentile = totalItems > 1 
                    ? (double)groupMiddlePosition / (totalItems - 1) * 100.0 
                    : 50.0; // If only one item, it's at the 50th percentile
                
                // Store this unique percentile boundary
                options.UniquePercentiles.Add(percentile);
                
                // Assign the same percentile to all items in this count group
                foreach (var item in group)
                {
                    percentileMap[item.ngram] = percentile;
                }
                
                itemsProcessed += groupSize;
            }
            
            // Filter items by percentile
            var filteredItems = new List<(string ngram, int count, double ppm, double z, double pmi)>();
            foreach (var item in items)
            {
                double itemPercentile = percentileMap[item.ngram];
                if (PassPercentileFilters(itemPercentile, options.PercentileFilters))
                {
                    filteredItems.Add(item);
                }
            }
            
            items = filteredItems;
        }
        
        // Continue with the rest of the sorting and limiting logic
        
        // Determine whether to use PPM for sorting and limiting
        bool usePpm = options.SortBy == SortBy.Ppm || 
                     ((options.Mode == OutputMode.Enhanced || options.Mode == OutputMode.Detailed) && 
                      options.SortBy == SortBy.Count && // Default to PPM for detailed modes if not explicitly set
                      items.Any(x => x.ppm > 0));  // Only use PPM if values are meaningful
        
        // Handle top:N and/or bottom:N limits
        IEnumerable<(string ngram, int count, double ppm, double z, double pmi)> limitedSeq = items;
        bool hasLimits = false;
        
        // Calculate item counts if using percentages
        int totalItemCount = items.Count();
        
        // Apply top:N or top:N% limit if specified
        if (options.Limit < int.MaxValue || options.LimitIsPercentage)
        {
            // Sort the items by highest frequency (count or ppm)
            var sortedItems = usePpm
                ? items.OrderByDescending(x => x.ppm).ThenBy(x => x.ngram).ToList()
                : items.OrderByDescending(x => x.count).ThenBy(x => x.ngram).ToList();
            
            if (sortedItems.Count > 0)
            {
                // Determine how many items to take
                int itemsToTake;
                
                if (options.LimitIsPercentage)
                {
                    // Calculate number of items based on percentage
                    double fraction = options.LimitPercentage / 100.0;
                    itemsToTake = Math.Max(1, (int)Math.Ceiling(totalItemCount * fraction));
                }
                else
                {
                    // Use explicit limit
                    itemsToTake = options.Limit;
                }
                
                itemsToTake = Math.Min(itemsToTake, sortedItems.Count);
                
                // Guard against --top 0 (or a 0%/negative computed itemsToTake): treat as
                // "show nothing" rather than indexing sortedItems[-1], which used to throw
                // an unhandled ArgumentOutOfRangeException (found probing CDRs/ with
                // `ngc 1 --top 0`, see ngc-feedback.md). Every other 0-meaning-unlimited
                // flag in ngc (--max-items, --top-files, etc.) is a DIFFERENT flag/knob;
                // --top 0 has no documented "unlimited" meaning, so literal "zero items" is
                // the safe, non-crashing interpretation here.
                if (itemsToTake <= 0)
                {
                    limitedSeq = Enumerable.Empty<(string ngram, int count, double ppm, double z, double pmi)>();
                }
                // Include all items tied with the boundary value — but ONLY when sorting by
                // integer count. PPM is a continuous/floating value and, especially at the
                // low-frequency (long-tail) end of real text, thousands of distinct n-grams can
                // share the exact same rounded ppm — tie-expansion there silently blows top:N
                // out to the entire result set. So for ppm-based limiting, honor N strictly.
                else if (usePpm)
                {
                    limitedSeq = sortedItems.Take(itemsToTake);
                }
                else
                {
                    var boundaryValue = sortedItems[itemsToTake - 1].count;
                    limitedSeq = sortedItems.Where(x => x.count >= boundaryValue);
                }
            }
            else
            {
                limitedSeq = sortedItems;
            }
            
            hasLimits = true;
        }
        
        // Apply bottom:N or bottom:N% limit if specified
        if (options.BottomLimit > 0 || options.BottomLimitIsPercentage)
        {
            // Sort the items by lowest frequency (count or ppm)
            var sortedItems = usePpm
                ? items.OrderBy(x => x.ppm).ThenBy(x => x.ngram).ToList()
                : items.OrderBy(x => x.count).ThenBy(x => x.ngram).ToList();
            
            if (sortedItems.Count > 0)
            {
                // Determine how many items to take
                int itemsToTake;
                
                if (options.BottomLimitIsPercentage)
                {
                    // Calculate number of items based on percentage
                    double fraction = options.BottomLimitPercentage / 100.0;
                    itemsToTake = Math.Max(1, (int)Math.Ceiling(totalItemCount * fraction));
                }
                else
                {
                    // Use explicit limit
                    itemsToTake = options.BottomLimit;
                }
                
                itemsToTake = Math.Min(itemsToTake, sortedItems.Count);
                
                // Same rationale as the top:N branch above: only tie-expand for integer count,
                // not ppm, to avoid blowing bottom:N out to the whole (mostly-tied) long tail.
                // Also guards against --bottom 0 the same way --top 0 is guarded above (see
                // ngc-feedback.md) — "zero items" instead of an out-of-range crash.
                IEnumerable<(string ngram, int count, double ppm, double z, double pmi)> bottomItems;
                if (itemsToTake <= 0)
                {
                    bottomItems = Enumerable.Empty<(string ngram, int count, double ppm, double z, double pmi)>();
                }
                else if (usePpm)
                {
                    bottomItems = sortedItems.Take(itemsToTake);
                }
                else
                {
                    var boundaryValue = sortedItems[itemsToTake - 1].count;
                    bottomItems = sortedItems.Where(x => x.count <= boundaryValue);
                }
                
                // If we also have top items, merge them
                if (hasLimits)
                {
                    limitedSeq = limitedSeq.Concat(bottomItems);
                }
                else
                {
                    limitedSeq = bottomItems;
                }
            }
            
            hasLimits = true;
        }
        
        // No limits specified, use the entire sequence
        if (!hasLimits)
        {
            limitedSeq = items;
        }
        
        // Now sort the (possibly limited) sequence according to user preference
        IOrderedEnumerable<(string ngram, int count, double ppm, double z, double pmi)> sorted;
        if (usePpm)
        {
            sorted = options.Sort == SortDirection.Asc
                ? limitedSeq.OrderBy(x => x.ppm).ThenBy(x => x.ngram)
                : limitedSeq.OrderByDescending(x => x.ppm).ThenBy(x => x.ngram);
        }
        else
        {
            sorted = options.Sort == SortDirection.Asc
                ? limitedSeq.OrderBy(x => x.count).ThenBy(x => x.ngram)
                : limitedSeq.OrderByDescending(x => x.count).ThenBy(x => x.ngram);
        }
        
        return sorted;
    }

    static bool PassPercentileFilters(double percentile, List<PercentileFilter> filters)
    {
        if (filters.Count == 0) return true;
        bool ok = true;
        
        // Get the precomputed unique percentile boundaries from the program state
        List<double> uniquePercentiles = Program.CurrentOptions.UniquePercentiles;
        
        foreach (var f in filters)
        {
            bool inRange = true;
            
            // Handle minimum percentile boundary (snap to nearest group boundary)
            if (f.Min.HasValue)
            {
                double minValue = f.Min.Value;
                
                // Find the nearest group boundary
                if (uniquePercentiles.Count > 0)
                {
                    // Sort percentiles if not already sorted
                    if (!Program.PercentilesAreSorted)
                    {
                        uniquePercentiles.Sort();
                        Program.PercentilesAreSorted = true;
                    }
                    
                    // Find the nearest percentile group boundary
                    double nearestBoundary = uniquePercentiles[0];
                    double minDiff = Math.Abs(minValue - nearestBoundary);
                    
                    foreach (double boundary in uniquePercentiles)
                    {
                        double diff = Math.Abs(minValue - boundary);
                        if (diff < minDiff)
                        {
                            minDiff = diff;
                            nearestBoundary = boundary;
                        }
                    }
                    
                    // Use the nearest boundary for comparison
                    // If we're testing for "above X", use the boundary at or below X
                    // so we include all items in that group
                    if (percentile < nearestBoundary && minValue > nearestBoundary)
                        inRange = false;
                    else if (percentile < minValue && (nearestBoundary >= minValue || minDiff > 5.0))
                        inRange = false;
                }
                else if (percentile < minValue)
                {
                    inRange = false;
                }
            }
            
            // Handle maximum percentile boundary (snap to nearest group boundary)
            if (f.Max.HasValue)
            {
                double maxValue = f.Max.Value;
                
                // Find the nearest group boundary
                if (uniquePercentiles.Count > 0)
                {
                    // Sort percentiles if not already sorted
                    if (!Program.PercentilesAreSorted)
                    {
                        uniquePercentiles.Sort();
                        Program.PercentilesAreSorted = true;
                    }
                    
                    // Find the nearest percentile group boundary
                    double nearestBoundary = uniquePercentiles[0];
                    double minDiff = Math.Abs(maxValue - nearestBoundary);
                    
                    foreach (double boundary in uniquePercentiles)
                    {
                        double diff = Math.Abs(maxValue - boundary);
                        if (diff < minDiff)
                        {
                            minDiff = diff;
                            nearestBoundary = boundary;
                        }
                    }
                    
                    // Use the nearest boundary for comparison
                    // If we're testing for "below X", use the boundary at or above X
                    // so we include all items in that group
                    if (percentile > nearestBoundary && maxValue < nearestBoundary)
                        inRange = false;
                    else if (percentile > maxValue && (nearestBoundary <= maxValue || minDiff > 5.0))
                        inRange = false;
                }
                else if (percentile > maxValue)
                {
                    inRange = false;
                }
            }
            
            if (f.Outside)
            {
                if (inRange) { ok = false; break; }
            }
            else
            {
                if (!inRange) { ok = false; break; }
            }
        }
        return ok;
    }

    static void PrintEntry((string ngram, int count, double ppm, double z, double pmi) it, CommandOptions options, string tag)
    {
        // Build the row using the granular Show* flags (Mode is kept in sync by presets,
        // but explicit --show-x/--hide-x flags always win since they're applied after presets).
        bool showPmiForThisRow = options.ShowPmi && tag != "1"; // PMI meaningless for unigrams
        var sb = new StringBuilder();
        if (options.ShowCount) sb.Append($"{it.count,-7} ");
        if (options.ShowPpm) sb.Append($"{it.ppm,-7:F0}  ");
        if (options.ShowZ) sb.Append($"{it.z,-5:F1}  ");
        if (showPmiForThisRow) sb.Append($"{it.pmi,-6:F2} ");

        if (sb.Length == 0)
        {
            // Bare phrase only (e.g. "--" minimal preset)
            Console.WriteLine(it.ngram);
            return;
        }

        if (options.ShowCount && !options.ShowPpm && !options.ShowZ && !showPmiForThisRow)
        {
            // Default style: "count: ngram"
            Console.WriteLine($"{it.count}: {it.ngram}");
            return;
        }

        Console.WriteLine($"{sb}{it.ngram}");
    }

    // Drops an n-gram only if EVERY word in it is a stopword — mixed phrases like
    // "the guard" or "state of the art" survive since they carry real content; only
    // fully-function-word phrases like "to the" or "Do not" are filtered. Splits on the
    // same separator the ngram was joined with (space for prose, "/" for path-segments).
    // An empty EffectiveStopWords set (via --no-stop-words with no re-adds) naturally means
    // "filter off" — no phrase can ever be all-stopwords against an empty set.
    static bool PassStopwordFilter(string ngram, CommandOptions options, string separator = " ")
    {
        if (options.EffectiveStopWords.Count == 0) return true;
        var words = ngram.Split(new[] { separator }, StringSplitOptions.None);
        foreach (var w in words)
        {
            if (!options.EffectiveStopWords.Contains(w)) return true; // at least one real word
        }
        return false; // every word was a stopword
    }

    // Drops an n-gram if its FIRST OR LAST word is a trim word — a stricter, position-
    // sensitive sibling of PassStopwordFilter (see TrimWords.cs for why this is a genuinely
    // different, smaller set rather than a reuse of StopWords). Kills glue phrases like
    // "for the" / "the same" / "its own" (edge word carries no content) while preserving
    // signal phrases like "not yet resolved" / "does not decide" (their edge words — "not",
    // "does" — are deliberately excluded from TrimWords.Default even though they ARE
    // stopwords). Single-word n-grams (n=1) are exempt: there's no separate "edge" to test.
    // An empty EffectiveTrimWords set (via --no-trim-words with no re-adds) means "filter off".
    static bool PassTrimFilter(string ngram, CommandOptions options, string separator = " ")
    {
        if (options.EffectiveTrimWords.Count == 0) return true;
        var words = ngram.Split(new[] { separator }, StringSplitOptions.None);
        if (words.Length < 2) return true; // nothing to "trim" on a single token
        var first = words[0];
        var last = words[words.Length - 1];
        if (options.EffectiveTrimWords.Contains(first)) return false;
        if (options.EffectiveTrimWords.Contains(last)) return false;
        return true;
    }


    // Pre-tokenization corpus filter: decides whether a whole SOURCE LINE enters the
    // token pool at all, BEFORE any n-grams are built from it. Distinct pipeline stage
    // from PassTextFilters (which filters already-built n-gram PHRASE results after the
    // fact) — see --line-contains/--remove-line-contains in --help. Reuses the same
    // TextFilter.Contains/NotContains matching semantics against the whole line string.
    // Also supports StartsWith/EndsWith (ngc-feedback.md #1) for --line-starts-with/
    // --line-ends-with — named sugar over the same "match the whole raw line" mechanism
    // as --line-contains, for the common "isolate this kind of line" case (e.g. Markdown
    // ATX headings via --line-starts-with "^#{1,6}\s") without needing to hand-write a
    // regex anchor onto --line-contains yourself.
    static bool PassLineFilters(string line, List<TextFilter> filters, bool ignoreCase = false)
    {
        if (filters.Count == 0) return true;
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var f in filters)
        {
            if (f.CompiledRegex != null)
            {
                bool match = f.CompiledRegex.IsMatch(line);
                if (f.Type == TextFilter.TypeEnum.Contains && !match) return false;
                if (f.Type == TextFilter.TypeEnum.NotContains && match) return false;
                if (f.Type == TextFilter.TypeEnum.StartsWith && !match) return false;
                if (f.Type == TextFilter.TypeEnum.NotStartsWith && match) return false;
                if (f.Type == TextFilter.TypeEnum.EndsWith && !match) return false;
                if (f.Type == TextFilter.TypeEnum.NotEndsWith && match) return false;
                continue;
            }
            switch (f.Type)
            {
                case TextFilter.TypeEnum.Contains:
                    if (line.IndexOf(f.Pattern, cmp) < 0) return false;
                    break;
                case TextFilter.TypeEnum.NotContains:
                    if (line.IndexOf(f.Pattern, cmp) >= 0) return false;
                    break;
                case TextFilter.TypeEnum.StartsWith:
                    if (!line.StartsWith(f.Pattern, cmp)) return false;
                    break;
                case TextFilter.TypeEnum.NotStartsWith:
                    if (line.StartsWith(f.Pattern, cmp)) return false;
                    break;
                case TextFilter.TypeEnum.EndsWith:
                    if (!line.EndsWith(f.Pattern, cmp)) return false;
                    break;
                case TextFilter.TypeEnum.NotEndsWith:
                    if (line.EndsWith(f.Pattern, cmp)) return false;
                    break;
            }
        }
        return true;
    }

    static bool PassTextFilters(string ngram, List<TextFilter> filters, bool ignoreCase = false)    {
        // Case sensitivity for the regex path is already baked into how the regex was
        // compiled (see TryCompileRegex's ignoreCase param at parse time), so we match the
        // raw ngram directly here — no pre-lowercasing, which would corrupt case-sensitive
        // patterns like "[A-Z][a-z]+[A-Z]" by mangling the case of the text being matched.
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var f in filters)
        {
            // If we have a compiled regex, use it for Contains and NotContains filters
            if (f.CompiledRegex != null)
            {
                bool match = f.CompiledRegex.IsMatch(ngram);
                
                switch (f.Type)
                {
                    case TextFilter.TypeEnum.Contains:
                        if (!match) return false;
                        break;
                    case TextFilter.TypeEnum.NotContains:
                        if (match) return false;
                        break;
                    // For other types, continue to use string methods below
                    default:
                        // Fall through to standard string processing
                        break;
                }
                
                // If we used regex for Contains or NotContains, continue to next filter
                if (f.Type == TextFilter.TypeEnum.Contains || f.Type == TextFilter.TypeEnum.NotContains)
                    continue;
            }
            
            // For non-regex patterns or other filter types, use the existing string methods
            switch (f.Type)
            {
                case TextFilter.TypeEnum.Contains:
                    if (ngram.IndexOf(f.Pattern, cmp) < 0) return false;
                    break;
                case TextFilter.TypeEnum.NotContains:
                    if (ngram.IndexOf(f.Pattern, cmp) >= 0) return false;
                    break;
                case TextFilter.TypeEnum.StartsWith:
                    if (!ngram.StartsWith(f.Pattern, cmp)) return false;
                    break;
                case TextFilter.TypeEnum.NotStartsWith:
                    if (ngram.StartsWith(f.Pattern, cmp)) return false;
                    break;
                case TextFilter.TypeEnum.EndsWith:
                    if (!ngram.EndsWith(f.Pattern, cmp)) return false;
                    break;
                case TextFilter.TypeEnum.NotEndsWith:
                    if (ngram.EndsWith(f.Pattern, cmp)) return false;
                    break;
            }
        }
        return true;
    }

    static bool PassFrequencyFilters(int count, List<FrequencyFilter> filters)
    {
        if (filters.Count == 0) return true;
        bool ok = true;
        foreach (var f in filters)
        {
            bool inRange = true;
            if (f.Min.HasValue && count < f.Min.Value) inRange = false;
            if (f.Max.HasValue && count > f.Max.Value) inRange = false;
            if (f.Outside)
            {
                if (inRange) { ok = false; break; }
            }
            else
            {
                if (!inRange) { ok = false; break; }
            }
        }
        return ok;
    }

    static bool PassPpmFilters(double ppm, List<PpmFilter> filters)
    {
        if (filters.Count == 0) return true;
        bool ok = true;
        foreach (var f in filters)
        {
            bool inRange = true;
            if (f.Min.HasValue && ppm < f.Min.Value) inRange = false;
            if (f.Max.HasValue && ppm > f.Max.Value) inRange = false;
            if (f.Outside)
            {
                if (inRange) { ok = false; break; }
            }
            else
            {
                if (!inRange) { ok = false; break; }
            }
        }
        return ok;
    }

    static bool PassZFilters(double z, List<ZFilter> filters)
    {
        if (filters.Count == 0) return true;
        bool ok = true;
        foreach (var f in filters)
        {
            bool inRange = true;
            if (f.Min.HasValue && z < f.Min.Value) inRange = false;
            if (f.Max.HasValue && z > f.Max.Value) inRange = false;
            if (f.Outside)
            {
                if (inRange) { ok = false; break; }
            }
            else
            {
                if (!inRange) { ok = false; break; }
            }
        }
        return ok;
    }

    static bool PassPmiFilters(double pmi, List<PmiFilter> filters)
    {
        if (filters.Count == 0) return true;
        bool ok = true;
        foreach (var f in filters)
        {
            bool inRange = true;
            if (f.Min.HasValue && pmi < f.Min.Value) inRange = false;
            if (f.Max.HasValue && pmi > f.Max.Value) inRange = false;
            if (f.Outside)
            {
                if (inRange) { ok = false; break; }
            }
            else
            {
                if (!inRange) { ok = false; break; }
            }
        }
        return ok;
    }

    static bool PassTfidfFilters(double tfidf, List<TfidfFilter> filters)
    {
        if (filters.Count == 0) return true;
        bool ok = true;
        foreach (var f in filters)
        {
            bool inRange = true;
            if (f.Min.HasValue && tfidf < f.Min.Value) inRange = false;
            if (f.Max.HasValue && tfidf > f.Max.Value) inRange = false;
            if (f.Outside)
            {
                if (inRange) { ok = false; break; }
            }
            else
            {
                if (!inRange) { ok = false; break; }
            }
        }
        return ok;
    }

    // Feedback item #2 (CDRs/ngc-feedback.md Round 3): a legitimately empty, filtered-down-to-
    // zero result set currently looks identical (an all-zero stats header) to a query that
    // silently no-op'd due to a bad regex/anchor. Print a visibly distinct notice so users can
    // trust a real zero-match result instead of re-checking their filters every time.
    static void PrintNoMatchesNotice()
    {
        Console.WriteLine("⚠ No matches found for these filters.");
    }

    static CommandOptions ParseArgs(string[] args)
    {
        var options = new CommandOptions();

        // --no-stop-words / --no-trim-words reset their respective set to empty as the
        // STARTING BASIS, before any --stop-words/--trim-words additions are applied below.
        // This pre-scan makes the two flags order-independent: "--stop-words x --no-stop-words"
        // and "--no-stop-words --stop-words x" both end up with EffectiveStopWords == {x},
        // regardless of which one appears first on the command line, because the reset
        // always happens before the (order-preserving) additive pass in the main loop.
        if (args.Contains("--no-stop-words")) options.EffectiveStopWords.Clear();
        if (args.Contains("--no-trim-words")) options.EffectiveTrimWords.Clear();
        // Same order-independent reset pattern for the three new tokenizer-layer sets
        // (ngc-feedback.md #4): a --no-X anywhere on the command line clears the default
        // basis to empty BEFORE any --X additions (parsed in the main loop below) are added.
        if (args.Contains("--no-pair-chars")) options.EffectivePairChars.Clear();
        if (args.Contains("--no-keep-symbols")) options.EffectiveKeepSymbols.Clear();
        if (args.Contains("--no-trim-symbols")) options.EffectiveTrimSymbols.Clear();
        // --ignore-case must be known before any --contains/--starts/--ends/etc. are parsed
        // below, since it's baked into the compiled Regex at parse time (RegexOptions.IgnoreCase)
        // — same order-independence concern as the two lines above.
        if (args.Contains("--ignore-case")) options.IgnoreCase = true;
        // --max-items / --max-items-per-file / --top-files all now scale with the
        // --less--/--less/(default)/--more/--more++/--more+++ verbosity ladder
        // (ngc-feedback.md #4 and #6) instead of one flat number. Same
        // order-independence concern as above: detect explicit use of these three
        // flags BEFORE the main loop runs, so an explicit value always wins no matter
        // which side of a preset flag it appears on.
        if (args.Contains("--max-items")) options.MaxItemsSetExplicitly = true;
        if (args.Contains("--max-items-per-file")) options.MaxItemsPerFileSetExplicitly = true;
        if (args.Contains("--top-files")) options.TopFilesSetExplicitly = true;

        // Used by --files parsing: decides where the glob list for --files ends,
        // i.e. the next recognized ngc token (n-gram size, filter prefix, keyword,
        // or anything starting with "-"/"--"). Everything before that is a glob.
        bool IsLikelyOptionToken(string tok)
        {
            if (tok.StartsWith("-")) return true; // "--show-x", "--contains", "--less", "--more++", etc.
            if (Regex.IsMatch(tok, @"^\d+$")) return true;                 // n-gram size
            if (Regex.IsMatch(tok, @"^\d+\.\.\d+$")) return true;          // n-gram range
            if (tok.Contains(",") && Regex.IsMatch(tok, @"^[\d,]+$")) return true; // n-gram list
            return false;
        }

        // Distinguishes an actual file glob ("*.md", "src/**/*.cs", "notes.txt")
        // from a content-filter pattern that happens to appear right after --files's
        // glob list (e.g. "obsess|relentless", "word..", "!..word"). Globs are
        // recognized by wildcard chars, path separators, or a trailing real file
        // extension — NOT by a trailing/leading ".." (that's the text-filter
        // startswith/endswith marker, not a file extension).
        bool LooksLikeFileGlob(string tok)
        {
            // "../" or "..\" is a real relative-path traversal prefix (e.g. "../src/**/*.cs"),
            // not the text-filter startswith/endswith ".." marker — only treat a bare/leading
            // ".." as the text-filter marker when it's NOT immediately followed by a path
            // separator (that distinguishes "..word" from "../word" or "..\word").
            bool startsWithDotDotMarker = tok.StartsWith("..") && !(tok.Length > 2 && (tok[2] == '/' || tok[2] == '\\'));
            bool endsWithDotDotMarker = tok.EndsWith("..");
            if (startsWithDotDotMarker || endsWithDotDotMarker) return false; // text-filter marker, not a glob
            if (tok.IndexOfAny(new[] { '*', '?', '/', '\\' }) >= 0) return true;
            if (Regex.IsMatch(tok, @"\.[A-Za-z0-9]{1,8}$")) return true;   // trailing "real" extension, e.g. .md/.txt/.cs
            return false;
        }

        // Check for help flag
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            // Check if input is being piped (no args but input is redirected), or --files given
            if (args.Length == 0 && Console.IsInputRedirected)
            {
                // Default if no args provided but input is piped
                options.NGramSizes = new List<int> { 1, 2, 3 };
                options.ShowMergedNGrams = true; 
                options.ShowNGrams = true;    
                options.Sort = SortDirection.Asc;
                options.StatsOnly = true; // Only show statistics, not the phrases
                return options;
            }
            
            Console.WriteLine("NGramCounter (ngc) v2.0 - N-gram frequency counter for piped text or files");
            
            Console.WriteLine("\nWHAT NGC DOES:");
            Console.WriteLine("  - Counts n-gram frequencies in YOUR piped input (or --files)");
            Console.WriteLine("  - Calculates descriptive statistics (mean, std dev, PDF, CDF) OF YOUR INPUT");
            Console.WriteLine("  - Detects collocations (PMI) and, with --files, distinctive terms (TF-IDF)");
            Console.WriteLine("  - Filters, sorts, and reports on these measures");
            Console.WriteLine();
            Console.WriteLine("WHAT NGC DOES NOT DO:");
            Console.WriteLine("  - Does NOT determine if patterns are important or meaningful");
            Console.WriteLine("  - Does NOT tell you if patterns generalize beyond your input");
            Console.WriteLine("  - Does NOT perform statistical hypothesis testing or significance testing");
            Console.WriteLine("  - Does NOT establish causation or explain WHY patterns occur");
            Console.WriteLine();
            Console.WriteLine("⚠️  HASTY GENERALIZATION WARNING:");
            Console.WriteLine("  Small samples → weak conclusions. A pattern in ONE file/repo does NOT prove:");
            Console.WriteLine("    • It's common across the domain");
            Console.WriteLine("    • It's important or essential");
            Console.WriteLine("    • It generalizes to other languages/contexts");
            Console.WriteLine("  Document your sample size and scope. Validate across diverse sources.");
            Console.WriteLine();
            Console.WriteLine("INTERPRET WITH CAUTION: Frequency ≠ Importance, Sample ≠ Population");
            
            Console.WriteLine("\nQUICK START:");
            Console.WriteLine("  ngc 1 --top 30 --desc                    # Most frequent terms, highest to lowest");
            Console.WriteLine("  ngc 2..3 --contains \"pattern\" --desc     # 2-3 word phrases containing \"pattern\"");
            Console.WriteLine("  ngc 1 --cdf 5 --more+++                  # Top/bottom 5% by frequency, with metrics");
            Console.WriteLine("  ngc 1 --show-pdf                         # Frequency histogram (how many words occur N times)");
            Console.WriteLine("  ngc 1 --show-cdf                         # Percentile ladder (cumulative distribution)");
            Console.WriteLine("  ngc 2 --show-pmi --desc --top 20         # Bigrams glued together more than chance predicts");
            Console.WriteLine("  ngc 1 --files \"*.md\" --show-tfidf --desc # Distinctive terms across many files");
            
            Console.WriteLine("\nINPUT SOURCE:");
            Console.WriteLine("  (stdin)                  # Default: read piped text as one combined blob");
            Console.WriteLine("  --files glob [glob ...]  # Read one or more files/globs instead of stdin");
            Console.WriteLine("                           # Each matched file becomes its own \"document\" —");
            Console.WriteLine("                           # required for --show-tfidf and --per-file.");
            Console.WriteLine("                           # Everything else (PDF/CDF/PMI/etc.) still works,");
            Console.WriteLine("                           # computed across the combined token pool.");
            Console.WriteLine("  Examples:");
            Console.WriteLine("    ngc 1 --files \"*.md\" --top 20 --desc");
            Console.WriteLine("    ngc 2 --files \"src/**/*.cs\" \"docs/**/*.md\" --desc");
            Console.WriteLine("    ngc 1 --files \"../other-repo/**/*.cs\" --top 20 --desc   # relative ../ globs work");
            Console.WriteLine("  Notes:");
            Console.WriteLine("    - No --files and no piped stdin = immediate error (not a hang).");
            Console.WriteLine("    - `--files \"*.md\"` (no --files at all, or a glob with 0 matches) errors immediately too.");
            
            Console.WriteLine("\nN-GRAM SIZE:");
            Console.WriteLine("  3         # Only trigrams (3-word phrases)");
            Console.WriteLine("  2..4      # 2, 3, and 4-grams (analyze multiple sizes)");
            Console.WriteLine("  1,3,5     # 1, 3, and 5-grams only (specific sizes)");
            
            Console.WriteLine("\nCONTENT FILTERS (supports regex) — explicit flags ONLY, no bare/implicit");
            Console.WriteLine("syntax. This is deliberate: an unrecognized/mistyped token is now always");
            Console.WriteLine("an error, never silently absorbed as a no-op filter.");
            Console.WriteLine("CASE-SENSITIVE BY DEFAULT (e.g. --contains \"class [A-Z]\" only matches real");
            Console.WriteLine("capitalized identifiers). Pass --ignore-case for case-insensitive prose search.");
            Console.WriteLine();
            Console.WriteLine("TWO DIFFERENT FILTER STAGES — don't confuse them:");
            Console.WriteLine("  --contains/--starts/--ends (and --phrase-contains, an explicit alias for");
            Console.WriteLine("  --contains) match against the ASSEMBLED N-GRAM PHRASE — i.e. AFTER lines are");
            Console.WriteLine("  tokenized and n-grams are built/counted. `^`/`$` anchor to that phrase, NOT");
            Console.WriteLine("  to the original source line — `--contains \"^Guard\"` matches n-grams whose");
            Console.WriteLine("  FIRST WORD is \"Guard\", not lines containing a \"## Guard\" heading.");
            Console.WriteLine("  --line-contains/--remove-line-contains match against the whole RAW SOURCE");
            Console.WriteLine("  LINE, BEFORE tokenization — use this to restrict which lines even enter the");
            Console.WriteLine("  n-gram pool at all (e.g. only lines mentioning \"Status\").");
            Console.WriteLine();
            Console.WriteLine("TOKENIZATION NOTE: Markdown syntax characters (`#`, backticks, etc.) are NOT");
            Console.WriteLine("preserved as part of any word/phrase — a line like \"## Guard\" tokenizes to");
            Console.WriteLine("the bare word \"Guard\" (the \"##\" is discarded), so `--contains \"## Guard\"`");
            Console.WriteLine("will never match anything; use `--contains \"^Guard$\"` (n=1) instead, or");
            Console.WriteLine("`--line-contains \"## Guard\"` to filter on the literal raw line text.");
            Console.WriteLine();
            Console.WriteLine("TRICK — finding Markdown HEADINGS specifically (not just any line mentioning");
            Console.WriteLine("a word): --line-contains is regex and matches the whole RAW line, so you can");
            Console.WriteLine("anchor it to the ATX heading marker itself, e.g.:");
            Console.WriteLine("    --line-contains \"^#{1,6}\\s\"              # any heading line, any level");
            Console.WriteLine("    --line-starts-with \"## Guard\"            # only \"## Guard\" headings specifically");
            Console.WriteLine("This distinguishes a real `## Guard` SECTION HEADING from ordinary prose that");
            Console.WriteLine("merely mentions the word \"Guard\" somewhere in a line — something plain");
            Console.WriteLine("`--contains \"^Guard$\"` (which matches the tokenized PHRASE, not the raw line)");
            Console.WriteLine("cannot distinguish, since tokenization strips the leading \"#\"s either way.");
            Console.WriteLine();
            Console.WriteLine("APOSTROPHE NOTE: possessives and contractions stay glued as ONE token —");
            Console.WriteLine("\"Android's\", \"don't\", \"isn't\" tokenize whole, not as \"Android\"+\"s\" or");
            Console.WriteLine("\"don\"+\"t\". Genuine single-quoted spans ('like this') are still detected and");
            Console.WriteLine("kept as one atomic unit; the difference is a letter/digit immediately touching");
            Console.WriteLine("the quote mark on at least one side (a possessive/contraction), vs. whitespace/");
            Console.WriteLine("punctuation on both sides (a real quoted phrase).");
            Console.WriteLine("    --contains \"pattern\"        # Include phrases containing \"pattern\"");
            Console.WriteLine("    --remove-contains \"pattern\" # Exclude phrases containing \"pattern\"");
            Console.WriteLine("    --phrase-contains \"pattern\" # Explicit alias for --contains (same thing)");
            Console.WriteLine("    --starts \"pattern\"          # Include phrases starting with \"pattern\"");
            Console.WriteLine("    --remove-starts \"pattern\"   # Exclude phrases starting with \"pattern\"");
            Console.WriteLine("    --ends \"pattern\"            # Include phrases ending with \"pattern\"");
            Console.WriteLine("    --remove-ends \"pattern\"     # Exclude phrases ending with \"pattern\"");
            Console.WriteLine("    --line-contains \"pattern\"        # Only tokenize lines containing \"pattern\"");
            Console.WriteLine("    --remove-line-contains \"pattern\" # Skip tokenizing lines containing \"pattern\"");
            Console.WriteLine("    --line-starts-with \"pattern\"     # Only tokenize lines starting with \"pattern\"");
            Console.WriteLine("    --remove-line-starts-with \"pattern\" # Skip lines starting with \"pattern\"");
            Console.WriteLine("    --line-ends-with \"pattern\"       # Only tokenize lines ending with \"pattern\"");
            Console.WriteLine("    --remove-line-ends-with \"pattern\"   # Skip lines ending with \"pattern\"");
            Console.WriteLine("    --exclude-file file.txt     # Exclude phrases containing any term listed in file");
            Console.WriteLine("    --ignore-case               # Make ALL of the above case-insensitive");
            
            Console.WriteLine("\nSTOPWORDS — dropped if EVERY word in the phrase is a stopword (on by default).");
            Console.WriteLine("Mixed phrases like \"the guard\" survive (real content present); only");
            Console.WriteLine("all-function-word phrases like \"to the\" or \"is not\" are filtered:");
            Console.WriteLine("    --no-stop-words              # Reset the stopword set to EMPTY (filter off)");
            Console.WriteLine("    --stop-words L1 md cycod    # ADD words to the CURRENT set (defaults, or");
            Console.WriteLine("                                 # empty if --no-stop-words also given — order");
            Console.WriteLine("                                 # on the command line doesn't matter)");
            Console.WriteLine("    (--stop is a short alias for --stop-words)");
            Console.WriteLine("    e.g. --no-stop-words --stop-words foo bar   => stopword set is JUST {foo, bar}");
            Console.WriteLine("");
            Console.WriteLine("TRIM WORDS — dropped if the FIRST OR LAST word is a trim word (on by default).");
            Console.WriteLine("A stricter, position-sensitive sibling of stopwords: kills glue phrases like");
            Console.WriteLine("\"for the\" / \"the same\" / \"its own\" (edge word carries no content), while");
            Console.WriteLine("preserving signal phrases like \"not yet resolved\" / \"does not decide\" (\"not\"/");
            Console.WriteLine("\"does\" are real stopwords but deliberately EXCLUDED from the trim-word set,");
            Console.WriteLine("since negation/modals/wh-words routinely ARE the content at a phrase edge):");
            Console.WriteLine("    --no-trim-words              # Reset the trim-word set to EMPTY (filter off)");
            Console.WriteLine("    --trim-words foo bar         # ADD words to the CURRENT set, same semantics");
            Console.WriteLine("                                 # as --stop-words above");
            Console.WriteLine("    (--trim is a short alias for --trim-words)");

            Console.WriteLine("\nTOKENIZER LAYERS — three lower-level, independently controllable stages that");
            Console.WriteLine("run BEFORE stopwords/trim-words above, turning raw text into words/phrases at");
            Console.WriteLine("all (see ngc-feedback.md #4 for the full design rationale):");
            Console.WriteLine("");
            Console.WriteLine("  Layer 1 — PAIR-CHARS: protected atomic spans, immune to all later splitting.");
            Console.WriteLine("  Defaults: \"..\" (double-quote), ''..'' (single-quote/apostrophe-aware — see");
            Console.WriteLine("  APOSTROPHE NOTE above), and `..` (backtick code-spans — protects `RemoteApi`");
            Console.WriteLine("  as one atomic display token, so a following possessive like `RemoteApi`'s");
            Console.WriteLine("  doesn't leave a stray orphaned \"'s\" token with nothing left to glue to).");
            Console.WriteLine("  Path-shape detection (\"word/word/word\") stays a separate, shape-based");
            Console.WriteLine("  detector, not a pair-chars entry — it isn't a delimiter pair, it's a shape.");
            Console.WriteLine("    --no-pair-chars          # Reset to EMPTY (disables ALL pair-protection,");
            Console.WriteLine("                             # including the 3 defaults above)");
            Console.WriteLine("    --pair-chars \"=;\" \"()\"   # ADD open/close delimiter pairs on top of the");
            Console.WriteLine("                             # current basis — e.g. \"=;\" protects everything");
            Console.WriteLine("                             # from = to the next ; as one atomic span");
            Console.WriteLine("                             # (config-value-style spans); each pair must be");
            Console.WriteLine("                             # exactly 2 characters (open then close; open ==");
            Console.WriteLine("                             # close is fine, e.g. \"==\")");
            Console.WriteLine("");
            Console.WriteLine("  Layer 2 — KEEP-SYMBOLS: characters that glue to their word instead of hard-");
            Console.WriteLine("  splitting it. Defaults: '-' (hyphenated compounds: \"re-run\", \"multi-device\")");
            Console.WriteLine("  and ''' (possessives/contractions: \"Android's\", \"don't\", \"isn't\").");
            Console.WriteLine("    --no-keep-symbols        # Reset to EMPTY (every non-alnum char splits)");
            Console.WriteLine("    --keep-symbols \"._\"      # ADD characters (each char in each arg is added");
            Console.WriteLine("                             # individually) to the CURRENT glue-set");
            Console.WriteLine("");
            Console.WriteLine("  Layer 3 — TRIM-SYMBOLS: strip leading/trailing characters from each already-");
            Console.WriteLine("  formed word (edge-only, never mid-token). EMPTY by default — Layer 2 already");
            Console.WriteLine("  can't leave a non-keep character stuck to a word's edge, so this only matters");
            Console.WriteLine("  once you opt extra punctuation INTO Layer 2 via --keep-symbols and want it");
            Console.WriteLine("  excluded from just the edges, e.g. --keep-symbols \".\" --trim-symbols \".\" keeps");
            Console.WriteLine("  interior dots (\"U.S.\") but still drops a trailing sentence-final dot.");
            Console.WriteLine("    --no-trim-symbols        # Reset to EMPTY (no-op vs. the already-empty");
            Console.WriteLine("                             # default; kept for symmetry/discoverability)");
            Console.WriteLine("    --trim-symbols \".,;\"     # ADD characters to the CURRENT edge-trim set");
            
            Console.WriteLine("\nFREQUENCY FILTERS:");
            Console.WriteLine("  --freq 10+     # Frequency ≥ 10 occurrences");
            Console.WriteLine("  --freq 5..20   # Between 5 and 20 occurrences");
            Console.WriteLine("  --freq ..20    # Frequency ≤ 20 occurrences");
            Console.WriteLine("  --freq !10+    # Less than 10 occurrences");
            Console.WriteLine("  --freq !5..20  # Outside the range 5-20 occurrences");
            Console.WriteLine("  --freq 10      # Exactly 10 occurrences");
            
            Console.WriteLine("\nDESCRIPTIVE FILTERS (apply to YOUR input only):");
            Console.WriteLine("  --cdf 90+      # Top 10% most frequent items (was 'percentile:', renamed)");
            Console.WriteLine("  --cdf ..50     # Bottom 50% of items");
            Console.WriteLine("  --cdf 25..75   # Middle 50% of items (interquartile range)");
            Console.WriteLine("  --cdf !25..75  # Outside the middle range (potential outliers)");
            Console.WriteLine("  --cdf 5        # Top/bottom 5% by frequency in YOUR input (not 'outliers')");
            Console.WriteLine("  ");
            Console.WriteLine("  --ppm 1000+        # At least 1000 occurrences per million tokens");
            Console.WriteLine("  --ppm 500..1000    # Between 500-1000 occurrences per million");
            Console.WriteLine("  --ppm ..100        # At most 100 occurrences per million");
            Console.WriteLine("  ");
            Console.WriteLine("  --z 2              # Within 2 standard deviations of mean (typical)");
            Console.WriteLine("  --z !2             # Outside 2 standard deviations (unusual)");
            Console.WriteLine("  ");
            Console.WriteLine("  --pmi 2+           # Pointwise Mutual Information ≥ 2 (occurs ≥4x more than");
            Console.WriteLine("                     # chance predicts from the words' own frequencies — a real");
            Console.WriteLine("                     # \"glued together\" collocation, e.g. \"customer obsession\")");
            Console.WriteLine("  --pmi !0           # PMI below 0 (occurs LESS than chance — anti-collocation)");
            Console.WriteLine("                     # Only meaningful for n-grams with n >= 2.");
            Console.WriteLine("  ");
            Console.WriteLine("  --tfidf 20+        # TF-IDF score ≥ 20 (requires --files + --show-tfidf)");
            Console.WriteLine("  --tfidf 5..50      # TF-IDF between 5 and 50");
            Console.WriteLine("  --tfidf ..10       # TF-IDF ≤ 10 (common-everywhere terms, low distinctiveness)");
            
            Console.WriteLine("\nSHOW / HIDE FLAGS (fine-grained control over report sections & columns):");
            Console.WriteLine("  Report sections:");
            Console.WriteLine("    --show-input / --hide-input                # Chars/Lines/Words block");
            Console.WriteLine("    --show-section-header / --hide-section-header  # '## N-grams Results'");
            Console.WriteLine("    --show-summary / --hide-summary            # unique/filtered/retained %");
            Console.WriteLine("    --show-freq-stats / --hide-freq-stats      # min/max/median/avg/90%<");
            Console.WriteLine("    --show-ppm-stats / --hide-ppm-stats");
            Console.WriteLine("    --show-headers / --hide-headers");
            Console.WriteLine("    --show-phrases / --hide-phrases            # the ngram list body");
            Console.WriteLine("    --show-tfidf-phrases / --hide-tfidf-phrases # the TF-IDF table body (separate)");
            Console.WriteLine("    --show-ngrams / --hide-ngrams               # the n-grams report itself (per-size)");
            Console.WriteLine("    --show-merged-ngrams / --hide-merged-ngrams # combined-across-sizes report");
            Console.WriteLine("    (--show-n-grams / --show-merged-n-grams are accepted aliases for both)");
            Console.WriteLine("  Per-item columns:");
            Console.WriteLine("    --show-count / --hide-count");
            Console.WriteLine("    --show-ppm / --hide-ppm");
            Console.WriteLine("    --show-z / --hide-z");
            Console.WriteLine("  New reports (see REPORTS section below):");
            Console.WriteLine("    --show-pdf / --hide-pdf");
            Console.WriteLine("    --show-cdf / --hide-cdf");
            Console.WriteLine("    --show-pmi / --hide-pmi     # adds a PMI column to phrase rows");
            Console.WriteLine("    --show-tfidf / --hide-tfidf # requires --files");
            Console.WriteLine("    --show-path-ngrams / --hide-path-ngrams # separate report, path-like units only");
            Console.WriteLine("    --show-dispersion / --hide-dispersion   # Juilland's D / DP, requires --files");
            Console.WriteLine("    --show-logdice / --hide-logdice         # alt collocation measure vs. PMI");
            Console.WriteLine("    --show-tscore / --hide-tscore           # alt collocation measure vs. PMI");
            Console.WriteLine("    --show-mi3 / --hide-mi3                 # alt collocation measure vs. PMI");
            Console.WriteLine("    --show-readability / --hide-readability # TTR/MTLD summary, per file + corpus");
            Console.WriteLine("  ");
            Console.WriteLine("  IMPORTANT — report selection: --show-ngrams is ON by default. The moment you");
            Console.WriteLine("  pass any OTHER \"big\" --show-X report flag (--show-pdf/--show-cdf/--show-tfidf/");
            Console.WriteLine("  --show-path-ngrams/--show-dispersion/--show-logdice/--show-tscore/--show-mi3/");
            Console.WriteLine("  --show-readability), the n-grams report is no longer shown automatically — add");
            Console.WriteLine("  --show-ngrams explicitly if you still want it alongside the new report. The one");
            Console.WriteLine("  exception is --show-pmi: it's a COLUMN on the n-grams report, not a report of");
            Console.WriteLine("  its own, so using it still implies --show-ngrams (nothing else for it to attach");
            Console.WriteLine("  to). An explicit --show-ngrams/--hide-ngrams always wins outright, either way.");
            Console.WriteLine("  ");
            Console.WriteLine("  Explicit flags always override whatever a preset (--more/--more++/--more+++/--less/--less--) set,");
            Console.WriteLine("  regardless of order, e.g.:  ngc 1 --more+++ --hide-z   (detailed, but no Z column)");
            
            Console.WriteLine("\nREPORTS (new views beyond the phrase list):");
            Console.WriteLine("  --show-pdf   # Frequency histogram: how many distinct n-grams occur exactly");
            Console.WriteLine("               # N times (auto log-scale bucketed: 1, 2, 3, 4-7, 8-15, ...).");
            Console.WriteLine("               # This is the classic long-tail / Zipf's-law shape of language.");
            Console.WriteLine("  ");
            Console.WriteLine("  --show-cdf   # Percentile ladder: frequency value at 0/10/25/50/75/90/95/");
            Console.WriteLine("               # 99/99.9/100th percentile. Shows how quickly frequency mass");
            Console.WriteLine("               # concentrates in a small number of common n-grams.");
            Console.WriteLine("  ");
            Console.WriteLine("  --show-pmi   # Adds a PMI column per n-gram row: log2(observed / expected),");
            Console.WriteLine("               # where 'expected' comes from the n-gram's own component word");
            Console.WriteLine("               # frequencies. High PMI = a real fixed phrase, not coincidence.");
            Console.WriteLine("               # (For n=1 this is meaningless and is skipped.)");
            Console.WriteLine("  ");
            Console.WriteLine("  --show-tfidf # Requires --files. One row per n-gram: COUNT, DF (# files it");
            Console.WriteLine("               # appears in), IDF, TF-IDF (max across files), BEST-FILE (where");
            Console.WriteLine("               # it's most concentrated). High TF-IDF = distinctive of a FEW");
            Console.WriteLine("               # documents, not just generically common across ALL of them.");
            Console.WriteLine("               # IDF uses smoothed formula: log(N / (1+DF)) + 1");
            Console.WriteLine("  ");
            Console.WriteLine("  --per-file   # Modifier: instead of one aggregate table, break the report");
            Console.WriteLine("               # (currently --show-tfidf) into one table per matched file —");
            Console.WriteLine("               # that file's own top distinctive terms. Requires --files.");
            Console.WriteLine("  ");
            Console.WriteLine("  --top-files N       # With --per-file: only show breakdown tables for the N");
            Console.WriteLine("                      # most distinctive files (ranked by an aggregate TF-IDF");
            Console.WriteLine("                      # score — see --top-files-by), not all matched files.");
            Console.WriteLine("                      # --top-files 0 = unlimited. Default is 20, and — like");
            Console.WriteLine("                      # --max-items/--max-items-per-file below — SCALES with");
            Console.WriteLine("                      # the --less/--more verbosity ladder (see OUTPUT OPTIONS):");
            Console.WriteLine("                      #   --less--=5  --less=10  (default)=20  --more=50");
            Console.WriteLine("                      #   --more++=100  --more+++=0 (unlimited)");
            Console.WriteLine("                      # An explicit --top-files N always overrides the active");
            Console.WriteLine("                      # preset, regardless of which one comes first on the line.");
            Console.WriteLine("  --top-files-by MODE # How to rank each file's distinctiveness for --top-files:");
            Console.WriteLine("                      #   avg-top5 (default) - avg of the file's top 5 TF-IDF");
            Console.WriteLine("                      #     terms; robust, rewards several distinctive terms");
            Console.WriteLine("                      #   max      - the file's single highest TF-IDF term;");
            Console.WriteLine("                      #     cheap, but one outlier term can dominate");
            Console.WriteLine("                      #   sum      - sum of all the file's TF-IDF terms;");
            Console.WriteLine("                      #     rewards breadth of distinctiveness");
            Console.WriteLine("  ");
            Console.WriteLine("  --show-path-ngrams # N-grams built from the interior segments of path-like");
            Console.WriteLine("               # units (e.g. \"src/whatever/services\" -> \"src/whatever\",");
            Console.WriteLine("               # \"whatever/services\"). A SEPARATE namespace/section from the");
            Console.WriteLine("               # main n-gram report: path-segment adjacency reflects directory");
            Console.WriteLine("               # nesting, not English word order, so it's never merged into");
            Console.WriteLine("               # regular phrase n-grams (which correctly treat a whole path as");
            Console.WriteLine("               # ONE opaque slot, never fragmenting it into fake phrases).");
            
            Console.WriteLine("\nOUTPUT OPTIONS - a single monotonic verbosity ladder (each level a strict");
            Console.WriteLine("superset of the previous one: more '--more's = more detail, more '--less's =");
            Console.WriteLine("less detail. These are presets/aliases for bundles of the --show/--hide flags");
            Console.WriteLine("above, and apply uniformly across ALL sections (n-grams, PDF, CDF, PMI, TF-IDF).");
            Console.WriteLine("Every ngc argument starts with \"--\" — there is no bare/colon-form syntax:");
            Console.WriteLine("  --less--           # Ultra-minimal: bare phrase only, nothing else");
            Console.WriteLine("  --less             # Minimal: phrase + count only");
            Console.WriteLine("  (default)          # phrase + count + summary/freq-stats/section-header");
            Console.WriteLine("  --more             # default + ppm column, ppm-stats, column-header");
            Console.WriteLine("  --more++           # '--more' + merged section (compare multiple n-gram sizes)");
            Console.WriteLine("  --more+++          # '--more++' + z-score column (full detail)");
            Console.WriteLine("  ");
            Console.WriteLine("  Explicit --show-x/--hide-x flags always override whatever a preset set,");
            Console.WriteLine("  regardless of order, e.g.:  ngc 1 --more+++ --hide-z   (detailed, but no Z column)");
            Console.WriteLine("  ");
            Console.WriteLine("  --asc                     # Sort ascending (least frequent first)");
            Console.WriteLine("  --desc / --rev            # Sort descending (most frequent first)");
            Console.WriteLine("  --sort count              # Sort by raw count (default)");
            Console.WriteLine("  --sort ppm                # Sort by normalized frequency (parts per million - NOT statistical significance!)");
            Console.WriteLine("  ");
            Console.WriteLine("  --top 50                # Show only top 50 most frequent results");
            Console.WriteLine("  --top 10%               # Show top 10% of results");
            Console.WriteLine("  --bottom 20             # Show only bottom 20 least frequent results");
            Console.WriteLine("  --bottom 25%            # Show bottom 25% of results");
            Console.WriteLine("  (--top N/--bottom N tie-expand to include all items tied with the boundary");
            Console.WriteLine("   count — but only when sorting by raw count. With --sort ppm, N is honored");
            Console.WriteLine("   exactly, since ppm is continuous and long-tail text can have thousands of");
            Console.WriteLine("   items tied at the same low ppm, which would otherwise blow --top N out to");
            Console.WriteLine("   nearly the whole list.)");
            Console.WriteLine("  ");
            Console.WriteLine("  --max-items 200    # Default cap on phrase rows per section when you have");
            Console.WriteLine("                     # not given an explicit --top/--bottom. If the filtered");
            Console.WriteLine("                     # result set is bigger, ngc trims it and prints a notice");
            Console.WriteLine("                     # (at the END of the list, after it scrolls) telling you");
            Console.WriteLine("                     # how to see more. SCALES with the --less/--more verbosity");
            Console.WriteLine("                     # ladder (see OUTPUT OPTIONS) unless set explicitly:");
            Console.WriteLine("                     #   --less--=25  --less=50  (default)=200  --more=500");
            Console.WriteLine("                     #   --more++=1000  --more+++=0 (unlimited)");
            Console.WriteLine("  --max-items 0      # Unlimited - never trim, no matter how big the result set");
            Console.WriteLine("  --max-items-per-file 15  # Default cap on rows shown WITHIN EACH FILE's own");
            Console.WriteLine("                           # table under --show-tfidf --per-file, independent");
            Console.WriteLine("                           # of --max-items above (which is a GLOBAL cap, not");
            Console.WriteLine("                           # a per-file one). Each capped file's header shows");
            Console.WriteLine("                           # \"(showing top N of M)\" so it's clear which file");
            Console.WriteLine("                           # tables were trimmed vs. shown in full. Also SCALES");
            Console.WriteLine("                           # with the --less/--more ladder unless set explicitly:");
            Console.WriteLine("                           #   --less--=5  --less=10  (default)=15  --more=30");
            Console.WriteLine("                           #   --more++=60  --more+++=0 (unlimited)");
            Console.WriteLine("  --max-items-per-file 0   # Unlimited rows per file");
            Console.WriteLine("  ");
            Console.WriteLine("  NOTE: for all three of --max-items/--max-items-per-file/--top-files, an");
            Console.WriteLine("  explicit N on the command line always overrides whatever the active");
            Console.WriteLine("  --less/--more preset set, regardless of which one appears first.");
            
            Console.WriteLine("\nANALYSIS STRATEGIES:");
            Console.WriteLine("  ");
            Console.WriteLine("  # Cold Start on an Unfamiliar Corpus (never seen this text before)");
            Console.WriteLine("  # Raw top-frequency is dominated by generic connective words even with");
            Console.WriteLine("  # stopwords on (\"one\", \"own\", \"any\", \"here\"...) — not informative for a");
            Console.WriteLine("  # totally cold start. This combo finds the corpus's OWN jargon instead:");
            Console.WriteLine("  ngc 2..3 --show-pmi --pmi 3+ --freq 4+ --desc --top 40");
            Console.WriteLine("                                    # fixed phrases/jargon glued tighter than");
            Console.WriteLine("                                    # chance predicts — this corpus's own");
            Console.WriteLine("                                    # terms-of-art, not generic English");
            Console.WriteLine("  ngc 2 --files \"**/*.md\" --show-tfidf --per-file --top-files 15 --desc");
            Console.WriteLine("                                    # which FILES are distinctive (lots of");
            Console.WriteLine("                                    # unique content) vs. which just reuse");
            Console.WriteLine("                                    # the same shared vocabulary as everyone");
            Console.WriteLine("  # Run both before reading anything — they tell you what to read next.");
            Console.WriteLine("  ");
            Console.WriteLine("  # Exploratory Analysis (Start Here)");
            Console.WriteLine("  ngc 1 --top 30 --desc                    # Most frequent terms in your input");
            Console.WriteLine("  ngc 2 --cdf 95+ --desc                   # Top 5% most frequent phrases in your input");
            Console.WriteLine("  ngc 1 --cdf 5 --desc --more+++           # Top/bottom 5% by frequency, with metrics");
            Console.WriteLine("  ngc 1 --show-pdf                    # See the overall shape of the distribution");
            Console.WriteLine("  ");
            Console.WriteLine("  # Collocation / Phrase Discovery");
            Console.WriteLine("  ngc 2 --show-pmi --desc --top 30          # Which bigrams are 'real phrases', not chance");
            Console.WriteLine("  ngc 3 --show-pmi --pmi 2+ --freq 5+ --desc  # Strongly glued trigrams, filtered to real signal");
            Console.WriteLine("  ");
            Console.WriteLine("  # Distinctiveness Across Documents (requires --files)");
            Console.WriteLine("  ngc 2 --files \"*.md\" --show-tfidf --desc --top 30   # What's distinctive, not just frequent");
            Console.WriteLine("  ngc 2 --files \"*.md\" --show-tfidf --per-file   # Each file's own top distinctive terms");
            Console.WriteLine("  ");
            Console.WriteLine("  # Real-Term / Glossary Extraction (surface form, not statistics)");
            Console.WriteLine("  # PMI/TF-IDF above are great at finding TEMPLATE/structural patterns (repeated");
            Console.WriteLine("  # boilerplate phrases), but they routinely bury genuine domain vocabulary under");
            Console.WriteLine("  # common connective phrases. For an actual glossary of real identifiers/terms,");
            Console.WriteLine("  # exploit surface form instead of frequency — e.g. PascalCase/CamelCase is a");
            Console.WriteLine("  # strong signal for \"this is a real API/class/service name,\" not English prose.");
            Console.WriteLine("  # Requires case-sensitive matching (the ngc default) — see CASE-SENSITIVE note above.");
            Console.WriteLine("  ngc 1 --contains \"^[A-Z][a-z]+[A-Z]\" --freq 2+ --desc --top 30");
            Console.WriteLine("                                       # PascalCase identifiers (ClassNames,");
            Console.WriteLine("                                       # ServiceNames, MethodNames) sorted by");
            Console.WriteLine("                                       # mention count — an instant glossary of");
            Console.WriteLine("                                       # every real technical term in the corpus,");
            Console.WriteLine("                                       # with zero prior vocabulary knowledge needed");
            Console.WriteLine("  ngc 1 --contains \"^[A-Z]{2,}$\" --freq 2+ --desc     # ALL-CAPS acronyms/constants");
            Console.WriteLine("    ");
            Console.WriteLine("  # Code Structure Analysis");
            Console.WriteLine("  ngc 2 --contains \"class [A-Z]\" --desc             # Find class definitions");
            Console.WriteLine("  ngc 3 --contains \"public (class|interface)\" --desc # Find public type definitions");
            Console.WriteLine("  ngc 3 --contains \"new [A-Z]\" --sort ppm --desc      # Object instantiation by normalized frequency");
            Console.WriteLine("  ngc 2 --contains \"import|using\" --top 20 --desc     # Most frequent dependencies");
            Console.WriteLine("  ");
            Console.WriteLine("  # Frequency Pattern Discovery");
            Console.WriteLine("  ngc 3 --contains \"if\" --z !2 --freq 5+ --desc         # 'if' patterns >2 std devs from mean, 5+ occurrences");
            Console.WriteLine("  ngc 2 --contains \"null\" --cdf 95+ --desc            # Top 5% most frequent null-related patterns");
            Console.WriteLine("  ngc 3 --contains \"try catch\" --sort ppm --desc      # Error handling patterns by normalized frequency");
            Console.WriteLine("  ngc 2 --contains \"TODO|FIXME\" --desc              # Find technical debt markers");
            Console.WriteLine("  ");
            Console.WriteLine("  # Documentation Analysis");
            Console.WriteLine("  ngc 3 --contains \"should\" --cdf 80+ --desc          # Top 20% frequent 'should' phrases");
            Console.WriteLine("  ngc 2 --remove-contains \"^(the|a|an|of|in)$\" --cdf 95+   # Frequent terms excluding common words");
            Console.WriteLine("  ngc 3 --contains \"Inconsistencies|Issues\" --desc  # Find problem-related phrases");
            Console.WriteLine("  ngc 2 --contains \"is|are\" --z !2 --freq 5+ --more+++     # Definition patterns >2 std devs, with metrics");
            
            Console.WriteLine("\nTROUBLESHOOTING:");
            Console.WriteLine("  ");
            Console.WriteLine("  # If you get no results:");
            Console.WriteLine("  1. Try broadening your n-gram size range (e.g., ngc 1..3 instead of just ngc 2)");
            Console.WriteLine("  2. Reduce the strictness of your filters (lower cdf or frequency thresholds)");
            Console.WriteLine("  3. Check if your regex pattern might need escaping or simplification");
            Console.WriteLine("  4. For descriptive filters, ensure you have enough text (larger sample = more reliable stats)");
            Console.WriteLine("  5. --show-tfidf / --per-file need --files (a document-boundary concept stdin can't provide)");
            Console.WriteLine("  ");
            Console.WriteLine("  # For better results:");
            Console.WriteLine("  1. Increase sample size: More diverse input = more generalizable patterns");
            Console.WriteLine("  2. Document your input source: What repos/files did you analyze?");
            Console.WriteLine("  3. Use '--more+++' to see full metrics when results seem incorrect");
            Console.WriteLine("  4. Validate against different samples: Same pattern in diverse sources = stronger evidence");
            
            Console.WriteLine("\nCOMMON COMBINATIONS:");
            Console.WriteLine("  ngc 1..3 --cdf 5 --desc                  # Statistical outliers across different n-gram sizes");
            Console.WriteLine("  ngc 2 --remove-contains \"^(the|a|an|of|in)$\" --sort ppm  # Meaningful phrases sorted by statistical significance");
            Console.WriteLine("  ngc 3 --contains \"pattern\" --z !2 --freq 5+        # Unusual but recurring patterns containing \"pattern\"");
            Console.WriteLine("  ngc 2..3 --z !2 --freq 5+ --more++            # Statistically significant phrases of different lengths");
            Console.WriteLine("  ngc 2 --files \"*.md\" --show-tfidf --show-pmi --desc  # Distinctive AND glued-together phrases");
            
            Console.WriteLine("\nTIPS FOR EFFECTIVE ANALYSIS:");
            Console.WriteLine("  1. Start broad, then narrow: Begin with `ngc 1 --top 50 --desc` to get an overview");
            Console.WriteLine("  2. Use cdf filters for deeper insights: `--cdf 5` is more revealing than just `--top N`");
            Console.WriteLine("  3. Look for both common and rare patterns: Outliers (`--z !2`) often reveal key insights");
            Console.WriteLine("  4. Combine with grep for further filtering: Pipe ngc output to grep to find specific terms");
            Console.WriteLine("  5. Statistical metrics reveal more than raw counts: Use `--more+++` and `--sort ppm` to find significance");
            Console.WriteLine("  6. Use --show-pmi to separate 'real phrases' from words that are merely common individually");
            Console.WriteLine("  7. Use --files + --show-tfidf to separate 'distinctive to a document' from 'common everywhere'");

            if (args.Length == 0) {
                // Exit the program when no args are provided and no input is piped
                // (This should not be reached, as we handle this case earlier)
                Environment.Exit(0);
            }
            
            Environment.Exit(0);
        }

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];

            if (a == "--asc") { options.Sort = SortDirection.Asc; continue; }
            if (a == "--desc" || a == "--rev") { options.Sort = SortDirection.Desc; continue; }

            if (a == "--less--") {
                // Ultra-minimal: bare phrase only. Strict subset of everything else.
                options.MinimalOutput = true;
                options.ShowInput = false;
                options.ShowSectionHeader = false;
                options.ShowSummary = false;
                options.ShowFreqStats = false;
                options.ShowPpmStats = false;
                options.ShowColumnHeader = false;
                options.ShowCount = false;
                options.ShowPpm = false;
                options.ShowZ = false;
                options.ShowMergedNGrams = false;
                // ngc-feedback.md #4/#6: row-count caps scale with the ladder too —
                // tightest at this end. Only applied if the user didn't give an
                // explicit value (see the order-independent pre-scan above ParseArgs'
                // main loop), so an explicit flag always wins regardless of order.
                if (!options.MaxItemsSetExplicitly) options.MaxItems = 25;
                if (!options.MaxItemsPerFileSetExplicitly) options.MaxItemsPerFile = 5;
                if (!options.TopFilesSetExplicitly) options.TopFiles = 5;
                continue;
            }
            if (a == "--less") {
                // Minimal: phrase + count only, no stats/headers.
                options.MinimalOutput = true;
                options.ShowInput = false;
                options.ShowSectionHeader = false;
                options.ShowSummary = false;
                options.ShowFreqStats = false;
                options.ShowPpmStats = false;
                options.ShowColumnHeader = false;
                options.ShowCount = true;
                options.ShowPpm = false;
                options.ShowZ = false;
                options.ShowMergedNGrams = false;
                if (!options.MaxItemsSetExplicitly) options.MaxItems = 50;
                if (!options.MaxItemsPerFileSetExplicitly) options.MaxItemsPerFile = 10;
                if (!options.TopFilesSetExplicitly) options.TopFiles = 10;
                continue;
            }
            if (a == "--more") {
                // Default + ppm column, ppm stats, column header.
                options.Mode = OutputMode.Enhanced;
                options.ShowPpm = true;
                options.ShowPpmStats = true;
                options.ShowColumnHeader = true;
                options.MorePresetUsed = true;
                if (!options.MaxItemsSetExplicitly) options.MaxItems = 500;
                if (!options.MaxItemsPerFileSetExplicitly) options.MaxItemsPerFile = 30;
                if (!options.TopFilesSetExplicitly) options.TopFiles = 50;
                continue;
            }
            if (a == "--more++") {
                // '--more' plus a merged section combining multiple n-gram sizes.
                options.Mode = OutputMode.Both;
                options.ShowMergedNGrams = true;
                options.ShowPpm = true;
                options.ShowPpmStats = true;
                options.ShowColumnHeader = true;
                options.MorePresetUsed = true;
                if (!options.MaxItemsSetExplicitly) options.MaxItems = 1000;
                if (!options.MaxItemsPerFileSetExplicitly) options.MaxItemsPerFile = 60;
                if (!options.TopFilesSetExplicitly) options.TopFiles = 100;
                continue;
            }
            if (a == "--more+++") {
                // '--more++' plus the Z-score column (full detail — the "kitchen sink").
                options.Mode = OutputMode.Detailed;
                options.ShowMergedNGrams = true;
                options.ShowPpm = true;
                options.ShowPpmStats = true;
                options.ShowColumnHeader = true;
                options.ShowZ = true;
                options.MorePresetUsed = true;
                // "Full detail" means unlimited across the board, matching --more+++'s
                // existing "kitchen sink" framing.
                if (!options.MaxItemsSetExplicitly) options.MaxItems = 0;
                if (!options.MaxItemsPerFileSetExplicitly) options.MaxItemsPerFile = 0;
                if (!options.TopFilesSetExplicitly) options.TopFiles = int.MaxValue;
                continue;
            }
            if (a == "--show-input") { options.ShowInput = true; continue; }
            if (a == "--hide-input") { options.ShowInput = false; continue; }
            if (a == "--show-section-header") { options.ShowSectionHeader = true; continue; }
            if (a == "--hide-section-header") { options.ShowSectionHeader = false; continue; }
            if (a == "--show-summary") { options.ShowSummary = true; continue; }
            if (a == "--hide-summary") { options.ShowSummary = false; continue; }
            if (a == "--show-freq-stats") { options.ShowFreqStats = true; continue; }
            if (a == "--hide-freq-stats") { options.ShowFreqStats = false; continue; }
            if (a == "--show-ppm-stats") { options.ShowPpmStats = true; continue; }
            if (a == "--hide-ppm-stats") { options.ShowPpmStats = false; continue; }
            if (a == "--show-headers") { options.ShowColumnHeader = true; continue; }
            if (a == "--hide-headers") { options.ShowColumnHeader = false; continue; }
            if (a == "--show-phrases") { options.ShowPhrases = true; options.ShowPhrasesExplicit = true; continue; }
            if (a == "--hide-phrases") { options.ShowPhrases = false; options.ShowPhrasesExplicit = true; continue; }
            if (a == "--show-tfidf-phrases") { options.ShowTfidfPhrases = true; continue; }
            if (a == "--hide-tfidf-phrases") { options.ShowTfidfPhrases = false; continue; }
            if (a == "--show-merged-ngrams" || a == "--show-merged-n-grams") { options.ShowMergedNGrams = true; continue; }
            if (a == "--hide-merged-ngrams" || a == "--hide-merged-n-grams") { options.ShowMergedNGrams = false; continue; }
            if (a == "--show-ngrams" || a == "--show-n-grams") { options.ShowNGrams = true; options.ShowNGramsExplicit = true; continue; }
            if (a == "--hide-ngrams" || a == "--hide-n-grams") { options.ShowNGrams = false; options.ShowNGramsExplicit = true; continue; }
            if (a == "--show-count") { options.ShowCount = true; continue; }
            if (a == "--hide-count") { options.ShowCount = false; continue; }
            if (a == "--show-ppm") { options.ShowPpm = true; continue; }
            if (a == "--hide-ppm") { options.ShowPpm = false; continue; }
            if (a == "--show-z") { options.ShowZ = true; continue; }
            if (a == "--hide-z") { options.ShowZ = false; continue; }
            if (a == "--show-pdf") { options.ShowPdf = true; continue; }
            if (a == "--hide-pdf") { options.ShowPdf = false; continue; }
            if (a == "--show-cdf") { options.ShowCdf = true; continue; }
            if (a == "--hide-cdf") { options.ShowCdf = false; continue; }
            if (a == "--show-pmi") { options.ShowPmi = true; continue; }
            if (a == "--hide-pmi") { options.ShowPmi = false; continue; }
            if (a == "--show-tfidf") { options.ShowTfidf = true; continue; }
            if (a == "--hide-tfidf") { options.ShowTfidf = false; continue; }
            if (a == "--show-path-ngrams") { options.ShowPathNGrams = true; continue; }
            if (a == "--hide-path-ngrams") { options.ShowPathNGrams = false; continue; }
            if (a == "--show-dispersion") { options.ShowDispersion = true; continue; }
            if (a == "--hide-dispersion") { options.ShowDispersion = false; continue; }
            if (a == "--show-logdice") { options.ShowLogDice = true; continue; }
            if (a == "--hide-logdice") { options.ShowLogDice = false; continue; }
            if (a == "--show-tscore") { options.ShowTScore = true; continue; }
            if (a == "--hide-tscore") { options.ShowTScore = false; continue; }
            if (a == "--show-mi3") { options.ShowMi3 = true; continue; }
            if (a == "--hide-mi3") { options.ShowMi3 = false; continue; }
            if (a == "--show-readability") { options.ShowReadability = true; continue; }
            if (a == "--hide-readability") { options.ShowReadability = false; continue; }
            if (a == "--per-file") { options.PerFile = true; continue; }
            if (a == "--no-stop-words")
            {
                // Basis reset is handled by the pre-scan before this loop runs (see ParseArgs
                // top); this token is consumed here just so it doesn't fall through to the
                // "unrecognized option" error path. No further action needed at this point.
                continue;
            }
            if (a == "--ignore-case")
            {
                // Also handled by the pre-scan (see ParseArgs top); consumed here as a no-op.
                continue;
            }
            if (a == "--stop-words" || a == "--stop")
            {
                // --stop-words word1 word2 ...: ADD extra words to the stopword set, on top
                // of whichever basis is currently in effect (full defaults, or empty if
                // --no-stop-words was also given — order on the command line doesn't matter,
                // see the pre-scan in ParseArgs). Bare --stop-words with no following words
                // is a no-op (nothing to add).
                int j = i + 1;
                var added = new List<string>();
                while (j < args.Length && !IsLikelyOptionToken(args[j]))
                {
                    added.Add(args[j]);
                    j++;
                }
                foreach (var w in added) options.EffectiveStopWords.Add(w);
                i = j - 1;
                continue;
            }
            if (a == "--no-trim-words")
            {
                // Same pre-scan pattern as --no-stop-words; consumed here as a no-op.
                continue;
            }
            if (a == "--trim-words" || a == "--trim")
            {
                // --trim-words word1 word2 ...: ADD extra words to the trim-word set, on top
                // of whichever basis is currently in effect. Symmetric with --stop-words.
                int j = i + 1;
                var added = new List<string>();
                while (j < args.Length && !IsLikelyOptionToken(args[j]))
                {
                    added.Add(args[j]);
                    j++;
                }
                foreach (var w in added) options.EffectiveTrimWords.Add(w);
                i = j - 1;
                continue;
            }
            if (a == "--no-pair-chars")
            {
                // Basis reset handled by the pre-scan in ParseArgs; consumed here as a no-op.
                continue;
            }
            if (a == "--pair-chars")
            {
                // --pair-chars "=;" "()" ...: ADD extra (open,close) delimiter pairs on top of
                // whichever basis is currently in effect (defaults, or empty if
                // --no-pair-chars was also given). Each argument must be exactly 2 characters:
                // the open delimiter followed by the close delimiter (open == close is fine,
                // e.g. "==" protects "=...=" spans the same way quotes/backticks do).
                int j = i + 1;
                while (j < args.Length && !IsLikelyOptionToken(args[j]))
                {
                    var p = args[j];
                    if (p.Length == 2) options.EffectivePairChars.Add((p[0], p[1]));
                    else Console.Error.WriteLine($"--pair-chars: ignoring \"{p}\" — each pair must be exactly 2 characters (open then close).");
                    j++;
                }
                i = j - 1;
                continue;
            }
            if (a == "--no-keep-symbols")
            {
                continue;
            }
            if (a == "--keep-symbols")
            {
                // --keep-symbols "._" ...: ADD characters (one per arg, or a run of chars in
                // one arg — each char in each arg is added individually) to the Layer-2
                // word-glue set, on top of whichever basis is currently in effect (defaults
                // {-, '}, or empty if --no-keep-symbols was also given).
                int j = i + 1;
                while (j < args.Length && !IsLikelyOptionToken(args[j]))
                {
                    foreach (var ch in args[j]) options.EffectiveKeepSymbols.Add(ch);
                    j++;
                }
                i = j - 1;
                continue;
            }
            if (a == "--no-trim-symbols")
            {
                continue;
            }
            if (a == "--trim-symbols")
            {
                // --trim-symbols ".,;" ...: ADD characters to the Layer-3 edge-trim set, on
                // top of whichever basis is currently in effect (empty by default).
                int j = i + 1;
                while (j < args.Length && !IsLikelyOptionToken(args[j]))
                {
                    foreach (var ch in args[j]) options.EffectiveTrimSymbols.Add(ch);
                    j++;
                }
                i = j - 1;
                continue;
            }
            if (a == "--files")
            {
                int j = i + 1;
                while (j < args.Length && !IsLikelyOptionToken(args[j]) && LooksLikeFileGlob(args[j]))
                {
                    options.FileGlobs.Add(args[j]);
                    j++;
                }
                i = j - 1;
                continue;
            }
            if (a == "--sort" && i + 1 < args.Length) {
                i++;
                if (args[i] == "count") options.SortBy = SortBy.Count;
                else if (args[i] == "ppm") options.SortBy = SortBy.Ppm;
                continue;
            }
            if (a == "--top" && i + 1 < args.Length) {
                i++; string value = args[i];
                // Check if it's a percentage
                if (value.EndsWith("%")) {
                    string percentStr = value.Substring(0, value.Length - 1);
                    if (double.TryParse(percentStr, out double percent)) {
                        // Store as negative to indicate it's a percentage
                        options.LimitIsPercentage = true;
                        options.LimitPercentage = percent;
                    }
                } else {
                    // Regular numeric limit
                    if (int.TryParse(value, out int t)) {
                        options.Limit = t;
                        options.LimitIsPercentage = false;
                    }
                }
                continue; 
            }
            if (a == "--bottom" && i + 1 < args.Length) {
                i++; string value = args[i];
                // Check if it's a percentage
                if (value.EndsWith("%")) {
                    string percentStr = value.Substring(0, value.Length - 1);
                    if (double.TryParse(percentStr, out double percent)) {
                        // Store as negative to indicate it's a percentage
                        options.BottomLimitIsPercentage = true;
                        options.BottomLimitPercentage = percent;
                    }
                } else {
                    // Regular numeric limit
                    if (int.TryParse(value, out int b)) {
                        options.BottomLimit = b;
                        options.BottomLimitIsPercentage = false;
                    }
                }
                continue; 
            }
            // nrange
            if (Regex.IsMatch(a, @"^\d+$"))
            {
                int n = int.Parse(a); options.NGramSizes = new List<int> { n }; options.ShowMergedNGrams = false; continue;
            }
            if (Regex.IsMatch(a, @"^\d+\.\.\d+$"))
            {
                var p = a.Split(new[] { ".." }, StringSplitOptions.None);
                int s = int.Parse(p[0]); int e = int.Parse(p[1]); options.NGramSizes = Enumerable.Range(s, e - s + 1).ToList(); options.ShowMergedNGrams = false; continue;
            }
            if (a.Contains(",") && Regex.IsMatch(a, @"^[\d,]+$"))
            {
                var parts = a.Split(','); options.NGramSizes = parts.Select(x => int.Parse(x)).ToList(); options.ShowMergedNGrams = false; continue;
            }
            // exclude file
            if (a == "--exclude-file" && i + 1 < args.Length) { i++; options.ExcludeFiles.Add(args[i]); continue; }
            // explicit, unambiguous filter flags (preferred when combining with --files)
            // --phrase-contains / --remove-phrase-contains are explicit, self-documenting
            // aliases for --contains / --remove-contains: both filter the assembled n-gram
            // PHRASE text (post-tokenization, post-count) — never the raw source line. See
            // --line-contains above for the genuinely different, line-level filter stage.
            if ((a == "--contains" || a == "--phrase-contains") && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.Contains, Pattern = p };
                if (TryCompileRegex(p, out Regex rgx, options.IgnoreCase)) f.CompiledRegex = rgx;
                options.TextFilters.Add(f); continue;
            }
            if ((a == "--remove-contains" || a == "--remove-phrase-contains") && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.NotContains, Pattern = p };
                if (TryCompileRegex(p, out Regex rgx, options.IgnoreCase)) f.CompiledRegex = rgx;
                options.TextFilters.Add(f); continue;
            }
            // --line-contains / --remove-line-contains: a genuinely SEPARATE pipeline
            // stage from --contains/--remove-contains above — these filter whole SOURCE
            // LINES before tokenization (deciding what enters the n-gram pool at all),
            // not already-built n-gram phrase results after the fact. See PassLineFilters.
            if (a == "--line-contains" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.Contains, Pattern = p };
                if (TryCompileRegex(p, out Regex rgx, options.IgnoreCase)) f.CompiledRegex = rgx;
                options.LineFilters.Add(f); continue;
            }
            if (a == "--remove-line-contains" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.NotContains, Pattern = p };
                if (TryCompileRegex(p, out Regex rgx, options.IgnoreCase)) f.CompiledRegex = rgx;
                options.LineFilters.Add(f); continue;
            }
            // --line-starts-with / --line-ends-with (ngc-feedback.md #1): named line-level
            // counterparts to --starts/--ends, for the common "isolate this kind of line"
            // case (e.g. Markdown ATX headings) without hand-writing a regex anchor onto
            // --line-contains yourself. Same LineFilters pipeline stage as --line-contains.
            if (a == "--line-starts-with" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.StartsWith, Pattern = p };
                if (TryCompileRegex(p, out Regex rgx, options.IgnoreCase)) f.CompiledRegex = rgx;
                options.LineFilters.Add(f); continue;
            }
            if (a == "--remove-line-starts-with" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.NotStartsWith, Pattern = p };
                if (TryCompileRegex(p, out Regex rgx, options.IgnoreCase)) f.CompiledRegex = rgx;
                options.LineFilters.Add(f); continue;
            }
            if (a == "--line-ends-with" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.EndsWith, Pattern = p };
                if (TryCompileRegex(p, out Regex rgx, options.IgnoreCase)) f.CompiledRegex = rgx;
                options.LineFilters.Add(f); continue;
            }
            if (a == "--remove-line-ends-with" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.NotEndsWith, Pattern = p };
                if (TryCompileRegex(p, out Regex rgx, options.IgnoreCase)) f.CompiledRegex = rgx;
                options.LineFilters.Add(f); continue;
            }
            if (a == "--starts" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.StartsWith, Pattern = p };
                if (TryCompileRegex(p, out Regex rgx, options.IgnoreCase)) f.CompiledRegex = rgx;
                options.TextFilters.Add(f); continue;
            }
            if (a == "--remove-starts" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.NotStartsWith, Pattern = p };
                if (TryCompileRegex(p, out Regex rgx, options.IgnoreCase)) f.CompiledRegex = rgx;
                options.TextFilters.Add(f); continue;
            }
            if (a == "--ends" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.EndsWith, Pattern = p };
                if (TryCompileRegex(p, out Regex rgx, options.IgnoreCase)) f.CompiledRegex = rgx;
                options.TextFilters.Add(f); continue;
            }
            if (a == "--remove-ends" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.NotEndsWith, Pattern = p };
                if (TryCompileRegex(p, out Regex rgx, options.IgnoreCase)) f.CompiledRegex = rgx;
                options.TextFilters.Add(f); continue;
            }
            // max-items cap (default 200; 0 = unlimited); only applies when user gave no explicit top:/bottom:
            if (a == "--max-items" && i + 1 < args.Length) {
                i++; if (int.TryParse(args[i], out int mi)) { options.MaxItems = mi; options.MaxItemsSetExplicitly = true; }
                continue;
            }
            // --max-items-per-file N (default 15; 0 = unlimited) — caps rows per-file within
            // `--show-tfidf --per-file` tables, independent of the global --max-items above
            // (see ngc-feedback.md #3). Also scales with the --less/--more ladder (see
            // presets above) unless set explicitly here, in which case this always wins.
            if (a == "--max-items-per-file" && i + 1 < args.Length) {
                i++; if (int.TryParse(args[i], out int mipf)) { options.MaxItemsPerFile = mipf; options.MaxItemsPerFileSetExplicitly = true; }
                continue;
            }
            // --top-files N (0 = unlimited): caps how many --files documents get a full
            // breakdown table under `--show-tfidf --per-file`, after ranking documents by
            // an aggregate per-file distinctiveness score (see TopFilesBy / PrintTfidf).
            // Also scales with the --less/--more ladder (see presets above) unless set
            // explicitly here, in which case this always wins.
            if (a == "--top-files" && i + 1 < args.Length) {
                i++; if (int.TryParse(args[i], out int tf)) { options.TopFiles = tf == 0 ? int.MaxValue : tf; options.TopFilesSetExplicitly = true; }
                continue;
            }
            // --top-files-by max|sum|avg-top5 — which aggregate to rank files by (see
            // CommandOptions.TopFilesBy doc comment for what each one rewards/penalizes).
            if (a == "--top-files-by" && i + 1 < args.Length) {
                i++; var mode = args[i];
                if (mode == "max" || mode == "sum" || mode == "avg-top5") options.TopFilesBy = mode;
                continue;
            }
            // NOTE: bare/implicit content-filter syntax ("pattern", -"pattern", "pattern..",
            // "..pattern", "!word..", "!..word") has been removed entirely. Content filters
            // now REQUIRE one of the explicit flags: --contains, --remove-contains, --starts,
            // --remove-starts, --ends, --remove-ends. This closes the "silent absorption" bug
            // where any unrecognized/mistyped token (a flag typo, a stray word, etc.) used to
            // fall through and become a no-op Contains filter instead of an error.
            //
            // NOTE 2: bare colon-form ("freq:10+", "top:50", "cdf:5", etc.) has been removed
            // entirely too (ngc-feedback.md #2) — every value flag below now REQUIRES the
            // explicit "--flag VALUE" form, one consistent grammar, no guessing whether an
            // unprefixed token is a positional argument, a filter, or a typo'd flag.
            if (a == "--freq" && i + 1 < args.Length)
            {
                i++; var freqExpr = args[i];
                if (Regex.IsMatch(freqExpr, @"^\d+$")) { int exactFreq = int.Parse(freqExpr); options.FrequencyFilters.Add(new FrequencyFilter { Min = exactFreq, Max = exactFreq, Outside = false }); continue; }
                if (Regex.IsMatch(freqExpr, @"^\d+\.\.\d+$")) { var pp = freqExpr.Split(new[] { ".." }, StringSplitOptions.None); options.FrequencyFilters.Add(new FrequencyFilter { Min = int.Parse(pp[0]), Max = int.Parse(pp[1]), Outside = false }); continue; }
                if (Regex.IsMatch(freqExpr, @"^\d+\+$")) { options.FrequencyFilters.Add(new FrequencyFilter { Min = int.Parse(freqExpr.TrimEnd('+')), Max = null, Outside = false }); continue; }
                if (Regex.IsMatch(freqExpr, @"^\.\.\d+$")) { options.FrequencyFilters.Add(new FrequencyFilter { Min = null, Max = int.Parse(freqExpr.Substring(2)), Outside = false }); continue; }
                if (Regex.IsMatch(freqExpr, @"^!\d+\+$")) { options.FrequencyFilters.Add(new FrequencyFilter { Min = null, Max = int.Parse(freqExpr.Substring(1).TrimEnd('+')) - 1, Outside = false }); continue; }
                if (Regex.IsMatch(freqExpr, @"^!\d+\.\.\d+$")) { var pp = freqExpr.Substring(1).Split(new[] { ".." }, StringSplitOptions.None); options.FrequencyFilters.Add(new FrequencyFilter { Min = int.Parse(pp[0]), Max = int.Parse(pp[1]), Outside = true }); continue; }
                continue;
            }
            if (a == "--ppm" && i + 1 < args.Length)
            {
                i++; var p = args[i];
                if (Regex.IsMatch(p, @"^\d+(\.\d+)?$") && !p.EndsWith("+")) { double exactPpm = double.Parse(p); options.PpmFilters.Add(new PpmFilter { Min = exactPpm, Max = exactPpm, Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^\d+\.\.\d+$")) { var pp = p.Split(new[] { ".." }, StringSplitOptions.None); options.PpmFilters.Add(new PpmFilter { Min = double.Parse(pp[0]), Max = double.Parse(pp[1]), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^\d+\+$")) { options.PpmFilters.Add(new PpmFilter { Min = double.Parse(p.TrimEnd('+')), Max = null, Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^\.\.\d+$")) { options.PpmFilters.Add(new PpmFilter { Min = null, Max = double.Parse(p.Substring(2)), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^!\d+\+$")) { options.PpmFilters.Add(new PpmFilter { Min = null, Max = double.Parse(p.Substring(1).TrimEnd('+')), Outside = true }); continue; }
                if (Regex.IsMatch(p, @"^!\d+\.\.\d+$")) { var pp = p.Substring(1).Split(new[] { ".." }, StringSplitOptions.None); options.PpmFilters.Add(new PpmFilter { Min = double.Parse(pp[0]), Max = double.Parse(pp[1]), Outside = true }); continue; }
                continue;
            }
            if (a == "--z" && i + 1 < args.Length)
            {
                i++; var p = args[i];
                if (Regex.IsMatch(p, @"^\d+(\.\d+)?\.\.\d+(\.\d+)?$")) { var pp = p.Split(new[] { ".." }, StringSplitOptions.None); options.ZFilters.Add(new ZFilter { Min = double.Parse(pp[0]), Max = double.Parse(pp[1]), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^\d+(\.\d+)?$")) { options.ZFilters.Add(new ZFilter { Min = -double.Parse(p), Max = double.Parse(p), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^!\d+(\.\d+)?$")) { options.ZFilters.Add(new ZFilter { Min = double.Parse(p.Substring(1)), Max = null, Outside = true }); continue; }
                continue;
            }
            if (a == "--pmi" && i + 1 < args.Length)
            {
                i++; var p = args[i];
                if (Regex.IsMatch(p, @"^-?\d+(\.\d+)?\.\.-?\d+(\.\d+)?$")) { var pp = p.Split(new[] { ".." }, StringSplitOptions.None); options.PmiFilters.Add(new PmiFilter { Min = double.Parse(pp[0]), Max = double.Parse(pp[1]), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^-?\d+(\.\d+)?\+$")) { options.PmiFilters.Add(new PmiFilter { Min = double.Parse(p.TrimEnd('+')), Max = null, Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^!-?\d+(\.\d+)?\+$")) { options.PmiFilters.Add(new PmiFilter { Min = null, Max = double.Parse(p.Substring(1).TrimEnd('+')), Outside = true }); continue; }
                if (Regex.IsMatch(p, @"^!-?\d+(\.\d+)?$")) { options.PmiFilters.Add(new PmiFilter { Min = null, Max = double.Parse(p.Substring(1)), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^-?\d+(\.\d+)?$")) { double exactPmi = double.Parse(p); options.PmiFilters.Add(new PmiFilter { Min = exactPmi, Max = exactPmi, Outside = false }); continue; }
                continue;
            }
            if (a == "--tfidf" && i + 1 < args.Length)
            {
                i++; var p = args[i];
                if (Regex.IsMatch(p, @"^\d+(\.\d+)?\.\.\d+(\.\d+)?$")) { var pp = p.Split(new[] { ".." }, StringSplitOptions.None); options.TfidfFilters.Add(new TfidfFilter { Min = double.Parse(pp[0]), Max = double.Parse(pp[1]), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^\d+(\.\d+)?\+$")) { options.TfidfFilters.Add(new TfidfFilter { Min = double.Parse(p.TrimEnd('+')), Max = null, Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^\.\.\d+(\.\d+)?$")) { options.TfidfFilters.Add(new TfidfFilter { Min = null, Max = double.Parse(p.Substring(2)), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^!\d+(\.\d+)?\+$")) { options.TfidfFilters.Add(new TfidfFilter { Min = null, Max = double.Parse(p.Substring(1).TrimEnd('+')), Outside = true }); continue; }
                if (Regex.IsMatch(p, @"^!\d+(\.\d+)?\.\.\d+(\.\d+)?$")) { var pp = p.Substring(1).Split(new[] { ".." }, StringSplitOptions.None); options.TfidfFilters.Add(new TfidfFilter { Min = double.Parse(pp[0]), Max = double.Parse(pp[1]), Outside = true }); continue; }
                if (Regex.IsMatch(p, @"^\d+(\.\d+)?$")) { double exactTfidf = double.Parse(p); options.TfidfFilters.Add(new TfidfFilter { Min = exactTfidf, Max = exactTfidf, Outside = false }); continue; }
                continue;
            }
            if (a == "--cdf" && i + 1 < args.Length)
            {
                i++; var p = args[i];
                // Handle shorthand notation (with or without % sign)
                if (Regex.IsMatch(p, @"^\d+(\.\d+)?%?$") && !p.Contains("..") && !p.EndsWith("+"))
                {
                    // Remove % sign if present
                    string valueStr = p.EndsWith("%") ? p.Substring(0, p.Length - 1) : p;
                    double percentage = double.Parse(valueStr);

                    // Calculate min and max percentiles for the central range to exclude
                    double lowerBound = percentage;
                    double upperBound = 100 - percentage;

                    options.PercentileFilters.Add(new PercentileFilter {
                        Min = lowerBound,
                        Max = upperBound,
                        Outside = true  // We want items OUTSIDE this range
                    });
                    continue;
                }

                // Standard percentile range patterns
                if (Regex.IsMatch(p, @"^\d+(\.\d+)?\.\.\d+(\.\d+)?$")) { var pp = p.Split(new[] { ".." }, StringSplitOptions.None); options.PercentileFilters.Add(new PercentileFilter { Min = double.Parse(pp[0]), Max = double.Parse(pp[1]), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^\d+(\.\d+)?\+$")) { options.PercentileFilters.Add(new PercentileFilter { Min = double.Parse(p.TrimEnd('+')), Max = null, Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^\.\.\d+(\.\d+)?$")) { options.PercentileFilters.Add(new PercentileFilter { Min = null, Max = double.Parse(p.Substring(2)), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^!\d+(\.\d+)?\+$")) { options.PercentileFilters.Add(new PercentileFilter { Min = null, Max = double.Parse(p.Substring(1).TrimEnd('+')), Outside = true }); continue; }
                if (Regex.IsMatch(p, @"^!\d+(\.\d+)?\.\.\d+(\.\d+)?$")) { var pp = p.Substring(1).Split(new[] { ".." }, StringSplitOptions.None); options.PercentileFilters.Add(new PercentileFilter { Min = double.Parse(pp[0]), Max = double.Parse(pp[1]), Outside = true }); continue; }
                continue;
            }
            // Any token that reaches here didn't match a known flag/filter/n-gram-size/preset —
            // it is NOT silently absorbed as a content filter anymore. Error out loudly instead,
            // so typos (like "--by-file" instead of "--per-file") or stray words fail immediately
            // rather than quietly becoming a no-op filter that matches nothing.
            Console.Error.WriteLine($"Unrecognized argument: {a}");
            Console.Error.WriteLine("Every ngc argument must start with \"--\" (e.g. --top 50, --freq 10+, --cdf 5).");
            Console.Error.WriteLine("Bare/colon-form tokens (\"top:50\", \"freq:10+\", \"rev\", \"+++\") are no longer supported.");
            Console.Error.WriteLine("Content filters require an explicit flag: --contains, --remove-contains, --starts, --remove-starts, --ends, or --remove-ends.");
            Console.Error.WriteLine("Run with --help to see available options.");
            Environment.Exit(1);
        }

        // Rule (agreed in CDRs/ngc-feedback.md Round 3, decision #1): if the user asked for
        // a summary-only report (--show-cdf and/or --show-pdf) and did NOT explicitly say
        // --show-phrases/--hide-phrases, and hasn't opted into the "show everything" ladder
        // via --more++/--more+++, then suppress the phrase-list body by default — a bare
        // `ngc 1 --show-cdf` should print just the ladder, not also a huge raw phrase dump.
        // Explicit --show-phrases always wins (handled above by ShowPhrasesExplicit), and
        // --more+++'s existing "show everything" behavior is untouched (MorePresetUsed gate).
        if ((options.ShowCdf || options.ShowPdf) && !options.ShowPhrasesExplicit && !options.MorePresetUsed)
        {
            options.ShowPhrases = false;
        }

        // The "big" --show-X report-selector flags: each of these prints its own report
        // section. --show-pmi is deliberately NOT in this list — it's a COLUMN added onto
        // the n-grams report, not a report of its own, so it has nothing to attach to unless
        // n-grams are already showing (see the "implied ngrams" carve-out just below).
        bool anyBigReportFlagUsed = options.ShowPdf || options.ShowCdf || options.ShowTfidf ||
            options.ShowPathNGrams || options.ShowDispersion || options.ShowLogDice ||
            options.ShowTScore || options.ShowMi3 || options.ShowReadability;

        // Rule (CDRs discussion, this session): --show-X flags are report SELECTORS. If the
        // user asked for any of the "big" reports above, nothing is implicit any more — the
        // n-grams report only shows if --show-ngrams was ALSO explicitly requested. The one
        // exception is --show-pmi: since it's a column on the n-grams report rather than its
        // own report, requesting it implies the n-grams report must be visible too (there's
        // nothing else for that column to attach to). An explicit --show-ngrams/--hide-ngrams
        // always wins outright, regardless of order (ShowNGramsExplicit tracks that).
        if (!options.ShowNGramsExplicit)
        {
            if (anyBigReportFlagUsed && !options.ShowPmi)
                options.ShowNGrams = false;
            else
                options.ShowNGrams = true; // default (no big flags, or only --show-pmi): show n-grams, matches today's baseline
        }

        return options;
    }
}
