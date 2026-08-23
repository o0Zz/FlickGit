using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using FlickGit.Ai;
using FlickGit.App.Localization;
using FlickGit.App.Rendering;
using FlickGit.App.Resident;
using FlickGit.App.Settings;
using FlickGit.App.Shell;

namespace FlickGit.App.Views;

/// <summary>Which tab <see cref="SettingsWindow"/> opens on.</summary>
public enum SettingsTab
{
    General,
    Help,
    About,
}

/// <summary>
/// The common settings, the help page and the about box, in one small window.
///
/// <b>Not the settings window CLAUDE.md Phase 5 dropped.</b> That one was a drag-and-drop action
/// list with per-row icon pickers and an inline action editor — more UI than Phases 1 to 4 put
/// together, and a graphical front end for a file that is documented and hand-editable. That
/// reasoning still stands, and <c>actions.json</c> is still the way to customise the menu.
///
/// What it does not cover is the handful of switches whose JSON key nobody can guess before they
/// have found the file: whether the Explorer menu is registered at all, whether the tool starts
/// with Windows, and which language this is. So those are here, everything else says where it
/// lives, and the window stays one screen.
///
/// The three registry- and disk-touching collaborators arrive through the constructor; the window
/// itself only reads and writes their state, which is the whole of what "apply" means here.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly FlickSettings _settings;
    private readonly ShellIntegration _shell;
    private readonly Autostart _autostart;
    private readonly ApiKeyStore _keys;

    /// <summary>The language selected when the window opened, to tell a real change from a re-pick.</summary>
    private readonly string _languageOnOpen;

    public SettingsWindow(FlickSettings settings, ShellIntegration shell, Autostart autostart, ApiKeyStore keys)
    {
        _settings = settings;
        _shell = shell;
        _autostart = autostart;
        _keys = keys;
        _languageOnOpen = settings.Language;

        InitializeComponent();

        ApplyText();
        LoadValues();
        LoadHelp();
        LoadAbout();
    }

    /// <summary>Opens on one of the three tabs. The tray's About entry is the only caller that picks.</summary>
    public void Select(SettingsTab tab) =>
        Tabs.SelectedItem = tab switch
        {
            SettingsTab.Help => HelpTab,
            SettingsTab.About => AboutTab,
            _ => GeneralTab,
        };

    /// <summary>
    /// Every label, once, at construction.
    ///
    /// In code rather than in the XAML because the strings come from the embedded .lang files —
    /// same as every other window in the product.
    /// </summary>
    private void ApplyText()
    {
        Title = Strings.Get("settings.title");

        GeneralTab.Header = Strings.Get("settings.tab.general");
        HelpTab.Header = Strings.Get("settings.tab.help");
        AboutTab.Header = Strings.Get("settings.tab.about");

        ExplorerSection.Text = Strings.Get("settings.section.explorer");
        ContextMenuBox.Content = Strings.Get("settings.contextmenu");
        ContextMenuHint.Text = Strings.Get("settings.contextmenu.hint");
        AutostartBox.Content = Strings.Get("settings.autostart");
        AutostartHint.Text = Strings.Get("settings.autostart.hint");

        CommitSection.Text = Strings.Get("settings.section.commit");
        WarnPrimaryBox.Content = Strings.Get("settings.warnprimary");
        CloseAfterBox.Content = Strings.Get("settings.closeafter");
        NotifyBox.Content = Strings.Get("settings.notify");

        AiSection.Text = Strings.Get("settings.section.ai");
        AiProviderLabel.Text = Strings.Get("settings.ai.provider");
        AiKeyButton.Content = Strings.Get("settings.ai.key");
        AiKeyClearButton.Content = Strings.Get("settings.ai.key.clear");
        AiAllowBox.Content = Strings.Get("settings.ai.allow");
        AiAllowHint.Text = Strings.Get("settings.ai.allow.hint");

        LanguageSection.Text = Strings.Get("settings.section.language");
        LanguageHint.Text = Strings.Get("settings.language.hint");

        AdvancedText.Text = Strings.Get("settings.advanced");
        AdvancedPaths.Text = $"{FlickSettings.FilePath}\n{FlickSettings.ActionsFilePath}";
        OpenFolderButton.Content = Strings.Get("settings.advanced.open");

        EditHelpButton.Content = Strings.Get("settings.help.edit");
        ReloadHelpButton.Content = Strings.Get("settings.help.reload");

        SaveButton.Content = Strings.Get("settings.save");
        CloseButton.Content = Strings.Get("commit.button.cancel");
    }

    /// <summary>
    /// The current state of everything the window can change.
    ///
    /// Read from the source of truth in each case — the registry for the context menu, the Task
    /// Scheduler for autostart — never from a remembered flag. A menu removed by
    /// `flick uninstall-shell`, or a task deleted outside FlickGit, has to show here as what it
    /// actually is.
    /// </summary>
    private void LoadValues()
    {
        ContextMenuBox.IsChecked = _shell.IsInstalled();
        AutostartBox.IsChecked = _autostart.IsEnabled();

        //One entry per provider, the enum as the item so nothing has to map a display string back.
        AiProviderBox.Items.Clear();
        foreach (AiProvider provider in new[] { AiProvider.Disabled, AiProvider.Anthropic, AiProvider.OpenAi })
            AiProviderBox.Items.Add(new ProviderChoice(provider));

        AiProviderBox.SelectedItem = AiProviderBox.Items
            .Cast<ProviderChoice>()
            .FirstOrDefault(c => c.Provider == ParseProvider(_settings.AiProvider))
            ?? AiProviderBox.Items.Cast<ProviderChoice>().First();

        AiAllowBox.IsChecked = _settings.AiAllowDiffsToLeaveMachine;

        WarnPrimaryBox.IsChecked = _settings.WarnWhenCommittingToPrimaryBranch;
        CloseAfterBox.IsChecked = _settings.CloseCommitWindowAfterSuccess;
        NotifyBox.IsChecked = _settings.ShowSuccessNotification;

        //Automatic first, then the embedded languages in the order Strings lists them: English, then
        //the rest by their own name for themselves.
        LanguageBox.Items.Add(new ComboBoxItem { Content = Strings.Get("settings.language.auto"), Tag = string.Empty });

        foreach (Strings.Language language in Strings.Available)
            LanguageBox.Items.Add(new ComboBoxItem { Content = language.Name, Tag = language.Code });

        LanguageBox.SelectedIndex = 0;

        for (int i = 1; i < LanguageBox.Items.Count; i++)
        {
            if (((ComboBoxItem)LanguageBox.Items[i]!).Tag is string code &&
                code.Equals(_settings.Language, StringComparison.OrdinalIgnoreCase) &&
                _settings.Language.Length > 0)
            {
                LanguageBox.SelectedIndex = i;
                break;
            }
        }
    }

    /// <summary>The two links in the About tab that are not text: the icon and the version.</summary>
    private void LoadAbout()
    {
        AboutVersion.Text = Strings.Get("settings.about.version", App.Version);
        AboutTagline.Text = Strings.Get("settings.about.tagline");
        AboutAuthor.Text = Strings.Get("settings.about.author");

        //The same file the registry hands to Explorer for the context menu, so there is one icon to
        //keep in step. Missing is cosmetic: the row simply stays hidden.
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "flickgit.ico");

        if (!File.Exists(iconPath))
            return;

        try
        {
            AppIcon.Source = new BitmapImage(new Uri(iconPath, UriKind.Absolute));
            AppIcon.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            //An unreadable icon is not worth a message on an About box.
        }
    }

    /// <summary>
    /// The help page, from <c>Help.md</c> beside the executable.
    ///
    /// A file rather than a compiled-in page, and the reason is the whole design of this tab: the
    /// user can open it in any text editor, change it, and press Reload. Missing is reported in
    /// place, with the path, because "where would I put one?" is the only question that follows.
    /// </summary>
    private void LoadHelp()
    {
        string path = HelpFilePath;

        HelpPathText.Text = path;
        HelpPathText.ToolTip = path;

        string markdown;

        try
        {
            markdown = File.Exists(path)
                ? File.ReadAllText(path)
                : Strings.Get("settings.help.missing", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            markdown = Strings.Get("settings.help.unreadable", ex.Message);
        }

        HelpView.Document = MarkdownFlow.Render(markdown);

        //Nothing to open when there is no file. Reload stays live: the user may be about to write one.
        EditHelpButton.IsEnabled = File.Exists(path);
    }

    private static string HelpFilePath => Path.Combine(AppContext.BaseDirectory, "Help.md");

    /// <summary>
    /// Applies everything, and closes when there is nothing left to say.
    ///
    /// The window stays open on a failure — the message is beside the buttons and would go with it —
    /// and on a language change, where the only useful thing left to report is that a restart is
    /// needed before it shows.
    /// </summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.WarnWhenCommittingToPrimaryBranch = WarnPrimaryBox.IsChecked == true;
        _settings.CloseCommitWindowAfterSuccess = CloseAfterBox.IsChecked == true;
        _settings.ShowSuccessNotification = NotifyBox.IsChecked == true;

        string language = LanguageBox.SelectedItem is ComboBoxItem { Tag: string code } ? code : string.Empty;
        bool languageChanged = !language.Equals(_languageOnOpen, StringComparison.OrdinalIgnoreCase);

        _settings.Language = language;

        _settings.AiProvider = SelectedProvider.ToString().ToLowerInvariant();

        //Ticking the box *is* answering the one-time consent question, so it is recorded as asked.
        //Otherwise the first generation would ask again about something the user just agreed to.
        bool allow = AiAllowBox.IsChecked == true;

        if (allow != _settings.AiAllowDiffsToLeaveMachine)
            _settings.AiDiffConsentShown = true;

        _settings.AiAllowDiffsToLeaveMachine = allow;

        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(Strings.Get("settings.savefailed", ex.Message));
            return;
        }

        //The registry and the Task Scheduler after the file, and only when the answer actually
        //changed: re-registering the whole context menu because the user ticked a different box is
        //work nobody asked for, and it is the one operation here that can fail on its own.
        if (ApplyContextMenu() is { } shellError)
        {
            Report(shellError);
            return;
        }

        if (ApplyAutostart() is { } autostartError)
        {
            Report(autostartError);
            return;
        }

        if (languageChanged)
        {
            Report(Strings.Get("settings.language.restart"));
            return;
        }

        Close();
    }

    /// <summary>Registers or removes the Explorer entries, returning the failure message or null.</summary>
    private string? ApplyContextMenu()
    {
        bool wanted = ContextMenuBox.IsChecked == true;

        if (wanted == _shell.IsInstalled())
            return null;

        InstallResult result = wanted ? _shell.Install() : _shell.Uninstall();

        //Whatever happened, the box must show the truth afterwards rather than the request.
        ContextMenuBox.IsChecked = _shell.IsInstalled();

        return result.Succeeded ? null : result.Message;
    }

    /// <summary>Registers or removes the logon task, returning the failure message or null.</summary>
    private string? ApplyAutostart()
    {
        bool wanted = AutostartBox.IsChecked == true;

        if (wanted == _autostart.IsEnabled())
            return null;

        (bool succeeded, string message) = wanted ? _autostart.Enable() : _autostart.Disable();

        AutostartBox.IsChecked = _autostart.IsEnabled();

        return succeeded ? null : message;
    }

    /// <summary>The provider the ComboBox is showing, whether or not Save has been pressed.</summary>
    private AiProvider SelectedProvider =>
        AiProviderBox.SelectedItem is ProviderChoice choice ? choice.Provider : AiProvider.Disabled;

    /// <summary>
    /// Says whether a key is stored for the selected provider, without reading it.
    ///
    /// Per provider, because <see cref="ApiKeyStore"/> keeps one credential each — so switching the
    /// ComboBox has to re-ask rather than carry the previous answer across.
    /// </summary>
    private void RefreshKeyStatus()
    {
        AiProvider provider = SelectedProvider;
        bool disabled = provider == AiProvider.Disabled;

        //Nothing to store a key for, and nothing to send. The rest of the section stays visible so
        //it is obvious what turning a provider on would offer.
        AiKeyButton.IsEnabled = !disabled;
        AiAllowBox.IsEnabled = !disabled;

        if (disabled)
        {
            AiKeyClearButton.IsEnabled = false;
            AiKeyStatus.Text = string.Empty;
            return;
        }

        bool stored = _keys.Has(provider);

        AiKeyClearButton.IsEnabled = stored;
        AiKeyStatus.Text = Strings.Get(stored ? "settings.ai.key.stored" : "settings.ai.key.missing", provider.ToString());
    }

    private void OnAiProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        //Fires during InitializeComponent, before the store is assigned.
        if (_keys is not null)
            RefreshKeyStatus();
    }

    /// <summary>
    /// Stores a key for the selected provider.
    ///
    /// <b>Applied immediately, unlike everything else in this window.</b> "Nothing is applied until
    /// Save" is about the registry, the Task Scheduler and settings.json; a key is none of those. The
    /// alternative is holding the secret in a field until the user presses Save, which is worse than
    /// writing it to the credential store the moment it is typed — and a Cancel that silently threw
    /// away a key the user had just pasted would be its own kind of wrong.
    /// </summary>
    private void OnSetApiKey(object sender, RoutedEventArgs e)
    {
        AiProvider provider = SelectedProvider;

        if (provider == AiProvider.Disabled)
            return;

        //The window returns the key; it is never logged and never comes back out of the store.
        if (ApiKeyWindow.Ask(provider) is not { Length: > 0 } typed)
            return;

        Report(_keys.Write(provider, typed)
            ? Strings.Get("ai.key.saved", provider.ToString())
            : Strings.Get("ai.key.failed"));

        RefreshKeyStatus();
    }

    private void OnClearApiKey(object sender, RoutedEventArgs e)
    {
        AiProvider provider = SelectedProvider;

        if (provider == AiProvider.Disabled)
            return;

        Report(_keys.Clear(provider)
            ? Strings.Get("ai.key.cleared", provider.ToString())
            : Strings.Get("ai.key.failed"));

        RefreshKeyStatus();
    }

    /// <summary>
    /// A settings value that is not a known provider is read as disabled, the same way
    /// <c>AiConfiguration</c> reads it — a typo in a hand-edited file must not silently pick one.
    /// </summary>
    private static AiProvider ParseProvider(string name) =>
        Enum.TryParse(name, ignoreCase: true, out AiProvider provider) ? provider : AiProvider.Disabled;

    private void Report(string message) => StatusText.Text = message;

    /// <summary>
    /// One ComboBox row. A value object rather than a string, so the selection carries the provider
    /// itself and nothing has to map a display name back to an enum.
    /// </summary>
    private sealed record ProviderChoice(AiProvider Provider)
    {
        public override string ToString() => Provider switch
        {
            AiProvider.Anthropic => "Anthropic — Claude Haiku 4.5",
            AiProvider.OpenAi => "OpenAI — GPT-5.6 Luna",
            _ => Strings.Get("settings.ai.provider.disabled"),
        };
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            //The folder rather than either file: a .json with no registered handler would fail, and
            //explorer.exe opening a directory always works.
            Directory.CreateDirectory(FlickSettings.DirectoryPath);

            using Process? opened = Process.Start(new ProcessStartInfo
            {
                FileName = FlickSettings.DirectoryPath,
                UseShellExecute = true,
            });

            _ = opened;
        }
        catch (Exception ex)
        {
            //The paths are on screen already, which is the part that answers the question.
            Report(Strings.Get("settings.openfailed", ex.Message));
        }
    }

    private void OnEditHelp(object sender, RoutedEventArgs e)
    {
        try
        {
            using Process? opened = Process.Start(new ProcessStartInfo
            {
                FileName = HelpFilePath,
                UseShellExecute = true,
            });

            _ = opened;
        }
        catch (Exception ex)
        {
            Report(Strings.Get("settings.openfailed", ex.Message));
        }
    }

    private void OnReloadHelp(object sender, RoutedEventArgs e) => LoadHelp();

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
            _ = started;
        }
        catch (Exception ex)
        {
            Report(Strings.Get("settings.openfailed", ex.Message));
        }

        e.Handled = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
