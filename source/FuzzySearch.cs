using System;
using System.Collections.Generic;
using System.Linq;

namespace GW2WikiTool;

public static class FuzzySearch
{
    public static string FixQuotes(string s) =>
        s.Replace('\u2019', '\'').Replace('\u2018', '\'');

    private static readonly char[] SplitChars = { ' ', '\'', '-', ',', '.', ':' };

    private static IEnumerable<string> Chop(string s)
    {
        var clean = FixQuotes(s).ToLowerInvariant();
        foreach (var piece in clean.Split(SplitChars, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return piece.Length > 3 && piece.EndsWith('s') ? piece[..^1] : piece;
        }
    }

    public static bool LooseMatch(string name, string query)
    {
        var nameBits = Chop(name).ToList();
        var queryBits = Chop(query).ToList();
        if (queryBits.Count == 0 || nameBits.Count == 0) return false;

        foreach (var q in queryBits)
        {
            var hit = false;
            foreach (var n in nameBits)
            {
                if (n.Contains(q) || q.Contains(n))
                {
                    hit = true;
                    break;
                }
            }
            if (!hit) return false;
        }
        return true;
    }
}