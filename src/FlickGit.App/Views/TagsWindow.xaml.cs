using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlickGit.App.Localization;
using FlickGit.Matching;
using FlickGit.Models;
using FlickGit.Tags;

namespace FlickGit.App.Views;

/// <summary>
/// The tag window: what exists, create one, delete one.
///
/// One window rather than three, because all three questions are the same question with a different
/// verb attached — and every one of them starts with "what is there already". A create dialog that
/// could not show the existing tags would be a dialog whose first job the user has to do somewhere
/// else.
///
/// <b>It is shaped like the branch picker on purpose.</b> Creating is the filter box's neighbour and
/// deleting is a right-click on the row it acts on — not a footer button acting on "whatever is
/// highlighted", which is how the wrong tag goes. The three buttons that used to sit in the footer
/// (Push, Delete locally, Delete here and on remote) are gone rather than hidden: publishing is no
/// longer a separate act, and there was never a second question worth asking about where a deletion
/// should land.
///
/// Three things it refuses to do, all of them from CLAUDE.md's "Safety Rules":
///
/// <list type="bullet">
/// <item><description><b>Deleting always asks first</b>, and the remote variant asks a different
/// question rather than the same one twice.</description></item>
/// <item><description><b>Nothing is ever forced.</b> There is no <c>--force</c> anywhere below this,
/// so an existing tag cannot be moved onto a different commit by accident — Git refuses and says so.
/// </description></item>
/// <item><description><b>No button deletes.</b> Enter in the filter box creates or does nothing, and
/// the only path to a deletion is a right-click on a named row.</description></item>
/// </list>
/// </summary>
public partial class TagsWindow : Window
{
    private readonly RepositoryInfo _repository;
    private readonly TagService _tags;
    private readonly List<GitTag> _all = [];

    /// <summary>
    /// The remote to publish to and delete from, resolved once on open. Null when there is none, or
    /// when there are several and none is called <c>origin</c> — in which case creating stays local
    /// and the delete item says so, rather than guessing which remote other people read.
    /// </summary>
    private string? _remote;

    public TagsWindow(RepositoryInfo repository, TagService tags)
    {
        InitializeComponent();

        _repository = repository;
        _tags = tags;

        Title = Strings.Get("tag.title", repository.Name);
        NewLabel.Text = Strings.Get("tag.new");
        NameLabel.Text = Strings.Get("tag.name.label");
        MessageLabel.Text = Strings.Get("tag.message.label");
        CreateButton.Content = Strings.Get("tag.create");
        CloseButton.Content = Strings.Get("common.close");

        NoteBox.ToolTip = Strings.Get("tag.message.hint");

        Loaded += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        //Both reads at once: neither needs the other's answer, and the window is not painted until
        //the list arrives anyway.
        Task<IReadOnlyList<GitTag>> listing = _tags.ListAsync(_repository, CancellationToken.None);
        Task<string?> remote = _tags.ResolveRemoteAsync(_repository, CancellationToken.None);

        _all.Clear();
        _all.AddRange(await listing.ConfigureAwait(true));
        _remote = await remote.ConfigureAwait(true);

        ApplyFilter();
        UpdateNewHint();
        FilterBox.Focus();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string pattern = FilterBox.Text.Trim();

        //With nothing typed, Git's own ordering: `--sort=-v:refname` puts 1.10 above 1.9, and the
        //matcher would fall back to alphabetical and undo exactly that. Once there is a pattern the
        //best match wins, which is what somebody who typed a version wants.
        List<GitTag> matches = pattern.Length == 0
            ? [.. _all]
            : [.. FuzzyMatcher
                .Rank(_all.Select(tag => tag.Name), pattern)
                .Select(match => _all.First(tag => tag.Name == match.Value))];

        TagList.ItemsSource = matches.Select(Row).ToList();
        TagList.SelectedIndex = matches.Count > 0 ? 0 : -1;

        StatusText.Text = _all.Count == 0
            ? Strings.Get("tag.none")
            : matches.Count == 0 ? Strings.Get("tag.nomatch")
            : Strings.Get("tag.count", _all.Count);
    }

