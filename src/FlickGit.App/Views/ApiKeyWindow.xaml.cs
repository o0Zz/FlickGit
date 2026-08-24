using System.Windows;
using FlickGit.Ai;
using FlickGit.App.Localization;
using FlickGit.App.Settings;

namespace FlickGit.App.Views;

/// <summary>
/// Asks for an API key, once, and hands it back.
///
/// The key is returned rather than stored here: this window knows how to ask a question, and
/// <see cref="ApiKeyStore"/> knows where secrets live. Nothing in between logs it, and the window
/// holds it only for as long as it is open.
/// </summary>
public partial class ApiKeyWindow : Window
{
    private string? _key;

    private ApiKeyWindow(AiProvider provider)
    {
        InitializeComponent();

        TitleText.Text = Strings.Get("ai.key.title", provider.ToString());
        //Copilot gets its own sentence, and it earns the branch: the other two want a key from a
        //dashboard, and this one wants the OAuth token an editor already stored on this machine. A
        //user handed the generic wording pastes a personal access token, which the exchange refuses
        //with a 401 that reads like a revoked key.
        PromptText.Text = provider == AiProvider.Copilot
            ? Strings.Get("ai.key.prompt.copilot")
            : Strings.Get("ai.key.prompt", provider.ToString());
        TargetText.Text = Strings.Get("ai.key.target", ApiKeyStore.TargetFor(provider));
        SaveButton.Content = Strings.Get("ai.key.save");
        CancelButton.Content = Strings.Get("commit.button.cancel");

        //Focused on open: there is exactly one thing to do here.
        Loaded += (_, _) => KeyBox.Focus();
    }

    /// <summary>
    /// Shows the prompt and returns what was typed, or null if the user cancelled or typed nothing.
    /// </summary>
    public static string? Ask(AiProvider provider)
    {
        var window = new ApiKeyWindow(provider);
        window.ShowDialog();

        return window._key;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        //Whitespace-only is a cancel, not a key. Storing one would produce a 401 that reads like a
        //revoked key rather than like a typo.
        string typed = KeyBox.Password.Trim();

        _key = typed.Length > 0 ? typed : null;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _key = null;
        Close();
    }
}
