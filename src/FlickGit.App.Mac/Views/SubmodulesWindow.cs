using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FlickGit.App.CommandLine;
using FlickGit.App.Localization;
using FlickGit.Models;
using FlickGit.Submodules;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// What submodules are there, and removing one.
///
/// <b>It commits nothing.</b> Both operations leave their work in the index and the window says so —
/// the next step is the commit window, which is where a commit is made in this product. And
/// <c>.git/modules/&lt;name&gt;</c> is never deleted: it can hold commits made in there and never
/// pushed, so removing a submodule takes it out of the working tree and the index and leaves that
/// alone.
/// </summary>
internal sealed class SubmodulesWindow : ListWindow
{
    private readonly SubmoduleService _submodules;
    private readonly IDialogs _dialogs;
    private readonly RepositoryInfo _repository;

    public SubmodulesWindow(RepositoryInfo repository, SubmoduleService submodules, IDialogs dialogs)
        : base(Strings.Get("submodule.title", repository.Name))
    {
        _repository = repository;
        _submodules = submodules;
        _dialogs = dialogs;

        Items.ItemTemplate = RowTemplate();

        Add(Strings.Get("submodule.menu.remove"), RemoveAsync);

        Opened += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        Items.ItemsSource = await _submodules.ListAsync(_repository, CancellationToken.None)
            .ConfigureAwait(true);

        Status.Text = Strings.Get("submodule.commit");
    }

    private async Task RemoveAsync()
    {
        if (Items.SelectedItem is not GitSubmodule submodule)
            return;

        bool yes = await _dialogs.ConfirmAsync(
            Strings.Get("submodule.remove.title"),
            Strings.Get("submodule.remove.ask", submodule.Path),
            Strings.Get("submodule.remove.yes"),
            Strings.Get("common.cancel"),
            destructive: true).ConfigureAwait(true);

        if (!yes)
            return;

        //force: false. Git's own refusal is the guard, and only a second answer to *that* forces --
        //which this window does not offer, so the refusal stands and is reported.
        SubmoduleOutcome outcome = await _submodules
            .RemoveAsync(_repository, submodule.Path, force: false, CancellationToken.None)
            .ConfigureAwait(true);

        if (!outcome.Succeeded)
            Report(outcome.GitError, Strings.Get("submodule.remove.title"));

        await LoadAsync().ConfigureAwait(true);
    }

    private static FuncDataTemplate<GitSubmodule> RowTemplate() =>
        new((submodule, _) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(6, 3),
            Children =
            {
                Column(new TextBlock { Text = submodule.Path, TextTrimming = TextTrimming.CharacterEllipsis }, 0),
                Column(new TextBlock { Text = submodule.Url, Opacity = 0.55, Margin = new Thickness(10, 0) }, 1),
                Column(new TextBlock
                {
                    Text = submodule.IsInitialised
                        ? Strings.Get("submodule.state.changed")
                        : Strings.Get("submodule.state.uninitialised"),
                    Opacity = 0.7,
                }, 2),
            },
        });
}
