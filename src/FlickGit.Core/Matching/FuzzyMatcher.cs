namespace FlickGit.Matching;

/// <summary>
/// Subsequence fuzzy matching, scored.
///
/// CLAUDE.md specifies the behaviour: "Subsequence fuzzy matching (<c>cnb</c> →
/// <c>commit-new-branch</c>), scored by contiguity, word-boundary hits and MRU rank." Used by
/// the branch picker now and by the repository palette in Phase 5, which is why it lives in
/// Core rather than in a dialog.
///
/// Scoring is what makes it usable rather than merely correct. A plain subsequence test says
/// <c>fsg</c> matches both <c>feature/storage-gw</c> and <c>fix/something-generic</c>; the
/// score is what puts the one the user meant at the top.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>Characters that start a "word" for boundary scoring.</summary>
    private static bool IsBoundary(char previous) =>
        previous is '/' or '-' or '_' or '.' or ' ' or ':';

    /// <summary>
    /// Scores <paramref name="candidate"/> against <paramref name="pattern"/>, or returns null
    /// when the pattern is not a subsequence of it.
    ///
    /// Higher is better. An empty pattern matches everything with score 0, which is what leaves
    /// the list in its natural order until the user types.
    /// </summary>
    public static int? Score(string candidate, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return 0;

        if (string.IsNullOrEmpty(candidate))
            return null;

        int score = 0;
        int candidateIndex = 0;
        int previousMatch = -2;

        foreach (char wanted in pattern)
        {
            int found = IndexOfIgnoringCase(candidate, wanted, candidateIndex);
            if (found < 0)
                return null;

            //Contiguity: a run of adjacent characters is what the user almost always typed, so
            //it is worth more than the same characters scattered through the string.
            if (found == previousMatch + 1)
                score += 8;

            //A word boundary, or the very start. "sg" meaning storage-gw beats "sg" landing
            //mid-word somewhere.
            if (found == 0 || IsBoundary(candidate[found - 1]))
                score += 6;

            //An exact-case hit is a weak signal that the user is typing what they see.
            if (candidate[found] == wanted)
                score += 1;

            //Earlier is better, mildly. Enough to break ties, not enough to beat contiguity.
            score += Math.Max(0, 4 - (found / 8));

            previousMatch = found;
            candidateIndex = found + 1;
        }

        //A short candidate that used most of its characters is a better match than a long one
        //that happened to contain the same letters.
        score += Math.Max(0, 12 - (candidate.Length - pattern.Length) / 4);

        return score;
    }

    /// <summary>
    /// Filters and orders <paramref name="candidates"/>.
    /// </summary>
    /// <param name="recencyRank">
    /// Optional MRU position, 0 being most recent. Folded into the score so that with no pattern
    /// typed the list is ordered by what the user touched last.
    /// </param>
    public static IReadOnlyList<FuzzyMatch> Rank(
        IEnumerable<string> candidates,
        string pattern,
        Func<string, int>? recencyRank = null)
    {
        var matches = new List<FuzzyMatch>();

        foreach (string candidate in candidates)
        {
            if (Score(candidate, pattern) is not { } score)
                continue;

            if (recencyRank is not null)
                score += Math.Max(0, 10 - recencyRank(candidate));

            matches.Add(new FuzzyMatch(candidate, score));
        }

        //Stable within a score, alphabetical, so the list does not reshuffle between keystrokes
        //that do not change the ranking.
        matches.Sort((a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase);
        });

        return matches;
    }

    private static int IndexOfIgnoringCase(string text, char wanted, int start)
    {
        for (int i = start; i < text.Length; i++)
        {
            if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(wanted))
                return i;
        }

        return -1;
    }
}

/// <param name="Value">The candidate.</param>
/// <param name="Score">Higher is a better match.</param>
public sealed record FuzzyMatch(string Value, int Score);
