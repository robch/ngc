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

class PercentileFilter
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
    public List<PercentileFilter> PercentileFilters = new List<PercentileFilter>();
    public SortDirection Sort = SortDirection.Asc;
    public SortBy SortBy = SortBy.Count;
    public OutputMode Mode = OutputMode.Default;
    public int Limit = int.MaxValue;
    public bool LimitIsPercentage = false;
    public double LimitPercentage = 0;
    public int BottomLimit = 0;
    public bool BottomLimitIsPercentage = false;
    public double BottomLimitPercentage = 0;
    public bool MinimalOutput = false;
    public bool StatsOnly = false; // Only show statistics, not full phrase lists
    public List<double> UniquePercentiles = new List<double>();
    public List<string> ExcludeFiles = new List<string>();
}

class Program
{
    // Static properties to maintain state for percentile filtering
    public static CommandOptions CurrentOptions { get; set; }
    public static bool PercentilesAreSorted { get; set; } = false;
    
    // Helper method to detect if a pattern contains regex special characters and compile it
    private static bool TryCompileRegex(string pattern, out Regex regex)
    {
        regex = null;
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

        // Read input
        string input = Console.In.ReadToEnd();

        // Tokenize and count total tokens for ppm
        var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var totalTokensPerN = new Dictionary<int, int>();
        foreach (int n in options.NGramSizes) totalTokensPerN[n] = 0;

        var nGramCounts = new Dictionary<int, Dictionary<string, int>>();
        foreach (int n in options.NGramSizes) nGramCounts[n] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        // Input statistics tracking
        int totalChars = input.Length;
        int totalLines = lines.Length;
        int totalWords = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var sb = new StringBuilder(line.Length);
            foreach (var ch in line)
            {
                if (char.IsLetterOrDigit(ch) || ch == '-') sb.Append(ch); else sb.Append(' ');
            }
            var words = sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            totalWords += words.Length;
            for (int n = 1; n <= (options.NGramSizes.Count > 0 ? options.NGramSizes.Max() : 3); n++)
            {
                if (!options.NGramSizes.Contains(n)) continue;
                if (words.Length < n) continue;
                totalTokensPerN[n] += Math.Max(0, words.Length - n + 1);
                for (int i = 0; i <= words.Length - n; i++)
                {
                    var ngram = string.Join(" ", words.Skip(i).Take(n));
                    if (nGramCounts[n].ContainsKey(ngram)) nGramCounts[n][ngram]++; else nGramCounts[n][ngram] = 1;
                }
            }
        }

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
        Console.WriteLine($"Chars: {totalChars}\nLines: {totalLines}\nWords: {totalWords}");
        // Single blank line after input stats
        Console.WriteLine();

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

        bool needPpm = options.PpmFilters.Count > 0 || options.Mode == OutputMode.Enhanced || options.Mode == OutputMode.Detailed;
        bool needZ = options.ZFilters.Count > 0 || options.Mode == OutputMode.Detailed;

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

