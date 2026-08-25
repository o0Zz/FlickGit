using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.Blame;
using FlickGit.Diagnostics;
using FlickGit.Diff;
using FlickGit.Git;
using FlickGit.History;
using FlickGit.Logging;
using FlickGit.Models;
using Microsoft.Win32;

namespace FlickGit.App.Views;

/// <summary>
/// The log window: commit history, and the diff of whatever is selected.
///
/// <b>The feature it exists for is the multi-selection.</b> Picking several commits shows their
/// <i>combined</i> diff -- <c>git diff &lt;oldest&gt;^ &lt;newest&gt;</c> -- which is what answers
/// "what changed between Tuesday and now" and what no per-commit view can answer.
///
/// <b>It performs nothing.</b> No checkout, reset, revert, cherry-pick, tag or branch-from-here.
/// That list is a boundary, and it is written down so it does not grow one release at a time. The
/// single outward action is Save as patch, which writes outside the repository.
///
/// Code-behind rather than a view model: <c>ListBox.SelectedItems</c> is not bindable, so a view
/// model would need an attached behaviour invented so a second class could avoid touching the
/// first.
/// </summary>
public partial class LogWindow : Window
{
    /// <summary>
    /// How long the selection has to settle before Git is asked anything. A key-repeated Shift+Down
    /// fires roughly every 30 ms, so this collapses a whole drag into one reload rather than starting
    /// a process per row.
    /// </summary>
    private static readonly TimeSpan SelectionSettleDelay = TimeSpan.FromMilliseconds(120);

    private const int PrefetchCount = 5;

    private readonly RepositoryInfo _repository;
    private readonly HistoryService _history;
    private readonly DiffService _diffs;
    private readonly BlameService _blame;
    private readonly FlickSettings _settings;
    private readonly OperationTimings _timings;
    private readonly ILog _log;

    private readonly ObservableCollection<CommitRow> _commits = [];

    /// <summary>
    /// Computed diffs, keyed by range and path. Not <c>DiffCache</c>, which is a set of working-tree
    /// rules -- invalidate on save, clear on commit -- none of which mean anything here: history is
    /// immutable, so an entry stays valid until the window closes.
    /// </summary>
    private readonly Dictionary<string, SideBySideDiff> _cache = new(StringComparer.Ordinal);

    private readonly DispatcherTimer _settle;

    /// <summary>
    /// <c>rev-list --count HEAD</c>, read once with the first page. Zero until it is known, and zero
    /// forever on an unborn HEAD -- <see cref="Revision"/> then shows nothing rather than a number
    /// that means nothing.
    /// </summary>
    private int _headCount;

    private CommitRange? _shown;
    private CancellationTokenSource? _inFlight;
    private int _generation;
    private bool _endOfHistory;
    private bool _loading;

    public LogWindow(
        RepositoryInfo repository,
        HistoryService history,
        DiffService diffs,
        BlameService blame,
        FlickSettings settings,
        OperationTimings timings,
        ILog log)
    {
        InitializeComponent();

        _repository = repository;
        _history = history;
        _diffs = diffs;
        _blame = blame;
        _settings = settings;
        _timings = timings;
        _log = log;

        Title = Strings.Get("log.title", repository.Name);
        RepositoryText.Text = repository.Name;
        FilesHeader.Text = Strings.Get("log.files.header");
        LoadMoreButton.Content = Strings.Get("log.loadmore", HistoryService.PageSize);
        BlameFileItem.Header = Strings.Get("log.blame");
        SavePatchButton.Content = Strings.Get("log.patch");
        CloseButton.Content = Strings.Get("log.close");
        PagingText.Text = Strings.Get("log.loading");
        RangeText.Text = Strings.Get("log.select.prompt");
        MetaText.Text = Strings.Get("log.hint");

        CommitList.ItemsSource = _commits;
        Diff.SetTypography(settings.DiffFontFamily, settings.DiffFontSize);
        Diff.Show(null, isLoading: false);

        _settle = new DispatcherTimer { Interval = SelectionSettleDelay };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _ = ReloadRangeAsync();
        };

