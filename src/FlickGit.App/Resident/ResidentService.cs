using System.IO;
using FlickGit.Ipc;

namespace FlickGit.App.Resident;

/// <summary>
/// Whether a resident service is listening.
///
/// Asked by <c>flick diag doctor</c>, which exists to answer "why is this slow?" — and "no resident
/// service" is the first thing to rule out. Existence is tested by looking for the pipe rather than
/// by hunting for a process name: the pipe is what the stub actually needs, and a process that is
/// running but failed to open it is not a resident service in any sense that helps.
///
/// An instance rather than a static because it touches the file system, which
/// <b>Hard Requirement 3</b> puts behind a constructor: a static here would make every caller
/// untestable and would say nothing about what it depends on.
/// </summary>
public sealed class ResidentService
{
    /// <summary>
    /// True when the pipe exists.
    ///
    /// Named pipes live in a file-system-like namespace, so this is a cheap existence check rather
    /// than a connection: asking to connect would consume the single server instance and race with
    /// a real request arriving at the same moment.
    /// </summary>
    public bool IsRunning()
    {
        try
        {
            return File.Exists(IpcProtocol.LocalPipePath());
        }
        catch (Exception)
        {
            return false;
        }
    }
}