        // Apply filters per n and build output sets
        var outputs = new Dictionary<int, List<(string ngram, int count, double ppm, double z)>>();
        foreach (var n in options.NGramSizes)
        {
            var list = new List<(string, int, double, double)>();
            foreach (var kv in nGramCounts[n])
            {
                var ngram = kv.Key; var count = kv.Value; var ppm = needPpm ? ppmValues[n][ngram] : 0.0; var z = needZ ? zValues[n][ngram] : 0.0;
                if (!PassTextFilters(ngram, options.TextFilters)) continue;
                if (!PassFrequencyFilters(count, options.FrequencyFilters)) continue;
                if (!PassPpmFilters(ppm, options.PpmFilters)) continue;
                if (!PassZFilters(z, options.ZFilters)) continue;
                list.Add((ngram, count, ppm, z));
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
        var merged = new List<(string ngram, int count, double ppm, double z)>();
        if (options.ShowMerged || options.Mode == OutputMode.Both)
        {
            var dict = new Dictionary<string, (int count, double ppm, double z)>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in options.NGramSizes)
            {
                foreach (var item in outputs[n])
                {
                    if (dict.ContainsKey(item.ngram)) dict[item.ngram] = (dict[item.ngram].count + item.count, dict[item.ngram].ppm, dict[item.ngram].z);
                    else dict[item.ngram] = (item.count, item.ppm, item.z);
                }
            }
            foreach (var kv in dict) merged.Add((kv.Key, kv.Value.count, kv.Value.ppm, kv.Value.z));
        }

        // Output according to mode
        if (options.MinimalOutput)
        {
            // only ngrams lines from merged or per-bucket depending on ShowMerged/ShowSeparate
            if (options.ShowMerged)
            {
                foreach (var it in SortAndLimit(merged, options)) Console.WriteLine(it.ngram);
            }
            else
            {
                foreach (var n in options.NGramSizes.OrderBy(x => x))
                {
                    foreach (var it in SortAndLimit(outputs[n], options)) Console.WriteLine(it.ngram);
                }
            }
            return;
        }

        if (options.ShowSeparate)
        {
            foreach (var n in options.NGramSizes.OrderBy(x => x))
            {
                // Add a blank line before section header (except the first one)
                if (n != options.NGramSizes.OrderBy(x => x).First()) {
                    Console.WriteLine();
                }
                
                Console.WriteLine($"## {n}-grams Results");
                Console.WriteLine();
                
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
                Console.WriteLine($"Count: {preStats.uniqueCount} (unique), {preStats.uniqueCount - postFilterCount} (filtered), {postFilterCount} ({percentRetained:F1}%)");
                Console.WriteLine($"Freq: {minFreq}..{maxFreq}, {medianFreq} (median), {avgFreq} (avg), 90% < {p90Freq}");

                if (minPpm > 0 || maxPpm > 0)
                {
                    Console.WriteLine($"PPM: {minPpm:F0}..{maxPpm:F0}, {medianPpm:F0} (median), {avgPpm:F0} (avg)");
                }
                
                Console.WriteLine();
                
                // Add column headers for Enhanced and Detailed modes
                if (!options.StatsOnly) {
                    if (options.Mode == OutputMode.Enhanced)
                        Console.WriteLine("COUNT   PPM     PHRASE");
                    else if (options.Mode == OutputMode.Detailed)
                        Console.WriteLine("COUNT   PPM     Z      PHRASE");
                    
                    // Print final (post-filter) results for this n-gram size
                    foreach (var it in finalList) PrintEntry(it, options, n.ToString());
                }
            }
        }
        
        if (options.ShowMerged)
        {
            // Add a blank line before merged section
            Console.WriteLine();
            
            Console.WriteLine("## Merged N-grams Results");
            Console.WriteLine();

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
            Console.WriteLine($"Count: {finalMerged.Count} (unique)");
            Console.WriteLine($"Freq: {minFreq}..{maxFreq}, {medianFreq} (median), {avgFreq} (avg), 90% < {p90Freq}");

            if (minPpm > 0 || maxPpm > 0)
            {
                Console.WriteLine($"PPM: {minPpm:F0}..{maxPpm:F0}, {medianPpm:F0} (median), {avgPpm:F0} (avg)");
            }

            Console.WriteLine();

            // Add column headers for Enhanced and Detailed modes
            if (!options.StatsOnly) {
                if (options.Mode == OutputMode.Enhanced)
                    Console.WriteLine("COUNT   PPM     PHRASE");
                else if (options.Mode == OutputMode.Detailed)
                    Console.WriteLine("COUNT   PPM     Z      PHRASE");

                // Print final (post-filter) merged results
                foreach (var it in finalMerged)
                {
                    PrintEntry(it, options, "merged");
                }
            }
        }
    }

    static IEnumerable<(string ngram, int count, double ppm, double z)> SortAndLimit(IEnumerable<(string ngram, int count, double ppm, double z)> seq, CommandOptions options)
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
                return Enumerable.Empty<(string ngram, int count, double ppm, double z)>();
            
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
            var filteredItems = new List<(string ngram, int count, double ppm, double z)>();
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
        IEnumerable<(string ngram, int count, double ppm, double z)> limitedSeq = items;
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
                
                // Get the boundary value (count or ppm of the last item we'd include)
                var boundaryValue = usePpm
                    ? sortedItems[itemsToTake - 1].ppm
                    : sortedItems[itemsToTake - 1].count;
                
                // Include all items that match the boundary value
                limitedSeq = sortedItems.Where(x => 
                    usePpm
                        ? x.ppm >= boundaryValue
                        : x.count >= boundaryValue);
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
                
                // Get the boundary value (count or ppm of the last item we'd include)
                var boundaryValue = usePpm
                    ? sortedItems[itemsToTake - 1].ppm
                    : sortedItems[itemsToTake - 1].count;
                
                // Include all items that match the boundary value
                var bottomItems = sortedItems.Where(x => 
                    usePpm
                        ? x.ppm <= boundaryValue
                        : x.count <= boundaryValue);
                
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
        IOrderedEnumerable<(string ngram, int count, double ppm, double z)> sorted;
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