        //Esc is left to the Close button's IsCancel: this window has no state that must refuse to close,
        //which is the only reason CommitWindow intercepts it.
        InputBindings.Add(new KeyBinding
        {
            Key = Key.S,
            Modifiers = ModifierKeys.Control,
            Command = new Infrastructure.RelayCommand(() => OnSavePatch(this, new RoutedEventArgs())),
        });
    }

    /// <summary>
    /// Reads the first page. Separate from the constructor so the caller can time "visible" and
    /// "usable" apart.
    /// </summary>
    public async Task LoadFirstPageAsync() => await LoadPageAsync().ConfigureAwait(true);

    private async Task LoadPageAsync()
    {
        if (_loading || _endOfHistory)
            return;

        _loading = true;
        LoadMoreButton.IsEnabled = false;

        try
        {
            //Started before the page and awaited after it, so the two processes overlap. Only on the first
            //page -- history does not grow while the window is open, and "Load more" numbers its rows from
            //the same total.
            Task<int>? counting = _commits.Count == 0
                ? _history.GetCommitCountAsync(_repository, CancellationToken.None)
                : null;

            LogPage page = await _history
                .GetPageAsync(_repository, _commits.Count, CancellationToken.None)
                .ConfigureAwait(true);

            if (counting is not null)
                _headCount = await counting.ConfigureAwait(true);

            //Added into the bound collection rather than reassigning ItemsSource: a reassignment would drop
            //the selection, the scroll position and every virtualized container on every page.
            foreach (LogCommit commit in page.Commits)
                _commits.Add(Row(_commits.Count, commit));

            _endOfHistory = !page.HasMore;

            if (_commits.Count == 0)
            {
                PagingText.Text = Strings.Get("log.empty");
                RangeText.Text = Strings.Get("log.empty");
                return;
            }

            LoadedText.Text = Strings.Get("log.loaded", _commits.Count);
            PagingText.Text = _endOfHistory ? Strings.Get("log.end") : Strings.Get("log.loaded", _commits.Count);

            //The first page selects its newest commit, so the window opens showing something rather than a
            //prompt over three empty panes.
            if (CommitList.SelectedItems.Count == 0)
                CommitList.SelectedIndex = 0;
        }
        catch (GitNotFoundException ex)
        {
            Report(Strings.Get("log.failed"), ex.Message);
        }
        catch (Exception ex)
        {
            _log.Warn($"Log page failed for {_repository.Root}: {ex.Message}");
            Report(Strings.Get("log.failed"), ex.Message);
        }
        finally
        {
            _loading = false;
            LoadMoreButton.IsEnabled = !_endOfHistory;
            LoadMoreButton.Visibility = _endOfHistory ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private async void OnLoadMore(object sender, RoutedEventArgs e) => await LoadPageAsync().ConfigureAwait(true);

    private void OnCommitSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        //Painted immediately: the range line is arithmetic over two indices, so it must never wait for a
        //process.
        UpdateRangeBand();

        _settle.Stop();
        _settle.Start();
    }

    private CommitRange? CurrentRange()
    {
        var selected = new HashSet<string>(
            CommitList.SelectedItems.OfType<CommitRow>().Select(r => r.Commit.Sha),
            StringComparer.Ordinal);

        //Resolved in Core rather than here: the list is newest-first, so the *smallest* index is the
        //newest commit, and "the range came out the wrong way round" is exactly the bug clicking does
        //not reveal.
        return CommitRange.Resolve([.. _commits.Select(r => r.Commit)], selected);
    }

    private void UpdateRangeBand()
    {
        CommitRange? range = CurrentRange();

        if (range is null)
        {
            RangeText.Text = Strings.Get("log.select.prompt");
            GapText.Visibility = Visibility.Collapsed;
            SubjectText.Text = string.Empty;
            BodyBox.Visibility = Visibility.Collapsed;
            MetaText.Text = Strings.Get("log.hint");
            return;
        }

        RangeText.Text = range.SelectedCount == 1
            ? Strings.Get("log.range.one", range.Newest.ShortSha)
            : range.Oldest.IsRoot
                ? Strings.Get("log.range.root", range.SelectedCount, range.Newest.ShortSha)
                : Strings.Get("log.range.many", range.SelectedCount, range.Oldest.ShortSha, range.Newest.ShortSha);

        GapText.Text = Strings.Get("log.range.gap", range.ImplicitCount);
        GapText.Visibility = range.ImplicitCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (range.SelectedCount == 1)
        {
            LogCommit one = range.Newest;

            SubjectText.Text = one.Subject;
            BodyBox.Text = one.Body;
            BodyBox.Visibility = one.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            MetaText.Text = one.IsMerge
                ? Strings.Get("log.commit.merge")
                : Strings.Get("log.commit.meta", one.Author, Stamp(one.When), string.Join(", ", one.Parents.Select(Short)));
        }
        else
        {
            SubjectText.Text = range.Newest.Subject;
            BodyBox.Visibility = Visibility.Collapsed;
            MetaText.Text = Strings.Get("log.hint");
        }
    }

    private async Task ReloadRangeAsync()
    {
        if (CurrentRange() is not { } range)
        {
            FileList.ItemsSource = null;
            Diff.Show(null, isLoading: false);
            SavePatchButton.IsEnabled = false;
            StatusText.Text = string.Empty;
            _shown = null;
            return;
        }

        //A shift-arrow that widened and narrowed back lands on a range already on screen.
        if (_shown is { } current && current.BaseSpec == range.BaseSpec && current.TipSpec == range.TipSpec)
            return;

        //The token kills the Git process; the generation guards the repaint. Both, because a cancelled
        //process can still complete before its cancellation is observed.
        int mine = ++_generation;

        _inFlight?.Cancel();
        var cancellation = new CancellationTokenSource();
        _inFlight = cancellation;

        var clock = Stopwatch.StartNew();

        IReadOnlyList<GitFileChange> files;

        try
        {
            files = await _history.GetFilesAsync(_repository, range.BaseSpec, range.TipSpec, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.Warn($"Range file list failed for {range.Label}: {ex.Message}");
            Report(Strings.Get("log.failed"), ex.Message);
            return;
        }

        if (mine != _generation)
            return;

        _shown = range;
        ApplyFiles(range, files);
        _timings.Record("log.range", clock.Elapsed);

        _ = PrefetchAsync(range, [.. files.Take(PrefetchCount)]);
    }

    private void ApplyFiles(CommitRange range, IReadOnlyList<GitFileChange> files)
    {
        //Kept if it survives into the new list: widening a range while reading one file must keep
        //showing that file. Nothing here can be dirty, so there is nothing to confirm.
        string? keep = (FileList.SelectedItem as FileRow)?.Change.Path;

        List<FileRow> rows = [.. files.Select(f => new FileRow(f))];

        FileList.ItemsSource = rows;
        FileList.SelectedItem = rows.FirstOrDefault(r => r.Change.Path == keep) ?? rows.FirstOrDefault();

        int added = files.Sum(f => f.AddedLines ?? 0);
        int removed = files.Sum(f => f.RemovedLines ?? 0);

        StatusText.Text = Strings.Get("log.totals", rows.Count, added, removed);
        SavePatchButton.IsEnabled = rows.Count > 0;

        if (rows.Count == 0)
            Diff.Show(null, isLoading: false);

        _ = range;
    }

    /// <summary>
    /// Blame the selected file at the commit being looked at. The revision is the range's tip rather
    /// than the working tree, which is the whole reason this entry is worth having here: every other
    /// way into blame answers the question about disk instead.
    /// </summary>
    private async void OnBlameFile(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } range || FileList.SelectedItem is not FileRow row)
            return;

        var window = new BlameWindow(
            _repository,
            row.Change.Path,
            range.TipSpec,
            _blame,
            _settings,
            _timings,
            _log)
        {
            Owner = this,
        };

        window.Show();

        await window.LoadAsync().ConfigureAwait(true);
    }

    private async void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_shown is not { } range || FileList.SelectedItem is not FileRow row)
            return;

        string key = CacheKey(range, row.Change.Path);

        if (_cache.TryGetValue(key, out SideBySideDiff? cached))
        {
            Diff.Show(cached, isLoading: false);
            return;
        }

        Diff.Show(null, isLoading: true);

        var clock = Stopwatch.StartNew();

        try
        {
            SideBySideDiff diff = await _diffs
                .ComputeRangeAsync(_repository, row.Change, range, CancellationToken.None)
                .ConfigureAwait(true);

            _cache[key] = diff;

            //The user may have moved on while this ran. Repainting then would show the previous file's diff
            //over the current row.
            if (_shown == range && FileList.SelectedItem == row)
            {
                Diff.Show(diff, isLoading: false);
                _timings.Record("log.diff", clock.Elapsed);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Range diff failed for {row.Change.Path}: {ex.Message}");
            Diff.Show(null, isLoading: false);
        }
    }

    /// <summary>
    /// Fills the cache for the first few files. Uncancelled and unreported: a file that fails here is
    /// computed again -- and reported -- when it is clicked.
    /// </summary>
    private async Task PrefetchAsync(CommitRange range, IReadOnlyList<GitFileChange> files)
    {
        foreach (GitFileChange file in files)
        {
            string key = CacheKey(range, file.Path);

            if (_cache.ContainsKey(key))
                continue;

            try
            {
                _cache[key] = await _diffs
                    .ComputeRangeAsync(_repository, file, range, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _log.Debug($"Prefetch failed for {file.Path}: {ex.Message}");
            }
        }
    }

    private async void OnSavePatch(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } range || !SavePatchButton.IsEnabled)
            return;

        var dialog = new SaveFileDialog
        {
            FileName = PatchFileName(range),
            DefaultExt = ".patch",
            AddExtension = true,
            Filter = Strings.Get("log.patch.filter"),

            //The repository's parent, not the repository: a .patch dropped inside the working tree comes
            //straight back as an untracked file in the commit window.
            InitialDirectory = Path.GetDirectoryName(_repository.Root.TrimEnd('\\', '/')) ?? _repository.Root,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var clock = Stopwatch.StartNew();

        try
        {
            //Git writes the file itself, so the patch never becomes a string here -- see
            //HistoryService.SavePatchAsync for why that is the whole point.
            GitResult result = await _history
                .SavePatchAsync(_repository, range, dialog.FileName, CancellationToken.None)
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                Report(Strings.Get("log.patch.failed"), $"{result.ErrorText}\n\n{dialog.FileName}");
                return;
            }

            //A range whose endpoints hold the same tree writes nothing. Said in the footer rather than
            //leaving a zero-byte file the user would later find and not understand.
            StatusText.Text = new FileInfo(dialog.FileName) is { Exists: true, Length: > 0 } written
                ? Strings.Get("log.patch.saved", Path.GetFileName(written.FullName))
                : Strings.Get("log.patch.empty");

            _timings.Record("log.patch", clock.Elapsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(Strings.Get("log.patch.failed"), $"{ex.Message}\n\n{dialog.FileName}");
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _settle.Stop();
        _inFlight?.Cancel();
        base.OnClosed(e);
    }

    /// <summary>
    /// <c>a1b2c3d-add-pgbouncer-pooling.patch</c> for one commit, <c>4d5e6f7..a1b2c3d.patch</c> for a
    /// range: a subject is only useful when there is exactly one.
    /// </summary>
    private static string PatchFileName(CommitRange range)
    {
        if (range.SelectedCount != 1)
            return $"{Short(range.Oldest.Sha)}..{Short(range.Newest.Sha)}.patch";

        //IsLetterOrDigit drops every invalid file-name character as a side effect, and keeps a non-ASCII
        //subject legible.
        string slug = new string([.. range.Newest.Subject.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')])
            .Trim('-');

        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);

        if (slug.Length > 40)
            slug = slug[..40].TrimEnd('-');

        return slug.Length == 0
            ? $"{range.Newest.ShortSha}.patch"
            : $"{range.Newest.ShortSha}-{slug}.patch";
    }

    private void Report(string title, string message) =>
        new NoticeWindow(title, message, compact: false) { Owner = this }.ShowDialog();

    private static string CacheKey(CommitRange range, string path) => $"{range.BaseSpec} {range.TipSpec} {path}";

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>
    /// Absolute, never relative. "2 hours ago" needs plural forms per language in a flat
    /// <c>key = value</c> file a translator opens in Notepad.
    /// </summary>
    private static string Stamp(DateTimeOffset when) => when.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    private CommitRow Row(int index, LogCommit commit) =>
        new(index, commit, Revision(index), commit.ShortSha, commit.Subject, commit.Author, Stamp(commit.When), Decorate(commit.Refs));

    /// <summary>
    /// The row's revision number. Arithmetic rather than a Git call per row -- the list is
    /// newest-first, so the top row is <see cref="_headCount"/> and every row below is one fewer.
    /// Empty when the count is unknown: no number beats a wrong one.
    /// </summary>
    private string Revision(int index) =>
        _headCount > index ? (_headCount - index).ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static string Decorate(string refs) =>
        refs.Replace("HEAD -> ", string.Empty, StringComparison.Ordinal);

    /// <param name="Index">
    /// The row's position, kept on the row rather than looked up per selection change: the list is
    /// newest-first and the arithmetic reads backwards.
    /// </param>
    private sealed record CommitRow(
        int Index,
        LogCommit Commit,
        string Revision,
        string ShortSha,
        string Subject,
        string Author,
        string Date,
        string Refs)
    {
        public string Tooltip => Revision.Length == 0
            ? $"{Commit.Sha}\n{Commit.Author} · {Commit.When.LocalDateTime:F}"
            : $"{Commit.Sha}\n{Strings.Get("log.revision", Revision)}\n{Commit.Author} · {Commit.When.LocalDateTime:F}";

        //Overridden for TagRow's reason: a templated ListBoxItem has no text of its own and UI
        //Automation falls back to this. A record's synthesised version reads every property name out to
        //a screen reader.
        public override string ToString() => $"{Revision} {ShortSha} {Subject} {Author} {Date}".Trim();
    }

    /// <summary>
    /// One file row. A projection of <see cref="GitFileChange"/> rather than
    /// <see cref="ViewModels.FileChangeItem"/>, whose <c>IsSelected</c> writes through to decide a
    /// commit's contents and whose tooltip renders staged-versus-worktree counts -- which for a
    /// commit range is not merely irrelevant, it is false.
    /// </summary>
    private sealed class FileRow(GitFileChange change)
    {
        public GitFileChange Change { get; } = change;

        public string StatusCode => Change.DisplayStatus.ToShortCode();

        public string FileName => Change.Path[(Change.Path.LastIndexOf('/') + 1)..];

        public string Directory => Change.Path.LastIndexOf('/') is var i && i >= 0 ? Change.Path[..(i + 1)] : string.Empty;

        public string Added => Change.IsBinary ? Strings.Get("commit.summary.binary") : Change.AddedLines is { } a ? $"+{a}" : string.Empty;

        public string Removed => Change.IsBinary || Change.RemovedLines is null ? string.Empty : $"-{Change.RemovedLines}";

        public string Tooltip => Change.OldPath is { Length: > 0 } old
            ? Strings.Get("files.tooltip.renamed", Change.Path, old)
            : Change.Path;

        public override string ToString() => $"{StatusCode} {Change.Path} {Added} {Removed}".TrimEnd();
    }
}
