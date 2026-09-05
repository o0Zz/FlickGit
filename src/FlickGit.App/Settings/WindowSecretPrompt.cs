using System.Windows;
using FlickGit.Ai;
using FlickGit.App.Views;
using FlickGit.Forges;

namespace FlickGit.App.Settings;

/// <summary>
/// <see cref="ISecretPrompt"/> on WPF: the existing <see cref="SecretWindow"/>, shown modally.
///
/// <b>The task is already complete when it is returned</b>, because <c>ShowDialog</c> blocks until
/// the window closes. The asynchronous signature is macOS's requirement rather than this platform's,
/// and pretending otherwise here would mean two interfaces for one question.
///
/// <b>The owner is found rather than passed in.</b> An unowned modal over a window the user is
/// looking at can end up behind it, and a modal behind its own parent is a hung application as far
/// as the user can tell — so the active window is the owner, and a verb with none open (which is
/// what <c>flick ai key set</c> is) gets a centred window instead. Finding it here is what keeps a
/// <c>Window</c> out of the shared callers' signatures.
/// </summary>
public sealed class WindowSecretPrompt : ISecretPrompt
{
    public Task<string?> AskForApiKeyAsync(AiProvider provider) =>
        Task.FromResult(SecretWindow.AskForApiKey(Active, provider));

    public Task<string?> AskForForgeTokenAsync(ForgeKind kind, string host) =>
        Task.FromResult(SecretWindow.AskForForgeToken(Active, kind, host));

    private static Window? Active =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
}
