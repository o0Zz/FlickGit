using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.Models;
using FlickGit.Stashes;

namespace FlickGit.App.Views;

/// <summary>
/// The stash window: what is put away, put something away, take one back, throw one out.
///
/// Shaped like the tag window minus its filter box -- a repository has three stashes, not three
/// hundred, and a filter over three rows is a control that can only ever cost a keystroke. What is
/// left is the same three parts: the list, the thing you came to create below it, and one line of
/// outcome in the footer.
///
/// <b>The one rule that matters here is not visible in this file, and that is the point.</b> A stash
/// row is addressed by a reflog selector, and a selector is a position: the list is renumbered by
/// every push and every pop, including ones made in a terminal while this window sat open. So
/// <c>StashService</c> re-reads the list and checks the sha before it pops or drops anything, and
/// this window's job is to say so when the answer comes back
/// <see cref="StashRefusal.Moved"/> -- see <see cref="ReportMovedAsync"/>.
///
/// Two operations, asked about differently on purpose:
///
/// <list type="bullet">
/// <item><description><b>Pop asks nothing.</b> It puts work back rather than discarding any, and Git
/// refuses outright rather than overwriting a file that is in the way -- so a double-click is enough,
/// and the failure path has nothing to recover from.</description></item>
/// <item><description><b>Drop asks, in its own words.</b> A stash has no reflog of its own, so once
/// the entry is gone there is nothing here that finds it again. The question names the stash and
/// what was in it, and Enter means no.</description></item>
/// </list>
///
/// There is no <c>clear</c>, no <c>apply</c> and no way to force anything, because
/// <c>StashService</c> has none of them.
/// </summary>
public partial class StashesWindow : ReloadableWindow
{
    private readonly RepositoryInfo _repository;
    private readonly StashService _stashes;


    public StashesWindow(RepositoryInfo repository, StashService stashes)
    {
        InitializeComponent();

        _repository = repository;
        _stashes = stashes;

        Title = Strings.Get("stash.title", repository.Name);
        NewLabel.Text = Strings.Get("stash.new");
        MessageLabel.Text = Strings.Get("stash.message.label");
        UntrackedBox.Content = Strings.Get("stash.untracked");
        StashButton.Content = Strings.Get("stash.push");
        CloseButton.Content = Strings.Get("common.close");

        UntrackedBox.ToolTip = Strings.Get("stash.untracked.hint");


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

        StashList.ItemsSource = stashes.Select(Row).ToList();
        StashList.SelectedIndex = stashes.Count > 0 ? 0 : -1;

        StatusText.Text = stashes.Count == 0
            ? Strings.Get("stash.none")
            : Strings.Get("stash.count", stashes.Count);

        //Back to the box, which is the only thing here that takes typing. It is also cleared by a
        //successful push, so this is where the caret lands to type the next one.
        NoteBox.Focus();
    }

    private GitStash? Selected => (StashList.SelectedItem as StashRow)?.Stash;

    private void OnRowRightClick(object sender, MouseButtonEventArgs e) =>
        FilterList.SelectRowUnderPointer(StashList, e.OriginalSource);

    /// <summary>
    /// Built when the menu opens rather than declared in XAML, because both labels name the stash
    /// they would act on -- and on a list whose row numbers move, a menu item that did not say which
    /// one it meant would be the whole problem in miniature.
    /// </summary>
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        RowMenu.Items.Clear();

        if (Selected is not { } stash)
        {
            e.Handled = true;
            return;
        }

        RowMenu.Items.Add(Menus.Item(
            Strings.Get("stash.menu.pop", stash.Reference),
            () => PopAsync(stash)));

        RowMenu.Items.Add(Menus.Item(
            Strings.Get("stash.menu.drop", stash.Reference),
            () => ConfirmAndDropAsync(stash)));
    }

    /// <summary>
    /// Double-click pops the row <i>under the pointer</i>, never the selected one.
    /// <see cref="LoadAsync"/> selects index 0 whenever the list is rebuilt, so a double-click on the
    /// empty space below the last row would otherwise pop the newest stash in the repository -- from
    /// a gesture aimed at nothing.
    /// </summary>
    private async void OnPop(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject).FindAncestor<ListBoxItem>()?.Content is not StashRow row)
            return;

        await PopAsync(row.Stash).ConfigureAwait(true);
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
    /// Asks before dropping, naming the stash and what was in it.
    ///
    /// The message is in the question because the reference is not enough to identify a stash to a
    /// person -- <c>stash@{1}</c> is a position, and the whole reason this window verifies the sha is
    /// that positions move. Enter means no: <c>defaultIsAffirmative</c> is left at its default, which
    /// <c>ConfirmWindow</c> reserves for the two questions the Recycle Bin makes undoable. Nothing
    /// makes this one undoable.
    /// </summary>
    private Task ConfirmAndDropAsync(GitStash stash)
    {
        bool confirmed = ConfirmWindow.Ask(
            this,
            Strings.Get("stash.confirm.title"),
            Strings.Get("stash.confirm.drop", stash.Reference, stash.Message),
            Strings.Get("stash.confirm.yes"),
            Strings.Get("common.cancel"),
            destructive: true);

        return confirmed ? DropAsync(stash) : Task.CompletedTask;
    }

    private async Task DropAsync(GitStash stash)
    {
        await RunBusyAsync(async () =>
        {
            StashOutcome outcome = await _stashes
                .DropAsync(_repository, stash, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Refusal == StashRefusal.Moved)
            {
                await ReportMovedAsync().ConfigureAwait(true);
                return;
            }

            if (!outcome.Succeeded)
            {
                Notice.GitFailure(
                    this,
                    Strings.Get("stash.drop"),
                    Strings.Get("stash.drop.failed"),
                    outcome.GitError,
                    _repository.Root);
                return;
            }

            await LoadAsync().ConfigureAwait(true);

            StatusText.Text = Strings.Get("stash.dropped", stash.Reference);
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

    protected override void SetBusy(bool busy)
    {
        IsBusy = busy;

        StashList.IsEnabled = !busy;
        NoteBox.IsEnabled = !busy;
        UntrackedBox.IsEnabled = !busy;
        StashButton.IsEnabled = !busy;
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
}
