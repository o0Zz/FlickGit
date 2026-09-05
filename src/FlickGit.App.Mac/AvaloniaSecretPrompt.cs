using Avalonia.Threading;
using FlickGit.Ai;
using FlickGit.App.Mac.Views;
using FlickGit.App.Settings;
using FlickGit.Forges;

namespace FlickGit.App.Mac;

/// <summary>
/// <see cref="ISecretPrompt"/> on Avalonia.
///
/// Both methods marshal to the UI thread for the reason <see cref="AvaloniaDialogs"/> does: the ask
/// can come from the socket listener's thread — <c>flick ai key set</c> forwarded to the resident
/// service is exactly that — and a window touched from there throws.
///
/// <c>InvokeAsync</c> rather than <c>Post</c>, because the answer is the whole point.
/// </summary>
public sealed class AvaloniaSecretPrompt : ISecretPrompt
{
    public Task<string?> AskForApiKeyAsync(AiProvider provider) =>
        Dispatcher.UIThread.InvokeAsync(() => SecretWindow.AskForApiKeyAsync(provider));

    public Task<string?> AskForForgeTokenAsync(ForgeKind kind, string host) =>
        Dispatcher.UIThread.InvokeAsync(() => SecretWindow.AskForForgeTokenAsync(kind, host));
}
