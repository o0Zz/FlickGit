using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FlickGit.Diagnostics;
using FlickGit.Logging;

namespace FlickGit.App.Trigger;

/// <summary>Where a candidate folder came from, which decides what the popup says about it.</summary>
public enum FolderOrigin
{
    /// <summary>A folder selected in the active Explorer view.</summary>
    Selection,

    /// <summary>The folder the active Explorer tab is showing.</summary>
    CurrentFolder,

    /// <summary>Another tab of the same Explorer window.</summary>
    ExplorerTab,
}

/// <param name="Path">An absolute folder path. Not necessarily inside a repository.</param>
public readonly record struct FolderCandidate(string Path, FolderOrigin Origin);

/// <param name="Ordered">Best guess first.</param>
/// <param name="Ambiguous">
/// True when Explorer had several tabs open on one window and there was no way to tell which was
/// active. Logged, because it is the answer to "why did it open the wrong repository".
/// </param>
public readonly record struct FolderCandidates(IReadOnlyList<FolderCandidate> Ordered, bool Ambiguous);

/// <summary>
/// Which folder the user was looking at when the trigger fired.
///
/// Asks Explorer through the shell automation surface, in the order CLAUDE.md prescribes: the
/// selected folder, then the folder the active tab is showing.
///
/// <b>There is no most-recently-used fallback.</b> There was one, for the popup, which said so in
/// its header. A trigger pressed with no Explorer window in front now resolves to nothing and opens
/// nothing at all — guessing a repository the user is not looking at is the one thing this must not
/// do, and a window is a worse place to discover the guess was wrong than a popup was.
///
/// <b>On a dedicated STA thread with a deadline.</b> <c>IShellWindows</c> marshals cross-process
/// into <c>explorer.exe</c>, and a blocking COM call into a hung Explorer made from the WPF UI
/// thread would freeze the tray icon, the pipe listener and every pre-warmed window at once. Only
/// strings come back across, so there is no interface marshalling to arrange.
///
/// <b>Windows 11 tabs are the hard part.</b> <c>IShellWindows</c> returns one entry per tab and
/// every tab of a window reports the same frame HWND, so there is no documented way to ask which is
/// active. The frame's window text is the active tab's folder name, which resolves it in the common
/// case; two tabs on folders with the same leaf name is genuinely undecidable, and then the answer
/// is to say so rather than to guess.
/// </summary>
public sealed partial class ExplorerFolderResolver(OperationTimings timings, ILog log)
{
    /// <summary>
    /// How long Explorer gets to answer.
    ///
    /// Inside the 80 ms budget for the window being painted, because that budget starts before this
    /// runs. A hung Explorer costs the user this trigger, which is the honest outcome: there is
    /// nothing to fall back to and nothing worth guessing.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(120);

    /// <param name="foreground">
    /// The window that was in front when the trigger fired, captured at that instant. Asking
    /// Windows here instead would sometimes answer with FlickGit's own popup, which is hidden rather
    /// than closed between triggers and is therefore a plausible foreground window.
    /// </param>
    public async Task<FolderCandidates> ResolveAsync(nint foreground, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        List<FolderCandidate> explorer = await AskExplorerAsync(foreground, cancellationToken).ConfigureAwait(false);

        timings.Record("trigger.folder", clock.Elapsed);

        //Several tabs on one window and none of them identifiable as active.
        bool ambiguous = explorer.Count(c => c.Origin == FolderOrigin.ExplorerTab) > 0
                         && !explorer.Any(c => c.Origin == FolderOrigin.CurrentFolder);

        return new FolderCandidates(explorer, ambiguous);
    }

