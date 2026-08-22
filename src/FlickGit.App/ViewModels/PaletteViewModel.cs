using System.Collections.ObjectModel;
using FlickGit.Actions;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.App.Resident;
using FlickGit.App.Settings;
using FlickGit.Branches;
using FlickGit.Cli;
using FlickGit.Logging;
using FlickGit.Matching;
using FlickGit.Models;
using FlickGit.Palette;
using FlickGit.Tags;

namespace FlickGit.App.ViewModels;

/// <summary>
/// The repository palette: the way in when the user is not standing in the right folder in Explorer.
///
/// CLAUDE.md is specific about what makes this different from a command palette — it "opens on
/// repositories that have something to do, not on a command list". So the first thing on screen is a
/// list of repositories ordered by whether there is work in them, and an action is a second token
/// typed after one, never the other way round.
///
/// <b>It runs nothing itself.</b> Enter raises <see cref="ActionRequested"/> with a
/// <see cref="Verb"/>, and the composition root hands that to the same <c>VerbRunner</c> the CLI and
/// the context menu go through. CLAUDE.md: the palette is "not a shortcut around these rules", and
/// the only way to honour that is for it to have no second path to Git at all.
/// </summary>
public sealed class PaletteViewModel(
    ActionCatalog catalog,
    RepositoryOverviewCache overviews,
    BranchService branches,
    RecentRepositories recent,
    FlickSettings settings,
    ILog log) : ObservableObject
{
    private string _query = string.Empty;
    private PaletteRow? _selectedRow;
    private string? _hint;
    private string? _transient;

    /// <summary>
    /// The repository an action applies to, captured when the user typed the separator.
    ///
    /// Captured at that moment rather than re-derived per keystroke, so that arrowing down to the
    /// third repository and pressing space acts on the third repository — not on whichever one the
    /// filter text happens to rank first.
    /// </summary>
    private RepositoryOverview? _pinned;

    /// <summary>
    /// Branch completions for <see cref="_pinned"/>, read once on entering action mode.
    ///
    /// CLAUDE.md: "Repositories with thousands of refs must not be enumerated on every keystroke."
    /// Once per action-mode entry is the cheapest thing that still completes.
    /// </summary>
    private IReadOnlyList<string> _completions = [];

    /// <summary>
    /// Raised when the user has chosen something. The composition root runs it.
    ///
    /// A <see cref="GitAction"/> rather than a <see cref="Verb"/>: a user action from
    /// <c>actions.json</c> is a Git argument list or an external program, and only the built-ins have
    /// a verb at all. <c>ActionRunner</c> is what knows the difference.
    ///
    /// The third value is the action's second token when it declares one — the branch to switch to,
    /// the tag to create — and null when it does not. It is carried rather than dropped, which is
    /// what turned the branch completion from a list that looked like it did something into one that
    /// does: before <c>ActionRunner</c> could be handed an argument, choosing a branch here opened the
    /// picker with the branch thrown away.
    /// </summary>
    public event Action<GitAction, RepositoryInfo, string?>? ActionRequested;

    /// <summary>Raised when the palette is done and should close.</summary>
    public event Action? CloseRequested;

    public ObservableCollection<PaletteRow> Rows { get; } = [];

    public string Query
    {
        get => _query;
        set
        {
            //Read before the field changes: pinning has to see the selection as it was when the
            //separator was typed.
            bool hadSeparator = SeparatorIndex(_query) >= 0;

            if (!Set(ref _query, value))
                return;

            bool hasSeparator = SeparatorIndex(value) >= 0;

            if (hasSeparator && !hadSeparator)
                EnterActionMode();
            else if (!hasSeparator && hadSeparator)
                LeaveActionMode();

            Rebuild();
        }
    }

    public PaletteRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (Set(ref _selectedRow, value))
                Raise(nameof(FooterText));
        }
    }

    /// <summary>True while the palette is listing actions or completions rather than repositories.</summary>
    public bool IsActionMode => _pinned is not null;

    /// <summary>The pinned repository's name, shown as a chip while in action mode.</summary>
    public string ScopeText => _pinned?.Name ?? string.Empty;

    /// <summary>
    /// A sentence shown in place of the list when there is nothing in it.
    ///
    /// The empty state only, never a transient message: this is drawn over the list's own area, so
    /// anything shown here while there are rows is painted on top of them. What just happened goes in
    /// the footer instead. Always names a next step, because a palette that says only "nothing here"
    /// is a dead end.
    /// </summary>
    public string? Hint
    {
        get => _hint;
        private set
        {
            if (Set(ref _hint, value))
                Raise(nameof(HasHint));
        }
    }

    public bool HasHint => _hint is not null;

    /// <summary>
    /// The footer: the exact command Enter would run, or the key hints when nothing is selected.
    ///
    /// CLAUDE.md: "The exact command about to run is shown in the footer before Enter." The literal
    /// command line, not a paraphrase, so a user who wants to script the same thing can read it off
    /// the screen.
    /// </summary>
    public string FooterText =>
        //Whatever just happened outranks what would happen next: it is news, and it is gone as soon
        //as the user touches anything. Otherwise the selected row already knows its own command.
        _transient
        ?? (_selectedRow?.Command is { Length: > 0 } command ? command : Strings.Get("palette.hints"));

    /// <summary>
    /// What Enter does to a repository row. Commit, because that is what the user came for.
    ///
    /// Looked up rather than hard-coded, so hiding it in settings hides it here too instead of
    /// leaving a footer promising a command the catalog no longer offers.
    /// </summary>
    private GitAction? DefaultAction => catalog.ById("commit") is { Hidden: false } commit ? commit : null;

    /// <summary>
    /// The command line an action would run, for the footer.
    ///
    /// A built-in has a <c>flick</c> spelling and that is what is shown, because it is what the user
    /// could type themselves. A user action has none, so its actual command is shown instead --
    /// CLAUDE.md wants "the exact command about to run", and for a custom action that <i>is</i> the
    /// git or process line.
    /// </summary>
    private static string CommandLine(GitAction action, RepositoryOverview about, string? parameter)
    {
        //A built-in has a `flick` spelling and that is what is shown, because it is what the user
        //could type themselves.
        if (action.Cli is { Length: > 0 } cli)
        {
            return $"flick {cli} \"{about.Root}\""
                   + (parameter is { Length: > 0 } ? $" {parameter}" : string.Empty);
        }

        //A user action has no verb, so its actual command is shown -- expanded, because CLAUDE.md
        //wants "the exact command about to run" and `git fetch --prune {remote}` is not one.
        var context = new ActionContext(about.Repository, about.Branch);

        return ActionPlaceholders.Expand(action.Run, context).Describe();
    }

    /// <summary>
    /// Fills the list from the cache, without awaiting anything.
    ///
    /// This is what the 80 ms budget buys: the palette paints the previous snapshot immediately and
    /// <see cref="RefreshAsync"/> replaces it when Git has answered. CLAUDE.md: "Never wait on a
    /// `git` process before showing."
    /// </summary>
    public void Reset()
    {
        _query = string.Empty;
        _pinned = null;
        _completions = [];

        Raise(nameof(Query));
        Raise(nameof(IsActionMode));
        Raise(nameof(ScopeText));

        Rebuild();
    }

    /// <summary>Re-reads every repository, then re-renders in place.</summary>
    public async Task RefreshAsync()
    {
        if (!overviews.IsStale)
            return;

        try
        {
            await overviews
                .RefreshAsync(settings.PaletteScanRoots, recent.Paths, CancellationToken.None)
                .ConfigureAwait(true);

            Rebuild();
        }
        catch (Exception ex)
        {
            //The palette keeps whatever it was showing. A stale list is more use than an empty one.
            log.Debug($"Palette refresh failed: {ex.Message}");
        }
    }

    /// <summary>Enter: run what is selected.</summary>
    public void Accept()
    {
        if (_selectedRow is null)
            return;

        if (_selectedRow.Repository is { } repository)
        {
            if (DefaultAction is { } commit)
                Run(commit, repository.Repository);

            return;
        }

        if (_pinned is null)
            return;

        if (_selectedRow.Action is { } action)
        {
            //An action needing an argument is not runnable without one. Choosing it types it into
            //the query instead, which brings up the completions -- so Enter always advances.
            if (action.Parameter != ActionParameter.None)
            {
                Query = $"{QueryBeforeSeparator()} {action.Cli} ";
                return;
            }

            Run(action, _pinned.Repository);
            return;
        }

        //A completion row: the action was chosen a keystroke ago and this row is its argument.
        if (_selectedRow.Parameter is { Length: > 0 } parameter && CurrentAction is { } pending)
            Run(pending, _pinned.Repository, parameter);
    }

    /// <summary>
    /// Ctrl+Enter: pull --rebase every repository that is behind.
    ///
    /// The one bulk operation the palette offers, and the footer advertises it. Safe in bulk because
    /// <c>pull --rebase --autostash</c> refuses rather than discarding: a repository that cannot be
    /// rebased cleanly is reported, never forced.
    /// </summary>
    public void PullAllBehind()
    {
        var behind = overviews.Snapshot.Where(o => o.Behind > 0).ToList();

        if (behind.Count == 0)
        {
            _transient = Strings.Get("palette.nothingbehind");
            Raise(nameof(FooterText));
            return;
        }

        if (catalog.ById("pull-rebase-autostash") is not { } pull)
            return;

        foreach (RepositoryOverview overview in behind)
            ActionRequested?.Invoke(pull, overview.Repository, null);

        CloseRequested?.Invoke();
    }

    /// <summary>Backspace with the action text empty: back to the repository list.</summary>
    /// <returns>False when there was nothing to leave, so the key means what it usually means.</returns>
    public bool LeaveActionModeIfEmpty()
    {
        if (_pinned is null || ActionText().Length > 0)
            return false;

        Query = QueryBeforeSeparator();
        return true;
    }

    public void Cancel() => CloseRequested?.Invoke();

    /// <summary>The action the typed token names exactly, when it names one.</summary>
    private GitAction? CurrentAction
    {
        get
        {
            string[] tokens = ActionText().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return tokens.Length == 0
                ? null
                : Available().FirstOrDefault(a =>
                    string.Equals(a.Cli, tokens[0], StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// The actions offered for the pinned repository.
    ///
    /// No requirement filtering: the palette only ever pins something it found by locating a
    /// <c>.git</c>, so every action's one requirement is already satisfied.
    /// </summary>
    private IReadOnlyList<GitAction> Available() =>
        _pinned is null ? [] : catalog.For(ActionSurfaces.Palette);

    private void Run(GitAction action, RepositoryInfo repository, string? argument = null)
    {
        ActionRequested?.Invoke(action, repository, argument);
        CloseRequested?.Invoke();
    }

    private void EnterActionMode()
    {
        _pinned = PinFor(QueryBeforeSeparator());

        Raise(nameof(IsActionMode));
        Raise(nameof(ScopeText));

        if (_pinned is not null)
            _ = LoadCompletionsAsync(_pinned);
    }

    /// <summary>
    /// Which repository an action typed after <paramref name="prefix"/> applies to.
    ///
    /// Normally the highlighted row, so that arrowing down to the third repository and pressing space
    /// acts on the third repository. But only if that row still <i>matches</i> the prefix: text can
    /// arrive faster than a keystroke at a time — pasted, or set by a test — and then the highlight
    /// belongs to a filter that was never applied and naming it would act on the wrong repository.
    /// </summary>
    private RepositoryOverview? PinFor(string prefix)
    {
        if (_selectedRow?.Repository is { } highlighted
            && FuzzyMatcher.Score(highlighted.Name, prefix) is not null)
        {
            return highlighted;
        }

        return FuzzyMatcher.Rank(overviews.Snapshot.Select(o => o.Name), prefix) is [{ } best, ..]
            ? overviews.Snapshot.FirstOrDefault(o => o.Name == best.Value)
            : null;
    }

    private void LeaveActionMode()
    {
        _pinned = null;
        _completions = [];

        Raise(nameof(IsActionMode));
        Raise(nameof(ScopeText));
    }

    private async Task LoadCompletionsAsync(RepositoryOverview repository)
    {
        try
        {
            _completions = await branches
                .ListLocalBranchesAsync(repository.Repository, repository.Branch, CancellationToken.None)
                .ConfigureAwait(true);

            //Only re-render if the user is still looking at this repository's completions.
            if (ReferenceEquals(_pinned, repository))
                Rebuild();
        }
        catch (Exception ex)
        {
            //Completion is a convenience. Typing the branch name still works without it.
            log.Debug($"Branch completion failed for {repository.Root}: {ex.Message}");
        }
    }

    private void Rebuild()
    {
        _transient = null;
        Rows.Clear();

        if (_pinned is null)
            BuildRepositoryRows();
        else
            BuildActionRows();

        //Reset rather than preserved: the list reorders as the pattern changes, so keeping an index
        //would move the highlight to an unrelated row. The top row is always the best match.
        SelectedRow = Rows.FirstOrDefault();

        Hint = Rows.Count > 0
            ? null
            : _pinned is null && overviews.Snapshot.Count == 0
                ? Strings.Get("palette.empty.norepos", FlickSettings.FilePath)
                : Strings.Get("palette.empty.nomatch");
    }

    private void BuildRepositoryRows()
    {
        //Grouped by name rather than keyed by it: two clones can share a directory name, and a
        //dictionary would silently drop one of them off the list.
        var byName = new Dictionary<string, List<RepositoryOverview>>(StringComparer.OrdinalIgnoreCase);

        foreach (RepositoryOverview overview in overviews.Snapshot)
        {
            if (!byName.TryGetValue(overview.Name, out List<RepositoryOverview>? group))
                byName[overview.Name] = group = [];

            group.Add(overview);
        }

        IReadOnlyList<string> mru = recent.Paths;

        //MRU rank folded into the score by the matcher, which is what CLAUDE.md asks for: "scored by
        //contiguity, word-boundary hits and MRU rank". A group takes its best member's rank.
        IReadOnlyList<FuzzyMatch> ranked = FuzzyMatcher.Rank(
            byName.Keys,
            _query,
            name => byName[name].Min(o => IndexOfRoot(mru, o.Root)));

        IEnumerable<RepositoryOverview> ordered = ranked.SelectMany(m => byName[m.Value]);

        //With nothing typed the ordering rule is the product's, not the matcher's: repositories with
        //something to do come first. Once there is a pattern the best match wins outright -- someone
        //who typed a name wants that repository whether or not it is clean.
        if (_query.Length == 0)
            ordered = ordered.OrderByDescending(o => o.HasWork);

        foreach (RepositoryOverview overview in ordered)
            Rows.Add(ToRow(overview));
    }

    private void BuildActionRows()
    {
        string actionText = ActionText();
        string[] tokens = actionText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        //A complete action name followed by a space means the user has moved on to its argument.
        bool choosingParameter = tokens.Length > 1 || actionText.EndsWith(' ');

        if (choosingParameter && CurrentAction is { Parameter: ActionParameter.Branch } action)
        {
            string pattern = tokens.Length > 1 ? tokens[1] : string.Empty;

            foreach (FuzzyMatch match in FuzzyMatcher.Rank(_completions, pattern))
            {
                Rows.Add(new PaletteRow(
                    match.Value,
                    action.Label,
                    string.Empty,
                    CommandLine(action, _pinned!, match.Value),
                    Parameter: match.Value));
            }

            return;
        }

        if (choosingParameter && CurrentAction is { Parameter: ActionParameter.Tag } tagAction)
        {
            BuildNewTagRow(tagAction, tokens.Length > 1 ? tokens[1] : string.Empty);
            return;
        }

        string actionPattern = tokens.Length > 0 ? tokens[0] : string.Empty;

        //Keyed by the search text: the CLI spelling for a built-in, the label for a user action that
        //has no verb. Both are what someone would actually type looking for it.
        var byKey = new Dictionary<string, GitAction>(StringComparer.OrdinalIgnoreCase);

        foreach (GitAction candidate in Available())
            byKey[candidate.Cli ?? candidate.Label] = candidate;

        IEnumerable<GitAction> ordered = FuzzyMatcher
            .Rank(byKey.Keys, actionPattern)
            .Select(m => byKey[m.Value]);

        //With nothing typed, catalog order rather than the matcher's. Every candidate scores the same
        //against an empty pattern, so the matcher falls back to alphabetical -- which put "commit"
        //first only by luck of the alphabet.
        if (actionPattern.Length == 0)
            ordered = ordered.OrderBy(a => a.MenuOrder);

        foreach (GitAction candidate in ordered)
        {
            //The command rather than the id in the detail column: a user action's id says nothing,
            //and the command is what CLAUDE.md wants visible.
            Rows.Add(new PaletteRow(
                candidate.Label,
                candidate.Cli ?? candidate.Run.Describe(),
                candidate.RequiresConfirmation ? Strings.Get("palette.confirms") : string.Empty,
                CommandLine(candidate, _pinned!, null),
                Action: candidate));
        }
    }

    /// <summary>
    /// The single row for a tag name being typed.
    ///
    /// <b>Always exactly one row, runnable or not.</b> There is nothing to complete against — see
    /// <see cref="ActionParameter.Tag"/> — so this is the branch ComboBox's inline resolution rather
    /// than a completion list: the row says what will happen to what was typed, and only carries a
    /// <c>Parameter</c> when there is something Enter may actually do. A row with none is a row Enter
    /// ignores, which is how an invalid name is refused before any Git command runs.
    /// </summary>
    private void BuildNewTagRow(GitAction action, string typed)
    {
        if (typed.Length == 0)
        {
            Rows.Add(new PaletteRow(Strings.Get("palette.tag.prompt"), action.Label, string.Empty));
            return;
        }

        if (!TagService.LooksValid(typed))
        {
            Rows.Add(new PaletteRow(typed, Strings.Get("tag.invalid"), string.Empty));
            return;
        }

        Rows.Add(new PaletteRow(
            typed,
            Strings.Get("palette.tag.willcreate"),
            string.Empty,
            CommandLine(action, _pinned!, typed),
            Parameter: typed));
    }

    private PaletteRow ToRow(RepositoryOverview overview)
    {
        string detail =
            overview.Failed ? Strings.Get("palette.unreadable")
            : overview.Changed > 0 ? Strings.Get("palette.modified", overview.Changed)
            : overview.Untracked > 0 ? Strings.Get("palette.untrackedonly", overview.Untracked)
            : Strings.Get("palette.clean");

        //Only the non-zero half, so a repository that is merely ahead does not carry a "↓0" the user
        //has to read past.
        string trailing = string.Join(
            ' ',
            new[]
            {
                overview.Ahead > 0 ? $"↑{overview.Ahead}" : null,
                overview.Behind > 0 ? $"↓{overview.Behind}" : null,
            }.Where(s => s is not null));

        //Enter on a repository row commits, so that is the command it advertises.
        string command = DefaultAction is { } commit
            ? CommandLine(commit, overview, null)
            : overview.Root;

        return new PaletteRow(overview.Name, detail, trailing, command, overview.HasWork, Repository: overview);
    }

    /// <summary>The text after the separator: the action and its argument.</summary>
    private string ActionText()
    {
        int separator = SeparatorIndex(_query);
        return separator < 0 ? string.Empty : _query[(separator + 1)..];
    }

    private string QueryBeforeSeparator()
    {
        int separator = SeparatorIndex(_query);
        return separator < 0 ? _query : _query[..separator];
    }

    /// <summary>
    /// Where the repository filter ends and the action begins.
    ///
    /// CLAUDE.md: "a space or <c>&gt;</c> switches to action mode". Both, because a space is what
    /// people type and <c>&gt;</c> is what the prompt shows.
    /// </summary>
    private static int SeparatorIndex(string query) => query.IndexOfAny([' ', '>']);

    private static int IndexOfRoot(IReadOnlyList<string> mru, string root)
    {
        for (int i = 0; i < mru.Count; i++)
        {
            if (string.Equals(mru[i], root, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return mru.Count;
    }
}
