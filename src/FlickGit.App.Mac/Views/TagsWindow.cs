using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FlickGit.App.Localization;
using FlickGit.Branches;
using FlickGit.Matching;
using FlickGit.Models;
using FlickGit.Tags;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// What tags exist, creating one on HEAD, deleting one, and checking one out.
///
/// <b>Delete removes it on the remote first and is never forced.</b> That order is the point: a tag
/// deleted locally and left on the remote comes back on the next fetch, which reads as the delete
/// having silently failed. No moving a tag, no <c>--force</c>, no signing, and no tag at a chosen
/// commit — every one of those is in CLAUDE.md's list of things this window does not do.
///
/// <b>Checking a tag out is the only thing in FlickGit that detaches HEAD</b>, which is why it asks
/// and why the window stays open afterwards: the sentence naming the state HEAD is now in is the
/// whole reason the question was worth asking.
/// </summary>
internal sealed class TagsWindow : ReloadableWindow
{
    private readonly TagService _tags;
    private readonly SwitchService _switches;
    private readonly RepositoryInfo _repository;

    private readonly List<GitTag> _all = [];

    /// <summary>
    /// Where a created tag is published, resolved with the list. Null means there is nowhere to
    /// publish to, which the create hint says rather than leaving the user to find out.
    /// </summary>
    private string? _remote;

    private readonly TextBox _filter = new()
    {
        Margin = new Thickness(10, 10, 10, 6),
        PlaceholderText = Strings.Get("tag.filter.hint"),
    };

    private readonly ListBox _list = new() { Margin = new Thickness(10, 0) };
    private readonly ContextMenu _rowMenu = new();

    private readonly TextBox _name = new() { PlaceholderText = Strings.Get("tag.name.label") };

    private readonly TextBox _note = new() { PlaceholderText = Strings.Get("tag.message.label") };

    private readonly Button _create = new() { MinWidth = 110, IsEnabled = false, Classes = { "primary" } };
    private readonly TextBlock _hint = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(10, 6),
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly Button _close = new() { MinWidth = 90 };

    public TagsWindow(RepositoryInfo repository, TagService tags, SwitchService switches)
    {
        _repository = repository;
        _tags = tags;
        _switches = switches;

        Title = Strings.Get("tag.title", repository.Name);
        Width = 640;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _create.Content = Strings.Get("tag.create");
        _close.Content = Strings.Get("common.close");
        ToolTip.SetTip(_note, Strings.Get("tag.message.hint"));

        _list.ItemTemplate = RowTemplate();
        _list.ContextMenu = _rowMenu;
        _list.ContextRequested += OnContextRequested;
        _list.DoubleTapped += (_, _) => _ = CheckOutSelectedAsync();

        _filter.TextChanged += (_, _) => ApplyFilter();
        _filter.KeyDown += (_, e) => PickerList.RouteArrows(_list, e);

        //Both boxes feed the hint: a message is what makes the tag annotated, so typing one changes
        //what the sentence below promises.
        _name.TextChanged += (_, _) => UpdateNewHint();
        _note.TextChanged += (_, _) => UpdateNewHint();

        _name.KeyDown += OnNewKeyDown;
        _note.KeyDown += OnNewKeyDown;

        _create.Click += (_, _) => _ = CreateAsync();
        _close.Click += (_, _) => Close();

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"),
            Children =
            {
                Row(_filter, 0),
                Row(_list, 1),
                Row(NewTagPanel(), 2),
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

    private GitTag? Selected => _list.SelectedItem as GitTag;

    /// <summary>
    /// The create panel, below the list, so the window reads top to bottom: what is there, then how
    /// to add one.
    /// </summary>
    private Control NewTagPanel() =>
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
                    new TextBlock { Text = Strings.Get("tag.new"), Classes = { "section" } },

                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("220,*,Auto"),
                        Children =
                        {
                            Column(_name, 0),
                            Column(WithMargin(_note, new Thickness(8, 0)), 1),
                            Column(_create, 2),
                        },
                    },

                    _hint,
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
            Children = { _close },
        };

    protected override void SetBusy(bool busy)
    {
        IsBusy = busy;

        _list.IsEnabled = !busy;
        _filter.IsEnabled = !busy;
        _name.IsEnabled = !busy;
        _note.IsEnabled = !busy;

        //Through the hint rather than by flipping the flag, because Create is not enabled by "not
        //busy" -- it is enabled by what is typed. Setting it true here would offer Create on an
        //empty box, and setting it false and back would need somewhere to remember which it was.
        if (busy)
            _create.IsEnabled = false;
        else
            UpdateNewHint();
    }

    protected override async Task ReadStateAsync()
    {
        //Both at once: the remote is one more process and the create hint cannot be written without
        //it, so waiting for them in sequence would delay the panel for nothing.
        Task<IReadOnlyList<GitTag>> listing = _tags.ListAsync(_repository, ClosingToken);
        Task<string?> remote = _tags.ResolveRemoteAsync(_repository, ClosingToken);

        IReadOnlyList<GitTag> tags = await listing.ConfigureAwait(true);
        _remote = await remote.ConfigureAwait(true);

        _all.Clear();
        _all.AddRange(tags);

        ApplyFilter();
        UpdateNewHint();
    }

