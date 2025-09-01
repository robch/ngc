using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NGramCounter
{
    class Program
    {
        static void Main(string[] args)
        {
            // Parse the max n-gram size from command line (default to 3 if not specified)
            int maxN = 3;
            if (args.Length > 0 && int.TryParse(args[0], out int parsedMaxN) && parsedMaxN > 0)
            {
                maxN = parsedMaxN;
            }

            // Parse the minimum count from command line (default to 1 if not specified)
            int minCount = 1;
            if (args.Length > 1 && int.TryParse(args[1], out int parsedMinCount) && parsedMinCount > 0)
            {
                minCount = parsedMinCount;
            }

            // Read all text from standard input
            string input = Console.In.ReadToEnd();

            // Process each line separately
            string[] lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            
            // Dictionary to hold n-gram frequencies for each n
            var nGramCounts = new Dictionary<int, Dictionary<string, int>>();
            
            // Initialize dictionaries for each n-gram size
            for (int n = 1; n <= maxN; n++)
            {
                nGramCounts[n] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            // Process each line
            foreach (string line in lines)
            {
                // Skip empty lines
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Keep letters, digits, apostrophes, and hyphens; replace others with spaces
                var sb = new StringBuilder(line.Length);
                foreach (var ch in line)
                {
                    if (char.IsLetterOrDigit(ch) || ch == '-')
                        sb.Append(ch);
                    else
                        sb.Append(' ');
                }

                // Split the line into words, space-delimited
                string[] words = sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                // Process n-grams for each size
                for (int n = 1; n <= maxN; n++)
                {
                    // Skip if the line doesn't have enough words for this n-gram size
                    if (words.Length < n)
                        continue;
                    
                    // Generate all possible n-grams of size n from this line
                    for (int i = 0; i <= words.Length - n; i++)
                    {
                        string nGram = string.Join(" ", words.Skip(i).Take(n));
                        
                        if (nGramCounts[n].ContainsKey(nGram))
                            nGramCounts[n][nGram]++;
                        else
                            nGramCounts[n][nGram] = 1;
                    }
                }
            }

            // Output the results for each n-gram size, from 1 to maxN
            for (int n = 1; n <= maxN; n++)
            {
                Console.WriteLine($"\n## {n}-grams\n");
                
                // Sort by frequency (ascending)
                var sortedNGrams = nGramCounts[n]
                    .OrderBy(pair => pair.Value)
                    .ThenBy(pair => pair.Key);
                
                foreach (var pair in sortedNGrams)
                {
                    if (pair.Value >= minCount)
                    {
                        Console.WriteLine($"{pair.Value}: {pair.Key}");
                    }
                }
            }
        }
    }
}
