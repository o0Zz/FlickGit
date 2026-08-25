namespace FlickGit.Config;

/// <summary>
/// The <c>config --list -z</c> reader, on its own because there are now two files it is pointed at:
/// the repository's own config, and <c>.gitmodules</c>.
///
/// A pure function of one string, so it knows nothing about <c>--local</c>, <c>-f</c>, or which of
/// the two produced the bytes -- which is the whole reason a second caller costs nothing.
/// </summary>
internal static class GitConfigList
{
    /// <summary>
    /// Splits <c>config --list -z</c> into key/value pairs.
    ///
    /// Records are NUL-terminated and the key is separated from its value by the <b>first</b>
    /// newline -- which is what makes a value containing newlines survive, and why this is a state
    /// machine over the NUL stream rather than a line split. A record with no newline at all is a key
    /// set with no value, which Git reads as true.
    /// </summary>
    internal static IReadOnlyList<ConfigEntry> ParseList(string standardOutput)
    {
        var entries = new List<ConfigEntry>();

        foreach (string record in standardOutput.Split('\0'))
        {
            if (record.Length == 0)
                continue;

            int newline = record.IndexOf('\n');

            entries.Add(newline < 0
                ? new ConfigEntry(record, null)
                : new ConfigEntry(record[..newline], record[(newline + 1)..]));
        }

        return entries;
    }

    /// <summary>
    /// The subsection of <c>&lt;section&gt;.&lt;name&gt;.&lt;suffix&gt;</c>, taken verbatim, or null when the key
    /// is not of that shape.
    ///
    /// <b>Everything between the first separator and the last</b>, never the second field:
    /// <c>git config --list</c> lower-cases the section and the final component and leaves the
    /// subsection alone, and a subsection may itself contain dots -- a remote called
    /// <c>my.fork</c>, a submodule path used as its own name.
    /// </summary>
    internal static string? SubsectionOf(string key, string section, string suffix)
    {
        string prefix = section + '.';

        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        //Both ends are known, so what is left is the name -- including any dots of its own.
        if (key.Length <= prefix.Length + suffix.Length)
            return null;

        return key[prefix.Length..^suffix.Length];
    }
}

/// <param name="Key">As Git reported it: section and final component lower-cased, subsection verbatim.</param>
/// <param name="Value">Null when the key was set with no value, which Git reads as true.</param>
internal sealed record ConfigEntry(string Key, string? Value);
