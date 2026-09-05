using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using FlickGit.App.Localization;
using FlickGit.Config;
using FlickGit.Models;
using FlickGit.Remotes;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// The repository's own settings: the identity it commits as, its remotes, and the two preferences
/// FlickGit keeps per repository.
///
/// A window per repository rather than a tab in the settings window, because everything here is a
/// fact about one repository and <c>flick settings</c> is opened with no path at all — that window
/// would have had to grow a repository picker before it could show a single value.
///
/// <b>Two save rules, on purpose.</b> A remote edit applies the moment its button is pressed, the way
/// creating a tag does: each one is a single Git command with its own button and its own confirmation
/// where it needs one. The identity and the FlickGit defaults apply on Save, because they are a form
/// rather than a list of commands. The footer says which is which, so neither is a surprise.
///
/// Nothing here touches the network, and nothing here touches global or system config. A URL is not
/// checked for reachability — the next push answers that in Git's own words — and an identity set for
/// every repository on the machine is <c>git config --global</c>'s business, not a repository
/// window's.
/// </summary>
public sealed class RepositoryWindow : ReloadableWindow
{
    private readonly RepositoryInfo _repository;
    private readonly RepositoryConfigService _config;
    private readonly RemoteService _remotes;

    private readonly List<GitRemote> _configured = [];

    private readonly RadioButton _globalIdentity = new() { GroupName = "Identity" };
    private readonly RadioButton _repoIdentity = new() { GroupName = "Identity" };
    private readonly TextBox _name = new();
    private readonly TextBox _email = new();
    private readonly Grid _identityPanel;

    private readonly ListBox _remoteList = new()
    {
        Height = 132,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
    };

    private readonly TextBox _remoteName = new() { Classes = { "mono" } };
    private readonly TextBox _remoteUrl = new() { Classes = { "mono" } };
    private readonly Button _addRemote = new() { MinWidth = 90, Classes = { "strip" }, IsEnabled = false };
    private readonly Button _saveRemote = new() { MinWidth = 110, Classes = { "strip" }, IsEnabled = false };
    private readonly Button _removeRemote = new() { MinWidth = 90, Classes = { "strip" }, IsEnabled = false };

    private readonly TextBox _primaryBranch = new() { Classes = { "mono" }, Width = 200 };
    private readonly TextBlock _upstreamAnswer = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _askAgain = new() { MinWidth = 100, Classes = { "strip" } };