    /// <summary>
    /// With nothing typed, Git's own ordering: <c>--sort=-v:refname</c> puts 1.10 above 1.9, and the
    /// matcher would fall back to alphabetical and undo exactly that. Once there is a pattern the
    /// best match wins, which is what somebody who typed a version wants.
    /// </summary>
    private void ApplyFilter()
    {
        string pattern = (_filter.Text ?? string.Empty).Trim();

        List<GitTag> matches = pattern.Length == 0
            ? [.. _all]
            : [.. FuzzyMatcher
                .Rank(_all.Select(tag => tag.Name), pattern)
                .Select(match => _all.First(tag => tag.Name == match.Value))];

        _list.ItemsSource = matches;
        _list.SelectedIndex = matches.Count > 0 ? 0 : -1;

        _status.Text = _all.Count == 0
            ? Strings.Get("tag.none")
            //Only reachable with something typed, so the pattern is always there to name — and naming
            //it is what points at the create panel instead of leaving the user to retype.
            : matches.Count == 0 ? Strings.Get("tag.nomatch", pattern)
            : Strings.Get("tag.count", _all.Count);
    }

    /// <summary>
    /// Live feedback on the name being typed, in the spirit of the commit window's branch box: the
    /// consequence is visible before Enter rather than reported after it. That consequence includes
    /// the push, which is why the hint names the remote.
    /// </summary>
    private void UpdateNewHint()
    {
        string typed = (_name.Text ?? string.Empty).Trim();

        if (typed.Length == 0)
        {
            _hint.Text = string.Empty;
            _create.IsEnabled = false;

            return;
        }

        if (!TagService.LooksValid(typed))
        {
            _hint.Text = Strings.Get("tag.invalid");
            _create.IsEnabled = false;

            return;
        }

        //An existing name is refused here rather than by Git, because the only way past it is
        //--force and there is deliberately no button for that.
        if (_all.Any(tag => string.Equals(tag.Name, typed, StringComparison.Ordinal)))
        {
            _hint.Text = Strings.Get("tag.exists", typed);
            _create.IsEnabled = false;

            return;
        }

        bool annotated = (_note.Text ?? string.Empty).Trim().Length > 0;

        _hint.Text = _remote is { } remote
            ? Strings.Get(annotated ? "tag.willannotate" : "tag.willcreate", typed, remote)
            : Strings.Get(annotated ? "tag.willannotate.local" : "tag.willcreate.local", typed);

        _create.IsEnabled = !IsBusy;
    }

