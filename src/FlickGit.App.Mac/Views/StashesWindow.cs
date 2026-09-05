using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FlickGit.App.Localization;
using FlickGit.Diff;
using FlickGit.Models;
using FlickGit.Stashes;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// What is put away, <b>what is in the one you are pointing at</b>, putting the working tree away,
/// popping one back, and dropping some.
///
/// <b>A stash is a commit, which is what makes its contents readable without popping it</b> — and
/// popping it to find out what is in it is the exact thing this window exists to stop the user
/// doing. So the middle of the window is the log window's lower half: a file list against a
/// read-only diff pane, reached through <see cref="StashService.ListFilesAsync"/>. The untracked
/// half is listed too, against the empty tree, so <c>git stash show</c>'s blind spot is not
/// reproduced. Nothing in that half writes anything.
///
/// <b>A stash is named by a position, and that is the whole safety rule here.</b> <c>stash@{1}</c> is
/// whatever is second at the moment the command runs, and any push or pop renumbers the list — a
/// terminal's, an IDE's, or FlickGit's own stash-switch-restore while this window sits open. So
/// <see cref="GitStash"/> carries the stash commit's sha, and <see cref="StashService"/> re-reads the
/// list and refuses unless the reference still names that commit. This window passes the row the
/// user pointed at and lets Core do the checking; it does not compute a reflog selector itself.
///
/// Pop asks nothing — it restores work rather than discarding any, and Git refuses rather than
/// overwriting. Drop asks, in its own words, because a stash has no reflog and nothing finds it
/// again. Drop takes a multi-selection and asks <b>once, with the totals</b>; Pop stays one row,
/// because popping several is a chain of merges in which the second lands on a tree the first has
/// already changed.
/// </summary>
internal sealed class StashesWindow : ReloadableWindow
{
    private readonly StashService _stashes;
    private readonly DiffService _diffs;
    private readonly RepositoryInfo _repository;

    private readonly ListBox _list = new()
    {
        Margin = new Thickness(10, 10, 10, 0),
        SelectionMode = SelectionMode.Multiple,
    };

    private readonly ContextMenu _rowMenu = new();

    private readonly TextBlock _rangeText = new() { Classes = { "section" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _totalsText = new() { Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Right };

    private readonly ListBox _files = new();
    private readonly DiffPane _pane = new();

    private readonly TextBox _note = new() { PlaceholderText = Strings.Get("stash.message.label") };
    private readonly CheckBox _untracked = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _push = new() { MinWidth = 110, Classes = { "primary" } };

    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(10, 6),
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly Button _close = new() { MinWidth = 90 };

    public StashesWindow(RepositoryInfo repository, StashService stashes, DiffService diffs)
    {
        _repository = repository;
        _stashes = stashes;
        _diffs = diffs;

        Title = Strings.Get("stash.title", repository.Name);
        Width = 1080;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _push.Content = Strings.Get("stash.push");
        _untracked.Content = Strings.Get("stash.untracked");
        _close.Content = Strings.Get("common.close");
        ToolTip.SetTip(_untracked, Strings.Get("stash.untracked.hint"));

        _list.ItemTemplate = StashRowTemplate();
        _list.ContextMenu = _rowMenu;
        _list.ContextRequested += OnContextRequested;
        _list.DoubleTapped += (_, _) => _ = PopSelectedAsync();
        _list.SelectionChanged += (_, _) => _ = OnStashSelectionChangedAsync();

        _files.ItemTemplate = FileRowTemplate();
        _files.SelectionChanged += (_, _) => _ = ShowFileAsync();

        _note.KeyDown += OnMessageKeyDown;
        _push.Click += (_, _) => _ = PushAsync();
        _close.Click += (_, _) => Close();

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("220,Auto,*,Auto,Auto,Auto"),
            Children =
            {
                Row(_list, 0),
                Row(ContentsHeader(), 1),
                Row(Contents(), 2),
                Row(NewStashPanel(), 3),
                Row(_status, 4),
                Row(Footer(), 5),
            },
        };

        Opened += (_, _) => _ = LoadAsync();
    }

    private IReadOnlyList<GitStash> Selected => _list.SelectedItems?.OfType<GitStash>().ToArray() ?? [];

    private Control ContentsHeader() =>
        new Border
        {
            Background = Application.Current?.FindResource("SurfaceAlt") as IBrush,
            BorderBrush = Application.Current?.FindResource("Border") as IBrush,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 10, 0, 0),
            Child = new Grid
            {
                Children = { _rangeText, _totalsText },
            },
        };

