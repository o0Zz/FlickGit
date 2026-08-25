using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.Matching;
using FlickGit.Models;
using FlickGit.Submodules;

namespace FlickGit.App.Views;

/// <summary>
/// The submodules window: what is declared, what is checked out, what has moved -- and add,
/// initialise, remove.
///
/// One screen per kind of ref, beside Branches and Tags, and it is the same window three times over
/// for the same reason: every question here begins with "what is there already", so a dialog that
/// could not show the existing rows would be a dialog whose first job the user has to do elsewhere.
///
/// <b>It commits nothing.</b> Add and Remove both leave their work staged, and the footer says so
/// and offers the commit window. That window is the product's only commit surface; a message box
/// here would be a second place for the primary-branch warning, the staging defaults and the push
/// guardrails to live.
/// </summary>
public partial class SubmodulesWindow : Window
{
    private readonly RepositoryInfo _repository;
    private readonly SubmoduleService _submodules;
    private readonly List<GitSubmodule> _all = [];

    /// <summary>
    /// Raised when the user asks to commit what this window staged. The window opens nothing itself,
    /// the way the palette does not run its own actions -- the caller owns the commit window.
    /// </summary>
    public event Action? CommitRequested;

    /// <summary>
    /// True once an operation here has put something in the index. It is never cleared: the commit
    /// window is what spends it, and this window cannot see that happen.
    /// </summary>
    private bool _staged;

    /// <summary>
    /// False once the user edits the target box themselves, after which the URL stops driving it.
    /// </summary>
    private bool _deriveInto = true;

    public SubmodulesWindow(RepositoryInfo repository, SubmoduleService submodules)
    {
        InitializeComponent();

        _repository = repository;
        _submodules = submodules;

        Title = Strings.Get("submodule.title", repository.Name);
        AddLabel.Text = Strings.Get("submodule.add");
        UrlLabel.Text = Strings.Get("submodule.add.url");
        IntoLabel.Text = Strings.Get("submodule.add.into");
        AddButton.Content = Strings.Get("submodule.add.button");
        CommitButton.Content = Strings.Get("submodule.commit");
        CloseButton.Content = Strings.Get("common.close");

        Loaded += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    private SubmoduleRow? Selected => ModuleList.SelectedItem as SubmoduleRow;

    /// <summary>
    /// Re-reads everything. Called after every mutation rather than patching the row that changed:
    /// <c>submodule add</c> touches <c>.gitmodules</c> as well as the row, and a removal takes a row
    /// away.
    /// </summary>
    private async Task LoadAsync()
    {
        _all.Clear();
        _all.AddRange(await _submodules.ListAsync(_repository, CancellationToken.None).ConfigureAwait(true));

        ApplyFilter();
        UpdateAddHint();
        FilterBox.Focus();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string pattern = FilterBox.Text.Trim();

        List<GitSubmodule> matches = pattern.Length == 0
            ? [.. _all]
            : [.. FuzzyMatcher
                .Rank(_all.Select(module => module.Path), pattern)
                .Select(match => _all.First(module => module.Path == match.Value))];

        ModuleList.ItemsSource = matches.Select(Row).ToList();
        ModuleList.SelectedIndex = matches.Count > 0 ? 0 : -1;

        SetStatus(_all.Count == 0
            ? Strings.Get("submodule.none")
            : matches.Count == 0 ? Strings.Get("submodule.nomatch")
            : Strings.Get("submodule.count", _all.Count));
    }

    /// <summary>
    /// Arrow keys move the selection without taking focus out of the filter box, so typing and
    /// choosing stay one gesture. The shared routing the other two pickers use.
    /// </summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e) => FilterList.RouteArrows(ModuleList, e);

    /// <summary>
    /// A ListBox does not select on right-click, so the menu would otherwise be built for whichever
    /// row happened to be highlighted -- and one of its items removes a submodule.
    /// </summary>
    private void OnRowRightClick(object sender, MouseButtonEventArgs e) =>
        FilterList.SelectRowUnderPointer(ModuleList, e.OriginalSource);

    /// <summary>
    /// What a row offers is what the row is. An item that cannot apply is absent rather than greyed:
    /// an uninitialised submodule has no folder to open and nothing checked out to remove.
    /// </summary>
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        RowMenu.Items.Clear();

        if (Selected is not { } row)
        {
            e.Handled = true;
            return;
        }

        if (!row.IsInitialised)
        {
            RowMenu.Items.Add(Menus.Item(
                Strings.Get("submodule.menu.init"),
                () => UpdateAsync(row.Path, initialising: true)));

            RowMenu.Items.Add(new Separator());
            RowMenu.Items.Add(Menus.Item(
                Strings.Get("submodule.menu.remove"),
                () => ConfirmAndRemoveAsync(row.Path)));

            return;
        }

        RowMenu.Items.Add(Menus.Item(
            Strings.Get("submodule.menu.update"),
            () => UpdateAsync(row.Path, initialising: false)));

        RowMenu.Items.Add(Menus.Item(
            Strings.Get("submodule.menu.open"),
            () =>
            {
                OpenFolder(row.Path);
                return Task.CompletedTask;
            }));

        RowMenu.Items.Add(new Separator());

        RowMenu.Items.Add(Menus.Item(
            Strings.Get("submodule.menu.remove"),
            () => ConfirmAndRemoveAsync(row.Path)));
    }

