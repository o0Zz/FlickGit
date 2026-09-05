using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FlickGit.Ai;
using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.Forges;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// Asks for a secret, once, and hands it back.
///
/// <b>A window rather than a command-line argument, and that is the whole reason it exists.</b>
/// <c>flick ai key set &lt;key&gt;</c> would put the key in the shell's history and in the process
/// list where any other process on the machine can read it. Reading stdin instead would work only
/// when the stub launched the app directly: over the socket the resident service has no access to
/// the stub's terminal, so the command would behave differently depending on whether the service
/// happened to be running.
///
/// The secret is returned rather than stored here: this window knows how to ask a question, and
/// <see cref="ISecretStore"/> knows where secrets live. Nothing in between logs it, and the window
/// holds it only for as long as it is open.
///
/// <c>PasswordChar</c> on an ordinary <c>TextBox</c> is Avalonia's answer to WPF's
/// <c>PasswordBox</c>: there is no separate control, and the property is what suppresses the echo.
/// </summary>
public sealed class SecretWindow : Window
{
    private readonly TaskCompletionSource<string?> _answer = new();

    private readonly TextBox _key = new()
    {
        Classes = { "mono" },
        PasswordChar = '•',
    };

    private SecretWindow(string title, string prompt, string target)
    {
        //The titlebar as well as the heading. Every other window in the product names its operation
        //there.
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        MinWidth = 420;

        var save = new Button
        {
            Content = Strings.Get("ai.key.save"),
            Classes = { "primary" },
            MinWidth = 100,
            IsDefault = true,
        };

        var cancel = new Button { Content = Strings.Get("common.cancel"), MinWidth = 90 };

        save.Click += (_, _) => Answer();
        cancel.Click += (_, _) => Close();

        Content = new Border
        {
            Padding = new Thickness(18, 16),
            MaxWidth = 520,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = title, Classes = { "title" }, Margin = new Thickness(0, 0, 0, 8) },
                    new TextBlock
                    {
                        Text = prompt,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 480,
                        LineHeight = 18,
                        Margin = new Thickness(0, 0, 0, 10),
                    },
                    _key,
                    new TextBlock
                    {
                        Text = Strings.Get("ai.key.target", target),
                        Classes = { "muted", "small" },
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 8, 0, 0),
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { save, cancel },
                    },
                },
            },
        };

        //Focused on open: there is exactly one thing to do here.
        Opened += (_, _) => _key.Focus();
    }

    /// <summary>Asks for an AI provider's API key.</summary>
    public static Task<string?> AskForApiKeyAsync(AiProvider provider) =>
        Ask(
            Strings.Get("ai.key.title", provider.ToString()),

            //Copilot gets its own sentence, and it earns the branch: the other two want a key from a
            //dashboard, and this one wants the OAuth token an editor already stored on this machine.
            //A user handed the generic wording pastes a personal access token, which the exchange
            //refuses with a 401 that reads like a revoked key.
            provider == AiProvider.Copilot
                ? Strings.Get("ai.key.prompt.copilot")
                : Strings.Get("ai.key.prompt", provider.ToString()),

            SecretTargets.AiTarget(provider));

    /// <summary>
    /// Asks for a token that opens pull requests on <paramref name="host"/>.
    ///
    /// The prompt names the service as well as the host, because what to create is different on each
    /// of the three and a user who is told only "a token for git.acme.io" has to guess which page of
    /// which settings screen. The host is what the answer is filed under, so it is what the sentence
    /// below the box says.
    /// </summary>
    public static Task<string?> AskForForgeTokenAsync(ForgeKind kind, string host) =>
        Ask(
            Strings.Get("pr.token.title", host),
            Strings.Get(kind switch
            {
                ForgeKind.GitHub => "pr.token.prompt.github",
                _ => "pr.token.prompt.azure",
            }, host),
            SecretTargets.ForgeTarget(host));

    private static Task<string?> Ask(string title, string prompt, string target)
    {
        var window = new SecretWindow(title, prompt, target);

        window.Show();

        return window._answer.Task;
    }

    private void Answer()
    {
        //Whitespace-only is a cancel, not a secret. Storing one would produce a 401 that reads like a
        //revoked credential rather than like a typo.
        string typed = (_key.Text ?? string.Empty).Trim();

        _answer.TrySetResult(typed.Length > 0 ? typed : null);

        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();

            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        //Closing without pressing Save is a cancel. TrySetResult, not SetResult: Answer closes the
        //window too, and the second call would throw on an already-completed source.
        _answer.TrySetResult(null);

        base.OnClosed(e);
    }
}
