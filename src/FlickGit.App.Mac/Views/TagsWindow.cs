using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FlickGit.App.CommandLine;
using FlickGit.App.Localization;
using FlickGit.Models;
using FlickGit.Tags;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// What tags exist, creating one on HEAD, and deleting one.
///
/// <b>Delete removes it on the remote first and is never forced.</b> That order is the point: a tag
/// deleted locally and left on the remote comes back on the next fetch, which reads as the delete
/// having silently failed. No moving a tag, no <c>--force</c>, no signing, and no tag at a chosen
/// commit — every one of those is in CLAUDE.md's list of things this window does not do.
/// </summary>
internal sealed class TagsWindow : ListWindow
{
    private readonly TagService _tags;
    private readonly IDialogs _dialogs;
    private readonly RepositoryInfo _repository;

    private readonly TextBox _name = new()
    {
        Margin = new Thickness(10, 10, 10, 6),
        PlaceholderText = Strings.Get("tag.filter.hint"),
    };

    public TagsWindow(RepositoryInfo repository, TagService tags, IDialogs dialogs)
        : base(Strings.Get("tag.title", repository.Name))
    {
        _repository = repository;
        _tags = tags;
        _dialogs = dialogs;

        Items.ItemTemplate = RowTemplate();

        Add(Strings.Get("tag.create"), CreateAsync);
        Add(Strings.Get("tag.delete"), DeleteAsync);

        //The name box above the list, so the window reads top to bottom: type a name, or pick a row.
        if (Content is Grid grid)
        {
            grid.RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto");

            foreach (Control child in grid.Children.OfType<Control>())
                child.SetValue(Grid.RowProperty, child.GetValue(Grid.RowProperty) + 1);

            grid.Children.Add(Row(_name, 0));
        }

        Opened += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync() =>
        Items.ItemsSource = await _tags.ListAsync(_repository, CancellationToken.None).ConfigureAwait(true);

    private async Task CreateAsync()
    {
        if (_name.Text is not { Length: > 0 } name)
            return;

        //Validated through Git's own check-ref-format before anything runs.
        TagOutcome validation = await _tags.ValidateAsync(_repository, name, CancellationToken.None)
            .ConfigureAwait(true);

        if (!validation.Succeeded)
        {
            Report(validation.GitError, Strings.Get("tag.nomatch", name));

            return;
        }

        TagOutcome outcome = await _tags
            .CreateAsync(_repository, name, message: null, commit: null, CancellationToken.None)
            .ConfigureAwait(true);

        if (!outcome.Succeeded)
        {
            Report(outcome.GitError, Strings.Get("tag.delete.failed"));

            return;
        }

        _name.Text = string.Empty;

        await LoadAsync().ConfigureAwait(true);
    }

    private async Task DeleteAsync()
    {
        if (Items.SelectedItem is not GitTag tag)
            return;

        //Resolved before the question, because which sentence to ask depends on whether there is a
        //remote to delete from as well.
        string? remote = await _tags.ResolveRemoteAsync(_repository, CancellationToken.None)
            .ConfigureAwait(true);

        bool yes = await _dialogs.ConfirmAsync(
            Strings.Get("tag.confirm.title"),
            remote is null
                ? Strings.Get("tag.confirm.local", tag.Name)
                : Strings.Get("tag.confirm.remote", tag.Name, remote),
            Strings.Get("tag.confirm.yes"),
            Strings.Get("common.cancel"),
            destructive: true).ConfigureAwait(true);

        if (!yes)
            return;

        //Passed on, so Core deletes on the remote first: a tag deleted locally and left on the
        //remote comes back on the next fetch, which reads as the delete having silently failed.
        TagOutcome outcome = await _tags.DeleteAsync(_repository, tag.Name, remote, CancellationToken.None)
            .ConfigureAwait(true);

        if (!outcome.Succeeded)
            Report(outcome.GitError, Strings.Get("tag.delete.failed"));

        await LoadAsync().ConfigureAwait(true);
    }

    private static FuncDataTemplate<GitTag> RowTemplate() =>
        new((tag, _) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(6, 3),
            Children =
            {
                Column(new TextBlock { Text = tag.Name, TextTrimming = TextTrimming.CharacterEllipsis }, 0),
                Column(new TextBlock { Text = tag.Subject, Opacity = 0.6, Margin = new Thickness(10, 0) }, 1),
                Column(new TextBlock { Text = tag.Date, Opacity = 0.5 }, 2),
            },
        });
}
