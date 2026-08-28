using System.Text;

namespace FlickGit.Logging;

/// <summary>
/// Appends to <c>%LOCALAPPDATA%\FlickGit\Logs\flickgit.log</c> with size-based
/// rotation, keeping one previous file.
///
/// Writes are serialised on a lock and flushed per line. That is slower than buffering,
/// and it is the point: the interesting log is the one written just before the tool
/// crashed or the user killed it, and a buffered line that never reached the disk is
/// worth nothing when an issue is reported.
///
/// <b>The directory arrives as a parameter and is never computed here.</b> Where
/// <c>%LOCALAPPDATA%</c> is is a fact about Windows, and <c>FlickGit.Core</c> is <c>net9.0</c>
/// precisely so it does not get to know one -- the same reason <c>PromptStore</c> and
/// <c>ActionCatalog</c> are handed <c>FlickSettings.DirectoryPath</c> rather than deriving it.
/// <c>FlickSettings.LogsDirectoryPath</c> is the one answer.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private const long MaxBytes = 2 * 1024 * 1024;

    private readonly string _path;
    private readonly string _rolledPath;
    private readonly bool _debugEnabled;
    private readonly Lock _gate = new();
    private bool _disabled;

    public FileLog(string directory, bool debugEnabled = false)
    {
        _debugEnabled = debugEnabled;
        _path = Path.Combine(directory, "flickgit.log");
        _rolledPath = Path.Combine(directory, "flickgit.previous.log");

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            //An unwritable log directory is not a reason to refuse to run. Every call
            //below turns into a no-op instead.
            _disabled = true;
        }
    }


    public void Debug(string message)
    {
        if (_debugEnabled)
            Write("DBG", message);
    }

    public void Info(string message) => Write("INF", message);

    public void Warn(string message) => Write("WRN", message);

    public void Error(string message) => Write("ERR", message);

    private void Write(string level, string message)
    {
        if (_disabled)
            return;

        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";

        lock (_gate)
        {
            try
            {
                Rotate();
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch (Exception)
            {
                //Two processes can hold this file (the resident service and a CLI
                //fallback launch). Losing a line to a sharing violation is acceptable;
                //throwing out of a log call is not.
            }
        }
    }

    private void Rotate()
    {
        var file = new FileInfo(_path);
        if (!file.Exists || file.Length < MaxBytes)
            return;

        //One generation kept. Enough to cover "it broke, here is the log" without
        //growing unbounded in a directory the user never looks at.
        File.Delete(_rolledPath);
        File.Move(_path, _rolledPath);
    }

    public void Dispose()
    {
        //Nothing held open: every line is a separate append, so there is no handle to
        //release. Implemented so callers can own this like any other resource.
        _disabled = true;
    }
}
