using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FlickGit.App.CommandLine;
using FlickGit.App.Localization;
using FlickGit.Models;
using FlickGit.Stashes;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// What is put away, and what is in the one you are pointing at.
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
internal sealed class StashesWindow : ListWindow
{
    private readonly StashService _stashes;
    private readonly IDialogs _dialogs;
    private readonly RepositoryInfo _repository;

    private readonly Button _pop;
    private readonly Button _drop;

    public StashesWindow(RepositoryInfo repository, StashService stashes, IDialogs dialogs)
        : base(Strings.Get("stash.title", repository.Name))
    {
        _repository = repository;
        _stashes = stashes;
        _dialogs = dialogs;

        Items.SelectionMode = SelectionMode.Multiple;
        Items.ItemTemplate = RowTemplate();

        _pop = Add(Strings.Get("stash.pop"), PopAsync);
        _drop = Add(Strings.Get("stash.drop"), DropAsync);

        Items.SelectionChanged += (_, _) => UpdateButtons();

        Opened += (_, _) => _ = LoadAsync();
    }

    private IReadOnlyList<GitStash> Selected =>
        Items.SelectedItems?.OfType<GitStash>().ToArray() ?? [];

    private async Task LoadAsync()
    {
        Items.ItemsSource = await _stashes.ListAsync(_repository, CancellationToken.None)
            .ConfigureAwait(true);

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        //Pop is one row for the reason in the class remarks. Saying so in the footer rather than
        //picking a row for the user is what CLAUDE.md asks of a double-click inside a selection.
        _pop.IsEnabled = Selected.Count == 1;
        _drop.IsEnabled = Selected.Count > 0;

        if (Selected.Count > 1)
            Status.Text = Strings.Get("stash.count", Selected.Count);
    }

    private async Task PopAsync()
    {
        if (Selected is not [GitStash stash])
            return;

        //Nothing asked: this restores work rather than discarding any, and a failed pop always leaves
        //the stash in place because Git applies and only then drops.
        StashOutcome outcome = await _stashes.PopAsync(_repository, stash, CancellationToken.None)
            .ConfigureAwait(true);

        if (!outcome.Succeeded)
        {
            Report(outcome.GitError, Strings.Get("stash.pop.kept", stash.Reference));

            return;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    private async Task DropAsync()
    {
        IReadOnlyList<GitStash> chosen = Selected;

        if (chosen.Count == 0)
            return;

        //One question with the totals, never one per item. A stash has no reflog, so this is the one
        //thing in this window that cannot be undone.
        bool yes = await _dialogs.ConfirmAsync(
            Strings.Get("stash.confirm.title"),
            chosen is [{ } one]
                ? Strings.Get("stash.confirm.drop", one.Reference, one.Message)
                : Strings.Get("stash.confirm.drop", Strings.Get("stash.count", chosen.Count), string.Empty),
            Strings.Get("stash.confirm.yes"),
            Strings.Get("common.cancel"),
            destructive: true).ConfigureAwait(true);

        if (!yes)
            return;

        //Core drops highest reflog index first and re-verifies each row as its turn comes, because
        //dropping stash@{k} renumbers everything above it.
        StashDropOutcome outcome = await _stashes.DropAsync(_repository, chosen, CancellationToken.None)
            .ConfigureAwait(true);

        if (!outcome.Outcome.Succeeded)
            Report(outcome.Outcome.GitError, Strings.Get("stash.drop.failed"));

        await LoadAsync().ConfigureAwait(true);
    }

    private static FuncDataTemplate<GitStash> RowTemplate() =>
        new((stash, _) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(6, 3),
            Children =
            {
                Column(new TextBlock { Text = stash.Reference, FontFamily = new FontFamily("monospace"), Margin = new Thickness(0, 0, 10, 0) }, 0),
                Column(new TextBlock { Text = stash.Message, TextTrimming = TextTrimming.CharacterEllipsis }, 1),
                Column(new TextBlock { Text = stash.Branch, Opacity = 0.6, Margin = new Thickness(10, 0, 0, 0) }, 2),
            },
        });
}
