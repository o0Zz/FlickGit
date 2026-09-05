using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.Matching;
using FlickGit.Models;
using FlickGit.Submodules;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// What submodules are there, adding one, initialising, updating and removing.
///
/// <b>It commits nothing.</b> Both write operations leave their work in the index and the window
/// says so — the next step is the commit window, which is where a commit is made in this product,
/// and which the footer button opens.
///
/// <c>.git/modules/&lt;name&gt;</c> is never deleted: it can hold commits made in there and never
/// pushed, so removing a submodule takes it out of the working tree and the index and leaves that
/// alone.
/// </summary>
internal sealed class SubmodulesWindow : ReloadableWindow
{
    private readonly SubmoduleService _submodules;
    private readonly RepositoryInfo _repository;

    private readonly List<GitSubmodule> _all = [];

    private readonly TextBox _filter = new()
    {
        Margin = new Thickness(10, 10, 10, 6),
        PlaceholderText = Strings.Get("submodule.filter.hint"),
    };

    private readonly ListBox _list = new() { Margin = new Thickness(10, 0) };
    private readonly ContextMenu _rowMenu = new();

    private readonly TextBox _url = new() { PlaceholderText = Strings.Get("submodule.add.url") };
    private readonly TextBox _into = new() { PlaceholderText = Strings.Get("submodule.add.into") };
    private readonly Button _add = new() { MinWidth = 90, IsEnabled = false, Classes = { "primary" } };
    private readonly TextBlock _addHint = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(10, 6),
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly Button _commit = new() { MinWidth = 110, IsVisible = false, Classes = { "primary" } };
    private readonly Button _close = new() { MinWidth = 90 };

    /// <summary>
    /// True once this window has staged something. What makes the Commit button appear — offering it
    /// before there is anything staged would be a button that opens a window with nothing in it.
    /// </summary>
    private bool _staged;

    /// <summary>
    /// True while the target folder is still being derived from the URL. Cleared the moment the user
    /// types in the folder box themselves, because from then on it is theirs.
    /// </summary>
    private bool _deriveInto = true;

    public SubmodulesWindow(RepositoryInfo repository, SubmoduleService submodules)
    {
        _repository = repository;
        _submodules = submodules;

        Title = Strings.Get("submodule.title", repository.Name);
        Width = 720;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _add.Content = Strings.Get("submodule.add.button");
        _commit.Content = Strings.Get("submodule.commit");
        _close.Content = Strings.Get("common.close");

        _list.ItemTemplate = RowTemplate();
        _list.ContextMenu = _rowMenu;
        _list.ContextRequested += OnContextRequested;
        _list.DoubleTapped += (_, _) => OpenSelectedFolder();

        _filter.TextChanged += (_, _) => ApplyFilter();
        _filter.KeyDown += (_, e) => PickerList.RouteArrows(_list, e);

        _url.TextChanged += (_, _) => OnUrlChanged();
        _into.TextChanged += (_, _) => OnIntoChanged();

        _url.KeyDown += OnAddKeyDown;
        _into.KeyDown += OnAddKeyDown;

        _add.Click += (_, _) => _ = AddAsync();
        _commit.Click += (_, _) => OnCommit();
        _close.Click += (_, _) => Close();

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"),
            Children =
            {
                Row(_filter, 0),
                Row(_list, 1),
                Row(AddPanel(), 2),
                Row(_status, 3),
                Row(Footer(), 4),
            },
        };