    /// <summary>
    /// Down/Up move the selection without leaving the filter box, so the whole interaction is
    /// type-then-arrow and the hands never leave the keyboard. The same behaviour as the branch
    /// picker, deliberately.
    /// </summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (TagList.Items.Count == 0)
            return;

        switch (e.Key)
        {
            case Key.Down:
                TagList.SelectedIndex = Math.Min(TagList.SelectedIndex + 1, TagList.Items.Count - 1);
                TagList.ScrollIntoView(TagList.SelectedItem);
                e.Handled = true;
                break;

            case Key.Up:
                TagList.SelectedIndex = Math.Max(TagList.SelectedIndex - 1, 0);
                TagList.ScrollIntoView(TagList.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    private string? Selected => (TagList.SelectedItem as TagRow)?.Name;

    /// <summary>
    /// The row under the pointer becomes the selected one before the menu is built. Without this the
    /// menu would be built for whatever was highlighted before, and a right-click on one tag would
    /// delete another.
    /// </summary>
    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject).FindAncestor<ListBoxItem>() is { } row)
            row.IsSelected = true;
    }

    /// <summary>
    /// One item, built when the menu opens rather than declared in XAML, because its label has to
    /// name the remote the deletion would reach — "Delete tag, here and on origin…" is a different
    /// promise from "Delete tag…", and it is the one the user needs before pressing it.
    /// </summary>
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        RowMenu.Items.Clear();

        if (Selected is not { } name)
        {
            e.Handled = true;
            return;
        }

        var item = new MenuItem
        {
            Header = _remote is { } remote
                ? Strings.Get("tag.menu.delete.remote", remote)
                : Strings.Get("tag.menu.delete"),
        };

        item.Click += async (_, _) => await ConfirmAndDeleteAsync(name).ConfigureAwait(true);
        RowMenu.Items.Add(item);
    }

    private void OnNewNameChanged(object sender, RoutedEventArgs e) => UpdateNewHint();

    /// <summary>
    /// Live feedback on the name being typed, in the spirit of the branch ComboBox: the consequence
    /// is visible before Enter rather than reported after it. That consequence now includes the push,
    /// which is why the hint names the remote.
    /// </summary>
    private void UpdateNewHint()
    {
        string typed = NameBox.Text.Trim();

        if (typed.Length == 0)
        {
            NewHint.Text = string.Empty;
            CreateButton.IsEnabled = false;
            return;
        }

        if (!TagService.LooksValid(typed))
        {
            NewHint.Text = Strings.Get("tag.invalid");
            CreateButton.IsEnabled = false;
            return;
        }

        //An existing name is refused here rather than by Git, because the only way past it is
        //--force and there is deliberately no button for that.
        if (_all.Any(tag => string.Equals(tag.Name, typed, StringComparison.Ordinal)))
        {
            NewHint.Text = Strings.Get("tag.exists", typed);
            CreateButton.IsEnabled = false;
            return;
        }

        bool annotated = NoteBox.Text.Trim().Length > 0;

        NewHint.Text = _remote is { } remote
            ? Strings.Get(annotated ? "tag.willannotate" : "tag.willcreate", typed, remote)
            : Strings.Get(annotated ? "tag.willannotate.local" : "tag.willcreate.local", typed);

        CreateButton.IsEnabled = true;
    }

    /// <summary>
    /// Creates the tag and publishes it, in that order, with no question in between.
    ///
    /// <b>The push is not a second decision.</b> A tag that exists only on this machine is a version
    /// number nobody else can resolve, and "and push it?" has the same answer every time it is asked
    /// — so it is not asked. Nothing is forced: a name the remote already carries is refused by Git
    /// and reported in Git's own words.
    /// </summary>
    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();

        if (name.Length == 0)
            return;

        //Captured before the reload below, which resolves it again: the status line has to name the
        //remote the push actually went to.
        string? remote = _remote;

        SetBusy(true);

        try
        {
            //Null commit: the tag lands on HEAD. There is a log viewer now, and it deliberately
            //offers no action on a commit -- no checkout, reset, revert, cherry-pick or tag. So
            //there is still nothing to pick a commit *from*, and that is a decision rather than a
            //missing feature.
            TagOutcome created = await _tags
                .CreateAsync(_repository, name, NoteBox.Text, null, CancellationToken.None)
                .ConfigureAwait(true);

            if (!created.Succeeded)
            {
                Report(Strings.Get("tag.create"), created);
                return;
            }

            NameBox.Clear();
            NoteBox.Clear();

            TagOutcome published = remote is null
                ? TagOutcome.Ok
                : await _tags.PushAsync(_repository, name, remote, CancellationToken.None).ConfigureAwait(true);

            await LoadAsync().ConfigureAwait(true);

            //Said in the footer rather than as a toast: the new row is on screen a line above, so
            //the confirmation is really just a label for what the user can already see.
            StatusText.Text = remote is not null && published.Succeeded
                ? Strings.Get("tag.created.pushed", name, remote)
                : Strings.Get("tag.created", name);

            //A failed push is its own report, because the two halves ended differently: the tag is
            //here and it is not there, which is the one outcome the footer line cannot say on its own.
            if (!published.Succeeded)
                Report(Strings.Get("tag.push"), published, Strings.Get("tag.push.failed", name, remote!));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Task ConfirmAndDeleteAsync(string name)
    {
        //Two different questions, because they are two different acts. A local tag is a line in
        //.git; a published one is something other people have already fetched, and a tag has no
        //reflog to recover it from either way.
        string question = _remote is { } remote
            ? Strings.Get("tag.confirm.remote", name, remote)
            : Strings.Get("tag.confirm.local", name);

        bool confirmed = ConfirmWindow.Ask(
            this,
            Strings.Get("tag.confirm.title"),
            question,
            Strings.Get("tag.confirm.yes"),
            Strings.Get("common.cancel"));

        return confirmed ? DeleteAsync(name, _remote) : Task.CompletedTask;
    }

    private async Task DeleteAsync(string name, string? remote)
    {
        SetBusy(true);

        try
        {
            TagOutcome outcome = await _tags
                .DeleteAsync(_repository, name, remote, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                //The remote goes first inside DeleteAsync, so a failure here means nothing was
                //deleted anywhere. Saying so is the difference between an error and a mystery.
                Report(Strings.Get("tag.delete"), outcome, Strings.Get("tag.delete.failed"));
                return;
            }

            await LoadAsync().ConfigureAwait(true);

            StatusText.Text = remote is null
                ? Strings.Get("tag.deleted", name)
                : Strings.Get("tag.deleted.remote", name, remote);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Git's own words, never paraphrased — CLAUDE.md, "Error Handling".</summary>
    private void Report(string title, TagOutcome outcome, string? preamble = null)
    {
        string body = preamble is { Length: > 0 }
            ? $"{preamble}\n\n{outcome.GitError}"
            : outcome.GitError ?? string.Empty;

        new NoticeWindow(title, body, compact: false) { Owner = this }.ShowDialog();
    }

    private void SetBusy(bool busy)
    {
        FilterBox.IsEnabled = !busy;
        TagList.IsEnabled = !busy;
        NameBox.IsEnabled = !busy;
        NoteBox.IsEnabled = !busy;

        if (busy)
        {
            CreateButton.IsEnabled = false;
            return;
        }

        //Re-derived rather than restored to what it was. One rule decides the Create button — is the
        //typed name usable — and the command that just ran may well have changed the answer. Putting
        //it back the way it was is how a Create button survives creating the tag whose name is now
        //taken.
        UpdateNewHint();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static TagRow Row(GitTag tag) =>
        new(tag.Name,
            tag.Target,
            //A lightweight tag has no message of its own, so the column says what kind it is rather
            //than standing empty and looking like a load that failed.
            tag.IsAnnotated ? tag.Subject : Strings.Get("tag.lightweight"),
            tag.Date);

    /// <summary>
    /// One row in the list.
    ///
    /// <see cref="ToString"/> is overridden for the reason the branch picker's is: a `ListBoxItem`
    /// whose content is a `DataTemplate` has no text of its own, so UI Automation falls back to it,
    /// and a record's synthesised version reads every property name out to a screen reader.
    /// </summary>
    private sealed record TagRow(string Name, string Target, string Subject, string Date)
    {
        public override string ToString() => $"{Name} {Target} {Subject} {Date}".TrimEnd();
    }
}
