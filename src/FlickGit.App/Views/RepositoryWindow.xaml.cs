using System.Windows;
using FlickGit.App.Localization;
using FlickGit.Config;
using FlickGit.Models;
using FlickGit.Remotes;

namespace FlickGit.App.Views;

/// <summary>
/// The repository's own settings: the identity it commits as, its remotes, and the two preferences
/// FlickGit keeps per repository.
///
/// A window per repository rather than a tab in the settings window, because everything here is a
/// fact about one repository and <c>flick settings</c> is opened with no path at all — that window
/// would have had to grow a repository picker before it could show a single value.
///
/// <b>Two save rules, on purpose.</b> A remote edit applies the moment its button is pressed, the way
/// creating a tag does: each one is a single Git command with its own button and its own confirmation
/// where it needs one. The identity and the FlickGit defaults apply on Save, the way the settings
/// window's do, because they are a form rather than a list of commands. The footer says which is
/// which, so neither is a surprise.
///
/// Nothing here touches the network, and nothing here touches global or system config. A URL is not
/// checked for reachability — the next push answers that in Git's own words — and an identity set
/// for every repository on the machine is <c>git config --global</c>'s business, not a repository
/// window's.
/// </summary>
public partial class RepositoryWindow : Window
{
    private readonly RepositoryInfo _repository;
    private readonly RepositoryConfigService _config;
    private readonly RemoteService _remotes;

    private readonly List<GitRemote> _configured = [];

    /// <summary>What the last read said. Null until <see cref="LoadAsync"/> has run once.</summary>
    private RepositoryConfig? _current;

    /// <summary>
    /// True while <see cref="LoadAsync"/> is populating.
    ///
    /// Ticking a radio raises Checked whoever did it, and the handler moves the caret into the name
    /// box -- which is right when the user chose it and wrong on open, where it would make the
    /// starting focus depend on whether this repository happens to have an identity of its own.
    /// </summary>
    private bool _loading;