        Opened += (_, _) =>
        {
            _filter.Focus();
            _ = LoadAsync();
        };
    }

    /// <summary>
    /// Raised when the user asks to commit what this window staged. The host opens the commit
    /// window: this one has no business constructing it, and the commit surface is one place.
    /// </summary>
    public event Action? CommitRequested;

    private GitSubmodule? Selected => _list.SelectedItem as GitSubmodule;

    private Control AddPanel() =>
        new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = Application.Current?.FindResource("Border") as IBrush,
            Padding = new Thickness(10),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = Strings.Get("submodule.add"), Classes = { "section" } },

                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,240,Auto"),
                        Children =
                        {
                            Column(_url, 0),
                            Column(WithMargin(_into, new Thickness(8, 0)), 1),
                            Column(_add, 2),
                        },
                    },

                    _addHint,
                },
            },
        };

    private Control Footer() =>
        new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(10),
            Children = { _commit, _close },
        };

    protected override void SetBusy(bool busy)
    {
        IsBusy = busy;

        _list.IsEnabled = !busy;
        _filter.IsEnabled = !busy;
        _url.IsEnabled = !busy;
        _into.IsEnabled = !busy;

        //Through the hint rather than by flipping the flag: Add is enabled by what is typed, not by
        //the window being idle.
        if (busy)
            _add.IsEnabled = false;
        else
            UpdateAddHint();
    }

    protected override async Task ReadStateAsync()
    {
        IReadOnlyList<GitSubmodule> modules = await _submodules
            .ListAsync(_repository, ClosingToken)
            .ConfigureAwait(true);

        _all.Clear();
        _all.AddRange(modules);

        ApplyFilter();
        UpdateAddHint();

        _commit.IsVisible = _staged;
    }

    private void ApplyFilter()
    {
        string pattern = (_filter.Text ?? string.Empty).Trim();

        List<GitSubmodule> matches = pattern.Length == 0
            ? [.. _all]
            : [.. FuzzyMatcher
                .Rank(_all.Select(module => module.Path), pattern)
                .Select(match => _all.First(module => module.Path == match.Value))];

        _list.ItemsSource = matches;
        _list.SelectedIndex = matches.Count > 0 ? 0 : -1;

        SetStatus(_all.Count == 0
            ? Strings.Get("submodule.none")
            : matches.Count == 0 ? Strings.Get("submodule.nomatch")
            : Strings.Get("submodule.count", _all.Count));
    }

    /// <summary>
    /// The status line, and the one place that keeps the staged sentence from being overwritten by
    /// an ordinary count.
    /// </summary>
    private void SetStatus(string text) =>
        _status.Text = _staged
            ? $"{text}   ·   {Strings.Get("submodule.staged")}"
            : text;

    // ---- the row menu -------------------------------------------------------

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!PickerList.SelectRowUnderPointer(_list, e.Source) || Selected is not { } row)
        {
            e.Handled = true;

            return;
        }

        var items = new List<Control>();

        if (!row.IsInitialised)
        {
            //Nothing to open and nothing to update: there is no folder yet. Initialise is the same
            //command as Update, which is why it is the same call with a different word on it.
            items.Add(PickerList.Item(
                Strings.Get("submodule.menu.init"),
                () => UpdateAsync(row.Path, initialising: true)));
        }
        else
        {
            items.Add(PickerList.Item(
                Strings.Get("submodule.menu.update"),
                () => UpdateAsync(row.Path, initialising: false)));

            items.Add(PickerList.Item(
                Strings.Get("submodule.menu.open"),
                () => OpenFolder(row.Path)));
        }

        items.Add(new Separator());

        items.Add(PickerList.Item(
            Strings.Get("submodule.menu.remove"),
            () => ConfirmAndRemoveAsync(row.Path)));

        _rowMenu.ItemsSource = items;
    }

    private void OpenSelectedFolder()
    {
        if (Selected is not { } row)
            return;

        if (!row.IsInitialised)
        {
            SetStatus(Strings.Get("submodule.notinitialised", row.Path));

            return;
        }

        OpenFolder(row.Path);
    }

    private void OpenFolder(string relative)
    {
        string path = System.IO.Path.Combine(_repository.Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        SetStatus(ShellOpen.Folder(path) is null
            ? Strings.Get("submodule.opened", path)
            : Strings.Get("submodule.openfailed", path));
    }

    /// <summary>
    /// Clones and checks out what is missing.
    ///
    /// One command for both words: "the submodule is not initialised" and "the submodule is stale"
    /// are the same fix, and a second spelling would be a second thing to keep right.
    /// </summary>
    private Task UpdateAsync(string path, bool initialising) =>
        RunBusyAsync(async () =>
        {
            SetStatus(Strings.Get(initialising ? "submodule.initialising" : "submodule.updating", path));

            SubmoduleOutcome outcome = await _submodules
                .UpdateAsync(_repository, path, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                Report(Strings.Get("submodule.menu.update"), outcome.GitError);

                return;
            }

            await LoadAsync().ConfigureAwait(true);
            SetStatus(Strings.Get(initialising ? "submodule.initialised" : "submodule.updated", path));
        });

    /// <summary>
    /// Removing is <c>deinit</c> then <c>git rm</c>, in that order, and only a second answer to
    /// Git's own refusal forces.
    ///
    /// The second question is the one that matters: a submodule holding commits that were never
    /// pushed has nothing behind it — not this repository, not the Trash, and not
    /// <c>git restore</c> — so the sentence says exactly that rather than repeating the first.
    /// </summary>
    private async Task ConfirmAndRemoveAsync(string path)
    {
        if (!await MessageWindow.AskAsync(
                Strings.Get("submodule.remove.title"),
                Strings.Get("submodule.remove.ask", path),
                Strings.Get("submodule.remove.yes"),
                Strings.Get("common.cancel"),
                destructive: true).ConfigureAwait(true))
        {
            return;
        }

        bool forceNext = false;

        await RunBusyAsync(async () =>
        {
            //force: false. Git's own refusal is the guard.
            SubmoduleOutcome outcome = await _submodules
                .RemoveAsync(_repository, path, force: false, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                _staged = true;

                await LoadAsync().ConfigureAwait(true);
                SetStatus(Strings.Get("submodule.removed", path));

                return;
            }

            if (outcome.HasLocalChanges)
            {
                forceNext = await MessageWindow.AskAsync(
                    Strings.Get("submodule.remove.dirty.title"),
                    Strings.Get("submodule.remove.dirty.ask", path),
                    Strings.Get("submodule.remove.dirty.yes"),
                    Strings.Get("common.cancel"),
                    destructive: true).ConfigureAwait(true);

                return;
            }

            Report(Strings.Get("submodule.remove.title"), outcome.GitError, Strings.Get("submodule.failed"));
        }).ConfigureAwait(true);

        //Outside the busy scope: the forced run takes it again, and a nested release would unlock the
        //window while it is still working.
        if (!forceNext)
            return;

        await RunBusyAsync(async () =>
        {
            SubmoduleOutcome forced = await _submodules
                .RemoveAsync(_repository, path, force: true, CancellationToken.None)
                .ConfigureAwait(true);

            if (!forced.Succeeded)
            {
                Report(Strings.Get("submodule.remove.title"), forced.GitError, Strings.Get("submodule.failed"));

                return;
            }

            _staged = true;

            await LoadAsync().ConfigureAwait(true);
            SetStatus(Strings.Get("submodule.removed", path));
        }).ConfigureAwait(true);
    }

    // ---- adding one ---------------------------------------------------------

    private void OnUrlChanged()
    {
        if (_deriveInto)
        {
            string derived = DirectoryNameFrom(_url.Text ?? string.Empty);

            if (derived.Length > 0 && !string.Equals(derived, _into.Text, StringComparison.Ordinal))
            {
                //Set without losing ownership: the assignment raises OnIntoChanged, which would
                //otherwise read as the user typing.
                _deriveInto = false;
                _into.Text = derived;
                _deriveInto = true;
            }
        }

        UpdateAddHint();
    }

    private void OnIntoChanged()
    {
        if (_deriveInto)
            _deriveInto = false;

        UpdateAddHint();
    }

    /// <summary>
    /// The hint is the refusal the service would give, shown while the user is still typing — the
    /// same rule the commit window's branch box follows: the consequence is visible before the
    /// button.
    /// </summary>
    private void UpdateAddHint()
    {
        string url = (_url.Text ?? string.Empty).Trim();
        string into = (_into.Text ?? string.Empty).Trim();

        if (url.Length == 0 && into.Length == 0)
        {
            _addHint.Text = string.Empty;
            _add.IsEnabled = false;

            return;
        }

        if (_submodules.CheckNewPath(_repository, url, into) is { } refusal)
        {
            _addHint.Text = RefusalText(refusal, into);
            _add.IsEnabled = false;

            return;
        }

        //Declared already. Git refuses too, but only after it has cloned.
        if (_all.Any(module => string.Equals(
                module.Path,
                into.Replace('\\', '/').Trim('/'),
                StringComparison.Ordinal)))
        {
            _addHint.Text = Strings.Get("submodule.add.refused.exists", into);
            _add.IsEnabled = false;

            return;
        }

        _addHint.Text = Strings.Get("submodule.add.hint", into);
        _add.IsEnabled = !IsBusy;
    }

    /// <summary>
    /// Enter in the URL or the folder box adds.
    ///
    /// Gated on the button, which is where the refusals already live: no URL, no path, an escaping
    /// path, a non-empty target, a path already declared. Re-deciding any of that here would be a
    /// second answer to it.
    /// </summary>
    private void OnAddKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || !_add.IsEnabled)
            return;

        e.Handled = true;
        _ = AddAsync();
    }

    private Task AddAsync()
    {
        string url = (_url.Text ?? string.Empty).Trim();
        string into = (_into.Text ?? string.Empty).Trim();

        return RunBusyAsync(async () =>
        {
            SetStatus(Strings.Get("submodule.adding", into));

            SubmoduleOutcome outcome = await _submodules
                .AddAsync(_repository, url, into, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                if (outcome.Refusal is { } refusal)
                    SetStatus(RefusalText(refusal, into));
                else
                    Report(Strings.Get("submodule.add.button"), outcome.GitError);

                return;
            }

            _staged = true;

            _url.Text = string.Empty;
            _into.Text = string.Empty;
            _deriveInto = true;

            await LoadAsync().ConfigureAwait(true);
            SetStatus(Strings.Get("submodule.added", into));
        });
    }

    private void OnCommit()
    {
        CommitRequested?.Invoke();
        Close();
    }

    /// <summary>
    /// The last segment of the URL with <c>.git</c> stripped — the same derivation the clone window
    /// makes, and the name Git itself would choose.
    /// </summary>
    private static string DirectoryNameFrom(string url)
    {
        string trimmed = url.Trim().TrimEnd('/', '\\');

        if (trimmed.Length == 0)
            return string.Empty;

        //Both separators, and the scp-style `git@host:path` colon, which is neither.
        int cut = trimmed.LastIndexOfAny(['/', '\\', ':']);
        string last = cut < 0 ? trimmed : trimmed[(cut + 1)..];

        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            last = last[..^4];

        return last.Trim();
    }

    private static string RefusalText(SubmoduleRefusal refusal, string path) => refusal switch
    {
        SubmoduleRefusal.NoUrl => Strings.Get("submodule.add.refused.nourl"),
        SubmoduleRefusal.NoPath => Strings.Get("submodule.add.refused.nopath"),
        SubmoduleRefusal.OutsideRepository => Strings.Get("submodule.add.refused.outside", path),
        _ => Strings.Get("submodule.add.refused.notempty", path),
    };

    /// <summary>Reports a failure in Git's own words. Never a generic sentence.</summary>
    private void Report(string title, string? gitError, string? fallback = null) =>
        MessageWindow.Notice(
            title,
            string.IsNullOrWhiteSpace(gitError) ? fallback ?? title : gitError);

    private static T WithMargin<T>(T control, Thickness margin)
        where T : Control
    {
        control.Margin = margin;

        return control;
    }

    private static FuncDataTemplate<GitSubmodule> RowTemplate() =>
        new((module, _) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(6, 3),
            Children =
            {
                Column(
                    new TextBlock { Text = module.Path, TextTrimming = TextTrimming.CharacterEllipsis },
                    0),

                Column(
                    new TextBlock
                    {
                        Text = module.Url,
                        Opacity = 0.55,
                        Margin = new Thickness(10, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    1),

                //Three states rather than two: not initialised, initialised with a moved pointer, and
                //initialised and matching — which says nothing, because there is nothing to do to it.
                Column(
                    new TextBlock
                    {
                        Text = module.IsInitialised
                            ? (module.HasChanges ? Strings.Get("submodule.state.changed") : string.Empty)
                            : Strings.Get("submodule.state.uninitialised"),
                        Opacity = 0.7,
                    },
                    2),
            },
        });

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
}
