using System;
using System.Collections.Generic;
using System.Linq;

namespace GW2WikiTool;

/// <summary>
/// Shared, forgiving name-matching used by AchievementLookup and WaypointLookup. Two-tier:
/// an exact (but apostrophe-normalized) substring match first, falling back to a looser
/// token-based match that tolerates missing words, minor plural differences, and doesn't
/// require the query to include words like "Waypoint".
/// </summary>
public static class FuzzySearch
{
    // GW2's API data uses the Unicode right single quote ('\u2019') in possessive names,
    // not the plain ASCII apostrophe (') a keyboard types.
    public static string NormalizeApostrophes(string s) =>
        s.Replace('\u2019', '\'').Replace('\u2018', '\'');

    private static readonly char[] TokenSeparators = { ' ', '\'', '-', ',', '.', ':' };

    private static IEnumerable<string> Tokenize(string s)
    {
        var normalized = NormalizeApostrophes(s).ToLowerInvariant();
        foreach (var word in normalized.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            // Naive de-pluralization so "Hollow" and "Hollows" compare equal; skip very short
            // words (e.g. "is", "vs") where stripping a trailing 's' would change the meaning.
            yield return word.Length > 3 && word.EndsWith('s') ? word[..^1] : word;
        }
    }

    /// <summary>True if every token in the query matches some token in the candidate name
    /// (as a substring either direction), after de-pluralization. Order-independent, and the
    /// query does not need to include suffix words like "Waypoint".</summary>
    public static bool FuzzyMatches(string candidateName, string query)
    {
        var candidateTokens = Tokenize(candidateName).ToList();
        var queryTokens = Tokenize(query).ToList();
        if (queryTokens.Count == 0 || candidateTokens.Count == 0) return false;

        return queryTokens.All(qt => candidateTokens.Any(ct => ct.Contains(qt) || qt.Contains(ct)));
    }
}
