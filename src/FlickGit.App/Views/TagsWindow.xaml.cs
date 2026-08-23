using System.Windows;
using System.Windows.Input;
using FlickGit.App.Localization;
using FlickGit.Matching;
using FlickGit.Models;
using FlickGit.Tags;

namespace FlickGit.App.Views;

/// <summary>
/// The tag window: what exists, create one, publish one, delete one.
///
/// One window rather than four, because all four questions are the same question with a different
/// verb attached — and every one of them starts with "what is there already". A create dialog that
/// could not show the existing tags would be a dialog whose first job the user has to do somewhere
/// else.
///
/// Three things it refuses to do, all of them from CLAUDE.md's "Safety Rules":
///
/// <list type="bullet">
/// <item><description><b>Deleting always asks first</b>, on both surfaces, and the remote variant
/// asks a different question rather than the same one twice.</description></item>
/// <item><description><b>Nothing is ever forced.</b> There is no <c>--force</c> anywhere below this,
/// so an existing tag cannot be moved onto a different commit by accident — Git refuses and says so.
/// </description></item>
/// <item><description><b>Delete is never the default button.</b> Enter in the filter box creates or
/// does nothing; it can never reach a deletion.</description></item>
/// </list>
/// </summary>
public partial class TagsWindow : Window
{
    private readonly RepositoryInfo _repository;
    private readonly TagService _tags;
    private readonly List<GitTag> _all = [];

    /// <summary>
    /// The remote to publish to and delete from, resolved once on open. Null when there is none, or
    /// when there are several and none is called <c>origin</c> — in which case the remote buttons stay
    /// disabled rather than guessing which of them other people read.
    /// </summary>
    private string? _remote;

    public TagsWindow(RepositoryInfo repository, TagService tags)
    {
        InitializeComponent();

        _repository = repository;
        _tags = tags;

        Title = Strings.Get("tag.title", repository.Name);
        NewLabel.Text = Strings.Get("tag.new");
        CreateButton.Content = Strings.Get("tag.create");
        PushButton.Content = Strings.Get("tag.push");
        DeleteButton.Content = Strings.Get("tag.delete");
        DeleteRemoteButton.Content = Strings.Get("tag.deleteremote");
        CloseButton.Content = Strings.Get("tag.close");

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

    private void OnSelectionChanged(object sender, RoutedEventArgs e) => UpdateSelectionButtons();

    /// <summary>
    /// The three buttons that act on the highlighted row.
    ///
    /// Both remote ones are gated on <see cref="_remote"/> as well as on a selection: a repository
    /// with no remote, or with several and no <c>origin</c>, has nowhere unambiguous to publish to —
    /// and a button that would have to guess is a button that stays off.
    /// </summary>
    private void UpdateSelectionButtons()
    {
        bool selected = Selected is not null;

        DeleteButton.IsEnabled = selected;
        PushButton.IsEnabled = selected && _remote is not null;
        DeleteRemoteButton.IsEnabled = selected && _remote is not null;
    }

    private string? Selected => (TagList.SelectedItem as TagRow)?.Name;

    private void OnNewNameChanged(object sender, RoutedEventArgs e) => UpdateNewHint();

    /// <summary>
    /// Live feedback on the name being typed, in the spirit of the branch ComboBox: the consequence
    /// is visible before Enter rather than reported after it.
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

        NewHint.Text = NoteBox.Text.Trim().Length > 0
            ? Strings.Get("tag.willannotate", typed)
            : Strings.Get("tag.willcreate", typed);

        CreateButton.IsEnabled = true;
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();

        if (name.Length == 0)
            return;

        SetBusy(true);

        try
        {
            //Null commit: the tag lands on HEAD. There is a log viewer now, and it deliberately
            //offers no action on a commit -- no checkout, reset, revert, cherry-pick or tag. So
            //there is still nothing to pick a commit *from*, and that is a decision rather than a
            //missing feature.
            TagOutcome outcome = await _tags
                .CreateAsync(_repository, name, NoteBox.Text, null, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                Report(Strings.Get("tag.create"), outcome);
                return;
            }

            NameBox.Clear();
            NoteBox.Clear();

            await LoadAsync().ConfigureAwait(true);

            //Said in the footer rather than as a toast: the new row is on screen a line above, so
            //the confirmation is really just a label for what the user can already see.
            StatusText.Text = Strings.Get("tag.created", name);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnPush(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } name || _remote is not { } remote)
            return;

        SetBusy(true);

        try
        {
            TagOutcome outcome = await _tags
                .PushAsync(_repository, name, remote, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Succeeded)
                StatusText.Text = Strings.Get("tag.pushed", name, remote);
            else
                Report(Strings.Get("tag.push"), outcome);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Task OnDeleteAsync(bool includeRemote)
    {
        if (Selected is not { } name)
            return Task.CompletedTask;

        string? remote = includeRemote ? _remote : null;

        if (includeRemote && remote is null)
            return Task.CompletedTask;

        //Two different questions, because they are two different acts. A local tag is a line in
        //.git; a published one is something other people have already fetched, and a tag has no
        //reflog to recover it from either way.
        string question = remote is null
            ? Strings.Get("tag.confirm.local", name)
            : Strings.Get("tag.confirm.remote", name, remote);

        bool confirmed = ConfirmWindow.Ask(
            this,
            Strings.Get("tag.confirm.title"),
            question,
            Strings.Get("tag.confirm.yes"),
            Strings.Get("action.confirm.no"));

        return confirmed ? DeleteAsync(name, remote) : Task.CompletedTask;
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

    private async void OnDelete(object sender, RoutedEventArgs e) =>
        await OnDeleteAsync(includeRemote: false).ConfigureAwait(true);

    private async void OnDeleteEverywhere(object sender, RoutedEventArgs e) =>
        await OnDeleteAsync(includeRemote: true).ConfigureAwait(true);

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
            PushButton.IsEnabled = false;
            DeleteButton.IsEnabled = false;
            DeleteRemoteButton.IsEnabled = false;
            return;
        }

        //Re-derived rather than restored to what it was. Two rules decide these four buttons — is the
        //typed name usable, and is a row selected — and a command that just ran may well have changed
        //both. Putting them back the way they were is how a Create button survives creating the tag
        //whose name is now taken.
        UpdateNewHint();
        UpdateSelectionButtons();
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
