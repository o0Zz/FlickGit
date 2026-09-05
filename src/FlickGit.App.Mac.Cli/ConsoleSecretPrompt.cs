using FlickGit.Ai;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.Forges;

namespace FlickGit.App.Mac;

/// <summary>
/// <see cref="ISecretPrompt"/> for a host with no windows: the terminal asks, with the echo off.
///
/// <b>A terminal is not the thing the window exists to avoid.</b> What <c>SecretWindow</c>'s
/// argument rules out is a secret as a <i>command-line argument</i> — that ends up in the shell's
/// history and in the process list, where any other process on the machine can read it. Typing one
/// at a prompt has neither problem, and it is what <c>git</c> itself does for a password.
///
/// So this is a real implementation rather than a refusal: `flick ai key set` works in a terminal
/// with no FlickGit host running, which is the case the resident service must never be a
/// dependency for.
///
/// <b>A redirected stdin answers null.</b> There is nobody to ask, and returning an empty string
/// would be stored as a key and later read as a revoked one. That is the same rule
/// <see cref="ConsoleDialogs"/> keeps for a question it cannot put to anyone.
/// </summary>
public sealed class ConsoleSecretPrompt : ISecretPrompt
{
    public Task<string?> AskForApiKeyAsync(AiProvider provider) =>
        Task.FromResult(Ask(
            Strings.Get("ai.key.title", provider.ToString()),

            //Copilot gets its own sentence, and it earns the branch: the other two want a key from a
            //dashboard, and this one wants the OAuth token an editor already stored on this machine.
            provider == AiProvider.Copilot
                ? Strings.Get("ai.key.prompt.copilot")
                : Strings.Get("ai.key.prompt", provider.ToString()),

            SecretTargets.AiTarget(provider)));

    public Task<string?> AskForForgeTokenAsync(ForgeKind kind, string host) =>
        Task.FromResult(Ask(
            Strings.Get("pr.token.title", host),
            Strings.Get(kind switch
            {
                ForgeKind.GitHub => "pr.token.prompt.github",
                _ => "pr.token.prompt.azure",
            }, host),
            SecretTargets.ForgeTarget(host)));

    private static string? Ask(string title, string prompt, string target)
    {
        ConsoleOutput.WriteLine(title);
        ConsoleOutput.WriteLine(prompt);
        ConsoleOutput.WriteLine(Strings.Get("ai.key.target", target));

        if (Console.IsInputRedirected)
        {
            //Said out loud rather than looking like a cancel: a script piping into this has to be
            //able to tell "you declined" from "there was no way to ask".
            ConsoleOutput.WriteError(Strings.Get("ai.key.cancelled"));

            return null;
        }

        Console.Write("> ");

        string typed = ReadWithoutEcho();

        ConsoleOutput.WriteLine(string.Empty);

        //Whitespace-only is a cancel, not a secret. Storing one would produce a 401 that reads like
        //a revoked credential rather than like a typo.
        return typed.Trim() is { Length: > 0 } secret ? secret : null;
    }

    /// <summary>
    /// One key at a time with <c>intercept: true</c>, so nothing is printed and nothing reaches the
    /// terminal's scrollback.
    ///
    /// Backspace is handled because a key is long enough to mistype and there is no other way to
    /// correct it; Enter ends the line; everything else that is not a control character is taken
    /// verbatim, which matters for a token containing punctuation.
    /// </summary>
    private static string ReadWithoutEcho()
    {
        var typed = new System.Text.StringBuilder();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
                return typed.ToString();

            if (key.Key == ConsoleKey.Escape)
                return string.Empty;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                    typed.Length--;

                continue;
            }

            if (!char.IsControl(key.KeyChar))
                typed.Append(key.KeyChar);
        }
    }
}
