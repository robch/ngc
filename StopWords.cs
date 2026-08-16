using System;
using System.Collections.Generic;

// A small, conservative default English stopword list used to filter out n-grams that are
// composed ENTIRELY of function words (e.g. "to the", "Do not", "of the"). Phrases with a
// stopword mixed in among real content words (e.g. "the guard", "state of the art") are
// intentionally NOT filtered — only all-stopword n-grams are dropped, since those carry no
// distinguishing content of their own. See CommandOptions.RemoveStopwords / --keep-words.
public static class StopWords
{
    public static readonly HashSet<string> Default = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the",
        // A free-standing "-" (used as a sentence-separator dash, not a bullet or a
        // hyphenated compound) is punctuation, not a word — treat it as a stopword so
        // "field - how" doesn't register as the phrase "- how".
        "-",
        "and", "or", "but", "if", "then", "else", "nor", "so", "yet",
        "of", "to", "in", "on", "at", "by", "with", "from", "as", "into", "onto", "than",
        "is", "are", "was", "were", "be", "been", "being",
        "do", "does", "did", "not", "no",
        "this", "that", "these", "those", "it", "its",
        "i", "you", "he", "she", "we", "they", "them", "his", "her", "their", "our", "your", "my", "me", "him", "us",
        "which", "who", "whom", "whose", "what", "when", "where", "why", "how",
        "can", "could", "will", "would", "shall", "should", "may", "might", "must",
        "have", "has", "had",
    };
}