    /// <summary>
    /// Enter in the name or the message box creates.
    ///
    /// Gated on the button rather than re-checking the name: <see cref="UpdateNewHint"/> already
    /// decided whether what is typed is a creatable tag, and a second answer to that here is a second
    /// place for the two to disagree.
    /// </summary>
    private void OnNewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || !_create.IsEnabled)
            return;

        e.Handled = true;
        _ = CreateAsync();
    }

    /// <summary>
    /// Creates the tag and publishes it, in that order, with no question in between.
    ///
    /// <b>The push is not a second decision.</b> A tag that exists only on this machine is a version
    /// number nobody else can resolve, and "and push it?" has the same answer every time it is asked
    /// — so it is not asked. Nothing is forced: a name the remote already carries is refused by Git
    /// and reported in Git's own words.
    /// </summary>
    private Task CreateAsync()
    {
        string name = (_name.Text ?? string.Empty).Trim();

        if (name.Length == 0)
            return Task.CompletedTask;

        //Captured before the reload below, which resolves it again: the status line has to name the
        //remote the push actually went to.
        string? remote = _remote;

        return RunBusyAsync(async () =>
        {
            //Null commit: the tag lands on HEAD. The log window deliberately offers no action on a
            //commit, so there is still nothing to pick a commit *from*, and that is a decision rather
            //than a missing feature.
            TagOutcome created = await _tags
                .CreateAsync(_repository, name, _note.Text, commit: null, CancellationToken.None)
                .ConfigureAwait(true);

            if (!created.Succeeded)
            {
                Report(Strings.Get("tag.create"), created.GitError);

                return;
            }

            _name.Text = string.Empty;
            _note.Text = string.Empty;

            TagOutcome published = remote is null
                ? TagOutcome.Ok
                : await _tags.PushAsync(_repository, name, remote, CancellationToken.None).ConfigureAwait(true);

            await LoadAsync().ConfigureAwait(true);

            //Said in the footer rather than as a notification: the new row is on screen a line above,
            //so the confirmation is really just a label for what the user can already see.
            _status.Text = remote is not null && published.Succeeded
                ? Strings.Get("tag.created.pushed", name, remote)
                : Strings.Get("tag.created", name);

            //A failed push is its own report, because the two halves ended differently: the tag is
            //here and it is not there, which is the one outcome the footer line cannot say on its own.
            if (!published.Succeeded)
            {
                MessageWindow.Notice(
                    Strings.Get("tag.push"),
                    Strings.Get("tag.push.failed", name, remote!),
                    published.GitError);
            }
        });
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!PickerList.SelectRowUnderPointer(_list, e.Source) || Selected is not { } tag)
        {
            e.Handled = true;

            return;
        }

        var items = new List<Control>
        {
            PickerList.Item(
                Strings.Get("tag.menu.checkout", tag.Name),
                () => CheckOutAsync(tag.Name)),

            new Separator(),

            //The remote is named in the label rather than discovered on click, so the item says where
            //the deletion would land before it is pressed.
            PickerList.Item(
                _remote is { } remote
                    ? Strings.Get("tag.menu.delete.remote", remote)
                    : Strings.Get("tag.menu.delete"),
                () => ConfirmAndDeleteAsync(tag.Name)),
        };

        _rowMenu.ItemsSource = items;
    }

    private Task CheckOutSelectedAsync() =>
        Selected is { } tag ? CheckOutAsync(tag.Name) : Task.CompletedTask;

    /// <summary>
    /// Detaches HEAD at the tag, after one question.
    ///
    /// The window stays open on success. The branch picker closes on a successful switch; this one
    /// cannot, because the sentence naming the state HEAD is now in is the whole reason the question
    /// above was worth asking. Nothing is reloaded: checking a tag out changes no tag.
    /// </summary>
    private async Task CheckOutAsync(string name)
    {
        if (!await MessageWindow.AskAsync(
                Strings.Get("tag.checkout.title"),
                Strings.Get("tag.checkout.confirm", name),
                Strings.Get("tag.checkout.yes"),
                Strings.Get("common.cancel"),
                destructive: false).ConfigureAwait(true))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            SwitchOutcome outcome = await _switches
                .DetachAsync(_repository, name, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                _status.Text = Strings.Get("tag.checkout.done", name);

                return;
            }

            if (outcome.RefusedByLocalChanges)
            {
                //Refused, with the working tree byte-identical. No stash offer here: that sequence is
                //the Branches window's, it cannot switch to a tag, and the accurate answer at this
                //window's size is the file list and the fact that nothing happened.
                MessageWindow.Notice(
                    Strings.Get("tag.checkout.yes"),
                    Strings.Get("tag.checkout.blocked", name),
                    string.Join(Environment.NewLine, outcome.BlockingFiles));

                return;
            }

            //A failure the file list cannot explain. Git's own words, unparaphrased.
            MessageWindow.GitFailure(
                Strings.Get("tag.checkout.yes"),
                Strings.Get("tag.checkout.failed"),
                outcome.GitError,
                _repository.Root);
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Two different questions, because they are two different acts. A local tag is a line in
    /// <c>.git</c>; a published one is something other people have already fetched, and a tag has no
    /// reflog either way.
    /// </summary>
    private async Task ConfirmAndDeleteAsync(string name)
    {
        string? remote = _remote;

        if (!await MessageWindow.AskAsync(
                Strings.Get("tag.confirm.title"),
                remote is null
                    ? Strings.Get("tag.confirm.local", name)
                    : Strings.Get("tag.confirm.remote", name, remote),
                Strings.Get("tag.confirm.yes"),
                Strings.Get("common.cancel"),
                destructive: true).ConfigureAwait(true))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            //Passed on, so Core deletes on the remote first: a tag deleted locally and left on the
            //remote comes back on the next fetch, which reads as the delete having silently failed.
            TagOutcome outcome = await _tags
                .DeleteAsync(_repository, name, remote, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                Report(Strings.Get("tag.delete"), outcome.GitError, Strings.Get("tag.delete.failed"));

                return;
            }

            await LoadAsync().ConfigureAwait(true);

            _status.Text = remote is null
                ? Strings.Get("tag.deleted", name)
                : Strings.Get("tag.deleted.remote", name, remote);
        }).ConfigureAwait(true);
    }

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

    private static FuncDataTemplate<GitTag> RowTemplate() =>
        new((tag, _) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(6, 3),
            Children =
            {
                Column(
                    new TextBlock
                    {
                        Text = tag.Name,
                        FontFamily = new FontFamily("monospace"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    0),

                //An annotated tag shows its own message; a lightweight one says which kind it is,
                //because the difference is the reason one of them has nothing to show.
                Column(
                    new TextBlock
                    {
                        Text = tag.IsAnnotated ? tag.Subject : Strings.Get("tag.lightweight"),
                        Opacity = tag.IsAnnotated ? 0.6 : 0.45,
                        Margin = new Thickness(10, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    1),

                Column(new TextBlock { Text = tag.Date, Opacity = 0.5 }, 2),
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