    public RepositoryWindow(RepositoryInfo repository, RepositoryConfigService config, RemoteService remotes)
    {
        InitializeComponent();

        _repository = repository;
        _config = config;
        _remotes = remotes;

        Title = Strings.Get("repo.title", repository.Name);
        RepositoryName.Text = repository.Name;
        RepositoryRoot.Text = repository.Root;

        IdentitySection.Text = Strings.Get("repo.section.identity");
        RepoIdentityRadio.Content = Strings.Get("repo.identity.local");
        NameLabel.Text = Strings.Get("repo.name");
        EmailLabel.Text = Strings.Get("repo.email");

        RemotesSection.Text = Strings.Get("repo.section.remotes");
        RemoteNameLabel.Text = Strings.Get("repo.remote.name");
        RemoteUrlLabel.Text = Strings.Get("repo.remote.url");
        AddRemoteButton.Content = Strings.Get("repo.remote.add");
        SaveRemoteButton.Content = Strings.Get("repo.remote.save");
        RemoveRemoteButton.Content = Strings.Get("repo.remote.remove");
        RemoteHint.Text = Strings.Get("repo.remote.hint");

        DefaultsSection.Text = Strings.Get("repo.section.defaults");
        PrimaryBranchLabel.Text = Strings.Get("repo.primarybranch");
        PrimaryBranchHint.Text = Strings.Get("repo.primarybranch.hint");
        AskAgainButton.Content = Strings.Get("repo.upstream.askagain");

        SaveButton.Content = Strings.Get("repo.save");
        CloseButton.Content = Strings.Get("common.close");

        Loaded += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        RepositoryConfig config = await _config.ReadAsync(_repository, CancellationToken.None).ConfigureAwait(true);
        _current = config;
        _loading = true;

        try
        {
            Populate(config);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Populate(RepositoryConfig config)
    {

        //The inherited value goes in the label, so "use the global identity" says what that identity
        //is rather than asking the user to trust that one exists.
        GlobalIdentityRadio.Content = config.EffectiveName is null && config.EffectiveEmail is null
            ? Strings.Get("repo.identity.global.none")
            : Strings.Get("repo.identity.global", Describe(config.EffectiveName, config.EffectiveEmail));

        //Prefilled from whatever is in force, so switching to a repository identity starts from the
        //one being replaced rather than from two empty boxes.
        NameBox.Text = config.LocalName ?? config.EffectiveName ?? string.Empty;
        EmailBox.Text = config.LocalEmail ?? config.EffectiveEmail ?? string.Empty;

        if (config.HasLocalIdentity)
            RepoIdentityRadio.IsChecked = true;
        else
            GlobalIdentityRadio.IsChecked = true;

        _configured.Clear();
        _configured.AddRange(config.Remotes);
        RemoteList.ItemsSource = _configured.Select(remote => Row(remote, config.TrackedRemote)).ToList();

        RemoteNameBox.Clear();
        RemoteUrlBox.Clear();

        PrimaryBranchBox.Text = config.PrimaryBranch ?? string.Empty;
        UpstreamAnswerText.Text = config.AllowUpstreamCreation switch
        {
            true => Strings.Get("repo.upstream.allowed"),
            false => Strings.Get("repo.upstream.refused"),
            null => Strings.Get("repo.upstream.unasked"),
        };

        //Nothing to reset when it was never asked, and a button that does nothing is worse than one
        //that is not there.
        AskAgainButton.IsEnabled = config.AllowUpstreamCreation is not null;

        StatusText.Text = _configured.Count == 0
            ? Strings.Get("repo.remote.none")
            : config.CurrentBranch is null ? Strings.Get("repo.detached") : string.Empty;

        UpdateRemoteButtons();
    }

    private void OnIdentityScopeChanged(object sender, RoutedEventArgs e)
    {
        bool local = RepoIdentityRadio.IsChecked == true;

        IdentityPanel.IsEnabled = local;

        if (local && !_loading)
            NameBox.Focus();
    }

    private void OnRemoteSelected(object sender, RoutedEventArgs e)
    {
        if (Selected is { } remote)
        {
            RemoteNameBox.Text = remote.Name;
            RemoteUrlBox.Text = remote.FetchUrl;
        }

        UpdateRemoteButtons();
    }

    private void OnRemoteFieldChanged(object sender, RoutedEventArgs e) => UpdateRemoteButtons();

    /// <summary>
    /// Which of the three remote buttons can do anything, from the two boxes and the selection.
    ///
    /// Add and Save are mutually exclusive by construction: a name that already exists cannot be
    /// added, and a name that does not exist cannot be renamed from a row that is not selected.
    /// </summary>
    private void UpdateRemoteButtons()
    {
        string name = RemoteNameBox.Text.Trim();
        string url = RemoteUrlBox.Text.Trim();
        bool filled = name.Length > 0 && url.Length > 0;
        GitRemote? selected = Selected;

        AddRemoteButton.IsEnabled = filled && !Exists(name);

        SaveRemoteButton.IsEnabled = filled
            && selected is not null
            && (!string.Equals(name, selected.Name, StringComparison.Ordinal)
                || !string.Equals(url, selected.FetchUrl, StringComparison.Ordinal));

        RemoveRemoteButton.IsEnabled = selected is not null;
    }

    private async void OnAddRemote(object sender, RoutedEventArgs e)
    {
        string name = RemoteNameBox.Text.Trim();
        string url = RemoteUrlBox.Text.Trim();

        SetBusy(true);

        try
        {
            ConfigOutcome outcome = await _remotes
                .AddAsync(_repository, name, url, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                Report(Strings.Get("repo.remote.add"), outcome);
                return;
            }

            await LoadAsync().ConfigureAwait(true);
            StatusText.Text = Strings.Get("repo.remote.added", name);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Renames, re-points, or both — in that order.
    ///
    /// The rename goes first because <c>set-url</c> takes the name: doing it the other way round
    /// would point the old name at the new URL and then rename it, which works, and then does not
    /// when the rename fails and leaves a remote nobody asked for pointing somewhere new.
    /// </summary>
    private async void OnSaveRemote(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected)
            return;

        string name = RemoteNameBox.Text.Trim();
        string url = RemoteUrlBox.Text.Trim();

        SetBusy(true);

        try
        {
            if (!string.Equals(name, selected.Name, StringComparison.Ordinal))
            {
                ConfigOutcome renamed = await _remotes
                    .RenameAsync(_repository, selected.Name, name, CancellationToken.None)
                    .ConfigureAwait(true);

                if (!renamed.Succeeded)
                {
                    Report(Strings.Get("repo.remote.save"), renamed);
                    return;
                }

                StatusText.Text = Strings.Get("repo.remote.renamed", selected.Name, name);
            }

            if (!string.Equals(url, selected.FetchUrl, StringComparison.Ordinal))
            {
                ConfigOutcome pointed = await _remotes
                    .SetUrlAsync(_repository, name, url, CancellationToken.None)
                    .ConfigureAwait(true);

                if (!pointed.Succeeded)
                {
                    Report(Strings.Get("repo.remote.save"), pointed);
                    return;
                }

                StatusText.Text = Strings.Get("repo.remote.urlset", name, url);
            }

            string said = StatusText.Text;
            await LoadAsync().ConfigureAwait(true);
            StatusText.Text = said;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRemoveRemote(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected)
            return;

        //Asked first. Nothing in the working tree is touched and no commit is lost, but the
        //remote-tracking branches go with it and a branch that tracked it comes back with no
        //upstream — which is more than the one row the user is looking at.
        bool confirmed = ConfirmWindow.Ask(
            this,
            Strings.Get("repo.remote.confirm.title"),
            Strings.Get("repo.remote.confirm", selected.Name),
            Strings.Get("repo.remote.confirm.yes"),
            Strings.Get("common.cancel"));

        if (!confirmed)
            return;

        SetBusy(true);

        try
        {
            ConfigOutcome outcome = await _remotes
                .RemoveAsync(_repository, selected.Name, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                Report(Strings.Get("repo.remote.remove"), outcome);
                return;
            }

            await LoadAsync().ConfigureAwait(true);
            StatusText.Text = Strings.Get("repo.remote.removed", selected.Name);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Forgets the remembered upstream answer. Immediate: it is a reset, not an edit.</summary>
    private async void OnAskAgain(object sender, RoutedEventArgs e)
    {
        SetBusy(true);

        try
        {
            ConfigOutcome outcome = await _config
                .UnsetAsync(_repository, RepositoryConfigService.UpstreamAnswerKey, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                Report(Strings.Get("repo.upstream.askagain"), outcome);
                return;
            }

            await LoadAsync().ConfigureAwait(true);
            StatusText.Text = Strings.Get("repo.upstream.reset");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// The identity and the FlickGit defaults, together.
    ///
    /// Stays open and says why on any failure, closes on success — the settings window's rule. The
    /// order is the order the user reads them in, so a failure names the first thing that did not
    /// happen rather than the last.
    /// </summary>
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        string title = Strings.Get("repo.section.identity");
        bool local = RepoIdentityRadio.IsChecked == true;
        string name = NameBox.Text.Trim();
        string email = EmailBox.Text.Trim();

        if (local && (name.Length == 0 || email.Length == 0))
        {
            //Both or neither: a commit carries an author line that needs the pair, and half of one
            //set here would silently inherit the other half from somewhere else.
            StatusText.Text = Strings.Get("repo.identity.needed");
            return;
        }

        SetBusy(true);

        try
        {
            ConfigOutcome identity = local
                ? await WritePairAsync(name, email).ConfigureAwait(true)
                : await ClearPairAsync().ConfigureAwait(true);

            if (!identity.Succeeded)
            {
                Report(title, identity);
                return;
            }

            string primary = PrimaryBranchBox.Text.Trim();

            ConfigOutcome branch = primary.Length == 0
                ? await _config
                    .UnsetAsync(_repository, RepositoryConfigService.PrimaryBranchKey, CancellationToken.None)
                    .ConfigureAwait(true)
                : await _config
                    .WriteAsync(_repository, RepositoryConfigService.PrimaryBranchKey, primary, CancellationToken.None)
                    .ConfigureAwait(true);

            if (!branch.Succeeded)
            {
                Report(Strings.Get("repo.primarybranch"), branch);
                return;
            }

            Close();
        }
        finally
        {
            //Reached when a failure returned early; harmless after Close.
            SetBusy(false);
        }
    }

    private async Task<ConfigOutcome> WritePairAsync(string name, string email)
    {
        ConfigOutcome written = await _config
            .WriteAsync(_repository, RepositoryConfigService.UserNameKey, name, CancellationToken.None)
            .ConfigureAwait(true);

        return written.Succeeded
            ? await _config
                .WriteAsync(_repository, RepositoryConfigService.UserEmailKey, email, CancellationToken.None)
                .ConfigureAwait(true)
            : written;
    }

    private async Task<ConfigOutcome> ClearPairAsync()
    {
        ConfigOutcome cleared = await _config
            .UnsetAsync(_repository, RepositoryConfigService.UserNameKey, CancellationToken.None)
            .ConfigureAwait(true);

        return cleared.Succeeded
            ? await _config
                .UnsetAsync(_repository, RepositoryConfigService.UserEmailKey, CancellationToken.None)
                .ConfigureAwait(true)
            : cleared;
    }

    private GitRemote? Selected =>
        RemoteList.SelectedItem is RemoteRow row
            ? _configured.FirstOrDefault(remote => string.Equals(remote.Name, row.Name, StringComparison.Ordinal))
            : null;

    private bool Exists(string name) =>
        _configured.Any(remote => string.Equals(remote.Name, name, StringComparison.Ordinal));

    /// <summary>Git's own words, never paraphrased — CLAUDE.md, "Error Handling".</summary>
    private void Report(string title, ConfigOutcome outcome) =>
        new NoticeWindow(title, outcome.GitError ?? string.Empty, compact: false) { Owner = this }.ShowDialog();

    private void SetBusy(bool busy)
    {
        RemoteList.IsEnabled = !busy;
        RemoteNameBox.IsEnabled = !busy;
        RemoteUrlBox.IsEnabled = !busy;
        PrimaryBranchBox.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
        GlobalIdentityRadio.IsEnabled = !busy;
        RepoIdentityRadio.IsEnabled = !busy;
        IdentityPanel.IsEnabled = !busy && RepoIdentityRadio.IsChecked == true;

        if (busy)
        {
            AddRemoteButton.IsEnabled = false;
            SaveRemoteButton.IsEnabled = false;
            RemoveRemoteButton.IsEnabled = false;
            AskAgainButton.IsEnabled = false;
            return;
        }

        //Re-derived rather than restored. The command that just ran is very likely to have changed
        //both things these buttons depend on — which remotes exist, and which row is selected — so
        //putting them back the way they were is how a Remove button survives the removal.
        AskAgainButton.IsEnabled = _current?.AllowUpstreamCreation is not null;
        UpdateRemoteButtons();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static string Describe(string? name, string? email) =>
        (name, email) switch
        {
            ({ } who, { } address) => $"{who} <{address}>",
            ({ } who, null) => who,
            (null, { } address) => address,
            _ => string.Empty,
        };

    private static RemoteRow Row(GitRemote remote, string? trackedRemote)
    {
        var notes = new List<string>();

        if (string.Equals(remote.Name, trackedRemote, StringComparison.Ordinal))
            notes.Add(Strings.Get("repo.remote.tracked"));

        if (remote.PushUrl is { } pushUrl)
            notes.Add(Strings.Get("repo.remote.push", pushUrl));

        return new RemoteRow(remote.Name, remote.FetchUrl, string.Join(" · ", notes));
    }

    /// <summary>
    /// One row in the list.
    ///
    /// <see cref="ToString"/> is overridden for the reason the tag window's is: a `ListBoxItem` whose
    /// content is a `DataTemplate` has no text of its own, so UI Automation falls back to it, and a
    /// record's synthesised version reads every property name out to a screen reader.
    /// </summary>
    private sealed record RemoteRow(string Name, string Url, string Note)
    {
        public override string ToString() => $"{Name} {Url} {Note}".TrimEnd();
    }
}