    static void PrintEntry((string ngram, int count, double ppm, double z) it, CommandOptions options, string tag)
    {
        switch (options.Mode)
        {
            case OutputMode.Default:
                Console.WriteLine($"{it.count}: {it.ngram}");
                break;
            case OutputMode.Enhanced:
                Console.WriteLine($"{it.count,-7} {it.ppm,-7:F0}  {it.ngram}");
                break;
            case OutputMode.Detailed:
                Console.WriteLine($"{it.count,-7} {it.ppm,-7:F0} {it.z,-5:F1}  {it.ngram}");
                break;
            case OutputMode.Both:
                Console.WriteLine($"{it.count}: {it.ngram}");
                break;
            default:
                Console.WriteLine($"{it.count}: {it.ngram}");
                break;
        }
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

    static CommandOptions ParseArgs(string[] args)
    {
        var options = new CommandOptions();

        // Check for help flag
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            // Check if input is being piped (no args but input is redirected)
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
            
            Console.WriteLine("NGramCounter (ngc) v1.0 - Statistical n-gram analysis for code and documentation");
            
            Console.WriteLine("\nQUICK START:");
            Console.WriteLine("  ngc 1 top:30 rev         # Most common terms, highest frequency first");
            Console.WriteLine("  ngc 2..3 \"pattern\" rev   # 2-3 word phrases containing \"pattern\"");
            Console.WriteLine("  ngc 1 percentile:5 +++   # Statistical outliers with detailed metrics");
            
            Console.WriteLine("\nN-GRAM SIZE:");
            Console.WriteLine("  3         # Only trigrams (3-word phrases)");
            Console.WriteLine("  2..4      # 2, 3, and 4-grams (analyze multiple sizes)");
            Console.WriteLine("  1,3,5     # 1, 3, and 5-grams only (specific sizes)");
            
            Console.WriteLine("\nCONTENT FILTERS (supports regex):");
            Console.WriteLine("  \"pattern\"  # Include phrases with \"pattern\"");
            Console.WriteLine("  -\"pattern\" # Exclude phrases with \"pattern\"");
            Console.WriteLine("  \"word..\"   # Phrases starting with \"word\"");
            Console.WriteLine("  \"..word\"   # Phrases ending with \"word\"");
            Console.WriteLine("  \"!word..\"  # Phrases NOT starting with \"word\"");
            Console.WriteLine("  \"!..word\"  # Phrases NOT ending with \"word\"");
            Console.WriteLine("  -@file.txt # Exclude phrases containing any terms in file");
            
            Console.WriteLine("\nFREQUENCY FILTERS:");
            Console.WriteLine("  freq:10+    # Frequency ≥ 10 occurrences");
            Console.WriteLine("  freq:5..20  # Between 5 and 20 occurrences");
            Console.WriteLine("  freq:..20   # Frequency ≤ 20 occurrences");
            Console.WriteLine("  freq:!10+   # Less than 10 occurrences");
            Console.WriteLine("  freq:!5..20 # Outside the range 5-20 occurrences");
            Console.WriteLine("  freq:10     # Exactly 10 occurrences");
            
            Console.WriteLine("\nSTATISTICAL FILTERS:");
            Console.WriteLine("  percentile:90+     # Top 10% most frequent items");
            Console.WriteLine("  percentile:..50    # Bottom 50% of items");
            Console.WriteLine("  percentile:25..75  # Middle 50% of items (interquartile range)");
            Console.WriteLine("  percentile:!25..75 # Outside the middle range (potential outliers)");
            Console.WriteLine("  percentile:5       # Statistical outliers (top AND bottom 5%)");
            Console.WriteLine("  ");
            Console.WriteLine("  ppm:1000+          # At least 1000 occurrences per million tokens");
            Console.WriteLine("  ppm:500..1000      # Between 500-1000 occurrences per million");
            Console.WriteLine("  ppm:..100          # At most 100 occurrences per million");
            Console.WriteLine("  ");
            Console.WriteLine("  z:2                # Within 2 standard deviations of mean (typical)");
            Console.WriteLine("  z:!2               # Outside 2 standard deviations (unusual)");
            
            Console.WriteLine("\nOUTPUT OPTIONS:");
            Console.WriteLine("  +                  # Enhanced output with PPM values");
            Console.WriteLine("  ++                 # Show both merged AND separate n-gram sizes");
            Console.WriteLine("  +++                # Full statistical details (PPM, Z-score)");
            Console.WriteLine("  --                 # Minimal output (phrases only)");
            Console.WriteLine("  ---                # Statistics only (no phrases)");
            Console.WriteLine("  ");
            Console.WriteLine("  asc                # Sort ascending (least frequent first)");
            Console.WriteLine("  desc/rev           # Sort descending (most frequent first)");
            Console.WriteLine("  sort:count         # Sort by raw count (default)");
            Console.WriteLine("  sort:ppm           # Sort by statistical significance (PPM)");
            Console.WriteLine("  ");
            Console.WriteLine("  top:50             # Show only top 50 most frequent results");
            Console.WriteLine("  top:10%            # Show top 10% of results");
            Console.WriteLine("  bottom:20          # Show only bottom 20 least frequent results");
            Console.WriteLine("  bottom:25%         # Show bottom 25% of results");
            
            Console.WriteLine("\nANALYSIS STRATEGIES:");
            Console.WriteLine("  ");
            Console.WriteLine("  # Exploratory Analysis (Start Here)");
            Console.WriteLine("  ngc 1 top:30 rev                    # Most common terms");
            Console.WriteLine("  ngc 2 percentile:95+ rev            # Most statistically significant phrases");
            Console.WriteLine("  ngc 1 percentile:5 rev +++          # Statistical outliers with full metrics");
            Console.WriteLine("  ");
            Console.WriteLine("  # Code Structure Analysis");
            Console.WriteLine("  ngc 2 \"class [A-Z]\" rev             # Find class definitions");
            Console.WriteLine("  ngc 3 \"public (class|interface)\" rev # Find public type definitions");
            Console.WriteLine("  ngc 3 \"new [A-Z]\" sort:ppm rev      # Object instantiation by significance");
            Console.WriteLine("  ngc 2 \"import|using\" top:20 rev     # Most common dependencies");
            Console.WriteLine("  ");
            Console.WriteLine("  # Code Quality Patterns");
            Console.WriteLine("  ngc 3 \"if\" z:!2 freq:5+ rev         # Unusual but recurring conditionals");
            Console.WriteLine("  ngc 2 \"null\" percentile:95+ rev     # Most common null reference patterns");
            Console.WriteLine("  ngc 3 \"try catch\" sort:ppm rev      # Error handling by significance");
            Console.WriteLine("  ngc 2 \"TODO|FIXME\" rev              # Find technical debt markers");
            Console.WriteLine("  ");
            Console.WriteLine("  # Documentation Analysis");
            Console.WriteLine("  ngc 3 \"should\" percentile:80+ rev   # Find requirements and expectations");
            Console.WriteLine("  ngc 2 -the -a -an -of -in percentile:95+ # Key terms without noise words");
            Console.WriteLine("  ngc 3 \"Inconsistencies|Issues\" rev  # Find documented problems and gaps");
            Console.WriteLine("  ngc 2 \"is|are\" z:!2 freq:5+ +++     # Unusual definitions that appear repeatedly");
            
            Console.WriteLine("\nTROUBLESHOOTING:");
            Console.WriteLine("  ");
            Console.WriteLine("  # If you get no results:");
            Console.WriteLine("  1. Try broadening your n-gram size range (e.g., ngc 1..3 instead of just ngc 2)");
            Console.WriteLine("  2. Reduce the strictness of your filters (lower percentile or frequency thresholds)");
            Console.WriteLine("  3. Check if your regex pattern might need escaping or simplification");
            Console.WriteLine("  4. For statistical filters, ensure you have enough text to generate meaningful statistics");
            Console.WriteLine("  ");
            Console.WriteLine("  # For better results:");
            Console.WriteLine("  1. Chain filters gradually - start broad, then add constraints one at a time");
            Console.WriteLine("  2. Use '+++' to see statistical details when results seem incorrect");
            Console.WriteLine("  3. Try both 'sort:count' and 'sort:ppm' as they can surface different insights");
            Console.WriteLine("  4. For documentation, filter out common words (-the -a -an -of -in) to reduce noise");
            
            Console.WriteLine("\nCOMMON COMBINATIONS:");
            Console.WriteLine("  ngc 1..3 percentile:5 rev           # Statistical outliers across different n-gram sizes");
            Console.WriteLine("  ngc 2 -the -a -an -of -in sort:ppm  # Meaningful phrases sorted by statistical significance");
            Console.WriteLine("  ngc 3 \"pattern\" z:!2 freq:5+        # Unusual but recurring patterns containing \"pattern\"");
            Console.WriteLine("  ngc 2..3 z:!2 freq:5+ ++            # Statistically significant phrases of different lengths");
            
            Console.WriteLine("\nTIPS FOR EFFECTIVE ANALYSIS:");
            Console.WriteLine("  1. Start broad, then narrow: Begin with `ngc 1 top:50 rev` to get an overview");
            Console.WriteLine("  2. Use percentile filters for deeper insights: `percentile:5` is more revealing than just `top:N`");
            Console.WriteLine("  3. Look for both common and rare patterns: Outliers (`z:!2`) often reveal key insights");
            Console.WriteLine("  4. Combine with grep for further filtering: Pipe ngc output to grep to find specific terms");
            Console.WriteLine("  5. Statistical metrics reveal more than raw counts: Use `+++` and `sort:ppm` to find significance");

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
            if (a == "+") { options.Mode = OutputMode.Enhanced; continue; }
            if (a == "++") { options.Mode = OutputMode.Both; options.ShowMerged = true; continue; }
            if (a == "+++") { options.Mode = OutputMode.Detailed; continue; }
            if (a == "--") { options.MinimalOutput = true; continue; }
            if (a == "---") { options.StatsOnly = true; continue; }
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
            if (a.StartsWith("-@")) { options.ExcludeFiles.Add(a.Substring(2)); continue; }
            // text filters starts/ends notation
            if (a.EndsWith("..")) { 
                var pattern1 = a.Substring(0, a.Length - 2);
                var filter1 = new TextFilter { Type = TextFilter.TypeEnum.StartsWith, Pattern = pattern1 };
                if (TryCompileRegex(pattern1.ToLower(), out Regex regex1))
                    filter1.CompiledRegex = regex1;
                options.TextFilters.Add(filter1);
                continue;
            }
            if (a.StartsWith("..")) { 
                var pattern2 = a.Substring(2);
                var filter2 = new TextFilter { Type = TextFilter.TypeEnum.EndsWith, Pattern = pattern2 };
                if (TryCompileRegex(pattern2.ToLower(), out Regex regex2))
                    filter2.CompiledRegex = regex2;
                options.TextFilters.Add(filter2);
                continue;
            }
            if (a.StartsWith("!"))
            {
                var rest = a.Substring(1);
                if (rest.EndsWith("..")) { 
                    var pattern3 = rest.Substring(0, rest.Length - 2);
                    var filter3 = new TextFilter { Type = TextFilter.TypeEnum.NotStartsWith, Pattern = pattern3 };
                    if (TryCompileRegex(pattern3.ToLower(), out Regex regex3))
                        filter3.CompiledRegex = regex3;
                    options.TextFilters.Add(filter3);
                    continue;
                }
                if (rest.StartsWith("..")) { 
                    var pattern4 = rest.Substring(2);
                    var filter4 = new TextFilter { Type = TextFilter.TypeEnum.NotEndsWith, Pattern = pattern4 };
                    if (TryCompileRegex(pattern4.ToLower(), out Regex regex4))
                        filter4.CompiledRegex = regex4;
                    options.TextFilters.Add(filter4);
                    continue;
                }
                // frequency outside or negation for plain words
                if (Regex.IsMatch(rest, @"^\d+\+$")) { int v = int.Parse(rest.TrimEnd('+')); options.FrequencyFilters.Add(new FrequencyFilter { Min = null, Max = v - 1, Outside = false }); continue; }
                if (Regex.IsMatch(rest, @"^\d+\.\.\d+$")) { var pp = rest.Split(new[] { ".." }, StringSplitOptions.None); int mi = int.Parse(pp[0]); int ma = int.Parse(pp[1]); options.FrequencyFilters.Add(new FrequencyFilter { Min = mi, Max = ma, Outside = true }); continue; }
                // treat as not contains
                var notContainsFilter = new TextFilter { Type = TextFilter.TypeEnum.NotContains, Pattern = rest };
                if (TryCompileRegex(rest.ToLower(), out Regex notContainsRegex))
                    notContainsFilter.CompiledRegex = notContainsRegex;
                options.TextFilters.Add(notContainsFilter);
                continue;
            }
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
            // percentile patterns
            if (a.StartsWith("percentile:"))
            {
                var p = a.Substring(11);
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
            // default text contains filter
            var filter = new TextFilter { Type = TextFilter.TypeEnum.Contains, Pattern = a };
            // Check if the pattern might be a regex and compile it
            if (TryCompileRegex(a.ToLower(), out Regex regex))
                filter.CompiledRegex = regex;
            options.TextFilters.Add(filter);
        }
        return options;
    }
}