    private readonly TextBlock _status = new()
    {
        Classes = { "muted" },
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly Button _save = new() { MinWidth = 100, Classes = { "primary" } };
    private readonly Button _close = new() { MinWidth = 80 };

    /// <summary>What the last read said. Null until <see cref="ReloadableWindow.LoadAsync"/> has run once.</summary>
    private RepositoryConfig? _current;

    /// <summary>
    /// True while the read is populating.
    ///
    /// Ticking a radio raises its change event whoever did it, and the handler moves the caret into
    /// the name box — which is right when the user chose it and wrong on open, where it would make
    /// the starting focus depend on whether this repository happens to have an identity of its own.
    /// </summary>
    private bool _loading;

    public RepositoryWindow(RepositoryInfo repository, RepositoryConfigService config, RemoteService remotes)
    {
        _repository = repository;
        _config = config;
        _remotes = remotes;

        Title = Strings.Get("repo.title", repository.Name);
        Width = 660;
        Height = 620;
        MinWidth = 520;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _repoIdentity.Content = Strings.Get("repo.identity.local");
        _addRemote.Content = Strings.Get("repo.remote.add");
        _saveRemote.Content = Strings.Get("repo.remote.save");
        _removeRemote.Content = Strings.Get("repo.remote.remove");
        _askAgain.Content = Strings.Get("repo.upstream.askagain");
        _save.Content = Strings.Get("repo.save");
        _close.Content = Strings.Get("common.close");

        _identityPanel = IdentityFields();

        _globalIdentity.IsCheckedChanged += (_, _) => OnIdentityScopeChanged();
        _repoIdentity.IsCheckedChanged += (_, _) => OnIdentityScopeChanged();

        _remoteList.ItemTemplate = RemoteRowTemplate();
        _remoteList.SelectionChanged += (_, _) => OnRemoteSelected();
        _remoteName.TextChanged += (_, _) => UpdateRemoteButtons();
        _remoteUrl.TextChanged += (_, _) => UpdateRemoteButtons();

        _addRemote.Click += (_, _) => _ = AddRemoteAsync();
        _saveRemote.Click += (_, _) => _ = SaveRemoteAsync();
        _removeRemote.Click += (_, _) => _ = RemoveRemoteAsync();
        _askAgain.Click += (_, _) => _ = AskAgainAsync();
        _save.Click += (_, _) => _ = SaveAsync();
        _close.Click += (_, _) => Close();

        Content = Build();

        //On Opened rather than in the constructor, and separate from every later reload: refocusing
        //after a remote edit would pull the caret out of whatever the user was typing in next.
        Opened += async (_, _) =>
        {
            await LoadAsync().ConfigureAwait(true);
            FocusFirstField();
        };
    }

    private Control Build()
    {
        var header = new Border
        {
            Background = Resource("SurfaceAlt"),
            BorderBrush = Resource("Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 10),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Children =
                {
                    Column(new TextBlock { Text = _repository.Name, Classes = { "title" } }, 0),
                    Column(
                        new TextBlock
                        {
                            Text = _repository.Root,
                            Classes = { "muted", "mono", "small" },
                            Margin = new Thickness(12, 0, 0, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                        1),
                },
            },
        };

        var body = new ScrollViewer
        {
            Padding = new Thickness(16, 14),
            Content = new StackPanel
            {
                Children =
                {
                    Section("repo.section.identity", top: 0),

                    //Two radios rather than a checkbox, because the two states are not "on and off":
                    //one inherits a value that is shown, the other sets one here. A checkbox would
                    //have to be labelled with whichever of those the author found more natural.
                    Spaced(_globalIdentity, 8),
                    Spaced(_repoIdentity, 8),
                    _identityPanel,

                    Section("repo.section.remotes", top: 24),

                    new Border
                    {
                        Margin = new Thickness(0, 8, 0, 0),
                        BorderBrush = Resource("Border"),
                        BorderThickness = new Thickness(1),
                        Background = Resource("SurfaceSunken"),
                        Child = _remoteList,
                    },

                    RemoteFields(),

                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 8, 0, 0),
                        Spacing = 8,

                        //A gap before Remove, and no default button anywhere near it.
                        Children =
                        {
                            _addRemote,
                            _saveRemote,
                            new Border { Width = 16 },
                            _removeRemote,
                        },
                    },

                    new TextBlock
                    {
                        Text = Strings.Get("repo.remote.hint"),
                        Classes = { "muted", "small" },
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 6, 0, 0),
                    },

                    Section("repo.section.defaults", top: 24),

                    PrimaryBranchFields(),

                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 12, 0, 0),
                        Spacing = 12,
                        Children = { _upstreamAnswer, _askAgain },
                    },
                },
            },
        };

        var footer = new Border
        {
            Background = Resource("SurfaceAlt"),
            BorderBrush = Resource("Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    Column(_status, 0),

                    //No default button: Enter in the remote URL box would otherwise save the identity
                    //rather than the remote the user was typing.
                    Column(
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 12,
                            Children = { _save, _close },
                        },
                        1),
                },
            },
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        grid.Children.Add(Row(header, 0));
        grid.Children.Add(Row(body, 1));
        grid.Children.Add(Row(footer, 2));

        return grid;
    }

    private Grid IdentityFields()
    {
        var grid = new Grid
        {
            Margin = new Thickness(22, 8, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"),
        };

        grid.Children.Add(Column(Label(Strings.Get("repo.name"), new Thickness(0, 0, 8, 0)), 0));
        grid.Children.Add(Column(_name, 1));
        grid.Children.Add(Column(Label(Strings.Get("repo.email"), new Thickness(14, 0, 8, 0)), 2));
        grid.Children.Add(Column(_email, 3));

        return grid;
    }

    private Grid RemoteFields()
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 8, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("Auto,120,Auto,*"),
        };

        grid.Children.Add(Column(Label(Strings.Get("repo.remote.name"), new Thickness(0, 0, 8, 0)), 0));
        grid.Children.Add(Column(_remoteName, 1));
        grid.Children.Add(Column(Label(Strings.Get("repo.remote.url"), new Thickness(12, 0, 8, 0)), 2));
        grid.Children.Add(Column(_remoteUrl, 3));

        return grid;
    }

    private Grid PrimaryBranchFields()
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 8, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("Auto,200,*"),
        };

        grid.Children.Add(Column(Label(Strings.Get("repo.primarybranch"), new Thickness(0, 0, 8, 0)), 0));
        grid.Children.Add(Column(_primaryBranch, 1));
        grid.Children.Add(Column(
            new TextBlock
            {
                Text = Strings.Get("repo.primarybranch.hint"),
                Classes = { "muted", "small" },
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            },
            2));

        return grid;
    }

    /// <summary>
    /// The first field the window is actually offering. The identity boxes when this repository sets
    /// its own, and the remote list when it inherits the global one — a disabled TextBox cannot take
    /// focus, so asking it to would leave the window with nothing focused at all.
    /// </summary>
    private void FocusFirstField()
    {
        if (_identityPanel.IsEnabled)
            _name.Focus();
        else
            _remoteList.Focus();
    }

    protected override async Task ReadStateAsync()
    {
        RepositoryConfig config = await _config.ReadAsync(_repository, ClosingToken).ConfigureAwait(true);

        _current = config;
        _loading = true;

        try
        {
            Populate(config);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Populate(RepositoryConfig config)
    {
        //The inherited value goes in the label, so "use the global identity" says what that identity
        //is rather than asking the user to trust that one exists.
        _globalIdentity.Content = config.EffectiveName is null && config.EffectiveEmail is null
            ? Strings.Get("repo.identity.global.none")
            : Strings.Get("repo.identity.global", Describe(config.EffectiveName, config.EffectiveEmail));

        //Prefilled from whatever is in force, so switching to a repository identity starts from the
        //one being replaced rather than from two empty boxes.
        _name.Text = config.LocalName ?? config.EffectiveName ?? string.Empty;
        _email.Text = config.LocalEmail ?? config.EffectiveEmail ?? string.Empty;

        if (config.HasLocalIdentity)
            _repoIdentity.IsChecked = true;
        else
            _globalIdentity.IsChecked = true;

        _configured.Clear();
        _configured.AddRange(config.Remotes);
        _remoteList.ItemsSource = _configured.Select(remote => Row(remote, config.TrackedRemote)).ToList();

        _remoteName.Text = string.Empty;
        _remoteUrl.Text = string.Empty;

        _primaryBranch.Text = config.PrimaryBranch ?? string.Empty;
        _upstreamAnswer.Text = config.AllowUpstreamCreation switch
        {
            true => Strings.Get("repo.upstream.allowed"),
            false => Strings.Get("repo.upstream.refused"),
            null => Strings.Get("repo.upstream.unasked"),
        };

        //Nothing to reset when it was never asked, and a button that does nothing is worse than one
        //that is not there.
        _askAgain.IsEnabled = config.AllowUpstreamCreation is not null;

        _status.Text = _configured.Count == 0
            ? Strings.Get("repo.remote.none")
            : config.CurrentBranch is null ? Strings.Get("repo.detached") : string.Empty;

        UpdateRemoteButtons();
    }

    private void OnIdentityScopeChanged()
    {
        bool local = _repoIdentity.IsChecked == true;

        _identityPanel.IsEnabled = local;

        if (local && !_loading)
            _name.Focus();
    }

    private void OnRemoteSelected()
    {
        if (Selected is { } remote)
        {
            _remoteName.Text = remote.Name;
            _remoteUrl.Text = remote.FetchUrl;
        }

        UpdateRemoteButtons();
    }

    /// <summary>
    /// Which of the three remote buttons can do anything, from the two boxes and the selection.
    ///
    /// Add and Save are mutually exclusive by construction: a name that already exists cannot be
    /// added, and a name that does not exist cannot be renamed from a row that is not selected.
    /// </summary>
    private void UpdateRemoteButtons()
    {
        string name = (_remoteName.Text ?? string.Empty).Trim();
        string url = (_remoteUrl.Text ?? string.Empty).Trim();
        bool filled = name.Length > 0 && url.Length > 0;
        GitRemote? selected = Selected;

        _addRemote.IsEnabled = filled && !Exists(name);

        _saveRemote.IsEnabled = filled
            && selected is not null
            && (!string.Equals(name, selected.Name, StringComparison.Ordinal)
                || !string.Equals(url, selected.FetchUrl, StringComparison.Ordinal));

        _removeRemote.IsEnabled = selected is not null;
    }

    private Task AddRemoteAsync()
    {
        string name = (_remoteName.Text ?? string.Empty).Trim();
        string url = (_remoteUrl.Text ?? string.Empty).Trim();

        return RunBusyAsync(async () =>
        {
            ConfigOutcome outcome = await _remotes
                .AddAsync(_repository, name, url, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                Report(Strings.Get("repo.remote.add"), outcome);

                return;
            }

            await LoadAsync().ConfigureAwait(true);
            _status.Text = Strings.Get("repo.remote.added", name);
        });
    }

    /// <summary>
    /// Renames, re-points, or both.
    ///
    /// <b>The order lives in <see cref="RemoteService.SaveAsync"/></b>, not here: it is a sequence
    /// whose correctness is entirely which step goes first, and a window can only be exercised by
    /// clicking. What is left in this method is the half that is genuinely presentation — which
    /// sentence the status line ends up with.
    /// </summary>
    private Task SaveRemoteAsync()
    {
        if (Selected is not { } selected)
            return Task.CompletedTask;

        string name = (_remoteName.Text ?? string.Empty).Trim();
        string url = (_remoteUrl.Text ?? string.Empty).Trim();

        return RunBusyAsync(async () =>
        {
            RemoteSave saved = await _remotes
                .SaveAsync(_repository, selected.Name, name, selected.FetchUrl, url, CancellationToken.None)
                .ConfigureAwait(true);

            if (!saved.Succeeded)
            {
                MessageWindow.GitFailure(
                    Strings.Get("repo.remote.save"),
                    Strings.Get("repo.failed"),
                    saved.GitError,
                    _repository.Root);

                return;
            }

            //The re-point last, so that an edit doing both says where the remote now points -- which
            //is the half the user is more likely to be checking.
            if (saved.Repointed)
                _status.Text = Strings.Get("repo.remote.urlset", name, url);
            else if (saved.Renamed)
                _status.Text = Strings.Get("repo.remote.renamed", selected.Name, name);

            string said = _status.Text ?? string.Empty;

            await LoadAsync().ConfigureAwait(true);

            _status.Text = said;
        });
    }

    private async Task RemoveRemoteAsync()
    {
        if (Selected is not { } selected)
            return;

        //Asked first. Nothing in the working tree is touched and no commit is lost, but the
        //remote-tracking branches go with it and a branch that tracked it comes back with no
        //upstream -- which is more than the one row the user is looking at.
        bool confirmed = await MessageWindow.AskAsync(
            Strings.Get("repo.remote.confirm.title"),
            Strings.Get("repo.remote.confirm", selected.Name),
            Strings.Get("repo.remote.confirm.yes"),
            Strings.Get("common.cancel"),
            destructive: true).ConfigureAwait(true);

        if (!confirmed)
            return;

        await RunBusyAsync(async () =>
        {
            ConfigOutcome outcome = await _remotes
                .RemoveAsync(_repository, selected.Name, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                Report(Strings.Get("repo.remote.remove"), outcome);

                return;
            }

            await LoadAsync().ConfigureAwait(true);
            _status.Text = Strings.Get("repo.remote.removed", selected.Name);
        }).ConfigureAwait(true);
    }

    /// <summary>Forgets the remembered upstream answer. Immediate: it is a reset, not an edit.</summary>
    private Task AskAgainAsync() =>
        RunBusyAsync(async () =>
        {
            ConfigOutcome outcome = await _config
                .UnsetAsync(_repository, RepositoryConfigService.UpstreamAnswerKey, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                Report(Strings.Get("repo.upstream.askagain"), outcome);

                return;
            }

            await LoadAsync().ConfigureAwait(true);
            _status.Text = Strings.Get("repo.upstream.reset");
        });

    /// <summary>
    /// The identity and the FlickGit defaults, together.
    ///
    /// Stays open and says why on any failure, closes on success. The order is the order the user
    /// reads them in, so a failure names the first thing that did not happen rather than the last.
    /// </summary>
    private Task SaveAsync()
    {
        string title = Strings.Get("repo.section.identity");
        bool local = _repoIdentity.IsChecked == true;
        string name = (_name.Text ?? string.Empty).Trim();
        string email = (_email.Text ?? string.Empty).Trim();

        if (local && (name.Length == 0 || email.Length == 0))
        {
            //Both or neither: a commit carries an author line that needs the pair, and half of one
            //set here would silently inherit the other half from somewhere else.
            _status.Text = Strings.Get("repo.identity.needed");

            return Task.CompletedTask;
        }

        //This one closes the window on success, so RunBusyAsync re-enables controls on a window that
        //is already gone. Harmless, and cheaper than a second exit path that skips it.
        return RunBusyAsync(async () =>
        {
            ConfigOutcome identity = local
                ? await WritePairAsync(name, email).ConfigureAwait(true)
                : await ClearPairAsync().ConfigureAwait(true);

            if (!identity.Succeeded)
            {
                Report(title, identity);

                return;
            }

            string primary = (_primaryBranch.Text ?? string.Empty).Trim();

            ConfigOutcome branch = primary.Length == 0
                ? await _config
                    .UnsetAsync(_repository, RepositoryConfigService.PrimaryBranchKey, CancellationToken.None)
                    .ConfigureAwait(true)
                : await _config
                    .WriteAsync(_repository, RepositoryConfigService.PrimaryBranchKey, primary, CancellationToken.None)
                    .ConfigureAwait(true);

            if (!branch.Succeeded)
            {
                Report(Strings.Get("repo.primarybranch"), branch);

                return;
            }

            Close();
        });
    }

    private async Task<ConfigOutcome> WritePairAsync(string name, string email)
    {
        ConfigOutcome written = await _config
            .WriteAsync(_repository, RepositoryConfigService.UserNameKey, name, CancellationToken.None)
            .ConfigureAwait(true);

        return written.Succeeded
            ? await _config
                .WriteAsync(_repository, RepositoryConfigService.UserEmailKey, email, CancellationToken.None)
                .ConfigureAwait(true)
            : written;
    }

    private async Task<ConfigOutcome> ClearPairAsync()
    {
        ConfigOutcome cleared = await _config
            .UnsetAsync(_repository, RepositoryConfigService.UserNameKey, CancellationToken.None)
            .ConfigureAwait(true);

        return cleared.Succeeded
            ? await _config
                .UnsetAsync(_repository, RepositoryConfigService.UserEmailKey, CancellationToken.None)
                .ConfigureAwait(true)
            : cleared;
    }

    private GitRemote? Selected =>
        _remoteList.SelectedItem is RemoteRow row
            ? _configured.FirstOrDefault(remote => string.Equals(remote.Name, row.Name, StringComparison.Ordinal))
            : null;

    private bool Exists(string name) =>
        _configured.Any(remote => string.Equals(remote.Name, name, StringComparison.Ordinal));

    /// <summary>Git's own words, never paraphrased — CLAUDE.md, "Error Handling".</summary>
    private void Report(string title, ConfigOutcome outcome) =>
        MessageWindow.GitFailure(title, Strings.Get("repo.failed"), outcome.GitError, _repository.Root);

    protected override void SetBusy(bool busy)
    {
        IsBusy = busy;

        _remoteList.IsEnabled = !busy;
        _remoteName.IsEnabled = !busy;
        _remoteUrl.IsEnabled = !busy;
        _primaryBranch.IsEnabled = !busy;
        _save.IsEnabled = !busy;
        _globalIdentity.IsEnabled = !busy;
        _repoIdentity.IsEnabled = !busy;
        _identityPanel.IsEnabled = !busy && _repoIdentity.IsChecked == true;

        if (busy)
        {
            _addRemote.IsEnabled = false;
            _saveRemote.IsEnabled = false;
            _removeRemote.IsEnabled = false;
            _askAgain.IsEnabled = false;

            return;
        }

        //Re-derived rather than restored. The command that just ran is very likely to have changed
        //both things these buttons depend on -- which remotes exist, and which row is selected -- so
        //putting them back the way they were is how a Remove button survives the removal.
        _askAgain.IsEnabled = _current?.AllowUpstreamCreation is not null;

        UpdateRemoteButtons();
    }

    private static FuncDataTemplate<RemoteRow> RemoteRowTemplate() =>
        new((_, _) =>
        {
            var name = new TextBlock { Classes = { "mono" }, TextTrimming = TextTrimming.CharacterEllipsis };

            var url = new TextBlock
            {
                Classes = { "mono", "muted", "small" },
                Margin = new Thickness(10, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            //"tracked", and a differing push url. Both are facts about the row that the URL alone
            //does not carry.
            var note = new TextBlock
            {
                Classes = { "small" },
                Margin = new Thickness(10, 0, 0, 0),
                Foreground = Resource("Accent"),
            };

            name.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(RemoteRow.Name)));
            url.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(RemoteRow.Url)));
            note.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(RemoteRow.Note)));

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            grid.ColumnDefinitions[0].MinWidth = 90;

            grid.Children.Add(Column(name, 0));
            grid.Children.Add(Column(url, 1));
            grid.Children.Add(Column(note, 2));

            return grid;
        });

    private static TextBlock Label(string text, Thickness margin) =>
        new()
        {
            Text = text,
            Classes = { "muted" },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin,
        };

    private static TextBlock Section(string key, double top) =>
        new()
        {
            Text = Strings.Get(key),
            Classes = { "section" },
            Margin = new Thickness(0, top, 0, 0),
        };

    private static T Spaced<T>(T control, double top)
        where T : Control
    {
        control.Margin = new Thickness(0, top, 0, 0);

        return control;
    }

    private static string Describe(string? name, string? email) =>
        (name, email) switch
        {
            ({ } who, { } address) => $"{who} <{address}>",
            ({ } who, null) => who,
            (null, { } address) => address,
            _ => string.Empty,
        };

    private static RemoteRow Row(GitRemote remote, string? trackedRemote)
    {
        var notes = new List<string>();

        if (string.Equals(remote.Name, trackedRemote, StringComparison.Ordinal))
            notes.Add(Strings.Get("repo.remote.tracked"));

        if (remote.PushUrl is { } pushUrl)
            notes.Add(Strings.Get("repo.remote.push", pushUrl));

        return new RemoteRow(remote.Name, remote.FetchUrl, string.Join(" · ", notes));
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
    /// One row in the list.
    ///
    /// <see cref="ToString"/> is overridden because a list item whose content is a template has no
    /// text of its own, so accessibility falls back to it — and a record's synthesised version reads
    /// every property name out to a screen reader.
    /// </summary>
    private sealed record RemoteRow(string Name, string Url, string Note)
    {
        public override string ToString() => $"{Name} {Url} {Note}".TrimEnd();
    }
}
