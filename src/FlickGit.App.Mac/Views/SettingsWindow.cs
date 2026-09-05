using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FlickGit.Ai;
using FlickGit.App.CommandLine;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.App.Mac.Rendering;
using FlickGit.App.Settings;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// The common settings, the help page and the about box, in one small window.
///
/// It carries the handful of switches whose JSON key nobody can guess before they have found the
/// file: whether FlickGit starts at login, which language this is, and where the AI key goes.
/// Everything else says where it lives. <c>actions.json</c> is still the way to customise the menu.
///
/// <b>Two rows the Windows window has are absent rather than dead.</b> The Explorer context menu is
/// a registry projection and the repository overlay is an <c>HKLM</c> handler — neither exists on
/// macOS, where the Finder Sync extension is installed by the app bundle rather than by a checkbox.
/// A checkbox that silently did nothing would be worse than its absence, which is the same argument
/// the pull-request window makes about GitHub's delete-on-merge flag.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly FlickSettings _settings;
    private readonly IAutostart _autostart;
    private readonly ISecretStore _keys;
    private readonly ISecretPrompt _prompt;

    /// <summary>The language selected when the window opened, to tell a real change from a re-pick.</summary>
    private readonly string _languageOnOpen;

    private readonly TabControl _tabs = new();
    private readonly TabItem _generalTab = new();
    private readonly TabItem _helpTab = new();
    private readonly TabItem _aboutTab = new();

    private readonly CheckBox _autostartBox = new();
    private readonly CheckBox _closeAfter = new();
    private readonly CheckBox _notify = new();
    private readonly CheckBox _closePull = new();
    private readonly TextBox _editor = new();
    private readonly ComboBox _provider = new() { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _setKey = new() { Classes = { "strip" } };
    private readonly Button _clearKey = new() { Classes = { "strip" } };
    private readonly TextBlock _keyStatus = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _language = new() { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };

    private readonly TextBlock _status = new()
    {
        Classes = { "muted" },
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 0, 12, 0),
    };

    private readonly Button _save = new() { MinWidth = 100, Classes = { "primary" }, IsDefault = true };
    private readonly Button _close = new() { MinWidth = 90 };

    /// <summary>
    /// What autostart read as last time, so a box the user has since changed can be told apart from
    /// one that is merely showing an old answer.
    /// </summary>
    private bool? _loadedAutostart;

    public SettingsWindow(
        FlickSettings settings,
        IAutostart autostart,
        ISecretStore keys,
        ISecretPrompt prompt)
    {
        _settings = settings;
        _autostart = autostart;
        _keys = keys;
        _prompt = prompt;
        _languageOnOpen = settings.Language;

        Title = Strings.Get("settings.title");
        Width = 600;
        Height = 620;
        MinWidth = 480;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _save.Click += (_, _) => _ = SaveAsync();
        _close.Click += (_, _) => Close();
        _setKey.Click += (_, _) => _ = SetApiKeyAsync();
        _clearKey.Click += (_, _) => ClearApiKey();
        _provider.SelectionChanged += (_, _) => RefreshKeyStatus();

        Content = Build();

        LoadValues();
        RefreshKeyStatus();

        //The window is reused for as long as it stays open, so what LoadValues read goes stale the
        //moment anything changes it from outside this process.
        Activated += (_, _) => RefreshExternalState();
    }

    public void Select(SettingsTab tab) =>
        _tabs.SelectedItem = tab switch
        {
            SettingsTab.Help => _helpTab,
            SettingsTab.About => _aboutTab,
            _ => _generalTab,
        };

    private Control Build()
    {
        _generalTab.Header = Strings.Get("settings.tab.general");
        _helpTab.Header = Strings.Get("settings.tab.help");
        _aboutTab.Header = Strings.Get("settings.tab.about");

        _generalTab.Content = GeneralTab();
        _helpTab.Content = HelpTab();
        _aboutTab.Content = AboutTab();

        _tabs.ItemsSource = new[] { _generalTab, _helpTab, _aboutTab };

        _save.Content = Strings.Get("settings.save");
        _close.Content = Strings.Get("common.close");

        var footer = new Grid
        {
            Margin = new Thickness(0, 12, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                Column(_status, 0),
                Column(
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { _save, _close },
                    },
                    1),
            },
        };

        var grid = new Grid
        {
            Margin = new Thickness(14, 12),
            RowDefinitions = new RowDefinitions("*,Auto"),
        };

        grid.Children.Add(Row(_tabs, 0));
        grid.Children.Add(Row(footer, 1));

        return grid;
    }

    private Control GeneralTab()
    {
        _autostartBox.Content = Strings.Get("settings.autostart");
        _closeAfter.Content = Strings.Get("settings.closeafter");
        _notify.Content = Strings.Get("settings.notify");
        _closePull.Content = Strings.Get("settings.closepull");
        _setKey.Content = Strings.Get("settings.ai.key");
        _clearKey.Content = Strings.Get("settings.ai.key.clear");

        var browse = new Button { Content = Strings.Get("settings.editor.browse"), Classes = { "strip" } };

        browse.Click += (_, _) => _ = BrowseEditorAsync();

        var openFolder = new Button
        {
            Content = Strings.Get("settings.advanced.open"),
            Classes = { "strip" },
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
        };

        openFolder.Click += (_, _) => OpenFolder();

        var editorRow = new Grid { Margin = new Thickness(0, 8, 0, 0), ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        editorRow.Children.Add(Column(_editor, 0));
        browse.Margin = new Thickness(8, 0, 0, 0);
        editorRow.Children.Add(Column(browse, 1));

        var panel = new StackPanel
        {
            Children =
            {
                Section("settings.section.startup", top: 0),
                Spaced(_autostartBox, 8),
                Hint("settings.autostart.hint.mac", left: 22),

                Section("settings.section.commit", top: 22),
                Spaced(_closeAfter, 8),
                Spaced(_notify, 10),

                //The commit window's Edit item hands a file to this program. It earns a row for the
                //same reason the checkboxes do: `externalEditor` is not a key anyone guesses, and the
                //alternative to guessing it is not knowing the item exists. Empty is the system
                //default, which is why there is no "reset" button — clearing the box is the reset.
                Section("settings.section.editor", top: 22),
                editorRow,
                Hint("settings.editor.hint.mac", left: 0),

                Section("settings.section.pull", top: 22),
                Spaced(_closePull, 8),

                //The AI section exists because the key had nowhere to go. It was `flick ai key set`
                //and nothing else, which is a fine way to store a secret and a hopeless way to
                //discover that you can.
                Section("settings.section.ai", top: 22),
                new TextBlock
                {
                    Text = Strings.Get("settings.ai.provider"),
                    Classes = { "muted", "small" },
                    Margin = new Thickness(0, 8, 0, 0),
                },
                Spaced(_provider, 4),

                //The key is never shown, only reported as present or absent: it is in the Keychain
                //and this window has no business reading it back.
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(0, 10, 0, 0),
                    Children = { _setKey, _clearKey },
                },
                Spaced(_keyStatus, 6),

                Section("settings.section.language", top: 22),
                Spaced(_language, 8),
                Hint("settings.language.hint", left: 0),

                //The escape hatch, and the honest one: this window is a shortcut to the common
                //switches, not a replacement for the files.
                new Border
                {
                    Margin = new Thickness(0, 24, 0, 0),
                    Padding = new Thickness(12, 10),
                    Background = Resource("SurfaceAlt"),
                    BorderBrush = Resource("Border"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Child = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = Strings.Get("settings.advanced"), TextWrapping = TextWrapping.Wrap },
                            new TextBlock
                            {
                                Text = string.Join(
                                    Environment.NewLine,
                                    FlickSettings.FilePath,
                                    FlickSettings.ActionsFilePath,
                                    Path.Combine(FlickSettings.DirectoryPath, PromptStore.CommitFileName),
                                    Path.Combine(FlickSettings.DirectoryPath, PromptStore.PullRequestFileName),
                                    Path.Combine(FlickSettings.DirectoryPath, PromptStore.ChangelogFileName)),
                                Classes = { "muted", "mono", "small" },
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 6, 0, 0),
                            },
                            openFolder,
                        },
                    },
                },
            },
        };

        return new ScrollViewer { Padding = new Thickness(16, 14), Content = panel };
    }

    /// <summary>
    /// The help page, from <c>Help.md</c> beside the executable. Read-only, and shown once when the
    /// window opens: this is documentation, and a row of buttons beneath it would invite the user to
    /// maintain a page they came to read.
    ///
    /// A missing or unreadable file is reported in place with the path — that is a broken install,
    /// and the path is what makes it diagnosable.
    /// </summary>
    private Control HelpTab()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Help.md");
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

        return new ScrollViewer
        {
            Padding = new Thickness(16, 14, 10, 12),
            Content = MarkdownView.Render(markdown),
        };
    }

    private Control AboutTab()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(24, 28, 24, 20),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        //The same file the app bundle uses for its own icon, so there is one to keep in step. Missing
        //is cosmetic: the row simply stays absent.
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "flickgit.ico");

        if (File.Exists(iconPath))
        {
            try
            {
                panel.Children.Add(new Image
                {
                    Source = new Bitmap(iconPath),
                    Width = 56,
                    Height = 56,
                });
            }
            catch (Exception)
            {
                //An unreadable icon is not worth a message on an about box.
            }
        }

        panel.Children.Add(new TextBlock
        {
            Text = "FlickGit",
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 14, 0, 0),
        });

        panel.Children.Add(new TextBlock
        {
            Text = Strings.Get("settings.about.version", EnvironmentReports.Version),
            Classes = { "muted" },
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        });

        panel.Children.Add(new TextBlock
        {
            Text = Strings.Get("settings.about.tagline"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            LineHeight = 19,
            Margin = new Thickness(0, 18, 0, 0),
        });

        panel.Children.Add(new TextBlock
        {
            Text = Strings.Get("settings.about.author"),
            Classes = { "muted" },
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 0),
        });

        const string url = "https://github.com/o0Zz/FlickGit/";

        var link = new TextBlock
        {
            Text = url,
            Foreground = Resource("Accent"),
            TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };

        link.PointerPressed += (_, e) =>
        {
            if (ShellOpen.Uri(url) is { } error)
                Report(Strings.Get("settings.openfailed", error));

            e.Handled = true;
        };

        panel.Children.Add(link);

        return panel;
    }

    /// <summary>
    /// The current state of everything the window can change, read from the source of truth in each
    /// case — launchd for autostart — never from a remembered flag. A logon agent removed by
    /// <c>flick autostart off</c> has to show here as what it is.
    /// </summary>
    private void LoadValues()
    {
        _autostartBox.IsChecked = _autostart.IsEnabled();
        _loadedAutostart = _autostartBox.IsChecked;

        //One entry per provider, the enum as the item so nothing has to map a display string back.
        var providers = new[]
        {
            AiProvider.Disabled,
            AiProvider.Anthropic,
            AiProvider.OpenAi,
            AiProvider.Copilot,

            //Last, and not because it is least: it is the only local one, so it is the only entry
            //that changes what the section below means rather than which service it points at.
            AiProvider.Ollama,
        }.Select(provider => new ProviderChoice(provider)).ToList();

        _provider.ItemsSource = providers;
        _provider.SelectedItem = providers.FirstOrDefault(c => c.Provider == ParseProvider(_settings.AiProvider))
                                 ?? providers[0];

        _closeAfter.IsChecked = _settings.CloseCommitWindowAfterSuccess;
        _notify.IsChecked = _settings.ShowSuccessNotification;
        _closePull.IsChecked = _settings.ClosePullWindowAfterSuccess;

        //From settings.json, which is this one's source of truth — unlike the box above, whose answer
        //lives in launchd.
        _editor.Text = _settings.ExternalEditor;

        var languages = new List<LanguageChoice>
        {
            new(string.Empty, Strings.Get("settings.language.auto")),
        };

        languages.AddRange(Strings.Available.Select(language => new LanguageChoice(language.Code, language.Name)));

        _language.ItemsSource = languages;
        _language.SelectedItem = languages.FirstOrDefault(l =>
                                     l.Code.Length > 0
                                     && l.Code.Equals(_settings.Language, StringComparison.OrdinalIgnoreCase))
                                 ?? languages[0];
    }

    /// <summary>
    /// Re-reads the one value that lives outside this process, every time the window comes back to
    /// the front.
    ///
    /// <b>Save compares the box against the live state and acts on the difference</b>, so a stale box
    /// is not a cosmetic problem: leave Settings open, run <c>flick autostart on</c> in a terminal,
    /// come back and change the AI provider, and Save would read an unticked box against an enabled
    /// agent and disable it during a save about something else.
    ///
    /// A box the user has already changed is left alone — refreshing that would throw away the very
    /// intent Save is about to act on.
    /// </summary>
    private void RefreshExternalState()
    {
        bool live = _autostart.IsEnabled();

        if (_autostartBox.IsChecked != _loadedAutostart)
            return;

        _autostartBox.IsChecked = live;
        _loadedAutostart = live;
    }

    /// <summary>
    /// Applies everything, and closes when there is nothing left to say. The window stays open on a
    /// failure — the message is beside the buttons and would go with it — and on a language change,
    /// where a restart is needed before it shows.
    /// </summary>
    private async Task SaveAsync()
    {
        _settings.CloseCommitWindowAfterSuccess = _closeAfter.IsChecked == true;
        _settings.ShowSuccessNotification = _notify.IsChecked == true;
        _settings.ClosePullWindowAfterSuccess = _closePull.IsChecked == true;
        _settings.ExternalEditor = (_editor.Text ?? string.Empty).Trim();

        string language = _language.SelectedItem is LanguageChoice choice ? choice.Code : string.Empty;
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

        //launchd after the file, and only when the answer actually changed: rewriting the agent
        //because the user ticked a different box is work nobody asked for.
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

        await Task.CompletedTask.ConfigureAwait(true);

        Close();
    }

    private string? ApplyAutostart()
    {
        bool wanted = _autostartBox.IsChecked == true;

        if (wanted == _autostart.IsEnabled())
        {
            _loadedAutostart = _autostartBox.IsChecked;

            return null;
        }

        (bool succeeded, string message) = wanted ? _autostart.Enable() : _autostart.Disable();

        //Whatever happened, the box must show the truth afterwards rather than the request.
        _autostartBox.IsChecked = _autostart.IsEnabled();
        _loadedAutostart = _autostartBox.IsChecked;

        return succeeded ? null : message;
    }

    private AiProvider SelectedProvider =>
        _provider.SelectedItem is ProviderChoice choice ? choice.Provider : AiProvider.Disabled;

    /// <summary>
    /// Says whether a key is stored for the selected provider, without reading it. Per provider, so
    /// switching the box has to re-ask rather than carry the previous answer across.
    /// </summary>
    private void RefreshKeyStatus()
    {
        AiProvider provider = SelectedProvider;
        bool disabled = provider == AiProvider.Disabled;

        //Nothing to store a key for, and nothing to send. The rest of the section stays visible so it
        //is obvious what turning a provider on would offer.
        _setKey.IsEnabled = !disabled && AiOptions.RequiresKey(provider);

        if (disabled)
        {
            _clearKey.IsEnabled = false;
            _keyStatus.Text = string.Empty;

            return;
        }

        if (!AiOptions.RequiresKey(provider))
        {
            //Ollama. Both buttons off and a sentence saying why, rather than a live Set button that
            //would store a secret nothing ever reads -- and rather than an empty row, which would
            //read as a section that had failed to load.
            _clearKey.IsEnabled = false;
            _keyStatus.Text = Strings.Get("settings.ai.key.notneeded", _settings.AiOllamaUrl);

            return;
        }

        bool stored = _keys.Has(SecretTargets.AiTarget(provider));

        _clearKey.IsEnabled = stored;
        _keyStatus.Text = Strings.Get(stored ? "settings.ai.key.stored" : "settings.ai.key.missing", provider.ToString());
    }

    /// <summary>
    /// Stores a key for the selected provider.
    ///
    /// <b>Applied immediately, unlike everything else in this window.</b> "Nothing is applied until
    /// Save" is about launchd and settings.json; a key is neither. The alternative is holding the
    /// secret in a field until Save, and a Close that silently threw away a key the user had just
    /// pasted would be its own kind of wrong.
    /// </summary>
    private async Task SetApiKeyAsync()
    {
        AiProvider provider = SelectedProvider;

        if (provider == AiProvider.Disabled)
            return;

        //The prompt returns the key; it is never logged and never comes back out of the store.
        if (await _prompt.AskForApiKeyAsync(provider).ConfigureAwait(true) is not { Length: > 0 } typed)
            return;

        Report(_keys.Write(SecretTargets.AiTarget(provider), typed)
            ? Strings.Get("ai.key.saved", provider.ToString())
            : Strings.Get("ai.key.failed"));

        RefreshKeyStatus();
    }

    private void ClearApiKey()
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
    /// Picks the editor. It only fills the box in — Save is still what applies it, like everything
    /// else on this tab.
    ///
    /// <c>/Applications</c> as the starting folder, and a macOS editor is a <c>.app</c> bundle rather
    /// than a bare executable, which is what the file picker's filter has to allow.
    /// </summary>
    private async Task BrowseEditorAsync()
    {
        IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> picked = await StorageProvider
            .OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = Strings.Get("settings.section.editor"),
                AllowMultiple = false,
                SuggestedStartLocation = await StorageProvider
                    .TryGetFolderFromPathAsync(new Uri("file:///Applications"))
                    .ConfigureAwait(true),
            })
            .ConfigureAwait(true);

        //Path.LocalPath rather than the uri: an editor picked from a folder with a space in its name
        //arrives percent-encoded, and that string handed to a process start is a path that does not
        //exist.
        if (picked.Count > 0 && picked[0].Path.LocalPath is { Length: > 0 } path)
            _editor.Text = path;
    }

    private void OpenFolder()
    {
        try
        {
            //The folder rather than either file: a .json with no registered handler would fail, and
            //opening a directory always works.
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

    private void Report(string message) => _status.Text = message;

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

    /// <summary>
    /// A settings value that is not a known provider is read as disabled, the same way
    /// <c>AiConfiguration</c> reads it — a typo in a hand-edited file must not silently pick one.
    /// </summary>
    private static AiProvider ParseProvider(string name) =>
        Enum.TryParse(name, ignoreCase: true, out AiProvider provider) ? provider : AiProvider.Disabled;

    private static TextBlock Section(string key, double top) =>
        new()
        {
            Text = Strings.Get(key),
            Classes = { "section" },
            Margin = new Thickness(0, top, 0, 0),
        };

    private static TextBlock Hint(string key, double left) =>
        new()
        {
            Text = Strings.Get(key),
            Classes = { "muted", "small" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(left, 3, 0, 0),
        };

    private static T Spaced<T>(T control, double top)
        where T : Control
    {
        control.Margin = new Thickness(0, top, 0, 0);

        return control;
    }

    private static T Row<T>(T control, int row)
        where T : Control
    {
        control.SetValue(Grid.RowProperty, row);

        return control;
    }

    private static T Column<T>(T control, int column)
        where T : Control
    {
        control.SetValue(Grid.ColumnProperty, column);

        return control;
    }

    private static IBrush? Resource(string key) => Application.Current?.FindResource(key) as IBrush;

    /// <summary>
    /// One row in the provider box. A value object rather than a string, so the selection carries the
    /// provider itself and nothing has to map a display name back to an enum.
    /// </summary>
    private sealed record ProviderChoice(AiProvider Provider)
    {
        /// <summary>
        /// The service's name and nothing else — naming the model too would be a second place for the
        /// default to be written down, wrong the moment <c>aiModel</c> is set. The four services are
        /// product names and are never translated; <c>Disabled</c> is a word, so it comes from the
        /// language file like every other string a window shows.
        /// <para>
        /// <b>One arm per member, and the discard names no provider.</b> A discard arm is unavoidable,
        /// since a switch expression must be exhaustive over the underlying <c>int</c>; what is
        /// avoidable is it carrying a label of its own, so it falls back to the enum's name. A provider
        /// added to <see cref="AiProvider"/> and forgotten here then shows as itself rather than as
        /// something else.
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

    /// <param name="Code">Empty for "follow the system", which is what the setting's empty value means.</param>
    private sealed record LanguageChoice(string Code, string Name)
    {
        public override string ToString() => Name;
    }
}
