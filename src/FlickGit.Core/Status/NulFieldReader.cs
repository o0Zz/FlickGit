namespace FlickGit.Status;

/// <summary>
/// Walks a NUL-terminated Git stream one field at a time.
///
/// This exists because CLAUDE.md, "Parsing traps" rules out the obvious approach:
/// <c>-z</c> output cannot be read line by line. Both `status --porcelain=v2 -z` and
/// `diff --numstat -z` emit records that consume a variable number of NUL-terminated
/// fields — a rename pulls in one or two extra — so the reader has to be a cursor over
/// fields, not a loop over lines.
///
/// "Paths may contain any byte except NUL. Never split on spaces." A path can contain
/// a newline, a tab, a quote and a literal <c>=&gt;</c>; NUL is the only safe delimiter
/// and this is the only place the product splits on it.
/// </summary>
internal struct NulFieldReader(string payload)
{
    private readonly string _payload = payload;
    private int _position;

    /// <summary>True while at least one more field is available.</summary>
    private bool HasMore => _position < _payload.Length;

    /// <summary>
    /// Consumes the next field, without its terminator.
    ///
    /// A final field with no trailing NUL is still returned: Git always terminates, but
    /// a truncated read must degrade to "one short record" rather than to an exception
    /// that loses every field already parsed.
    /// </summary>
    public bool TryRead(out string field)
    {
        if (!HasMore)
        {
            field = string.Empty;
            return false;
        }

        int end = _payload.IndexOf('\0', _position);
        if (end < 0)
        {
            field = _payload[_position..];
            _position = _payload.Length;
            return true;
        }

        field = _payload[_position..end];
        _position = end + 1;
        return true;
    }
}
