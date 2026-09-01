using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.Diagnostics;
using FlickGit.Diff;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Stashes;

namespace FlickGit.App.Views;

/// <summary>
/// The stash window: what is put away, what is in it, put something away, take one back, throw some
/// out.
///
/// Shaped like the log window rather than the tag picker it started as, and for the same reason that
/// window exists: the question a list of stashes raises is "what is in that one", and until this
/// window could answer it the only way to find out was to pop the stash — which is the very thing
/// the user was trying to decide about. So the middle of the window is the log window's lower half,
/// a file list against a read-only <see cref="DiffPane"/>, and the parts either side of it are the
/// picker's: the list, the thing you create below it, and one line of outcome in the footer.
///
/// <b>The one rule that matters here is not visible in this file, and that is the point.</b> A stash
/// row is addressed by a reflog selector, and a selector is a position: the list is renumbered by
/// every push and every pop, including ones made in a terminal while this window sat open. So
/// <c>StashService</c> re-reads the list and checks the sha before it pops or drops anything, and
/// this window's job is to say so when the answer comes back
/// <see cref="StashRefusal.Moved"/> — see <see cref="ReportMovedAsync"/>.
///
/// Three operations, asked about differently on purpose:
///
/// <list type="bullet">
/// <item><description><b>Pop asks nothing, and takes one row.</b> It puts work back rather than
/// discarding any, and Git refuses outright rather than overwriting a file that is in the way — so a
/// double-click is enough, and the failure path has nothing to recover from. One row because popping
/// several is a chain of merges in which the second lands on a tree the first has already
/// changed.</description></item>
/// <item><description><b>Drop asks, in its own words, and takes the selection.</b> A stash has no
/// reflog of its own, so once the entry is gone there is nothing here that finds it again. One
/// question with the totals rather than one per row, and Enter means no.</description></item>
/// <item><description><b>Reading a stash asks nothing and changes nothing.</b> Every diff the pane
/// is handed carries a <see cref="DiffRange"/>, which is what makes it read-only, and this window
/// subscribes to none of the pane's events.</description></item>
/// </list>
///
/// There is no <c>clear</c>, no <c>apply</c> and no way to force anything, because
/// <c>StashService</c> has none of them.
/// </summary>
public partial class StashesWindow : ReloadableWindow
{
    /// <summary>
    /// How long the selection has to stop moving before its contents are read.
    ///
    /// The log window's number, for the same reason: arrowing down the list would otherwise start a
    /// pair of Git processes per row passed over.
    /// </summary>
    private static readonly TimeSpan SelectionSettleDelay = TimeSpan.FromMilliseconds(120);

    private const int PrefetchCount = 5;

    private readonly RepositoryInfo _repository;
    private readonly StashService _stashes;
    private readonly DiffService _diffs;
    private readonly OperationTimings _timings;
    private readonly ILog _log;

    /// <summary>
    /// Diffs already computed, keyed by the two revisions and the path.
    ///
    /// Not <c>DiffCache</c>, which is a set of working-tree rules about a file that can change under
    /// it. A stash commit is immutable, so an entry stays valid until the window closes — including
    /// across a reload, and across the stash being dropped.
    /// </summary>
    private readonly Dictionary<string, SideBySideDiff> _cache = new(StringComparer.Ordinal);

    private readonly DispatcherTimer _settle;

    /// <summary>The stash whose contents are on screen, so a settled selection that did not change is free.</summary>
    private GitStash? _shown;

    private CancellationTokenSource? _inFlight;

    private int _generation;


    public StashesWindow(
        RepositoryInfo repository,
        StashService stashes,
        DiffService diffs,
        FlickSettings settings,
        OperationTimings timings,
        ILog log)
    {
        InitializeComponent();

        _repository = repository;
        _stashes = stashes;
        _diffs = diffs;
        _timings = timings;
        _log = log;

        Title = Strings.Get("stash.title", repository.Name);
        NewLabel.Text = Strings.Get("stash.new");
        MessageLabel.Text = Strings.Get("stash.message.label");
        UntrackedBox.Content = Strings.Get("stash.untracked");
        StashButton.Content = Strings.Get("stash.push");
        FilesHeader.Text = Strings.Get("stash.files.header");
        CloseButton.Content = Strings.Get("common.close");

        UntrackedBox.ToolTip = Strings.Get("stash.untracked.hint");

        Diff.SetTypography(settings.DiffFontFamily, settings.DiffFontSize);
        Diff.Show(null, isLoading: false);

        _settle = new DispatcherTimer { Interval = SelectionSettleDelay };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _ = ReloadContentsAsync();
        };

