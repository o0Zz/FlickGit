using Avalonia.Threading;
using FlickGit.App.CommandLine;
using FlickGit.App.Mac.Views;
using FlickGit.App.ViewModels;
using FlickGit.Cli;
using FlickGit.Models;
using FlickGit.Repositories;

namespace FlickGit.App.Mac;

/// <summary>
/// The verbs that open a window, on macOS.
///
/// <b>Two are real; the rest still say so.</b> `commit` and `palette` open the windows this port has
/// built. The other eleven raise <see cref="HostCapabilityException"/>, which
/// <see cref="VerbRunner"/> turns into exit code 4 and a sentence naming the verb — so a user asking
/// for `flick log` is told it is not built yet rather than watching nothing happen.
///
/// <b>Every method hops to the UI thread first.</b> A verb arrives on the socket listener's thread,
/// and constructing a <c>Window</c> from there throws.
///
/// <b>The windows are created per call rather than pre-warmed.</b> Pre-warming is the Windows host's
/// answer to a 120 ms budget it has measured; nothing here has been measured on real hardware yet,
/// and a pre-warmed window that has to be fully re-initialisable is a correctness cost to pay once
/// there is a number saying it is needed.
/// </summary>
public sealed class MacWindowVerbs(
    CommitViewModel commit,
    PaletteViewModel palette,
    RepositoryService repositories,
    IDialogs dialogs) : IWindowVerbs
{
    public Task<VerbResult> CommitAsync(VerbOutput output, RepositoryInfo repository) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            commit.Reset(repository);

            var window = new CommitWindow(commit, dialogs);
            window.Show();

            await commit.RefreshAsync().ConfigureAwait(true);

            //Stay: the window is the output, and shutting down here would close it.
            return VerbResult.Stay(ExitCodes.Success);
        });

    public Task<VerbResult> PaletteAsync() =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            palette.Reset();

            var window = new PaletteWindow(palette);
            window.Show();

            await palette.RefreshAsync().ConfigureAwait(true);

            return VerbResult.Stay(ExitCodes.Success);
        });

    /// <summary>
    /// Back to the primary branch. Added to this interface after the macOS host existed, which is
    /// the interface doing its job: a verb added on Windows shows up here as a compile error rather
    /// than as a verb that silently does nothing on the other platform.
    /// </summary>
    public Task<VerbResult> BackAsync(RepositoryInfo repository) => throw new HostCapabilityException("back");

    public Task<VerbResult> LogAsync(RepositoryInfo repository) => throw new HostCapabilityException("log");

    public Task<VerbResult> BlameAsync(VerbOutput output, RepositoryInfo repository, string path) =>
        throw new HostCapabilityException("blame");

    public Task<VerbResult> PullRequestAsync(RepositoryInfo repository) => throw new HostCapabilityException("pr");

    public Task<VerbResult> PullAsync(RepositoryInfo repository) => throw new HostCapabilityException("pull-rebase");

    public Task<VerbResult> SwitchPickerAsync(RepositoryInfo repository) => throw new HostCapabilityException("switch");

    public VerbResult TagPicker(RepositoryInfo repository) => throw new HostCapabilityException("tag");

    public VerbResult StashPicker(RepositoryInfo repository) => throw new HostCapabilityException("stash");

    public VerbResult Submodules(RepositoryInfo repository) => throw new HostCapabilityException("submodule");

    public VerbResult Repo(RepositoryInfo repository) => throw new HostCapabilityException("repo");

    public VerbResult Clone(string path, string? url) => throw new HostCapabilityException("clone");

    /// <summary>
    /// Opens a terminal at the folder.
    ///
    /// <c>open -a Terminal</c> rather than launching a shell directly: on macOS the terminal is an
    /// application to be told about a folder, not a process to spawn with a working directory.
    /// </summary>
    public VerbResult Terminal(VerbOutput output, string? path)
    {
        _ = repositories;

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                UseShellExecute = false,
                ArgumentList = { "-a", "Terminal", path ?? Environment.CurrentDirectory },
            });

            return VerbResult.Exit(ExitCodes.Success);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            output.Fail("FlickGit", ex.Message);

            return VerbResult.Exit(ExitCodes.ConfigurationError);
        }
    }
}
