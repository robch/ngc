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
    public bool ShowMerged = false;
    public bool ShowSeparate = true;
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
    public bool MinimalOutput = false;
    public bool StatsOnly = false; // Only show statistics, not full phrase lists
    public List<double> UniquePercentiles = new List<double>();
    public List<string> ExcludeFiles = new List<string>();

    // --show-x / --hide-x granular flags (presets below are just bundles of these)
    // Report sections:
    public bool ShowInput = true;
    public bool ShowSectionHeader = true;
    public bool ShowSummary = true;
    public bool ShowFreqStats = true;
    public bool ShowPpmStats = false;
    public bool ShowColumnHeader = false;
    public bool ShowPhrases = true;
    public bool ShowTfidfPhrases = true;
    // ShowMerged / ShowSeparate already declared above.
    // Per-item columns:
    public bool ShowCount = true;
    public bool ShowPpm = false;
    public bool ShowZ = false;
    // New reports (implemented in later steps; parsed now so flags are accepted):
    public bool ShowPdf = false;
    public bool ShowCdf = false;
    public bool ShowPmi = false;
    public bool ShowTfidf = false;
    public bool PerFile = false;
    // --files glob1 [glob2 ...] — when non-empty, read these files/globs as
    // separate documents instead of reading stdin as one blob. Relative globs
    // (including "../" parent-traversal, e.g. "../other-repo/**/*.cs") are
    // supported — see LooksLikeFileGlob's ".." handling in ParseArgs.
    public List<string> FileGlobs = new List<string>();
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
        
    // Helper method to detect if a pattern contains regex special characters and compile it
    private static bool TryCompileRegex(string pattern, out Regex regex)
    {
        regex = null!;
        // Check for common regex metacharacters
        if (pattern.IndexOfAny(new[] { '|', '*', '+', '?', '[', ']', '(', ')', '{', '}', '\\' }) < 0)
            return false; // Not a regex pattern
            
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
        bool needPmiCollection = options.ShowPmi || options.PmiFilters.Count > 0;
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

        // Per-document n-gram data, kept for future document-boundary features
        // (e.g. TF-IDF): one dictionary-of-dictionaries per matched document.
        var perDocNGramCounts = new Dictionary<string, Dictionary<int, Dictionary<string, int>>>();
        var perDocTotalTokensPerN = new Dictionary<string, Dictionary<int, int>>();

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

            foreach (var line in docLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var sb = new StringBuilder(line.Length);
                foreach (var ch in line)
                {
                    if (char.IsLetterOrDigit(ch) || ch == '-') sb.Append(ch); else sb.Append(' ');
                }
                var words = sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                totalWords += words.Length;
                for (int n = 1; n <= (collectSizes.Count > 0 ? collectSizes.Max() : 3); n++)
                {
                    if (!collectSizes.Contains(n)) continue;
                    if (words.Length < n) continue;
                    var tokenCountForLine = Math.Max(0, words.Length - n + 1);
                    totalTokensPerN[n] += tokenCountForLine;
                    docTotalTokensPerN[n] += tokenCountForLine;
                    for (int i = 0; i <= words.Length - n; i++)
                    {
                        var ngram = string.Join(" ", words.Skip(i).Take(n));
                        if (nGramCounts[n].ContainsKey(ngram)) nGramCounts[n][ngram]++; else nGramCounts[n][ngram] = 1;
                        if (docNGramCounts[n].ContainsKey(ngram)) docNGramCounts[n][ngram]++; else docNGramCounts[n][ngram] = 1;
                    }
                }
            }

            perDocNGramCounts[docName] = docNGramCounts;
            perDocTotalTokensPerN[docName] = docTotalTokensPerN;
        }

        // Make per-document data available for later features (TF-IDF etc.)
        Program.DocumentNames = documents.Select(d => d.Name).ToList();
        Program.PerDocNGramCounts = perDocNGramCounts;
        Program.PerDocTotalTokensPerN = perDocTotalTokensPerN;


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
                    if (TryCompileRegex(t.ToLower(), out Regex regex))
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
                if (!PassTextFilters(ngram, options.TextFilters)) continue;
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
        if (options.ShowMerged || options.Mode == OutputMode.Both)
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
        if (options.MinimalOutput)
        {
            // only ngrams lines from merged or per-bucket depending on ShowMerged/ShowSeparate
            if (options.ShowMerged)
            {
                foreach (var it in SortAndLimit(merged, options)) PrintEntry(it, options, "merged");
            }
            else
            {
                foreach (var n in options.NGramSizes.OrderBy(x => x))
                {
                    foreach (var it in SortAndLimit(outputs[n], options)) PrintEntry(it, options, n.ToString());
                }
            }
            return;
        }

        if (options.ShowSeparate)
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
        
        if (options.ShowMerged)
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
        }

        if (options.ShowTfidf)
        {
            if (options.ShowSectionHeader) Console.WriteLine();
            PrintTfidf(options);
        }
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
                if (!PassTextFilters(ngram, options.TextFilters)) continue;
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
                foreach (var docName in Program.DocumentNames)
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
                    var limitedDocList = ApplyMaxItemsCap(limited.ToList(), options, options.Sort == SortDirection.Asc ? "ascending by tfidf" : "descending by tfidf", out var tfidfDocMaxItemsNotice);

                    Console.WriteLine($"### {docName}");
                    Console.WriteLine();
                    if (options.ShowColumnHeader)
                        Console.WriteLine("COUNT   DF      IDF     TFIDF   PHRASE");

                    if (options.ShowTfidfPhrases)
                    {
                        foreach (var r in limitedDocList)
                            Console.WriteLine($"{r.count,-7} {r.docFreq,-7} {r.idf,-7:F2} {r.tfidf,-7:F2} {r.ngram}");
                    }
                    if (tfidfDocMaxItemsNotice != null) Console.WriteLine(tfidfDocMaxItemsNotice);
                    Console.WriteLine();
                }
            }
        }
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
        foreach (var b in buckets)
        {
            int count = 0;
            while (idx < sortedFrequencies.Length && sortedFrequencies[idx] <= b.hi)
            {
                if (sortedFrequencies[idx] >= b.lo) count++;
                idx++;
            }
            if (count == 0) continue;
            double pct = total > 0 ? (double)count / total * 100.0 : 0.0;
            Console.WriteLine($"{count,-9} {b.label,-11} {pct:F1}%");
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
                
                // Include all items tied with the boundary value — but ONLY when sorting by
                // integer count. PPM is a continuous/floating value and, especially at the
                // low-frequency (long-tail) end of real text, thousands of distinct n-grams can
                // share the exact same rounded ppm — tie-expansion there silently blows top:N
                // out to the entire result set. So for ppm-based limiting, honor N strictly.
                if (usePpm)
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
                IEnumerable<(string ngram, int count, double ppm, double z, double pmi)> bottomItems;
                if (usePpm)
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

    static bool PassTextFilters(string ngram, List<TextFilter> filters)
    {
        string ngramLower = ngram.ToLower();
        
        foreach (var f in filters)
        {
            // If we have a compiled regex, use it for Contains and NotContains filters
            if (f.CompiledRegex != null)
            {
                bool match = f.CompiledRegex.IsMatch(ngramLower);
                
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
                    if (!ngramLower.Contains(f.Pattern.ToLower())) return false;
                    break;
                case TextFilter.TypeEnum.NotContains:
                    if (ngramLower.Contains(f.Pattern.ToLower())) return false;
                    break;
                case TextFilter.TypeEnum.StartsWith:
                    if (!ngramLower.StartsWith(f.Pattern.ToLower())) return false;
                    break;
                case TextFilter.TypeEnum.NotStartsWith:
                    if (ngramLower.StartsWith(f.Pattern.ToLower())) return false;
                    break;
                case TextFilter.TypeEnum.EndsWith:
                    if (!ngramLower.EndsWith(f.Pattern.ToLower())) return false;
                    break;
                case TextFilter.TypeEnum.NotEndsWith:
                    if (ngramLower.EndsWith(f.Pattern.ToLower())) return false;
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

    static CommandOptions ParseArgs(string[] args)
    {
        var options = new CommandOptions();

        // Used by --files parsing: decides where the glob list for --files ends,
        // i.e. the next recognized ngc token (n-gram size, filter prefix, keyword,
        // or anything starting with "-"/"--"). Everything before that is a glob.
        bool IsLikelyOptionToken(string tok)
        {
            if (tok.StartsWith("-")) return true; // "--show-x", "--contains", "--", "---", etc.
            if (tok == "+" || tok == "++" || tok == "+++") return true;
            if (tok == "asc" || tok == "desc" || tok == "rev") return true;
            if (Regex.IsMatch(tok, @"^\d+$")) return true;                 // n-gram size
            if (Regex.IsMatch(tok, @"^\d+\.\.\d+$")) return true;          // n-gram range
            if (tok.Contains(",") && Regex.IsMatch(tok, @"^[\d,]+$")) return true; // n-gram list
            if (Regex.IsMatch(tok, @"^(freq|ppm|z|pmi|tfidf|cdf|sort|top|bottom):")) return true;
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
                options.ShowMerged = true; 
                options.ShowSeparate = true;  
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
            Console.WriteLine("  ngc 1 top:30 rev             # Most frequent terms, highest to lowest");
            Console.WriteLine("  ngc 2..3 --contains \"pattern\" rev       # 2-3 word phrases containing \"pattern\"");
            Console.WriteLine("  ngc 1 cdf:5 +++              # Top/bottom 5% by frequency, with metrics");
            Console.WriteLine("  ngc 1 --show-pdf             # Frequency histogram (how many words occur N times)");
            Console.WriteLine("  ngc 1 --show-cdf             # Percentile ladder (cumulative distribution)");
            Console.WriteLine("  ngc 2 --show-pmi rev top:20  # Bigrams glued together more than chance predicts");
            Console.WriteLine("  ngc 1 --files \"*.md\" --show-tfidf rev   # Distinctive terms across many files");
            
            Console.WriteLine("\nINPUT SOURCE:");
            Console.WriteLine("  (stdin)                  # Default: read piped text as one combined blob");
            Console.WriteLine("  --files glob [glob ...]  # Read one or more files/globs instead of stdin");
            Console.WriteLine("                           # Each matched file becomes its own \"document\" —");
            Console.WriteLine("                           # required for --show-tfidf and --per-file.");
            Console.WriteLine("                           # Everything else (PDF/CDF/PMI/etc.) still works,");
            Console.WriteLine("                           # computed across the combined token pool.");
            Console.WriteLine("  Examples:");
            Console.WriteLine("    ngc 1 --files \"*.md\" top:20 rev");
            Console.WriteLine("    ngc 2 --files \"src/**/*.cs\" \"docs/**/*.md\" rev");
            Console.WriteLine("    ngc 1 --files \"../other-repo/**/*.cs\" top:20 rev   # relative ../ globs work");
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
            Console.WriteLine("    --contains \"pattern\"        # Include phrases containing \"pattern\"");
            Console.WriteLine("    --remove-contains \"pattern\" # Exclude phrases containing \"pattern\"");
            Console.WriteLine("    --starts \"pattern\"          # Include phrases starting with \"pattern\"");
            Console.WriteLine("    --remove-starts \"pattern\"   # Exclude phrases starting with \"pattern\"");
            Console.WriteLine("    --ends \"pattern\"            # Include phrases ending with \"pattern\"");
            Console.WriteLine("    --remove-ends \"pattern\"     # Exclude phrases ending with \"pattern\"");
            Console.WriteLine("    --exclude-file file.txt     # Exclude phrases containing any term listed in file");
            
            Console.WriteLine("\nFREQUENCY FILTERS:");
            Console.WriteLine("  (all of these also work as `--freq VALUE`, e.g. `--freq 10+` == `freq:10+`)");
            Console.WriteLine("  freq:10+    # Frequency ≥ 10 occurrences");
            Console.WriteLine("  freq:5..20  # Between 5 and 20 occurrences");
            Console.WriteLine("  freq:..20   # Frequency ≤ 20 occurrences");
            Console.WriteLine("  freq:!10+   # Less than 10 occurrences");
            Console.WriteLine("  freq:!5..20 # Outside the range 5-20 occurrences");
            Console.WriteLine("  freq:10     # Exactly 10 occurrences");
            
            Console.WriteLine("\nDESCRIPTIVE FILTERS (apply to YOUR input only):");
            Console.WriteLine("  (cdf/ppm/z/pmi/tfidf also work as `--cdf VALUE`, `--ppm VALUE`, etc.)");
            Console.WriteLine("  cdf:90+     # Top 10% most frequent items (was 'percentile:', renamed)");
            Console.WriteLine("  cdf:..50    # Bottom 50% of items");
            Console.WriteLine("  cdf:25..75  # Middle 50% of items (interquartile range)");
            Console.WriteLine("  cdf:!25..75 # Outside the middle range (potential outliers)");
            Console.WriteLine("  cdf:5       # Top/bottom 5% by frequency in YOUR input (not 'outliers')");
            Console.WriteLine("  ");
            Console.WriteLine("  ppm:1000+          # At least 1000 occurrences per million tokens");
            Console.WriteLine("  ppm:500..1000      # Between 500-1000 occurrences per million");
            Console.WriteLine("  ppm:..100          # At most 100 occurrences per million");
            Console.WriteLine("  ");
            Console.WriteLine("  z:2                # Within 2 standard deviations of mean (typical)");
            Console.WriteLine("  z:!2               # Outside 2 standard deviations (unusual)");
            Console.WriteLine("  ");
            Console.WriteLine("  pmi:2+             # Pointwise Mutual Information ≥ 2 (occurs ≥4x more than");
            Console.WriteLine("                     # chance predicts from the words' own frequencies — a real");
            Console.WriteLine("                     # \"glued together\" collocation, e.g. \"customer obsession\")");
            Console.WriteLine("  pmi:!0             # PMI below 0 (occurs LESS than chance — anti-collocation)");
            Console.WriteLine("                     # Only meaningful for n-grams with n >= 2.");
            Console.WriteLine("  ");
            Console.WriteLine("  tfidf:20+          # TF-IDF score ≥ 20 (requires --files + --show-tfidf)");
            Console.WriteLine("  tfidf:5..50        # TF-IDF between 5 and 50");
            Console.WriteLine("  tfidf:..10         # TF-IDF ≤ 10 (common-everywhere terms, low distinctiveness)");
            
            Console.WriteLine("\nSHOW / HIDE FLAGS (fine-grained control over report sections & columns):");
            Console.WriteLine("  Report sections:");
            Console.WriteLine("    --show-input / --hide-input                # Chars/Lines/Words block");
            Console.WriteLine("    --show-section-header / --hide-section-header  # '## N-grams Results'");
            Console.WriteLine("    --show-summary / --hide-summary            # unique/filtered/retained %");
            Console.WriteLine("    --show-freq-stats / --hide-freq-stats      # min/max/median/avg/90%<");
            Console.WriteLine("    --show-ppm-stats / --hide-ppm-stats");
            Console.WriteLine("    --show-column-header / --hide-column-header");
            Console.WriteLine("    --show-phrases / --hide-phrases            # the ngram list body");
            Console.WriteLine("    --show-tfidf-phrases / --hide-tfidf-phrases # the TF-IDF table body (separate)");
            Console.WriteLine("    --show-merged / --hide-merged              # combined-size section");
            Console.WriteLine("    --show-separate / --hide-separate          # per-size sections");
            Console.WriteLine("  Per-item columns:");
            Console.WriteLine("    --show-count / --hide-count");
            Console.WriteLine("    --show-ppm / --hide-ppm");
            Console.WriteLine("    --show-z / --hide-z");
            Console.WriteLine("  New reports (see REPORTS section below):");
            Console.WriteLine("    --show-pdf / --hide-pdf");
            Console.WriteLine("    --show-cdf / --hide-cdf");
            Console.WriteLine("    --show-pmi / --hide-pmi     # adds a PMI column to phrase rows");
            Console.WriteLine("    --show-tfidf / --hide-tfidf # requires --files");
            Console.WriteLine("  ");
            Console.WriteLine("  Explicit flags always override whatever a preset (+/++/+++/--/---) set,");
            Console.WriteLine("  regardless of order, e.g.:  ngc 1 +++ --hide-z   (detailed, but no Z column)");
            
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
            
            Console.WriteLine("\nOUTPUT OPTIONS - a single monotonic verbosity ladder (each level a strict");
            Console.WriteLine("superset of the previous one: more '+' = more detail, more '-' = less detail.");
            Console.WriteLine("These are presets/aliases for bundles of the --show/--hide flags above, and");
            Console.WriteLine("apply uniformly across ALL sections (n-grams, PDF, CDF, PMI, TF-IDF):");
            Console.WriteLine("  ---                # Ultra-minimal: bare phrase only, nothing else");
            Console.WriteLine("  --                 # Minimal: phrase + count only");
            Console.WriteLine("  (default)          # phrase + count + summary/freq-stats/section-header");
            Console.WriteLine("  +                  # default + ppm column, ppm-stats, column-header");
            Console.WriteLine("  ++                 # '+' + merged section (compare multiple n-gram sizes)");
            Console.WriteLine("  +++                # '++' + z-score column (full detail)");
            Console.WriteLine("  ");
            Console.WriteLine("  Explicit --show-x/--hide-x flags always override whatever a preset set,");
            Console.WriteLine("  regardless of order, e.g.:  ngc 1 +++ --hide-z   (detailed, but no Z column)");
            Console.WriteLine("  ");
            Console.WriteLine("  --asc / asc             # Sort ascending (least frequent first)");
            Console.WriteLine("  --desc / --rev / desc/rev # Sort descending (most frequent first)");
            Console.WriteLine("  --sort count / sort:count # Sort by raw count (default)");
            Console.WriteLine("  --sort ppm / sort:ppm     # Sort by normalized frequency (parts per million - NOT statistical significance!)");
            Console.WriteLine("  ");
            Console.WriteLine("  --top 50 / top:50       # Show only top 50 most frequent results");
            Console.WriteLine("  --top 10% / top:10%     # Show top 10% of results");
            Console.WriteLine("  --bottom 20 / bottom:20 # Show only bottom 20 least frequent results");
            Console.WriteLine("  --bottom 25% / bottom:25% # Show bottom 25% of results");
            Console.WriteLine("  (top:N/bottom:N tie-expand to include all items tied with the boundary count —");
            Console.WriteLine("   but only when sorting by raw count. With sort:ppm, N is honored exactly, since");
            Console.WriteLine("   ppm is continuous and long-tail text can have thousands of items tied at the");
            Console.WriteLine("   same low ppm, which would otherwise blow top:N out to nearly the whole list.)");
            Console.WriteLine("  ");
            Console.WriteLine("  (--freq/--ppm/--z/--pmi/--tfidf/--cdf all have '--flag VALUE' forms too,");
            Console.WriteLine("   e.g. `--freq 10+` is the same as `freq:10+` — pick whichever reads better.)");
            Console.WriteLine("  ");
            Console.WriteLine("  --max-items 200    # Default cap on phrase rows per section when you have");
            Console.WriteLine("                     # not given an explicit top:/bottom:. If the filtered");
            Console.WriteLine("                     # result set is bigger, ngc trims it and prints a notice");
            Console.WriteLine("                     # (at the END of the list, after it scrolls) telling you");
            Console.WriteLine("                     # how to see more.");
            Console.WriteLine("  --max-items 0      # Unlimited - never trim, no matter how big the result set");
            
            Console.WriteLine("\nANALYSIS STRATEGIES:");
            Console.WriteLine("  ");
            Console.WriteLine("  # Exploratory Analysis (Start Here)");
            Console.WriteLine("  ngc 1 top:30 rev                    # Most frequent terms in your input");
            Console.WriteLine("  ngc 2 cdf:95+ rev                   # Top 5% most frequent phrases in your input");
            Console.WriteLine("  ngc 1 cdf:5 rev +++                 # Top/bottom 5% by frequency, with metrics");
            Console.WriteLine("  ngc 1 --show-pdf                    # See the overall shape of the distribution");
            Console.WriteLine("  ");
            Console.WriteLine("  # Collocation / Phrase Discovery");
            Console.WriteLine("  ngc 2 --show-pmi rev top:30          # Which bigrams are 'real phrases', not chance");
            Console.WriteLine("  ngc 3 --show-pmi pmi:2+ freq:5+ rev  # Strongly glued trigrams, filtered to real signal");
            Console.WriteLine("  ");
            Console.WriteLine("  # Distinctiveness Across Documents (requires --files)");
            Console.WriteLine("  ngc 2 --files \"*.md\" --show-tfidf rev top:30   # What's distinctive, not just frequent");
            Console.WriteLine("  ngc 2 --files \"*.md\" --show-tfidf --per-file   # Each file's own top distinctive terms");
            Console.WriteLine("  ");
            Console.WriteLine("  # Code Structure Analysis");
            Console.WriteLine("  ngc 2 --contains \"class [A-Z]\" rev             # Find class definitions");
            Console.WriteLine("  ngc 3 --contains \"public (class|interface)\" rev # Find public type definitions");
            Console.WriteLine("  ngc 3 --contains \"new [A-Z]\" sort:ppm rev      # Object instantiation by normalized frequency");
            Console.WriteLine("  ngc 2 --contains \"import|using\" top:20 rev     # Most frequent dependencies");
            Console.WriteLine("  ");
            Console.WriteLine("  # Frequency Pattern Discovery");
            Console.WriteLine("  ngc 3 --contains \"if\" z:!2 freq:5+ rev         # 'if' patterns >2 std devs from mean, 5+ occurrences");
            Console.WriteLine("  ngc 2 --contains \"null\" cdf:95+ rev            # Top 5% most frequent null-related patterns");
            Console.WriteLine("  ngc 3 --contains \"try catch\" sort:ppm rev      # Error handling patterns by normalized frequency");
            Console.WriteLine("  ngc 2 --contains \"TODO|FIXME\" rev              # Find technical debt markers");
            Console.WriteLine("  ");
            Console.WriteLine("  # Documentation Analysis");
            Console.WriteLine("  ngc 3 --contains \"should\" cdf:80+ rev          # Top 20% frequent 'should' phrases");
            Console.WriteLine("  ngc 2 --remove-contains \"^(the|a|an|of|in)$\" cdf:95+   # Frequent terms excluding common words");
            Console.WriteLine("  ngc 3 --contains \"Inconsistencies|Issues\" rev  # Find problem-related phrases");
            Console.WriteLine("  ngc 2 --contains \"is|are\" z:!2 freq:5+ +++     # Definition patterns >2 std devs, with metrics");
            
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
            Console.WriteLine("  3. Use '+++' to see full metrics when results seem incorrect");
            Console.WriteLine("  4. Validate against different samples: Same pattern in diverse sources = stronger evidence");
            
            Console.WriteLine("\nCOMMON COMBINATIONS:");
            Console.WriteLine("  ngc 1..3 cdf:5 rev                  # Statistical outliers across different n-gram sizes");
            Console.WriteLine("  ngc 2 --remove-contains \"^(the|a|an|of|in)$\" sort:ppm  # Meaningful phrases sorted by statistical significance");
            Console.WriteLine("  ngc 3 --contains \"pattern\" z:!2 freq:5+        # Unusual but recurring patterns containing \"pattern\"");
            Console.WriteLine("  ngc 2..3 z:!2 freq:5+ ++            # Statistically significant phrases of different lengths");
            Console.WriteLine("  ngc 2 --files \"*.md\" --show-tfidf --show-pmi rev  # Distinctive AND glued-together phrases");
            
            Console.WriteLine("\nTIPS FOR EFFECTIVE ANALYSIS:");
            Console.WriteLine("  1. Start broad, then narrow: Begin with `ngc 1 top:50 rev` to get an overview");
            Console.WriteLine("  2. Use cdf filters for deeper insights: `cdf:5` is more revealing than just `top:N`");
            Console.WriteLine("  3. Look for both common and rare patterns: Outliers (`z:!2`) often reveal key insights");
            Console.WriteLine("  4. Combine with grep for further filtering: Pipe ngc output to grep to find specific terms");
            Console.WriteLine("  5. Statistical metrics reveal more than raw counts: Use `+++` and `sort:ppm` to find significance");
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

            // Normalize the "--flag VALUE" form into the existing "flag:VALUE" token so all
            // existing parsing below (top:, bottom:, freq:, ppm:, z:, pmi:, tfidf:, cdf:, sort:)
            // keeps working unchanged for both spellings. The bare colon-form still works too.
            var valueFlagToColonPrefix = new Dictionary<string, string> {
                { "--top", "top:" }, { "--bottom", "bottom:" }, { "--sort", "sort:" },
                { "--freq", "freq:" }, { "--ppm", "ppm:" }, { "--z", "z:" },
                { "--pmi", "pmi:" }, { "--tfidf", "tfidf:" }, { "--cdf", "cdf:" },
            };
            if (valueFlagToColonPrefix.TryGetValue(a, out var colonPrefix) && i + 1 < args.Length)
            {
                i++;
                a = colonPrefix + args[i];
            }
            else if (a == "--asc") { a = "asc"; }
            else if (a == "--desc" || a == "--rev") { a = "rev"; }

            if (a == "---") {
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
                options.ShowMerged = false;
                continue;
            }
            if (a == "--") {
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
                options.ShowMerged = false;
                continue;
            }
            if (a == "+") {
                // Default + ppm column, ppm stats, column header.
                options.Mode = OutputMode.Enhanced;
                options.ShowPpm = true;
                options.ShowPpmStats = true;
                options.ShowColumnHeader = true;
                continue;
            }
            if (a == "++") {
                // '+' plus a merged section combining multiple n-gram sizes.
                options.Mode = OutputMode.Both;
                options.ShowMerged = true;
                options.ShowPpm = true;
                options.ShowPpmStats = true;
                options.ShowColumnHeader = true;
                continue;
            }
            if (a == "+++") {
                // '++' plus the Z-score column (full detail — the "kitchen sink").
                options.Mode = OutputMode.Detailed;
                options.ShowMerged = true;
                options.ShowPpm = true;
                options.ShowPpmStats = true;
                options.ShowColumnHeader = true;
                options.ShowZ = true;
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
            if (a == "--show-column-header") { options.ShowColumnHeader = true; continue; }
            if (a == "--hide-column-header") { options.ShowColumnHeader = false; continue; }
            if (a == "--show-phrases") { options.ShowPhrases = true; continue; }
            if (a == "--hide-phrases") { options.ShowPhrases = false; continue; }
            if (a == "--show-tfidf-phrases") { options.ShowTfidfPhrases = true; continue; }
            if (a == "--hide-tfidf-phrases") { options.ShowTfidfPhrases = false; continue; }
            if (a == "--show-merged") { options.ShowMerged = true; continue; }
            if (a == "--hide-merged") { options.ShowMerged = false; continue; }
            if (a == "--show-separate") { options.ShowSeparate = true; continue; }
            if (a == "--hide-separate") { options.ShowSeparate = false; continue; }
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
            if (a == "--per-file") { options.PerFile = true; continue; }
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
            if (a == "asc") { options.Sort = SortDirection.Asc; continue; }
            if (a == "desc" || a == "rev") { options.Sort = SortDirection.Desc; continue; }
            if (a == "sort:count") { options.SortBy = SortBy.Count; continue; }
            if (a == "sort:ppm") { options.SortBy = SortBy.Ppm; continue; }
            if (a.StartsWith("top:")) { 
                string value = a.Substring(4);
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
            if (a.StartsWith("bottom:")) { 
                string value = a.Substring(7);
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
                int n = int.Parse(a); options.NGramSizes = new List<int> { n }; options.ShowMerged = false; options.ShowSeparate = true; continue;
            }
            if (Regex.IsMatch(a, @"^\d+\.\.\d+$"))
            {
                var p = a.Split(new[] { ".." }, StringSplitOptions.None);
                int s = int.Parse(p[0]); int e = int.Parse(p[1]); options.NGramSizes = Enumerable.Range(s, e - s + 1).ToList(); options.ShowMerged = false; options.ShowSeparate = true; continue;
            }
            if (a.Contains(",") && Regex.IsMatch(a, @"^[\d,]+$"))
            {
                var parts = a.Split(','); options.NGramSizes = parts.Select(x => int.Parse(x)).ToList(); options.ShowMerged = false; options.ShowSeparate = true; continue;
            }
            // exclude file
            if (a == "--exclude-file" && i + 1 < args.Length) { i++; options.ExcludeFiles.Add(args[i]); continue; }
            // explicit, unambiguous filter flags (preferred when combining with --files)
            if (a == "--contains" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.Contains, Pattern = p };
                if (TryCompileRegex(p.ToLower(), out Regex rgx)) f.CompiledRegex = rgx;
                options.TextFilters.Add(f); continue;
            }
            if (a == "--remove-contains" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.NotContains, Pattern = p };
                if (TryCompileRegex(p.ToLower(), out Regex rgx)) f.CompiledRegex = rgx;
                options.TextFilters.Add(f); continue;
            }
            if (a == "--starts" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.StartsWith, Pattern = p };
                if (TryCompileRegex(p.ToLower(), out Regex rgx)) f.CompiledRegex = rgx;
                options.TextFilters.Add(f); continue;
            }
            if (a == "--remove-starts" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.NotStartsWith, Pattern = p };
                if (TryCompileRegex(p.ToLower(), out Regex rgx)) f.CompiledRegex = rgx;
                options.TextFilters.Add(f); continue;
            }
            if (a == "--ends" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.EndsWith, Pattern = p };
                if (TryCompileRegex(p.ToLower(), out Regex rgx)) f.CompiledRegex = rgx;
                options.TextFilters.Add(f); continue;
            }
            if (a == "--remove-ends" && i + 1 < args.Length) {
                i++; var p = args[i];
                var f = new TextFilter { Type = TextFilter.TypeEnum.NotEndsWith, Pattern = p };
                if (TryCompileRegex(p.ToLower(), out Regex rgx)) f.CompiledRegex = rgx;
                options.TextFilters.Add(f); continue;
            }
            // max-items cap (default 200; 0 = unlimited); only applies when user gave no explicit top:/bottom:
            if (a == "--max-items" && i + 1 < args.Length) {
                i++; if (int.TryParse(args[i], out int mi)) { options.MaxItems = mi; options.MaxItemsSetExplicitly = true; }
                continue;
            }
            // NOTE: bare/implicit content-filter syntax ("pattern", -"pattern", "pattern..",
            // "..pattern", "!word..", "!..word") has been removed entirely. Content filters
            // now REQUIRE one of the explicit flags: --contains, --remove-contains, --starts,
            // --remove-starts, --ends, --remove-ends. This closes the "silent absorption" bug
            // where any unrecognized/mistyped token (a flag typo, a stray word, etc.) used to
            // fall through and become a no-op Contains filter instead of an error.
            // frequency patterns with prefix
            if (a.StartsWith("freq:"))
            {
                var freqExpr = a.Substring(5);
                if (Regex.IsMatch(freqExpr, @"^\d+$")) { int exactFreq = int.Parse(freqExpr); options.FrequencyFilters.Add(new FrequencyFilter { Min = exactFreq, Max = exactFreq, Outside = false }); continue; }
                if (Regex.IsMatch(freqExpr, @"^\d+\.\.\d+$")) { var pp = freqExpr.Split(new[] { ".." }, StringSplitOptions.None); options.FrequencyFilters.Add(new FrequencyFilter { Min = int.Parse(pp[0]), Max = int.Parse(pp[1]), Outside = false }); continue; }
                if (Regex.IsMatch(freqExpr, @"^\d+\+$")) { options.FrequencyFilters.Add(new FrequencyFilter { Min = int.Parse(freqExpr.TrimEnd('+')), Max = null, Outside = false }); continue; }
                if (Regex.IsMatch(freqExpr, @"^\.\.\d+$")) { options.FrequencyFilters.Add(new FrequencyFilter { Min = null, Max = int.Parse(freqExpr.Substring(2)), Outside = false }); continue; }
                if (Regex.IsMatch(freqExpr, @"^!\d+\+$")) { options.FrequencyFilters.Add(new FrequencyFilter { Min = null, Max = int.Parse(freqExpr.Substring(1).TrimEnd('+')) - 1, Outside = false }); continue; }
                if (Regex.IsMatch(freqExpr, @"^!\d+\.\.\d+$")) { var pp = freqExpr.Substring(1).Split(new[] { ".." }, StringSplitOptions.None); options.FrequencyFilters.Add(new FrequencyFilter { Min = int.Parse(pp[0]), Max = int.Parse(pp[1]), Outside = true }); continue; }
            }
            
            // legacy frequency patterns (keep for backward compatibility)
            if (Regex.IsMatch(a, @"^\d+$") && !a.StartsWith("-") && !options.NGramSizes.Contains(int.Parse(a))) { int exactFreq = int.Parse(a); options.FrequencyFilters.Add(new FrequencyFilter { Min = exactFreq, Max = exactFreq, Outside = false }); continue; }
            if (Regex.IsMatch(a, @"^\d+\.\.\d+$")) { var pp = a.Split(new[] { ".." }, StringSplitOptions.None); options.FrequencyFilters.Add(new FrequencyFilter { Min = int.Parse(pp[0]), Max = int.Parse(pp[1]), Outside = false }); continue; }
            if (Regex.IsMatch(a, @"^\d+\+$")) { options.FrequencyFilters.Add(new FrequencyFilter { Min = int.Parse(a.TrimEnd('+')), Max = null, Outside = false }); continue; }
            if (Regex.IsMatch(a, @"^\.\.\d+$")) { options.FrequencyFilters.Add(new FrequencyFilter { Min = null, Max = int.Parse(a.Substring(2)), Outside = false }); continue; }
            if (Regex.IsMatch(a, @"^!\d+\+$")) { options.FrequencyFilters.Add(new FrequencyFilter { Min = null, Max = int.Parse(a.Substring(1).TrimEnd('+')) - 1, Outside = false }); continue; }
            if (Regex.IsMatch(a, @"^!\d+\.\.\d+$")) { var pp = a.Substring(1).Split(new[] { ".." }, StringSplitOptions.None); options.FrequencyFilters.Add(new FrequencyFilter { Min = int.Parse(pp[0]), Max = int.Parse(pp[1]), Outside = true }); continue; }
            // ppm patterns
            if (a.StartsWith("ppm:"))
            {
                var p = a.Substring(4);
                if (Regex.IsMatch(p, @"^\d+(\.\d+)?$") && !p.EndsWith("+")) { double exactPpm = double.Parse(p); options.PpmFilters.Add(new PpmFilter { Min = exactPpm, Max = exactPpm, Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^\d+\.\.\d+$")) { var pp = p.Split(new[] { ".." }, StringSplitOptions.None); options.PpmFilters.Add(new PpmFilter { Min = double.Parse(pp[0]), Max = double.Parse(pp[1]), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^\d+\+$")) { options.PpmFilters.Add(new PpmFilter { Min = double.Parse(p.TrimEnd('+')), Max = null, Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^\.\.\d+$")) { options.PpmFilters.Add(new PpmFilter { Min = null, Max = double.Parse(p.Substring(2)), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^!\d+\+$")) { options.PpmFilters.Add(new PpmFilter { Min = null, Max = double.Parse(p.Substring(1).TrimEnd('+')), Outside = true }); continue; }
                if (Regex.IsMatch(p, @"^!\d+\.\.\d+$")) { var pp = p.Substring(1).Split(new[] { ".." }, StringSplitOptions.None); options.PpmFilters.Add(new PpmFilter { Min = double.Parse(pp[0]), Max = double.Parse(pp[1]), Outside = true }); continue; }
            }
            // z patterns
            if (a.StartsWith("z:"))
            {
                var p = a.Substring(2);
                if (Regex.IsMatch(p, @"^\d+(\.\d+)?\.\.\d+(\.\d+)?$")) { var pp = p.Split(new[] { ".." }, StringSplitOptions.None); options.ZFilters.Add(new ZFilter { Min = double.Parse(pp[0]), Max = double.Parse(pp[1]), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^\d+(\.\d+)?$")) { options.ZFilters.Add(new ZFilter { Min = -double.Parse(p), Max = double.Parse(p), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^!\d+(\.\d+)?$")) { options.ZFilters.Add(new ZFilter { Min = double.Parse(p.Substring(1)), Max = null, Outside = true }); continue; }
            }
            // pmi patterns (log2 observed/expected; can be negative, so allow leading '-')
            if (a.StartsWith("pmi:"))
            {
                var p = a.Substring(4);
                if (Regex.IsMatch(p, @"^-?\d+(\.\d+)?\.\.-?\d+(\.\d+)?$")) { var pp = p.Split(new[] { ".." }, StringSplitOptions.None); options.PmiFilters.Add(new PmiFilter { Min = double.Parse(pp[0]), Max = double.Parse(pp[1]), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^-?\d+(\.\d+)?\+$")) { options.PmiFilters.Add(new PmiFilter { Min = double.Parse(p.TrimEnd('+')), Max = null, Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^!-?\d+(\.\d+)?\+$")) { options.PmiFilters.Add(new PmiFilter { Min = null, Max = double.Parse(p.Substring(1).TrimEnd('+')), Outside = true }); continue; }
                if (Regex.IsMatch(p, @"^!-?\d+(\.\d+)?$")) { options.PmiFilters.Add(new PmiFilter { Min = null, Max = double.Parse(p.Substring(1)), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^-?\d+(\.\d+)?$")) { double exactPmi = double.Parse(p); options.PmiFilters.Add(new PmiFilter { Min = exactPmi, Max = exactPmi, Outside = false }); continue; }
            }
            // tfidf patterns (always >= 0 in practice, but allow same shape as pmi minus negative)
            if (a.StartsWith("tfidf:"))
            {
                var p = a.Substring(6);
                if (Regex.IsMatch(p, @"^\d+(\.\d+)?\.\.\d+(\.\d+)?$")) { var pp = p.Split(new[] { ".." }, StringSplitOptions.None); options.TfidfFilters.Add(new TfidfFilter { Min = double.Parse(pp[0]), Max = double.Parse(pp[1]), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^\d+(\.\d+)?\+$")) { options.TfidfFilters.Add(new TfidfFilter { Min = double.Parse(p.TrimEnd('+')), Max = null, Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^\.\.\d+(\.\d+)?$")) { options.TfidfFilters.Add(new TfidfFilter { Min = null, Max = double.Parse(p.Substring(2)), Outside = false }); continue; }
                if (Regex.IsMatch(p, @"^!\d+(\.\d+)?\+$")) { options.TfidfFilters.Add(new TfidfFilter { Min = null, Max = double.Parse(p.Substring(1).TrimEnd('+')), Outside = true }); continue; }
                if (Regex.IsMatch(p, @"^!\d+(\.\d+)?\.\.\d+(\.\d+)?$")) { var pp = p.Substring(1).Split(new[] { ".." }, StringSplitOptions.None); options.TfidfFilters.Add(new TfidfFilter { Min = double.Parse(pp[0]), Max = double.Parse(pp[1]), Outside = true }); continue; }
                if (Regex.IsMatch(p, @"^\d+(\.\d+)?$")) { double exactTfidf = double.Parse(p); options.TfidfFilters.Add(new TfidfFilter { Min = exactTfidf, Max = exactTfidf, Outside = false }); continue; }
            }            // cdf patterns (formerly "percentile:")
            if (a.StartsWith("cdf:"))
            {
                var p = a.Substring(4);
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
            }
            // Any token that reaches here didn't match a known flag/filter/n-gram-size/preset —
            // it is NOT silently absorbed as a content filter anymore. Error out loudly instead,
            // so typos (like "--by-file" instead of "--per-file") or stray words fail immediately
            // rather than quietly becoming a no-op filter that matches nothing.
            Console.Error.WriteLine($"Unrecognized argument: {a}");
            Console.Error.WriteLine("Content filters now require an explicit flag: --contains, --remove-contains, --starts, --remove-starts, --ends, or --remove-ends.");
            Console.Error.WriteLine("Run with --help to see available options.");
            Environment.Exit(1);
        }
        return options;
    }
}
