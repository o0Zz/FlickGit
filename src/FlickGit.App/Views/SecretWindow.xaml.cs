using System.Windows;
using FlickGit.Ai;
using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.Forges;

namespace FlickGit.App.Views;

/// <summary>
/// Asks for a secret, once, and hands it back.
///
/// The secret is returned rather than stored here: this window knows how to ask a question, and
/// <see cref="CredentialStore"/> knows where secrets live. Nothing in between logs it, and the
/// window holds it only for as long as it is open.
///
/// It was <c>ApiKeyWindow</c> and asked only about an AI provider. The pull-request feature needs
/// the same question about a forge token — same <c>PasswordBox</c>, same argument against a command
/// line, same sentence naming where it will be filed — so the two callers became two static entry
/// points over one window rather than a second window that would drift from this one.
/// </summary>
public partial class SecretWindow : Window
{
    private string? _secret;

    private SecretWindow(string title, string prompt, string target)
    {
        InitializeComponent();

        TitleText.Text = title;
        PromptText.Text = prompt;
        TargetText.Text = Strings.Get("ai.key.target", target);
        SaveButton.Content = Strings.Get("ai.key.save");
        CancelButton.Content = Strings.Get("commit.button.cancel");

        //Focused on open: there is exactly one thing to do here.
        Loaded += (_, _) => KeyBox.Focus();
    }

    /// <summary>
    /// Asks for an AI provider's API key.
    /// </summary>
    public static string? AskForApiKey(AiProvider provider) =>
        Ask(
            Strings.Get("ai.key.title", provider.ToString()),

            //Copilot gets its own sentence, and it earns the branch: the other two want a key from a
            //dashboard, and this one wants the OAuth token an editor already stored on this machine.
            //A user handed the generic wording pastes a personal access token, which the exchange
            //refuses with a 401 that reads like a revoked key.
            provider == AiProvider.Copilot
                ? Strings.Get("ai.key.prompt.copilot")
                : Strings.Get("ai.key.prompt", provider.ToString()),

            CredentialStore.AiTarget(provider));

    /// <summary>
    /// Asks for a token that opens pull requests on <paramref name="host"/>.
    ///
    /// The prompt names the service as well as the host, because what to create is different on each
    /// of the three and a user who is told only "a token for git.acme.io" has to guess which page of
    /// which settings screen. The host is what the answer is filed under, so it is what the sentence
    /// below the box says.
    /// </summary>
    public static string? AskForForgeToken(ForgeKind kind, string host) =>
        Ask(
            Strings.Get("pr.token.title", host),
            Strings.Get(kind switch
            {
                ForgeKind.GitHub => "pr.token.prompt.github",
                ForgeKind.GitLab => "pr.token.prompt.gitlab",
                _ => "pr.token.prompt.azure",
            }, host),
            CredentialStore.ForgeTarget(host));

    private static string? Ask(string title, string prompt, string target)
    {
        var window = new SecretWindow(title, prompt, target);
        window.ShowDialog();

        return window._secret;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        //Whitespace-only is a cancel, not a secret. Storing one would produce a 401 that reads like
        //a revoked credential rather than like a typo.
        string typed = KeyBox.Password.Trim();

        _secret = typed.Length > 0 ? typed : null;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _secret = null;
        Close();
    }
}