        Loaded += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }


    /// <summary>
    /// Reads the list and puts the count in the footer.
    ///
    /// Called again after every operation, and every one of those then overwrites the footer with
    /// its own sentence -- the same order the tag window uses, so the count is what is showing
    /// whenever there is nothing more specific to say.
    /// </summary>
    protected override async Task ReadStateAsync()
    {
        IReadOnlyList<GitStash> stashes = await _stashes
            .ListAsync(_repository, ClosingToken)
            .ConfigureAwait(true);

        //Kept across the reload by sha rather than by position, and by sha rather than by reference
        //for the reason the whole feature turns on: after a drop or a push every row below the change
        //is at a different stash@{n}, so a reload that restored the *position* would move the diff
        //out from under the user without either of them noticing.
        string? keep = Selected?.Sha;

        List<StashRow> rows = [.. stashes.Select(Row)];

        StashList.ItemsSource = rows;
        StashList.SelectedItem = rows.FirstOrDefault(row => row.Stash.Sha == keep) ?? rows.FirstOrDefault();

        StatusText.Text = stashes.Count == 0
            ? Strings.Get("stash.none")
            : Strings.Get("stash.count", stashes.Count);

        //The list was rebuilt, so the row on screen is a different object even when it is the same
        //stash. Clearing this makes the settle below read rather than recognise the selection --
        //which is what refreshes the file list after a push added a stash.
        _shown = null;

        _settle.Stop();
        _settle.Start();

        //Back to the box, which is the only thing here that takes typing, unless the user has a stash
        //selected -- in which case they are reading it, and moving the caret would take the arrow keys
        //away from the list they are reading with.
        if (StashList.SelectedItem is null)
            NoteBox.Focus();
    }

    private GitStash? Selected => (StashList.SelectedItem as StashRow)?.Stash;

    /// <summary>
    /// Every highlighted stash, in the list's own order. What Drop acts on.
    ///
    /// <c>SelectedItems</c> rather than <c>SelectedItem</c>, and the service is what puts them into
    /// the order they must be dropped in -- see <see cref="StashService.DropAsync"/>, where getting
    /// that wrong drops a stash nobody selected.
    /// </summary>
    private List<GitStash> SelectedStashes =>
        [.. StashList.SelectedItems.OfType<StashRow>().Select(row => row.Stash)];

    private void OnRowRightClick(object sender, MouseButtonEventArgs e) =>
        FilterList.SelectRowUnderPointer(StashList, e.OriginalSource);

    /// <summary>
    /// Built when the menu opens rather than declared in XAML, because both labels name what they
    /// would act on -- and on a list whose row numbers move, a menu item that did not say which one
    /// it meant would be the whole problem in miniature.
    ///
    /// Several rows offer only Drop. Pop is absent rather than present and refused, because a
    /// disabled item invites a second click at the same place; the double-click path, which cannot be
    /// hidden the same way, is where the sentence explaining it lives.
    /// </summary>
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        RowMenu.Items.Clear();

        List<GitStash> selection = SelectedStashes;

        if (selection.Count == 0)
        {
            e.Handled = true;
            return;
        }

        if (selection.Count > 1)
        {
            RowMenu.Items.Add(Menus.Item(
                Strings.Get("stash.menu.drop.many", selection.Count),
                () => ConfirmAndDropAsync(selection)));

            return;
        }

        GitStash stash = selection[0];

        RowMenu.Items.Add(Menus.Item(
            Strings.Get("stash.menu.pop", stash.Reference),
            () => PopAsync(stash)));

        RowMenu.Items.Add(Menus.Item(
            Strings.Get("stash.menu.drop", stash.Reference),
            () => ConfirmAndDropAsync(selection)));
    }

    /// <summary>
    /// Double-click pops the row <i>under the pointer</i>, never the selected one.
    /// <see cref="ReadStateAsync"/> selects a row whenever the list is rebuilt, so a double-click on
    /// the empty space below the last row would otherwise pop a stash from a gesture aimed at
    /// nothing.
    ///
    /// A double-click inside a multi-selection keeps that selection, by
    /// <see cref="FilterList.SelectRowUnderPointer"/>'s own rule, so this refuses rather than picking
    /// one of the rows itself. The sentence goes where the count was: nothing happened, and nothing
    /// that did not happen deserves a dialog.
    /// </summary>
    private async void OnPop(object sender, MouseButtonEventArgs e)
    {
        if (!FilterList.SelectRowUnderPointer(StashList, e.OriginalSource))
            return;

        if (StashList.SelectedItems.Count > 1)
        {
            StatusText.Text = Strings.Get("stash.pop.one");
            return;
        }

        if (Selected is not { } stash)
            return;

        await PopAsync(stash).ConfigureAwait(true);
    }

    private async Task PopAsync(GitStash stash)
    {
        await RunBusyAsync(async () =>
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
                //Git applies and only then drops, so a pop that failed left the stash exactly where it
                //was -- unconditionally, whether it conflicted or was refused outright. That is the
                //actionable half of the message, and it goes above Git's own words rather than
                //instead of them.
                Notice.Show(
                    this,
                    Strings.Get("stash.pop"),
                    Strings.Get("stash.pop.kept", stash.Reference),
                    outcome.GitError);

                return;
            }

            await LoadAsync().ConfigureAwait(true);

            StatusText.Text = Strings.Get("stash.popped", stash.Reference);
        });
    }

    /// <summary>
    /// Asks before dropping, naming what is about to go.
    ///
    /// The message is in the question because the reference is not enough to identify a stash to a
    /// person -- <c>stash@{1}</c> is a position, and the whole reason this window verifies the sha is
    /// that positions move. One question with the totals for a selection, never one per row: that is
    /// CLAUDE.md's rule for a multi-selection, and here it is also the only way to state the number,
    /// which is the fact the user most needs before answering.
    ///
    /// Enter means no: <c>defaultIsAffirmative</c> is left at its default, which
    /// <c>ConfirmWindow</c> reserves for the two questions the Recycle Bin makes undoable. Nothing
    /// makes this one undoable.
    /// </summary>
    private Task ConfirmAndDropAsync(IReadOnlyList<GitStash> stashes)
    {
        if (stashes.Count == 0)
            return Task.CompletedTask;

        (string title, string question) = stashes.Count == 1
            ? (Strings.Get("stash.confirm.title"),
               Strings.Get("stash.confirm.drop", stashes[0].Reference, stashes[0].Message))
            : (Strings.Get("stash.confirm.title.many"),
               Strings.Get("stash.confirm.drop.many", stashes.Count, Describe(stashes)));

        bool confirmed = ConfirmWindow.Ask(
            this,
            title,
            question,
            Strings.Get("stash.confirm.yes"),
            Strings.Get("common.cancel"),
            destructive: true);

        return confirmed ? DropAsync(stashes) : Task.CompletedTask;
    }

    /// <summary>One line per stash, so the question names every one of them rather than just counting.</summary>
    private static string Describe(IReadOnlyList<GitStash> stashes) =>
        string.Join("\n", stashes.Select(stash => $"{stash.Reference} — {stash.Message}"));

    private async Task DropAsync(IReadOnlyList<GitStash> stashes)
    {
        await RunBusyAsync(async () =>
        {
            StashDropOutcome outcome = await _stashes
                .DropAsync(_repository, stashes, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Outcome.Refusal == StashRefusal.Moved)
            {
                await ReportMovedAsync().ConfigureAwait(true);
                return;
            }

            //Reloaded before the report either way: a batch that stopped half-way has already changed
            //the list, and the sentence about it has to arrive beside a list that agrees.
            await LoadAsync().ConfigureAwait(true);

            if (!outcome.Outcome.Succeeded)
            {
                //Partly done, which is the state that needs the count. The single-stash wording still
                //covers the case where nothing went at all.
                Notice.GitFailure(
                    this,
                    Strings.Get("stash.drop"),
                    outcome.Dropped == 0
                        ? Strings.Get("stash.drop.failed")
                        : Strings.Get("stash.drop.failed.many", outcome.Dropped, stashes.Count),
                    outcome.Outcome.GitError,
                    _repository.Root);

                return;
            }

            StatusText.Text = stashes.Count == 1
                ? Strings.Get("stash.dropped", stashes[0].Reference)
                : Strings.Get("stash.dropped.many", outcome.Dropped);
        });
    }

    /// <summary>Enter in the message box stashes. See the box's own comment in the XAML.</summary>
    private async void OnMessageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;

        await PushAsync().ConfigureAwait(true);
    }

    private async void OnPush(object sender, RoutedEventArgs e) => await PushAsync().ConfigureAwait(true);

    private async Task PushAsync()
    {
        await RunBusyAsync(async () =>
        {
            StashOutcome outcome = await _stashes
                .PushAsync(_repository, NoteBox.Text, UntrackedBox.IsChecked == true, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Refusal == StashRefusal.NothingToStash)
            {
                //Neither an error nor a dialog. Nothing happened, the list on screen is still correct,
                //and the sentence saying so belongs exactly where the count was.
                StatusText.Text = Strings.Get("stash.nothing");
                return;
            }

            if (!outcome.Succeeded)
            {
                Notice.GitFailure(
                    this,
                    Strings.Get("stash.push"),
                    Strings.Get("stash.push.failed"),
                    outcome.GitError,
                    _repository.Root);
                return;
            }

            NoteBox.Clear();

            await LoadAsync().ConfigureAwait(true);

            //No reference in the sentence: the new stash is the row directly above it, so this is a
            //label for something the user can already see. The wording says only what is true on the
            //command line too, which is the other surface reading this key.
            StatusText.Text = Strings.Get("stash.pushed");
        });
    }

    /// <summary>
    /// The list moved under the user, and nothing was asked of Git.
    ///
    /// Reloaded first, so the message arrives beside a list that already agrees with the repository.
    /// A notice rather than a footer line, because this is the one outcome where the row that was
    /// clicked was not the row it appeared to be -- which is worth interrupting for.
    /// </summary>
    private async Task ReportMovedAsync()
    {
        await LoadAsync().ConfigureAwait(true);

        Notice.Show(this, Strings.Get("stash.moved.title"), Strings.Get("stash.moved"));
    }


    // ---- reading a stash ----------------------------------------------------

    /// <summary>
    /// Debounced rather than read here. Selection changes arrive per keyboard row.
    /// </summary>
    private void OnStashSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>
    /// Fills the file list for the highlighted stash, or empties it.
    ///
    /// <b>A multi-selection shows nothing</b>, and that is a statement rather than a shortcut: with
    /// several rows highlighted the gesture in progress is a Drop, and picking one of them to render
    /// would be this window guessing which stash the user meant — the exact habit the right-click
    /// rule exists to break.
    ///
    /// The token kills the Git processes; the generation guards the repaint. Both, because a
    /// cancelled process can still complete before its cancellation is observed.
    /// </summary>
    private async Task ReloadContentsAsync()
    {
        if (StashList.SelectedItems.Count != 1 || Selected is not { } stash)
        {
            ClearContents();
            return;
        }

        //Already on screen: a click on the row that was already highlighted, or a reload that landed
        //on the same stash.
        if (_shown is { } current && current.Sha == stash.Sha)
            return;

        int mine = ++_generation;

        _inFlight?.Cancel();
        var cancellation = new CancellationTokenSource();
        _inFlight = cancellation;

        var clock = Stopwatch.StartNew();

        IReadOnlyList<StashChange> changes;

        try
        {
            changes = await _stashes
                .ListFilesAsync(_repository, stash, cancellation.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.Warn($"Reading stash {stash.Reference} failed: {ex.Message}");
            ClearContents();
            return;
        }

        if (mine != _generation)
            return;

        _shown = stash;

        ApplyContents(stash, changes);

        _timings.Record("stash.range", clock.Elapsed);

        _ = PrefetchAsync([.. changes.Take(PrefetchCount)]);
    }

    private void ClearContents()
    {
        _shown = null;
        RangeText.Text = string.Empty;
        TotalsText.Text = string.Empty;
        FileList.ItemsSource = null;
        Diff.Show(null, isLoading: false);
    }

    private void ApplyContents(GitStash stash, IReadOnlyList<StashChange> changes)
    {
        //The stash's own label rather than the first row's: with untracked files there are two ranges
        //in this list, and the header above the list has to name the stash, not whichever half
        //happens to be first.
        RangeText.Text = stash.TrackedRange?.Label ?? stash.Reference;

        List<FileRow> rows = [.. changes.Select(change => new FileRow(change))];

        FileList.ItemsSource = rows;

        //The first row selected outright, so a stash's contents are one click rather than two. There
        //is nothing to keep across this: a different stash is a different set of files.
        FileList.SelectedItem = rows.FirstOrDefault();

        int added = changes.Sum(change => change.File.AddedLines ?? 0);
        int removed = changes.Sum(change => change.File.RemovedLines ?? 0);

        TotalsText.Text = rows.Count == 0
            ? Strings.Get("stash.files.none")
            : Strings.Get("stash.totals", rows.Count, added, removed);

        if (rows.Count == 0)
            Diff.Show(null, isLoading: false);
    }

    private async void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not FileRow row)
            return;

        GitStash? stash = _shown;

        if (_cache.TryGetValue(CacheKey(row), out SideBySideDiff? cached))
        {
            Diff.Show(cached, isLoading: false);
            return;
        }

        Diff.Show(null, isLoading: true);

        var clock = Stopwatch.StartNew();

        try
        {
            SideBySideDiff diff = await _diffs
                .ComputeRangeAsync(_repository, row.Change.File, row.Change.Range, CancellationToken.None)
                .ConfigureAwait(true);

            _cache[CacheKey(row)] = diff;

            //The user may have moved on while this ran. Repainting then would show the previous file's
            //diff over the current row.
            if (_shown == stash && FileList.SelectedItem == row)
            {
                Diff.Show(diff, isLoading: false);
                _timings.Record("stash.diff", clock.Elapsed);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Stash diff failed for {row.Change.File.Path}: {ex.Message}");
            Diff.Show(null, isLoading: false);
        }
    }

    /// <summary>
    /// Fills the cache for the first few files. Uncancelled and unreported: a file that fails here is
    /// computed again -- and reported -- when it is clicked.
    /// </summary>
    private async Task PrefetchAsync(IReadOnlyList<StashChange> changes)
    {
        foreach (StashChange change in changes)
        {
            string key = CacheKey(change);

            if (_cache.ContainsKey(key))
                continue;

            try
            {
                _cache[key] = await _diffs
                    .ComputeRangeAsync(_repository, change.File, change.Range, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _log.Debug($"Prefetch failed for {change.File.Path}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Keyed by both revisions and not by the stash, because a stash carrying untracked files spans
    /// two ranges and a key naming only the stash would have the two halves overwrite each other.
    ///
    /// The NUL is written as an escape rather than as a literal byte, per the log window's cache key:
    /// a raw one in the source has git classify the file as binary.
    /// </summary>
    private static string CacheKey(StashChange change) =>
        $"{change.Range.BaseSpec}\0{change.Range.TipSpec}\0{change.File.Path}";

    private static string CacheKey(FileRow row) => CacheKey(row.Change);


    protected override void SetBusy(bool busy)
    {
        IsBusy = busy;

        StashList.IsEnabled = !busy;
        FileList.IsEnabled = !busy;
        NoteBox.IsEnabled = !busy;
        UntrackedBox.IsEnabled = !busy;
        StashButton.IsEnabled = !busy;
    }

    protected override void OnClosed(EventArgs e)
    {
        _settle.Stop();
        _inFlight?.Cancel();

        base.OnClosed(e);
    }



    private static StashRow Row(GitStash stash) =>
        new(stash,
            stash.Reference,
            stash.Branch,
            stash.Message,

            //Short, local, and carrying the time of day: two stashes made this afternoon are told
            //apart by nothing else. A default date is one the parser could not read, and it shows as
            //blank rather than as the first of January in year one.
            stash.Created == default
                ? string.Empty
                : stash.Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

    /// <summary>
    /// One row in the list.
    ///
    /// It carries the whole <see cref="GitStash"/> rather than its reference, because the reference
    /// alone is not enough to act on: <c>StashService</c> needs the sha to check that the reference
    /// still means what the row says it means.
    ///
    /// <see cref="ToString"/> is overridden for the reason both pickers override theirs: a
    /// <c>ListBoxItem</c> whose content is a <c>DataTemplate</c> has no text of its own, so UI
    /// Automation falls back to it, and a record's synthesised version reads every property name out
    /// to a screen reader.
    /// </summary>
    private sealed record StashRow(
        GitStash Stash,
        string Reference,
        string Branch,
        string Message,
        string Created)
    {
        public override string ToString() => $"{Reference} {Branch} {Message} {Created}".TrimEnd();
    }

    /// <summary>
    /// One file row, and the two revisions its diff is between.
    ///
    /// The log window's row type over a <see cref="StashChange"/> rather than a bare
    /// <c>GitFileChange</c>: the range travels per row here, because a stash made with untracked
    /// files has two of them.
    /// </summary>
    private sealed class FileRow(StashChange change)
    {
        public StashChange Change { get; } = change;

        public string StatusCode => Change.File.DisplayStatus.ToShortCode();

        public string FileName => Change.File.Path[(Change.File.Path.LastIndexOf('/') + 1)..];

        public string Directory =>
            Change.File.Path.LastIndexOf('/') is var i && i >= 0 ? Change.File.Path[..(i + 1)] : string.Empty;

        public string Added => Change.File.IsBinary
            ? Strings.Get("commit.summary.binary")
            : Change.File.AddedLines is { } a ? $"+{a}" : string.Empty;

        public string Removed =>
            Change.File.IsBinary || Change.File.RemovedLines is null ? string.Empty : $"-{Change.File.RemovedLines}";

        public string Tooltip => Change.File.OldPath is { Length: > 0 } old
            ? Strings.Get("files.tooltip.renamed", Change.File.Path, old)
            : Change.File.Path;

        public override string ToString() => $"{StatusCode} {Change.File.Path} {Added} {Removed}".TrimEnd();
    }
}