    private Control Contents() =>
        new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("320,4,*"),
            Children =
            {
                Column(_files, 0),
                Column(new GridSplitter { ResizeDirection = GridResizeDirection.Columns }, 1),
                Column(_pane, 2),
            },
        };

    /// <summary>
    /// Putting the working tree away. Below the contents, so the window reads top to bottom: what is
    /// stashed, what is in it, and then how to add one.
    /// </summary>
    private Control NewStashPanel() =>
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
                    new TextBlock { Text = Strings.Get("stash.new"), Classes = { "section" } },

                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                        Children =
                        {
                            Column(_note, 0),
                            Column(WithMargin(_untracked, new Thickness(12, 0)), 1),
                            Column(_push, 2),
                        },
                    },
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
        _note.IsEnabled = !busy;
        _untracked.IsEnabled = !busy;
        _push.IsEnabled = !busy;
    }

    protected override async Task ReadStateAsync()
    {
        IReadOnlyList<GitStash> stashes = await _stashes
            .ListAsync(_repository, ClosingToken)
            .ConfigureAwait(true);

        _list.ItemsSource = stashes;

        _status.Text = stashes.Count == 0
            ? Strings.Get("stash.none")
            : Strings.Get("stash.count", stashes.Count);

        //Reloading is what makes a popped or dropped row disappear, and the contents underneath were
        //about a row that may no longer exist. Cleared rather than left showing a stale stash.
        ClearContents();

        if (stashes.Count > 0)
            _list.SelectedIndex = 0;
    }

    // ---- reading a stash ----------------------------------------------------

    private async Task OnStashSelectionChangedAsync()
    {
        IReadOnlyList<GitStash> selection = Selected;

        //Pop is one row for the reason in the class remarks. Said in the footer rather than by
        //picking a row for the user.
        if (selection.Count > 1)
        {
            _status.Text = Strings.Get("stash.pop.one");
            ClearContents();

            return;
        }

        if (selection is not [GitStash stash])
        {
            ClearContents();

            return;
        }

        IReadOnlyList<StashChange> changes = await _stashes
            .ListFilesAsync(_repository, stash, ClosingToken)
            .ConfigureAwait(true);

        //The stash's own label rather than the first row's: with untracked files there are two ranges
        //in this list, and the header has to name the stash, not whichever half happens to be first.
        _rangeText.Text = stash.TrackedRange?.Label ?? stash.Reference;

        _files.ItemsSource = changes;

        int added = changes.Sum(change => change.File.AddedLines ?? 0);
        int removed = changes.Sum(change => change.File.RemovedLines ?? 0);

        _totalsText.Text = changes.Count == 0
            ? Strings.Get("stash.files.none")
            : Strings.Get("stash.totals", changes.Count, added, removed);

        //The first row selected outright, so a stash's contents are one click rather than two. There
        //is nothing to keep across this: a different stash is a different set of files.
        if (changes.Count > 0)
            _files.SelectedIndex = 0;
        else
            _pane.Show(null, isLoading: false);
    }

    private async Task ShowFileAsync()
    {
        if (_files.SelectedItem is not StashChange change)
            return;

        //The range travels with the diff rather than beside it: a non-null DiffRange is what makes
        //the pane read-only and supplies its header, so a stash's diff cannot be rendered under a
        //"Working tree ↔ HEAD" label. It comes off the row rather than off the stash because the
        //untracked half has a range of its own — the empty tree.
        SideBySideDiff diff = await _diffs
            .ComputeRangeAsync(_repository, change.File, change.Range, ClosingToken)
            .ConfigureAwait(true);

        _pane.Show(diff, isLoading: false);
    }

    private void ClearContents()
    {
        _rangeText.Text = string.Empty;
        _totalsText.Text = string.Empty;
        _files.ItemsSource = null;
        _pane.Show(null, isLoading: false);
    }

    // ---- acting on a stash --------------------------------------------------

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!PickerList.SelectRowUnderPointer(_list, e.Source))
        {
            e.Handled = true;

            return;
        }

        IReadOnlyList<GitStash> selection = Selected;

        if (selection.Count == 0)
        {
            e.Handled = true;

            return;
        }

        //Pop is absent rather than disabled on a multi-selection: a disabled item invites a second
        //click at the same place, and the footer already says why.
        if (selection.Count > 1)
        {
            _rowMenu.ItemsSource = new List<Control>
            {
                PickerList.Item(
                    Strings.Get("stash.menu.drop.many", selection.Count),
                    () => ConfirmAndDropAsync(selection)),
            };

            return;
        }

        GitStash stash = selection[0];

        _rowMenu.ItemsSource = new List<Control>
        {
            PickerList.Item(Strings.Get("stash.menu.pop", stash.Reference), () => PopAsync(stash)),
            PickerList.Item(Strings.Get("stash.menu.drop", stash.Reference), () => ConfirmAndDropAsync(selection)),
        };
    }

    private Task PopSelectedAsync()
    {
        IReadOnlyList<GitStash> selection = Selected;

        //A double-click inside a multi-selection says so rather than picking a row itself.
        if (selection.Count > 1)
        {
            _status.Text = Strings.Get("stash.pop.one");

            return Task.CompletedTask;
        }

        return selection is [GitStash stash] ? PopAsync(stash) : Task.CompletedTask;
    }

    /// <summary>
    /// Nothing asked: this restores work rather than discarding any, and a failed pop always leaves
    /// the stash in place because Git applies and only then drops.
    /// </summary>
    private Task PopAsync(GitStash stash) =>
        RunBusyAsync(async () =>
        {
            StashOutcome outcome = await _stashes
                .PopAsync(_repository, stash, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Refusal == StashRefusal.Moved)
            {
                await ReportMovedAsync().ConfigureAwait(true);

                return;
            }

            if (!outcome.Succeeded)
            {
                //Git's own message below our sentence: it either refused and changed nothing, or
                //applied what it could and left conflicts. Either way the stash is still in the list.
                MessageWindow.Notice(
                    Strings.Get("stash.pop"),
                    Strings.Get("stash.pop.kept", stash.Reference),
                    outcome.GitError);

                return;
            }

            await LoadAsync().ConfigureAwait(true);
            _status.Text = Strings.Get("stash.popped", stash.Reference);
        });

    /// <summary>
    /// One question with the totals, never one per item. A stash has no reflog, so this is the one
    /// thing in this window that cannot be undone.
    /// </summary>
    private async Task ConfirmAndDropAsync(IReadOnlyList<GitStash> stashes)
    {
        if (stashes.Count == 0)
            return;

        (string title, string body) = stashes is [{ } one]
            ? (Strings.Get("stash.confirm.title"),
               Strings.Get("stash.confirm.drop", one.Reference, one.Message))
            : (Strings.Get("stash.confirm.title.many"),
               Strings.Get("stash.confirm.drop.many", stashes.Count, Describe(stashes)));

        if (!await MessageWindow.AskAsync(
                title,
                body,
                Strings.Get("stash.confirm.yes"),
                Strings.Get("common.cancel"),
                destructive: true).ConfigureAwait(true))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            //Core drops highest reflog index first and re-verifies each row as its turn comes, because
            //dropping stash@{k} renumbers everything above it.
            StashDropOutcome outcome = await _stashes
                .DropAsync(_repository, stashes, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Outcome.Refusal == StashRefusal.Moved)
            {
                await ReportMovedAsync().ConfigureAwait(true);

                return;
            }

            if (!outcome.Outcome.Succeeded)
            {
                //How many went is the part a partial failure has to report: the rest are still there.
                MessageWindow.Notice(
                    Strings.Get("stash.drop"),
                    stashes.Count == 1
                        ? Strings.Get("stash.drop.failed")
                        : Strings.Get("stash.drop.failed.many", outcome.Dropped, stashes.Count),
                    outcome.Outcome.GitError);

                await LoadAsync().ConfigureAwait(true);

                return;
            }

            await LoadAsync().ConfigureAwait(true);

            _status.Text = stashes.Count == 1
                ? Strings.Get("stash.dropped", stashes[0].Reference)
                : Strings.Get("stash.dropped.many", outcome.Dropped);
        }).ConfigureAwait(true);
    }

    /// <summary>Enter in the message box stashes, which is the whole of that box's keyboard.</summary>
    private void OnMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return))
            return;

        e.Handled = true;
        _ = PushAsync();
    }

    private Task PushAsync() =>
        RunBusyAsync(async () =>
        {
            StashOutcome outcome = await _stashes
                .PushAsync(_repository, _note.Text, _untracked.IsChecked == true, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Refusal == StashRefusal.NothingToStash)
            {
                //Not a failure, and not something to interrupt for: the working tree is exactly as it
                //was, which is what the user wanted anyway.
                _status.Text = Strings.Get("stash.nothing");

                return;
            }

            if (!outcome.Succeeded)
            {
                MessageWindow.Notice(
                    Strings.Get("stash.push"),
                    Strings.Get("stash.push.failed"),
                    outcome.GitError);

                return;
            }

            _note.Text = string.Empty;

            await LoadAsync().ConfigureAwait(true);
            _status.Text = Strings.Get("stash.pushed");
        });

    /// <summary>
    /// The list moved under the user, and nothing was asked of Git.
    ///
    /// Reloaded first, so the message arrives beside a list that already agrees with the repository.
    /// A notice rather than a footer line, because this is the one outcome where the row that was
    /// clicked was not the row it appeared to be — which is worth interrupting for.
    /// </summary>
    private async Task ReportMovedAsync()
    {
        await LoadAsync().ConfigureAwait(true);

        MessageWindow.Notice(Strings.Get("stash.moved.title"), Strings.Get("stash.moved"));
    }

    private static string Describe(IReadOnlyList<GitStash> stashes) =>
        string.Join(Environment.NewLine, stashes.Select(stash => $"{stash.Reference} — {stash.Message}"));

    private static T WithMargin<T>(T control, Thickness margin)
        where T : Control
    {
        control.Margin = margin;

        return control;
    }

    private static FuncDataTemplate<GitStash> StashRowTemplate() =>
        new((stash, _) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(6, 3),
            Children =
            {
                Column(
                    new TextBlock
                    {
                        Text = stash.Reference,
                        FontFamily = new FontFamily("monospace"),
                        Margin = new Thickness(0, 0, 10, 0),
                    },
                    0),

                Column(
                    new TextBlock { Text = stash.Message, TextTrimming = TextTrimming.CharacterEllipsis },
                    1),

                Column(
                    new TextBlock { Text = stash.Branch, Opacity = 0.6, Margin = new Thickness(10, 0, 0, 0) },
                    2),
            },
        });

    private static FuncDataTemplate<StashChange> FileRowTemplate() =>
        new((change, _) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(6, 2),
            Children =
            {
                Column(
                    new TextBlock
                    {
                        Text = change.File.DisplayStatus.ToShortCode(),
                        FontFamily = new FontFamily("monospace"),
                        Width = 26,
                    },
                    0),

                Column(
                    new TextBlock { Text = change.File.Path, TextTrimming = TextTrimming.CharacterEllipsis },
                    1),

                Column(
                    new TextBlock
                    {
                        Text = Count(change.File.AddedLines),
                        Foreground = Brushes.Green,
                        Margin = new Thickness(8, 0, 0, 0),
                    },
                    2),

                Column(
                    new TextBlock
                    {
                        Text = Count(change.File.RemovedLines),
                        Foreground = Brushes.IndianRed,
                        Margin = new Thickness(6, 0, 0, 0),
                    },
                    3),
            },
        });

    /// <summary>A binary file reports no counts at all, which is not the same as reporting zero.</summary>
    private static string Count(int? value) =>
        value is null ? Strings.Get("commit.summary.binary") : value.Value.ToString();

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
