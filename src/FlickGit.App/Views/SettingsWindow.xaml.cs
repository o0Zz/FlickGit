using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Microsoft.Win32;
using FlickGit.Ai;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.App.CommandLine;
using FlickGit.App.Rendering;
using FlickGit.App.Resident;
using FlickGit.App.Settings;
using FlickGit.App.Shell;

namespace FlickGit.App.Views;

/// <summary>
/// The common settings, the help page and the about box, in one small window.
///
/// It carries the handful of switches whose JSON key nobody can guess before they have found the
/// file: whether the Explorer menu is registered at all, whether the tool starts with Windows,
/// and which language this is. Everything else says where it lives. <c>actions.json</c> is still
/// the way to customise the menu.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly FlickSettings _settings;
    private readonly ShellIntegration _shell;
    private readonly OverlayIntegration _overlay;
    private readonly Autostart _autostart;
    private readonly CredentialStore _keys;

    /// <summary>The language selected when the window opened, to tell a real change from a re-pick.</summary>
    private readonly string _languageOnOpen;

    public SettingsWindow(
        FlickSettings settings,
        ShellIntegration shell,
        OverlayIntegration overlay,
        Autostart autostart,
        CredentialStore keys)
    {
        _settings = settings;
        _shell = shell;
        _overlay = overlay;
        _autostart = autostart;
        _keys = keys;
        _languageOnOpen = settings.Language;

        InitializeComponent();

        ApplyText();
        LoadValues();
        LoadHelp();
        LoadAbout();
    }

    public void Select(SettingsTab tab) =>
        Tabs.SelectedItem = tab switch
        {
            SettingsTab.Help => HelpTab,
            SettingsTab.About => AboutTab,
            _ => GeneralTab,
        };

    private void ApplyText()
    {
        Title = Strings.Get("settings.title");

        GeneralTab.Header = Strings.Get("settings.tab.general");
        HelpTab.Header = Strings.Get("settings.tab.help");
        AboutTab.Header = Strings.Get("settings.tab.about");

        ExplorerSection.Text = Strings.Get("settings.section.explorer");
        ContextMenuBox.Content = Strings.Get("settings.contextmenu");
        ContextMenuHint.Text = Strings.Get("settings.contextmenu.hint");
        OverlayBox.Content = Strings.Get("settings.overlay");
        OverlayHint.Text = Strings.Get("settings.overlay.hint");
        AutostartBox.Content = Strings.Get("settings.autostart");
        AutostartHint.Text = Strings.Get("settings.autostart.hint");

        CommitSection.Text = Strings.Get("settings.section.commit");
        CloseAfterBox.Content = Strings.Get("settings.closeafter");
        NotifyBox.Content = Strings.Get("settings.notify");

        EditorSection.Text = Strings.Get("settings.section.editor");
        EditorBrowseButton.Content = Strings.Get("settings.editor.browse");
        EditorHint.Text = Strings.Get("settings.editor.hint");

        PullSection.Text = Strings.Get("settings.section.pull");
        ClosePullBox.Content = Strings.Get("settings.closepull");

        AiSection.Text = Strings.Get("settings.section.ai");
        AiProviderLabel.Text = Strings.Get("settings.ai.provider");
        AiKeyButton.Content = Strings.Get("settings.ai.key");
        AiKeyClearButton.Content = Strings.Get("settings.ai.key.clear");

        LanguageSection.Text = Strings.Get("settings.section.language");
        LanguageHint.Text = Strings.Get("settings.language.hint");

        AdvancedText.Text = Strings.Get("settings.advanced");
        AdvancedPaths.Text =
            $"{FlickSettings.FilePath}\n{FlickSettings.ActionsFilePath}\n" +
            $"{Path.Combine(FlickSettings.DirectoryPath, PromptStore.CommitFileName)}\n" +
            $"{Path.Combine(FlickSettings.DirectoryPath, PromptStore.PullRequestFileName)}\n" +
            $"{Path.Combine(FlickSettings.DirectoryPath, PromptStore.ChangelogFileName)}";
        OpenFolderButton.Content = Strings.Get("settings.advanced.open");

        SaveButton.Content = Strings.Get("settings.save");
        CloseButton.Content = Strings.Get("common.close");

        //The tab strip takes the keyboard, so Left and Right move between General, Help and About from
        //the moment the window opens. Focusing the first checkbox instead would put the caret on one
        //setting out of a dozen and make the tabs unreachable without the mouse. Loaded rather than
        //here, because focus cannot be given to an element that has not been arranged yet.
        Loaded += (_, _) => Tabs.Focus();

        //The window is reused for as long as it stays open, so what LoadValues read in the constructor
        //goes stale the moment anything changes it from outside this process.
        Activated += (_, _) => RefreshExternalState();
    }

    /// <summary>What the three external values read as last time, so a box the user has since changed
    /// can be told apart from one that is merely showing an old answer.</summary>
    private bool? _loadedContextMenu;

    private bool? _loadedOverlay;

    private bool? _loadedAutostart;

    /// <summary>
    /// Re-reads the three values that live outside this process, every time the window comes back to
    /// the front.
    ///
    /// <b>Save compares each box against the live state and acts on the difference</b>, so a stale box
    /// is not a cosmetic problem: leave Settings open, run <c>flick install-overlay</c> in a terminal,
    /// come back and change the AI provider, and Save reads an unticked box against an installed
    /// overlay and <i>uninstalls it</i>, with a UAC prompt, during a save about something else. The
    /// method's own contract already says these are never read from a remembered flag; this is what
    /// makes that true for a window that outlives its constructor.
    ///
    /// A box the user has already changed is left alone -- refreshing that would throw away the very
    /// intent Save is about to act on.
    /// </summary>
    private void RefreshExternalState()
    {
        Reread(ContextMenuBox, ref _loadedContextMenu, _shell.IsInstalled());
        Reread(OverlayBox, ref _loadedOverlay, _overlay.IsInstalled());
        Reread(AutostartBox, ref _loadedAutostart, _autostart.IsEnabled());

        static void Reread(System.Windows.Controls.CheckBox box, ref bool? loaded, bool live)
        {
            //Different from what was read means the user set it, and their request stands until Save.
            if (box.IsChecked != loaded)
                return;

            box.IsChecked = live;
            loaded = live;
        }
    }

    /// <summary>
    /// The current state of everything the window can change, read from the source of truth in each
    /// case -- the registry for the context menu, the Task Scheduler for autostart -- never from a
    /// remembered flag. A menu removed by `flick uninstall-shell` has to show here as what it is.
    /// </summary>
    private void LoadValues()
    {
        ContextMenuBox.IsChecked = _shell.IsInstalled();
        OverlayBox.IsChecked = _overlay.IsInstalled();
        AutostartBox.IsChecked = _autostart.IsEnabled();

        //Recorded so a later activation can tell an untouched box from one the user has set.
        _loadedContextMenu = ContextMenuBox.IsChecked;
        _loadedOverlay = OverlayBox.IsChecked;
        _loadedAutostart = AutostartBox.IsChecked;

        //One entry per provider, the enum as the item so nothing has to map a display string back.
        AiProviderBox.Items.Clear();
        foreach (AiProvider provider in new[]
                 {
                     AiProvider.Disabled, AiProvider.Anthropic, AiProvider.OpenAi, AiProvider.Copilot,

                     //Last, and not because it is least: it is the only local one, so it is the only entry that
                     //changes what the section below means rather than which service it points at.
                     AiProvider.Ollama,
                 })
            AiProviderBox.Items.Add(new ProviderChoice(provider));

        AiProviderBox.SelectedItem = AiProviderBox.Items
            .Cast<ProviderChoice>()
            .FirstOrDefault(c => c.Provider == ParseProvider(_settings.AiProvider))
            ?? AiProviderBox.Items.Cast<ProviderChoice>().First();

        CloseAfterBox.IsChecked = _settings.CloseCommitWindowAfterSuccess;
        NotifyBox.IsChecked = _settings.ShowSuccessNotification;
        ClosePullBox.IsChecked = _settings.ClosePullWindowAfterSuccess;

        //From settings.json, which is this one's source of truth -- unlike the boxes above, whose
        //answer lives in the registry or the Task Scheduler.
        EditorBox.Text = _settings.ExternalEditor;

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

    private void LoadAbout()
    {
        AboutVersion.Text = Strings.Get("settings.about.version", App.Version);
        AboutTagline.Text = Strings.Get("settings.about.tagline");
        AboutAuthor.Text = Strings.Get("settings.about.author");

        //The same file the registry hands to Explorer for the context menu, so there is one icon to keep
        //in step. Missing is cosmetic: the row simply stays hidden.
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
        }
    }

    /// <summary>
    /// The help page, from <c>Help.md</c> beside the executable. Read-only, and shown once when the
    /// window opens: this is documentation, and a row of buttons beneath it would invite the user to
    /// maintain a page they came to read.
    ///
    /// A missing or unreadable file is reported in place with the path -- that is a broken install,
    /// and the path is what makes it diagnosable.
    /// </summary>
    private void LoadHelp()
    {
        string path = HelpFilePath;

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
    }

    private static string HelpFilePath => Path.Combine(AppContext.BaseDirectory, "Help.md");

    /// <summary>
    /// Applies everything, and closes when there is nothing left to say. The window stays open on a
    /// failure -- the message is beside the buttons and would go with it -- and on a language change,
    /// where a restart is needed before it shows.
    /// </summary>
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.CloseCommitWindowAfterSuccess = CloseAfterBox.IsChecked == true;
        _settings.ShowSuccessNotification = NotifyBox.IsChecked == true;
        _settings.ClosePullWindowAfterSuccess = ClosePullBox.IsChecked == true;
        _settings.ExternalEditor = EditorBox.Text.Trim();

        string language = LanguageBox.SelectedItem is ComboBoxItem { Tag: string code } ? code : string.Empty;
        bool languageChanged = !language.Equals(_languageOnOpen, StringComparison.OrdinalIgnoreCase);

        _settings.Language = language;

        _settings.AiProvider = SelectedProvider.ToString().ToLowerInvariant();

        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(Strings.Get("settings.savefailed", ex.Message));
            return;
        }

        //The registry and the Task Scheduler after the file, and only when the answer actually changed:
        //re-registering the whole context menu because the user ticked a different box is work nobody
        //asked for, and it is the one operation here that can fail on its own.
        if (ApplyContextMenu() is { } shellError)
        {
            Report(shellError);
            return;
        }

        if (await ApplyOverlayAsync().ConfigureAwait(true) is { } overlayError)
        {
            Report(overlayError);
            return;
        }

        if (ApplyAutostart() is { } autostartError)
        {
            Report(autostartError);
            return;
        }

        //Registering an overlay handler does nothing until Explorer is restarted -- they are
        //enumerated once at its startup and no notification reloads them. Saying so and staying open
        //is the same shape as a language change, and for the same reason: the setting took, and the
        //user would otherwise go looking for a badge that cannot appear yet.
        if (_overlayChanged)
        {
            Report(_overlayMessage);
            return;
        }

        if (languageChanged)
        {
            Report(Strings.Get("settings.language.restart"));
            return;
        }

        Close();
    }

    private string? ApplyContextMenu()
    {
        bool wanted = ContextMenuBox.IsChecked == true;

        if (wanted == _shell.IsInstalled())
        {
            _loadedContextMenu = ContextMenuBox.IsChecked;
            return null;
        }

        InstallResult result = wanted ? _shell.Install() : _shell.Uninstall();

        //Whatever happened, the box must show the truth afterwards rather than the request.
        ContextMenuBox.IsChecked = _shell.IsInstalled();
        _loadedContextMenu = ContextMenuBox.IsChecked;

        return result.Succeeded ? null : result.Message;
    }

    /// <summary>Whether Save changed the overlay, and what to tell the user about it.</summary>
    private bool _overlayChanged;

    private string _overlayMessage = string.Empty;

    /// <summary>
    /// The overlay, and the one place in the settings window that can raise a UAC prompt.
    ///
    /// Guarded by the same "only when the answer changed" test as everything else here, which matters
    /// more in this case than in any other: without it, saving an unrelated checkbox would prompt for
    /// administrator rights.
    /// </summary>
    private async Task<string?> ApplyOverlayAsync()
    {
        bool wanted = OverlayBox.IsChecked == true;

        if (wanted == _overlay.IsInstalled())
        {
            _loadedOverlay = OverlayBox.IsChecked;
            return null;
        }

        InstallResult result = wanted
            ? await _overlay.InstallAsync().ConfigureAwait(true)
            : await _overlay.UninstallAsync().ConfigureAwait(true);

        //Whatever happened -- including a declined prompt -- the box shows the truth afterwards.
        OverlayBox.IsChecked = _overlay.IsInstalled();
        _loadedOverlay = OverlayBox.IsChecked;

        if (!result.Succeeded)
            return result.Message;

        _overlayChanged = true;
        _overlayMessage = result.Message;

        return null;
    }

    private string? ApplyAutostart()
    {
        bool wanted = AutostartBox.IsChecked == true;

        if (wanted == _autostart.IsEnabled())
        {
            _loadedAutostart = AutostartBox.IsChecked;
            return null;
        }

        (bool succeeded, string message) = wanted ? _autostart.Enable() : _autostart.Disable();

        AutostartBox.IsChecked = _autostart.IsEnabled();
        _loadedAutostart = AutostartBox.IsChecked;

        return succeeded ? null : message;
    }

    private AiProvider SelectedProvider =>
        AiProviderBox.SelectedItem is ProviderChoice choice ? choice.Provider : AiProvider.Disabled;

    /// <summary>
    /// Says whether a key is stored for the selected provider, without reading it. Per provider, so
    /// switching the ComboBox has to re-ask rather than carry the previous answer across.
    /// </summary>
    private void RefreshKeyStatus()
    {
        AiProvider provider = SelectedProvider;
        bool disabled = provider == AiProvider.Disabled;

        //Nothing to store a key for, and nothing to send. The rest of the section stays visible so it is
        //obvious what turning a provider on would offer.
        AiKeyButton.IsEnabled = !disabled && AiOptions.RequiresKey(provider);

        if (disabled)
        {
            AiKeyClearButton.IsEnabled = false;
            AiKeyStatus.Text = string.Empty;
            return;
        }

        if (!AiOptions.RequiresKey(provider))
        {
            //Ollama. Both buttons off and a sentence saying why, rather than a live Set button that would
            //store a secret nothing ever reads -- and rather than an empty row, which would read as a
            //section that had failed to load.
            AiKeyClearButton.IsEnabled = false;
            AiKeyStatus.Text = Strings.Get("settings.ai.key.notneeded", _settings.AiOllamaUrl);
            return;
        }

        bool stored = _keys.Has(SecretTargets.AiTarget(provider));

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
    /// alternative is holding the secret in a field until Save, and a Cancel that silently threw away
    /// a key the user had just pasted would be its own kind of wrong.
    /// </summary>
    private void OnSetApiKey(object sender, RoutedEventArgs e)
    {
        AiProvider provider = SelectedProvider;

        if (provider == AiProvider.Disabled)
            return;

        //The window returns the key; it is never logged and never comes back out of the store.
        if (SecretWindow.AskForApiKey(this, provider) is not { Length: > 0 } typed)
            return;

        Report(_keys.Write(SecretTargets.AiTarget(provider), typed)
            ? Strings.Get("ai.key.saved", provider.ToString())
            : Strings.Get("ai.key.failed"));

        RefreshKeyStatus();
    }

    private void OnClearApiKey(object sender, RoutedEventArgs e)
    {
        AiProvider provider = SelectedProvider;

        if (provider == AiProvider.Disabled)
            return;

        Report(_keys.Clear(SecretTargets.AiTarget(provider))
            ? Strings.Get("ai.key.cleared", provider.ToString())
            : Strings.Get("ai.key.failed"));

        RefreshKeyStatus();
    }

    /// <summary>
    /// A settings value that is not a known provider is read as disabled, the same way
    /// <c>AiConfiguration</c> reads it -- a typo in a hand-edited file must not silently pick one.
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
        /// <summary>
        /// The service's name and nothing else -- naming the model too would be a second place for the
        /// default to be written down, wrong the moment <c>aiModel</c> is set. The four services are
        /// product names and are never translated; <c>Disabled</c> is a word, so it comes from the
        /// language file like every other string a window shows.
        /// <para>
        /// <b>One arm per member, and the discard names no provider.</b> It used to read
        /// <c>_ =&gt; "Disabled"</c>, which is what made Ollama render as a second <c>Disabled</c> row for
        /// as long as it existed: it was in the list, selectable, and saved correctly -- only its name
        /// was another provider's, and the status line underneath contradicted the label. A discard
        /// arm is unavoidable, since a switch expression must be exhaustive over the underlying
        /// <c>int</c>; what is avoidable is it carrying a label of its own, so it falls back to the
        /// enum's name. A provider added to <see cref="AiProvider"/> and forgotten here then shows as
        /// itself rather than as something else.
        /// </para>
        /// </summary>
        public override string ToString() => Provider switch
        {
            AiProvider.Disabled => Strings.Get("settings.ai.provider.disabled"),
            AiProvider.Anthropic => "Anthropic",
            AiProvider.OpenAi => "OpenAI",
            AiProvider.Copilot => "GitHubCopilot",
            AiProvider.Ollama => "Ollama",
            _ => Provider.ToString(),
        };
    }

    /// <summary>
    /// Picks the editor executable. It only fills the box in -- Save is still what applies it, like
    /// everything else on this tab.
    /// </summary>
    private void OnBrowseEditor(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = Strings.Get("settings.editor.filter"),
            CheckFileExists = true,

            //Where editors install. Not the repository, and not wherever the process was started.
            InitialDirectory = EditorBox.Text.Trim() is { Length: > 0 } current
                ? Path.GetDirectoryName(current) ?? string.Empty
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };

        if (dialog.ShowDialog(this) == true)
            EditorBox.Text = dialog.FileName;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            //The folder rather than either file: a .json with no registered handler would fail, and
            //explorer.exe opening a directory always works.
            Directory.CreateDirectory(FlickSettings.DirectoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            //The paths are on screen already, which is the part that answers the question.
            Report(Strings.Get("settings.openfailed", ex.Message));
            return;
        }

        if (ShellOpen.Folder(FlickSettings.DirectoryPath) is { } error)
            Report(Strings.Get("settings.openfailed", error));
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (ShellOpen.Uri(e.Uri.ToString()) is { } error)
            Report(Strings.Get("settings.openfailed", error));

        e.Handled = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
