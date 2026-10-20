namespace FlickGit.Merges;

/// <summary>
/// Whether a file still carries a line Git writes into a conflicted file.
///
/// <b>Only the automatic path asks this.</b> Saving a conflicted file marks it resolved by itself, and
/// a Ctrl+S half-way through would otherwise record the blocks still in it as the resolution. The
/// explicit <i>Mark resolved</i> asks nothing — <see cref="ConflictService.MarkResolvedAsync"/> says
/// why a content check cannot be a refusal there.
///
/// <b>Wrong only in the direction that stops.</b> A run of seven or more of one marker character, then
/// a space or the end of the line, so a <c>conflict-marker-size</c> attribute is still caught. A
/// Markdown heading underlined with <c>=======</c> is caught too, and that costs one right-click; the
/// reverse mistake would commit markers.
/// </summary>
public static class ConflictMarkers
{
    /// <summary>Git's default marker length, and the shortest it will write.</summary>
    private const int MinimumSize = 7;

    public static bool Remain(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.Length < MinimumSize || "<=>|".IndexOf(line[0]) < 0)
                continue;

            int run = 1;

            while (run < line.Length && line[run] == line[0])
                run++;

            if (run >= MinimumSize && (run == line.Length || line[run] == ' '))
                return true;
        }

        return false;
    }
}
