using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FlickGit.App.Ai;
using FlickGit.App.Settings;
using FlickGit.Blame;
using FlickGit.Diagnostics;
using FlickGit.App.Localization;
using FlickGit.Diff;
using FlickGit.Git;
using FlickGit.History;
using FlickGit.Logging;
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
    private readonly AiTextService _ai;
    private readonly BlameService _blame;
    private readonly FlickSettings _settings;
    private readonly OperationTimings _timings;
    private readonly ILog _log;
    private readonly RepositoryInfo _repository;

    private readonly ListBox _commits = new() { SelectionMode = SelectionMode.Multiple };
    private readonly ListBox _files = new();
    private readonly DiffPane _pane = new();
    private readonly TextBlock _range = new() { Margin = new Thickness(10, 6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _footer = new()
    {
        Margin = new Thickness(10, 6),
        Classes = { "muted" },
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly TextBlock _subject = new()
    {
        FontWeight = FontWeight.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly TextBlock _meta = new()
    {
        Classes = { "muted", "small" },
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, 2, 0, 0),
    };

    private readonly TextBlock _paging = new()
    {
        Classes = { "muted", "small" },
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly Button _more = new() { MinWidth = 130, Classes = { "strip" }, IsVisible = false };

    private readonly Button _patch = new() { MinWidth = 130, Classes = { "strip" }, IsEnabled = false };
    private readonly Button _changelog = new() { MinWidth = 150, Classes = { "strip" }, IsEnabled = false };
    private readonly Button _close = new() { MinWidth = 90, Classes = { "strip" } };

    /// <summary>
    /// Every commit read so far, oldest page last.
    ///
    /// A list that grows rather than an ItemsSource that is reassigned: a reassignment drops the
    /// selection, the scroll position and every virtualised row, which for "load the next two
    /// hundred" is the whole thing the user was in the middle of.
    /// </summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<LogCommit> _page = [];

    private CommitRange? _current;
    private IReadOnlyList<GitFileChange> _changed = [];

    /// <summary>Set once Git says there is nothing after the page just read.</summary>
    private bool _endOfHistory;

    /// <summary>Guards against a second page being asked for while the first is still arriving.</summary>
    private bool _loading;

    public LogWindow(
        RepositoryInfo repository,
        HistoryService history,
        DiffService diffs,
        AiTextService ai,
        BlameService blame,
        FlickSettings settings,
        OperationTimings timings,
        ILog log)
    {
        _repository = repository;
        _history = history;
        _diffs = diffs;
        _ai = ai;
        _blame = blame;
        _settings = settings;
        _timings = timings;
        _log = log;

        Title = Strings.Get("log.title", repository.Name);
        Width = 1100;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _commits.SelectionChanged += (_, _) => _ = OnSelectionChangedAsync();
        _files.SelectionChanged += (_, _) => _ = ShowFileAsync();

        _commits.ItemTemplate = CommitRowTemplate();
        _files.ItemTemplate = FileRowTemplate();

        var blameItem = new MenuItem { Header = Strings.Get("log.blame") };

        blameItem.Click += (_, _) => _ = BlameFileAsync();

        _files.ContextMenu = new ContextMenu { ItemsSource = new[] { blameItem } };

        _more.Content = Strings.Get("log.loadmore", HistoryService.PageSize);
        _more.Click += (_, _) => _ = LoadPageAsync();

        _patch.Content = Strings.Get("log.patch");
        _changelog.Content = Strings.Get("log.changelog");
        _close.Content = Strings.Get("common.close");

        _patch.Click += (_, _) => _ = SavePatchAsync();
        _changelog.Click += (_, _) => _ = OpenChangelogAsync();
        _close.Click += (_, _) => Close();

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("2*,Auto,3*,Auto"),
            Children =
            {
                Row(_commits, 0),
                Row(RangeBand(), 1),
                Row(Lower(), 2),
                Row(Footer(), 3),
            },
        };

        Opened += (_, _) => _ = LoadAsync();
    }

    /// <summary>
    /// The band between the commits and their diff: what the selection covers, how much history is
    /// loaded, and the way to read more of it.
    ///
    /// One band rather than two, because all three sentences are about the list above it.
    /// </summary>
    private Control RangeBand()
    {
        _range.Margin = new Thickness(0);

        //The subject and its metadata above the range, because they answer different questions: one
        //is "what is this commit", the other is "what am I about to see the diff of".
        return new Border
        {
            Background = Resource("SurfaceAlt"),
            BorderBrush = Resource("Border"),
            BorderThickness = new Thickness(0, 1),
            Padding = new Thickness(10, 6),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    _subject,
                    _meta,
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                        Children =
                        {
                            Column(_range, 0),
                            Column(Spaced(_paging, 12), 1),
                            Column(Spaced(_more, 12), 2),
                        },
                    },
                },
            },
        };
    }

    private static T Spaced<T>(T control, double left)
        where T : Control
    {
        control.Margin = new Thickness(left, 0, 0, 0);

        return control;
    }

    /// <summary>
    /// The two outward actions, and the way out.
    ///
    /// Both write outside the repository and neither runs a Git command that changes anything, which
    /// is what lets this window keep "it performs nothing" while offering them.
    /// </summary>
    private Control Footer() =>
        new Border
        {
            Background = Resource("SurfaceAlt"),
            BorderBrush = Resource("Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(4),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    Column(_footer, 0),
                    Column(
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Margin = new Thickness(0, 0, 6, 0),
                            Children = { _changelog, _patch, _close },
                        },
                        1),
                },
            },
        };

    /// <summary>
    /// Writes the combined diff to a file, through Git rather than through a string.
    ///
    /// <c>git diff --binary --output=&lt;file&gt;</c> is what <see cref="HistoryService"/> runs, and the
    /// --output is the point: a patch that became a C# string could gain a BOM on the way back out,
    /// and `git apply` refuses one.
    /// </summary>
    private async Task SavePatchAsync()
    {
        if (_current is not { } range)
            return;

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = PatchFileName(range),
            DefaultExtension = "patch",
            ShowOverwritePrompt = true,

            //The repository's parent, not the repository: a .patch dropped inside the working tree
            //comes straight back as an untracked file in the commit window.
            SuggestedStartLocation = await StorageProvider
                .TryGetFolderFromPathAsync(ParentOfRepository())
                .ConfigureAwait(true),
        }).ConfigureAwait(true);

        if (file?.Path.LocalPath is not { Length: > 0 } path)
            return;

        try
        {
            GitResult result = await _history
                .SavePatchAsync(_repository, range, path, CancellationToken.None)
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                MessageWindow.Notice(
                    Strings.Get("log.patch.failed"),
                    result.ErrorText + Environment.NewLine + Environment.NewLine + path);

                return;
            }

            //A range whose endpoints hold the same tree writes nothing. Said in the footer rather
            //than leaving a zero-byte file the user would later find and not understand.
            _footer.Text = new FileInfo(path) is { Exists: true, Length: > 0 } written
                ? Strings.Get("log.patch.saved", Path.GetFileName(written.FullName))
                : Strings.Get("log.patch.empty");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageWindow.Notice(
                Strings.Get("log.patch.failed"),
                ex.Message + Environment.NewLine + Environment.NewLine + path);
        }
    }

    /// <summary>
    /// Writes a changelog over the selected commits -- the same range the diff and the patch are of,
    /// which is why it is handed <see cref="CommitRange"/> rather than the selection.
    ///
    /// Its own window rather than a dialog here: the answer arrives a token at a time, it is worth
    /// editing before it is used, and it has two destinations of its own.
    /// </summary>
    private async Task OpenChangelogAsync()
    {
        if (_current is not { } range)
            return;

        var window = new ChangelogWindow(_repository, range, _changed, _ai, _log);

        window.Show();

        await window.StartAsync().ConfigureAwait(true);
    }

    private string ParentOfRepository() =>
        Path.GetDirectoryName(_repository.Root.TrimEnd('/', '\\')) ?? _repository.Root;

    /// <summary>
    /// <c>a1b2c3d-add-pgbouncer-pooling.patch</c> for one commit, <c>4d5e6f7..a1b2c3d.patch</c> for a
    /// range: a subject is only useful when there is exactly one.
    /// </summary>
    private static string PatchFileName(CommitRange range)
    {
        if (range.SelectedCount != 1)
            return $"{range.Oldest.ShortSha}..{range.Newest.ShortSha}.patch";

        var slug = new System.Text.StringBuilder();

        foreach (char c in range.Newest.Subject.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
                slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }

        string trimmed = slug.ToString().Trim('-');

        //A subject of nothing but punctuation leaves the hash to name the file on its own, rather
        //than a trailing dash.
        return trimmed.Length == 0
            ? $"{range.Newest.ShortSha}.patch"
            : $"{range.Newest.ShortSha}-{trimmed[..Math.Min(trimmed.Length, 48)].TrimEnd('-')}.patch";
    }

    private static IBrush? Resource(string key) => Application.Current?.FindResource(key) as IBrush;

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
        _commits.ItemsSource = _page;
        _paging.Text = Strings.Get("log.loading");

        await LoadPageAsync().ConfigureAwait(true);

        if (_page.Count > 0)
            _commits.SelectedIndex = 0;
    }

    /// <summary>
    /// Reads the next page and appends it.
    ///
    /// <b>Paging is <c>--skip</c>, per CLAUDE.md, and that is the service's business</b> — the
    /// reason is that a last-sha cursor does not resolve at a root commit and silently follows the
    /// first-parent line at a merge. What this method owns is only the two states the button has.
    /// </summary>
    private async Task LoadPageAsync()
    {
        if (_loading || _endOfHistory)
            return;

        _loading = true;
        _more.IsEnabled = false;

        try
        {
            LogPage page = await _history
                .GetPageAsync(_repository, _page.Count, CancellationToken.None)
                .ConfigureAwait(true);

            foreach (LogCommit commit in page.Commits)
                _page.Add(commit);

            _endOfHistory = !page.HasMore;

            _paging.Text = _page.Count == 0
                ? Strings.Get("log.empty")
                : _endOfHistory
                    ? Strings.Get("log.end")
                    : Strings.Get("log.loaded", _page.Count);
        }
        finally
        {
            _loading = false;

            //Hidden at the end of history rather than disabled: there is nothing left for it to do,
            //and a permanently dead button beside a full list reads as a failure.
            _more.IsVisible = !_endOfHistory;
            _more.IsEnabled = !_endOfHistory;
        }
    }

    private async Task OnSelectionChangedAsync()
    {
        var selected = _commits.SelectedItems?.OfType<LogCommit>().Select(c => c.Sha).ToHashSet()
                       ?? [];

        //Resolved against everything read so far, not against the last page: a selection can span two
        //pages the moment the user presses Load more.
        IReadOnlyList<LogCommit> loaded = [.. _page];

        //Resolved in Core: the list is newest-first, so the newest selected commit is the *lowest*
        //index, and "the range came out the wrong way round" is exactly the bug clicking does not
        //reveal.
        _current = CommitRange.Resolve(loaded, selected);

        if (_current is not { } range)
        {
            _range.Text = string.Empty;
            _subject.Text = Strings.Get("log.select.prompt");
            _meta.Text = Strings.Get("log.hint");
            _files.ItemsSource = null;
            _pane.Show(null, isLoading: false);

            _patch.IsEnabled = false;
            _changelog.IsEnabled = false;

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

        Describe(range);

        _changed = await _history
            .GetFilesAsync(_repository, range.BaseSpec, range.TipSpec, CancellationToken.None)
            .ConfigureAwait(true);

        _files.ItemsSource = _changed;
        _footer.Text = Summary(_changed);

        _patch.IsEnabled = _changed.Count > 0;

        //Not gated on the file count, unlike the patch. A range that changes no file still has commit
        //subjects to describe, and an empty patch is not a legitimate patch.
        _changelog.IsEnabled = true;

        if (_changed.Count > 0)
            _files.SelectedIndex = 0;
        else
            _pane.Show(null, isLoading: false);
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

        _pane.Show(diff, isLoading: false);
    }

    /// <summary>
    /// The selected commit, or what a multi-selection means instead.
    ///
    /// <b>A merge says so rather than naming its parents.</b> The diff is against the first parent
    /// only — <c>CommitRange</c> takes <c>Parents[0]</c> with no special case — so a line reading
    /// "parent a1b2c3d, 9f0e1d2" over a diff that used one of them would be the window explaining
    /// itself wrongly.
    /// </summary>
    private void Describe(CommitRange range)
    {
        if (range.SelectedCount != 1)
        {
            _subject.Text = range.Newest.Subject;
            _meta.Text = Strings.Get("log.hint");

            return;
        }

        LogCommit one = range.Newest;

        _subject.Text = one.Subject;
        _meta.Text = one.IsMerge
            ? Strings.Get("log.commit.merge")
            : Strings.Get(
                "log.commit.meta",
                one.Author,
                one.When.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                string.Join(", ", one.Parents.Select(sha => sha.Length > 7 ? sha[..7] : sha)));
    }

    /// <summary>
    /// Blame on the selected file, <b>at the selected commit</b> rather than at the working tree.
    ///
    /// That is the whole reason it is here and not only on the Finder menu: the file has moved on
    /// since, and reading it at HEAD answers a question about today rather than about the change the
    /// user is looking at.
    /// </summary>
    private async Task BlameFileAsync()
    {
        if (_current is not { } range || _files.SelectedItem is not GitFileChange file)
            return;

        var window = new BlameWindow(
            _repository, file.Path, range.TipSpec, _blame, _settings, _timings, _log);

        window.Show();

        await window.LoadAsync().ConfigureAwait(true);
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
