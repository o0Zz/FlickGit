namespace FlickGit.Logging;

/// <summary>
/// Diagnostics, deliberately tiny.
///
/// CLAUDE.md, "Logging" is mostly a list of things that must never be written:
/// API keys, credentials, diff contents, file contents, commit message bodies. That
/// prohibition is easier to hold to with an interface this narrow than with a
/// general-purpose logging framework and structured scopes, which invite passing whole
/// objects in and hoping nothing sensitive is on them.
/// </summary>
public interface ILog
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

/// <summary>Discards everything. The default in tests and in the CLI stub.</summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    private NullLog() { }

    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
}
