using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using FlickGit.App.Localization;
using FlickGit.Diff;
using FlickGit.History;
using FlickGit.Models;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// Commit history, and the combined diff over a selection.
///
/// <b>The gap disclosure is the reason this window is careful rather than simple.</b> A gapped
/// selection — 1, 2 and 5 — diffs <c>1^..5</c>, so the commits in between are in the diff whether or
/// not they were picked. <see cref="CommitRange"/> computes that in FlickGit.Core, where it is
/// tested, and this window states it. A combined diff that quietly swept in commits the user did not
/// select is the one failure this window must not have.
///
/// <b>Nothing here writes to the repository.</b> <see cref="HistoryService"/> reaches Git only
/// through reads, and there is no checkout, reset, revert, cherry-pick, rebase, amend, tag-at-commit
/// or branch-from-here — by omission here and by construction there.
///
/// Built in code rather than XAML: it is a splitter, two lists and a diff pane, and the pane is a
/// control rather than markup anyway.
/// </summary>
internal sealed class LogWindow : Window
{
    private readonly HistoryService _history;
    private readonly DiffService _diffs;
    private readonly RepositoryInfo _repository;

    private readonly ListBox _commits = new() { SelectionMode = SelectionMode.Multiple };
    private readonly ListBox _files = new();
    private readonly DiffPane _pane = new();
    private readonly TextBlock _range = new() { Margin = new Thickness(10, 6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _footer = new() { Margin = new Thickness(10, 6), Opacity = 0.75 };

    private IReadOnlyList<LogCommit> _page = [];
    private CommitRange? _current;
    private IReadOnlyList<GitFileChange> _changed = [];

    public LogWindow(RepositoryInfo repository, HistoryService history, DiffService diffs)
    {
        _repository = repository;
        _history = history;
        _diffs = diffs;

        Title = Strings.Get("log.title", repository.Name);
        Width = 1100;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _commits.SelectionChanged += (_, _) => _ = OnSelectionChangedAsync();
        _files.SelectionChanged += (_, _) => _ = ShowFileAsync();

        _commits.ItemTemplate = CommitRowTemplate();
        _files.ItemTemplate = FileRowTemplate();

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("2*,Auto,3*,Auto"),
            Children =
            {
                Row(_commits, 0),
                Row(new Border { Background = Brushes.Transparent, Child = _range }, 1),
                Row(Lower(), 2),
                Row(_footer, 3),
            },
        };

        Opened += (_, _) => _ = LoadAsync();
    }

    private Control Lower() =>
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

    private async Task LoadAsync()
    {
        LogPage page = await _history.GetPageAsync(_repository, skip: 0, CancellationToken.None)
            .ConfigureAwait(true);

        _page = page.Commits;
        _commits.ItemsSource = _page;

        if (_page.Count > 0)
            _commits.SelectedIndex = 0;
    }

    private async Task OnSelectionChangedAsync()
    {
        var selected = _commits.SelectedItems?.OfType<LogCommit>().Select(c => c.Sha).ToHashSet()
                       ?? [];

        //Resolved in Core: the list is newest-first, so the newest selected commit is the *lowest*
        //index, and "the range came out the wrong way round" is exactly the bug clicking does not
        //reveal.
        _current = CommitRange.Resolve(_page, selected);

        if (_current is not { } range)
        {
            _range.Text = string.Empty;
            _files.ItemsSource = null;
            _pane.Show(null);

            return;
        }

        //The same three spellings the WPF window uses, with the gap sentence appended rather than
        //interpolated. ImplicitCount is computed in CommitRange, where it is tested, so the count and
        //the sentence cannot drift apart.
        string header = range.SelectedCount == 1
            ? Strings.Get("log.range.one", range.Newest.ShortSha)
            : range.Oldest.IsRoot
                ? Strings.Get("log.range.root", range.SelectedCount, range.Newest.ShortSha)
                : Strings.Get("log.range.many", range.SelectedCount, range.Oldest.ShortSha, range.Newest.ShortSha);

        _range.Text = range.ImplicitCount > 0
            ? header + "   ·   " + Strings.Get("log.range.gap", range.ImplicitCount)
            : header;

        _changed = await _history
            .GetFilesAsync(_repository, range.BaseSpec, range.TipSpec, CancellationToken.None)
            .ConfigureAwait(true);

        _files.ItemsSource = _changed;
        _footer.Text = Summary(_changed);

        if (_changed.Count > 0)
            _files.SelectedIndex = 0;
        else
            _pane.Show(null);
    }

    private async Task ShowFileAsync()
    {
        if (_files.SelectedItem is not GitFileChange file || _current is not { } range)
            return;

        //The range travels with the diff rather than beside it: a non-null DiffRange is what makes
        //the pane read-only and supplies its header, so a historical diff cannot be rendered under a
        //"Working tree ↔ HEAD" label.
        SideBySideDiff diff = await _diffs
            .ComputeRangeAsync(_repository, file, range.Diff, CancellationToken.None)
            .ConfigureAwait(true);

        _pane.Show(diff);
    }

    private static string Summary(IReadOnlyList<GitFileChange> files)
    {
        int added = files.Sum(f => f.AddedLines ?? 0);
        int removed = files.Sum(f => f.RemovedLines ?? 0);

        return Strings.Get("log.loaded", files.Count) + "   ·   +" + added + " −" + removed;
    }

    private static FuncDataTemplate<LogCommit> CommitRowTemplate() =>
        new((commit, _) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(6, 2),
            Children =
            {
                Column(new TextBlock { Text = commit.ShortSha, FontFamily = new FontFamily("monospace"), Margin = new Thickness(0, 0, 10, 0) }, 0),
                Column(new TextBlock { Text = commit.Subject, TextTrimming = TextTrimming.CharacterEllipsis }, 1),
                Column(new TextBlock { Text = commit.Refs, Opacity = 0.6, Margin = new Thickness(10, 0) }, 2),
                Column(new TextBlock { Text = commit.When.ToString("yyyy-MM-dd HH:mm"), Opacity = 0.6 }, 3),
            },
        });

    private static FuncDataTemplate<GitFileChange> FileRowTemplate() =>
        new((file, _) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(6, 2),
            Children =
            {
                Column(new TextBlock { Text = file.DisplayStatus.ToShortCode(), FontFamily = new FontFamily("monospace"), Width = 26 }, 0),
                Column(new TextBlock { Text = file.Path, TextTrimming = TextTrimming.CharacterEllipsis }, 1),
                Column(new TextBlock { Text = Count(file.AddedLines), Foreground = Brushes.Green, Margin = new Thickness(8, 0, 0, 0) }, 2),
                Column(new TextBlock { Text = Count(file.RemovedLines), Foreground = Brushes.IndianRed, Margin = new Thickness(6, 0, 0, 0) }, 3),
            },
        });

    /// <summary>A binary file reports no counts at all, which is not the same as reporting zero.</summary>
    private static string Count(int? value) => value is null ? "bin" : value.Value.ToString();

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