    /// <summary>
    /// The COM work, on its own STA thread, abandoned if Explorer does not answer in time.
    /// </summary>
    private Task<List<FolderCandidate>> AskExplorerAsync(nint foreground, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<List<FolderCandidate>>();

        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(Enumerate(foreground));
            }
            catch (Exception ex)
            {
                //Every failure here is the same failure as far as the caller is concerned: Explorer
                //did not say. The MRU covers it.
                log.Debug($"Explorer folder resolution failed: {ex.Message}");
                completion.TrySetResult([]);
            }
        })
        {
            IsBackground = true,
            Name = "FlickGit.ShellQuery",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return WithDeadline(completion.Task, cancellationToken);
    }

    private async Task<List<FolderCandidate>> WithDeadline(
        Task<List<FolderCandidate>> work,
        CancellationToken cancellationToken)
    {
        Task finished = await Task
            .WhenAny(work, Task.Delay(Deadline, cancellationToken))
            .ConfigureAwait(false);

        if (finished == work)
            return await work.ConfigureAwait(false);

        //The thread is left to finish on its own. A leaked background thread waiting on a hung
        //Explorer is a far better outcome than a frozen tray icon, and it ends when Explorer does.
        log.Debug($"Explorer did not answer within {Deadline.TotalMilliseconds:F0} ms; the trigger resolves to nothing.");
        return [];
    }

    /// <summary>
    /// Walks Explorer's views once and picks out the tabs belonging to the foreground frame.
    ///
    /// One pass, deliberately: the folder and the selection both come off the same
    /// <c>IShellFolderViewDual</c>, so reading them together costs one cross-process enumeration
    /// where reading them separately cost two.
    /// </summary>
    private List<FolderCandidate> Enumerate(nint foreground)
    {
        var candidates = new List<FolderCandidate>();

        if (foreground == 0)
            return candidates;

        IShellWindows? windows = ShellWindows.Create();

        if (windows is null)
        {
            log.Debug("Explorer's shell-windows collection was unavailable.");
            return candidates;
        }

        try
        {
            //The frame's window text is the active tab's folder name on Windows 11, and the whole
            //window's on Windows 10 -- which is the same thing when there is only one tab.
            string activeTab = WindowText(foreground);

            var tabs = new List<Tab>();
            int seen = 0;

            for (int i = 0; i < windows.Count; i++)
            {
                object? item = null;

                try
                {
                    item = windows.Item(i);

                    if (item is not IWebBrowser2 browser)
                        continue;

                    seen++;

                    if (browser.HWND == foreground && Read(browser) is { } tab)
                        tabs.Add(tab);
                }
                catch (COMException)
                {
                    //A window closing while it is being enumerated. Skip it; the others are fine.
                }
                finally
                {
                    ShellWindows.Release(item);
                }
            }

            if (tabs.Count == 0)
            {
                //Not an Explorer window. The tray, another application, or our own popup: all
                //ordinary, and all of them mean the trigger has no folder to act on.
                log.Debug($"None of {seen} Explorer view(s) owns window {foreground:X}; the trigger resolves to nothing.");
                return candidates;
            }

            //One tab, or the one whose folder name matches the frame's title. On Windows 11 two tabs
            //showing folders with the same leaf name are genuinely undecidable, and then there is no
            //active tab -- which is what makes the result ambiguous and Ctrl+R the answer.
            Tab? active = tabs.Count == 1
                ? tabs[0]
                : tabs.FirstOrDefault(t => string.Equals(t.Name, activeTab, StringComparison.OrdinalIgnoreCase));

            //The selection wins over the folder, per CLAUDE.md's order: a folder the user clicked is
            //a stronger statement of intent than the one they happen to be browsing.
            if (active?.Selected is { Length: > 0 } selected)
                candidates.Add(new FolderCandidate(selected, FolderOrigin.Selection));

            if (active is { Path.Length: > 0 })
                candidates.Add(new FolderCandidate(active.Path, FolderOrigin.CurrentFolder));

            //The rest, in enumeration order. Reachable with Ctrl+R, and the reason the ambiguous
            //case is answerable rather than a dead end.
            foreach (Tab tab in tabs)
            {
                if (!candidates.Any(c => string.Equals(c.Path, tab.Path, StringComparison.OrdinalIgnoreCase)))
                    candidates.Add(new FolderCandidate(tab.Path, FolderOrigin.ExplorerTab));
            }

            return candidates;
        }
        finally
        {
            ShellWindows.Release(windows);
        }
    }

    /// <param name="Path">The folder the tab is showing.</param>
    /// <param name="Name">Its display name, for matching against the frame's title.</param>
    /// <param name="Selected">The first selected folder in it, if there is one.</param>
    private sealed record Tab(string Path, string Name, string? Selected);

    /// <summary>
    /// One tab's folder and selection, or null when it is not showing a file-system path.
    /// </summary>
    private static Tab? Read(IWebBrowser2 browser)
    {
        object? document = null;
        object? folder = null;
        object? self = null;

        try
        {
            document = browser.Document;

            if (document is not IShellFolderViewDual view)
                return null;

            folder = view.Folder;

            if (folder is not IShellFolderDispatch dispatch)
                return null;

            self = dispatch.Self;

            if (self is not IFolderItem item)
                return null;

            string path = item.Path;

            //Empty for a control panel, a library or This PC. Nothing to resolve a repository from.
            if (path.Length == 0 || !Directory.Exists(path))
                return null;

            return new Tab(path, item.Name, SelectedFolder(view));
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            ShellWindows.Release(self);
            ShellWindows.Release(folder);
            ShellWindows.Release(document);
        }
    }

    /// <summary>
    /// The first selected item, if it is a folder.
    ///
    /// Only the first: "right-click a folder and commit it" is the case this serves, and a multiple
    /// selection is not a statement about one repository.
    /// </summary>
    private static string? SelectedFolder(IShellFolderViewDual view)
    {
        object? selected = null;
        object? first = null;

        try
        {
            selected = view.SelectedItems();

            if (selected is not IFolderItems items || items.Count == 0)
                return null;

            first = items.Item(0);

            return first is IFolderItem folder && folder.IsFolder && folder.Path is { Length: > 0 } path && Directory.Exists(path)
                ? path
                : null;
        }
        catch (COMException)
        {
            //Nothing selected, or the view is mid-navigation. Not an error.
            return null;
        }
        finally
        {
            ShellWindows.Release(first);
            ShellWindows.Release(selected);
        }
    }

    private static unsafe string WindowText(nint handle)
    {
        //A stack buffer rather than a StringBuilder: the LibraryImport source generator refuses to
        //marshal StringBuilder (SYSLIB1051), and a window title has a known small bound anyway.
        const int capacity = 512;
        char* buffer = stackalloc char[capacity];

        int length = GetWindowTextW(handle, buffer, capacity);

        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetWindowTextW(nint handle, char* text, int count);
}