    /// <summary>
    /// Acts on the row under the pointer, never on the selection: <see cref="ApplyFilter"/> selects
    /// index 0 every time the list is rebuilt, so a double-click on the empty space below the last
    /// row would otherwise open the first submodule in the repository.
    /// </summary>
    private void OnOpenFolder(object sender, MouseButtonEventArgs e)
    {
        if (!FilterList.SelectRowUnderPointer(ModuleList, e.OriginalSource) || Selected is not { } row)
            return;

        if (!row.IsInitialised)
        {
            SetStatus(Strings.Get("submodule.notinitialised", row.Path));
            return;
        }

        OpenFolder(row.Path);
    }

    private void OpenFolder(string path)
    {
        string absolute = System.IO.Path.Combine(_repository.Root, path.Replace('/', System.IO.Path.DirectorySeparatorChar));

        try
        {
            //UseShellExecute is required to hand a directory to the shell; without it this would be an
            //attempt to execute the folder.
            Process.Start(new ProcessStartInfo(absolute) { UseShellExecute = true });
            SetStatus(Strings.Get("submodule.opened", path));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or IOException)
        {
            SetStatus(Strings.Get("submodule.openfailed", path));
        }
    }

    /// <summary>
    /// Clones and checks out what is missing. Not confirmed, and it needs no confirmation: it is the
    /// one operation here that only ever creates -- the same command that already runs unasked after
    /// every <c>pull --rebase</c>.
    /// </summary>
    private async Task UpdateAsync(string path, bool initialising)
    {
        SetBusy(true);

        try
        {
            SetStatus(Strings.Get(initialising ? "submodule.initialising" : "submodule.updating", path));

            SubmoduleOutcome outcome = await _submodules
                .UpdateAsync(_repository, path, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                Report(Strings.Get("submodule.menu.update"), outcome);
                return;
            }

            await LoadAsync().ConfigureAwait(true);
            SetStatus(Strings.Get(initialising ? "submodule.initialised" : "submodule.updated", path));
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Two questions, and the second one only when Git has actually refused.
    ///
    /// The first is the ordinary confirmation. If Git says the submodule holds work that is not
    /// committed, the second names what forcing would destroy -- the shape <c>branch -d</c> and
    /// <c>branch -D</c> already have, and the only route to a forced removal anywhere in the product.
    /// </summary>
    private async Task ConfirmAndRemoveAsync(string path)
    {
        bool confirmed = ConfirmWindow.Ask(
            this,
            Strings.Get("submodule.remove.title"),
            Strings.Get("submodule.remove.ask", path),
            Strings.Get("submodule.remove.yes"),
            Strings.Get("common.cancel"));

        if (!confirmed)
            return;

        await RemoveAsync(path, force: false).ConfigureAwait(true);
    }

    private async Task RemoveAsync(string path, bool force)
    {
        SetBusy(true);

        bool askAgain = false;

        try
        {
            SubmoduleOutcome outcome = await _submodules
                .RemoveAsync(_repository, path, force, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                _staged = true;
                await LoadAsync().ConfigureAwait(true);
                SetStatus(Strings.Get("submodule.removed", path));
                return;
            }

            if (!force && outcome.HasLocalChanges)
            {
                askAgain = ConfirmWindow.Ask(
                    this,
                    Strings.Get("submodule.remove.dirty.title"),
                    Strings.Get("submodule.remove.dirty.ask", path),
                    Strings.Get("submodule.remove.dirty.yes"),
                    Strings.Get("common.cancel"));

                if (!askAgain)
                {
                    //Refused, and nothing was touched. Git's own words say which files, which is more
                    //than a status line can carry.
                    Report(Strings.Get("submodule.remove.title"), outcome);
                }

                return;
            }

            Report(Strings.Get("submodule.remove.title"), outcome);
        }
        finally
        {
            SetBusy(false);
        }

        if (askAgain)
        {
            //Outside the finally, so the busy flag is already down: the recursive call takes it again,
            //and a nested SetBusy(false) would otherwise unlock the window while it is still working.
            await RemoveAsync(path, force: true).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The target box follows the URL until the user takes it over, which is the rule the clone
    /// dialog's own target box follows.
    /// </summary>
    private void OnUrlChanged(object sender, RoutedEventArgs e)
    {
        if (_deriveInto)
        {
            string derived = DirectoryNameFrom(UrlBox.Text);

            if (derived.Length > 0 && !string.Equals(derived, IntoBox.Text, StringComparison.Ordinal))
            {
                //Set without losing ownership: the assignment raises OnIntoChanged, which would
                //otherwise read as the user typing.
                _deriveInto = false;
                IntoBox.Text = derived;
                _deriveInto = true;
            }
        }

        UpdateAddHint();
    }

    private void OnIntoChanged(object sender, RoutedEventArgs e)
    {
        if (_deriveInto)
            _deriveInto = false;

        UpdateAddHint();
    }

    /// <summary>
    /// The last segment of the URL with <c>.git</c> stripped -- the same derivation the clone dialog
    /// makes, and the name Git itself would choose.
    /// </summary>
    private static string DirectoryNameFrom(string url)
    {
        string trimmed = url.Trim().TrimEnd('/', '\\');

        if (trimmed.Length == 0)
            return string.Empty;

        //Both separators, and the scp-style `git@host:path` colon, which is neither.
        int cut = trimmed.LastIndexOfAny(['/', '\\', ':']);
        string last = cut < 0 ? trimmed : trimmed[(cut + 1)..];

        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            last = last[..^4];

        return last.Trim();
    }

    /// <summary>
    /// The hint is the refusal the service would give, shown while the user is still typing -- the
    /// same rule the branch ComboBox follows: the consequence is visible before the button.
    /// </summary>
    private void UpdateAddHint()
    {
        string url = UrlBox.Text.Trim();
        string into = IntoBox.Text.Trim();

        if (url.Length == 0 && into.Length == 0)
        {
            AddHint.Text = string.Empty;
            AddButton.IsEnabled = false;
            return;
        }

        if (_submodules.CheckNewPath(_repository, url, into) is { } refusal)
        {
            AddHint.Text = RefusalText(refusal, into);
            AddButton.IsEnabled = false;
            return;
        }

        //Declared already. Git refuses too, but only after it has cloned.
        if (_all.Any(module => string.Equals(module.Path, into.Replace('\\', '/').Trim('/'), StringComparison.Ordinal)))
        {
            AddHint.Text = Strings.Get("submodule.add.refused.exists", into);
            AddButton.IsEnabled = false;
            return;
        }

        AddHint.Text = Strings.Get("submodule.add.hint", into);
        AddButton.IsEnabled = true;
    }

    private static string RefusalText(SubmoduleRefusal refusal, string path) =>
        refusal switch
        {
            SubmoduleRefusal.NoUrl => Strings.Get("submodule.add.refused.nourl"),
            SubmoduleRefusal.NoPath => Strings.Get("submodule.add.refused.nopath"),
            SubmoduleRefusal.OutsideRepository => Strings.Get("submodule.add.refused.outside", path),
            _ => Strings.Get("submodule.add.refused.notempty", path),
        };

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        string into = IntoBox.Text.Trim();

        SetBusy(true);

        try
        {
            SetStatus(Strings.Get("submodule.adding", into));

            SubmoduleOutcome outcome = await _submodules
                .AddAsync(_repository, url, into, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                if (outcome.Refusal is { } refusal)
                    SetStatus(RefusalText(refusal, into));
                else
                    Report(Strings.Get("submodule.add.button"), outcome);

                return;
            }

            _staged = true;

            UrlBox.Text = string.Empty;
            IntoBox.Text = string.Empty;
            _deriveInto = true;

            await LoadAsync().ConfigureAwait(true);
            SetStatus(Strings.Get("submodule.added", into));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnCommit(object sender, RoutedEventArgs e)
    {
        CommitRequested?.Invoke();
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// The footer says one of two things: what just happened, or -- once something is in the index --
    /// that it is staged and not committed. The second wins, because it is the sentence with
    /// something left to do in it.
    /// </summary>
    private void SetStatus(string text)
    {
        StatusText.Text = _staged
            ? $"{text}  {Strings.Get("submodule.staged")}"
            : text;

        CommitButton.Visibility = _staged ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Report(string title, SubmoduleOutcome outcome) =>
        new NoticeWindow(title, outcome.GitError ?? string.Empty, compact: false) { Owner = this }.ShowDialog();

    private void SetBusy(bool busy)
    {
        FilterBox.IsEnabled = !busy;
        ModuleList.IsEnabled = !busy;
        UrlBox.IsEnabled = !busy;
        IntoBox.IsEnabled = !busy;
        CommitButton.IsEnabled = !busy;

        if (busy)
        {
            AddButton.IsEnabled = false;
            return;
        }

        //Re-derived rather than restored: what just happened may have changed the answer -- adding
        //the submodule the boxes still describe, for one.
        UpdateAddHint();
    }

    private static SubmoduleRow Row(GitSubmodule module) =>
        new(module.Path,
            module.Url,
            module.IsInitialised,
            module.HasChanges);

    /// <param name="State">
    /// The one thing on the row with something to do behind it. Uninitialised comes first: a
    /// submodule that is not checked out cannot meaningfully be "changed" as well.
    /// </param>
    private sealed record SubmoduleRow(string Path, string Url, bool IsInitialised, bool HasChanges)
    {
        public string State => !IsInitialised
            ? Strings.Get("submodule.state.uninitialised")
            : HasChanges ? Strings.Get("submodule.state.changed")
            : string.Empty;

        public Brush StateBrush => (Brush)Application.Current.Resources[IsInitialised && HasChanges ? "Accent" : "TextMuted"];

        //A templated ListBoxItem has no text of its own, so UI Automation falls back to this. A
        //record's synthesised version would read every property *name* to a screen reader.
        public override string ToString() => $"{Path} {Url} {State}".TrimEnd();
    }
}
