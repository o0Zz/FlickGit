using System.Diagnostics;
using System.Text;
using System.Windows;
using FlickGit.Ai;
using FlickGit.App.Ai;
using FlickGit.App.CommandLine;
using FlickGit.App.Localization;
using FlickGit.App.Resident;
using FlickGit.App.Settings;
using FlickGit.Commits;
using FlickGit.Forges;
using FlickGit.History;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Remotes;
using FlickGit.Status;

namespace FlickGit.App.Views;

/// <summary>
/// Propose the current branch, on GitHub, GitLab or Azure DevOps.
///
/// Thin on purpose. Which forge, which target and what is in the branch come from
/// <see cref="PullRequestService"/>; the order the network is spoken to in is
/// <see cref="PullRequestFlow"/>; the description is <see cref="AiTextService"/>. What is left is
/// presentation and one rule that genuinely belongs to a window: <b>never overwrite what the user
/// has typed.</b>
/// </summary>
public partial class PullRequestWindow : Window
{
    private readonly RepositoryInfo _repository;
    private readonly PullRequestService _pullRequests;
    private readonly PullRequestFlow _flow;
    private readonly ForgeCredentials _credentials;
    private readonly AiTextService _ai;
    private readonly StatusService _status;
    private readonly UpstreamConsent _consent;
    private readonly Notifier _notifier;
    private readonly FlickSettings _settings;
    private readonly ILog _log;

    private PullRequestPlan? _plan;
    private PullRequestSummary _summary = PullRequestSummary.Empty;
    private RepositoryStatus? _state;
    private PullRequestRef? _existing;

    private CancellationTokenSource? _generation;

    /// <summary>
    /// The target the summary and the description were built for, so a ComboBox event that changed
    /// nothing does not re-run three Git commands and a generation.
    /// </summary>
    private string _loadedTarget = string.Empty;

    /// <summary>True while this code is writing into the boxes, so it does not read that as typing.</summary>
    private bool _applying;

    /// <summary>Set the moment the user edits. The AI never wins after that.</summary>
    private bool _edited;

    private bool _busy;

    public PullRequestWindow(
        RepositoryInfo repository,
        PullRequestService pullRequests,
        PullRequestFlow flow,
        ForgeCredentials credentials,
        AiTextService ai,
        StatusService status,
        UpstreamConsent consent,
        Notifier notifier,
        FlickSettings settings,
        ILog log)
    {
        InitializeComponent();

        _repository = repository;
        _pullRequests = pullRequests;
        _flow = flow;
        _credentials = credentials;
        _ai = ai;
        _status = status;
        _consent = consent;
        _notifier = notifier;
        _settings = settings;
        _log = log;

        Title = Strings.Get("pr.title", repository.Name);
        TitleLabel.Text = Strings.Get("pr.field.title");
        DescriptionLabel.Text = Strings.Get("pr.field.description");
        DraftCheck.Content = Strings.Get("pr.draft");
        GenerateButton.Content = Strings.Get("pr.generate");
        CreateButton.Content = Strings.Get("pr.create");
        CancelButton.Content = Strings.Get("commit.button.cancel");
        OpenButton.Content = Strings.Get("pr.open");
        StatusText.Text = Strings.Get("pr.hint");

        //Nothing is proposable until the plan says so, so a window that is still resolving cannot have
        //Create pressed against a forge it has not identified.
        CreateButton.IsEnabled = false;
        GenerateButton.Visibility = Visibility.Collapsed;

        TargetBox.SelectionChanged += async (_, _) => await TargetChangedAsync().ConfigureAwait(true);
        TargetBox.LostFocus += async (_, _) => await TargetChangedAsync().ConfigureAwait(true);
        DescriptionBox.TextChanged += (_, _) => NoteEdit();
    }

    /// <summary>
    /// Resolves the forge and the branches, then fills the window in. Awaited by the verb after the
    /// window is on screen: "visible" and "usable" are two budgets.
    /// </summary>
    public async Task LoadAsync()
    {
        _state = await _status.GetStatusAsync(_repository, CancellationToken.None).ConfigureAwait(true);

        _plan = await _pullRequests
            .PlanAsync(_repository, _state, _settings.PrimaryBranch, CancellationToken.None)
            .ConfigureAwait(true);

        if (!_plan.CanPropose || _plan.Forge is not { } forge)
        {
            Refuse(_plan.Refusal ?? Strings.Get("error.title"));
            return;
        }

        SourceText.Text = _plan.SourceBranch;
        ForgeText.Text = $"{forge.Kind} · {forge.Display}";

        //GitHub has no per-request "delete the branch on merge" -- it is a repository setting there, so
        //the checkbox is absent rather than present and ignored.
        DeleteBranchCheck.Visibility = forge.Kind == ForgeKind.GitHub ? Visibility.Collapsed : Visibility.Visible;
        DeleteBranchCheck.Content = Strings.Get("pr.deletebranch", _plan.SourceBranch);

        _applying = true;
        TargetBox.ItemsSource = _plan.TargetCandidates;
        TargetBox.Text = _plan.TargetBranch;
        _applying = false;

        GenerateButton.Visibility = _ai.IsUsable ? Visibility.Visible : Visibility.Collapsed;
        CreateButton.IsEnabled = true;

        await ReloadTargetAsync(_plan.TargetBranch).ConfigureAwait(true);

        //The caret in the title box: it is the one field that must not be empty, and the AI is about to
        //fill it in.
        TitleBox.Focus();
    }

    private string Target => TargetBox.Text.Trim();

    private async Task TargetChangedAsync()
    {
        if (_applying || _busy || _plan is not { CanPropose: true })
            return;

        string target = Target;

        if (target.Length == 0 || target == _loadedTarget)
            return;

        await ReloadTargetAsync(target).ConfigureAwait(true);
    }

    /// <summary>
    /// Everything that depends on the target: the summary, the duplicate check, and the description.
    /// All three, because all three are answers about a specific pair of branches -- a description
    /// written for <c>main</c> is the wrong description for <c>develop</c>.
    /// </summary>
    private async Task ReloadTargetAsync(string target)
    {
        if (_plan is not { Forge: { } forge } plan)
            return;

        _loadedTarget = target;
        _existing = null;
        HideNotice();

        _summary = await _pullRequests
            .SummariseAsync(_repository, plan.Remote, target, CancellationToken.None)
            .ConfigureAwait(true);

        SummaryText.Text = _summary.Commits.Count == 0
            ? Strings.Get("pr.summary.empty", plan.SourceBranch, $"{plan.Remote}/{target}")
            : Strings.Get(
                "pr.summary",
                _summary.Commits.Count,
                _summary.Files.Count,
                _summary.Added,
                _summary.Removed);

        Prefill();

        //Both fire and forget, both harmless if they never finish. Awaiting the generation here would
        //lose the point of the window: `LoadAsync` is what the verb times as "populated", so a second of
        //model latency would become a second before the window could be typed in, for text the user is
        //free to ignore.
        _ = LookForExistingAsync(forge, plan.SourceBranch, target);

        if (_ai.IsUsable && !_edited)
            _ = GenerateAsync();
    }

    /// <summary>
    /// A title and description written from the commits, before any model is asked. Not a
    /// placeholder: one commit's subject is a perfectly good title. The AI is an accelerator, never a
    /// dependency, so the window has to be useful with it switched off.
    /// </summary>
    private void Prefill()
    {
        if (_edited || _plan is null)
            return;

        _applying = true;

        try
        {
            TitleBox.Text = _summary.Commits.Count == 1
                ? _summary.Commits[0].Subject
                : Humanise(_plan.SourceBranch);

            DescriptionBox.Text = _summary.Commits.Count > 1
                ? BulletList(_summary.Commits)
                : string.Empty;
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>
    /// <c>feature/storage-gw</c> becomes <c>Storage gw</c>. A last resort rather than a good title:
    /// an empty box with a disabled Create button says less about what to do than a mediocre
    /// suggestion the user will immediately improve.
    /// </summary>
    private static string Humanise(string branch)
    {
        string last = branch[(branch.LastIndexOf('/') + 1)..].Replace('-', ' ').Replace('_', ' ').Trim();

        return last.Length == 0 ? branch : char.ToUpperInvariant(last[0]) + last[1..];
    }

    private static string BulletList(IReadOnlyList<LogCommit> commits)
    {
        var text = new StringBuilder();

        for (int i = commits.Count - 1; i >= 0; i--)
            text.Append("- ").Append(commits[i].Subject).Append('\n');

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Asks the forge whether this branch already has a request open, using a credential already on
    /// the machine.
    ///
    /// <b>It never prompts.</b> Demanding a token for a check the user did not ask for would be the
    /// wrong first impression of the feature -- so with nothing stored this simply does not run, and
    /// the duplicate is caught by the create instead.
    /// </summary>
    private async Task LookForExistingAsync(ForgeRepository forge, string source, string target)
    {
        try
        {
            if (await _credentials.FindAsync(_repository, forge, CancellationToken.None).ConfigureAwait(true)
                is not { Length: > 0 } token)
            {
                return;
            }

            PullRequestRef? open = await _flow
                .FindOpenAsync(forge, source, target, token, CancellationToken.None)
                .ConfigureAwait(true);

            //The target may have changed while this was in flight, in which case the answer is about
            //branches nobody is looking at any more.
            if (open is null || target != _loadedTarget)
                return;

            _existing = open;
            ShowExisting(forge, open);
        }
        catch (Exception ex)
        {
            //A check that improves a message must never be able to break the window.
            _log.Debug($"Looking for an existing pull request failed: {ex.Message}");
        }
    }

    private void ShowExisting(ForgeRepository forge, PullRequestRef open)
    {
        NoticeText.Text = Strings.Get("pr.alreadyopen", Number(forge, open.Number), open.Title);
        OpenButton.Visibility = Visibility.Visible;
        NoticeStrip.Visibility = Visibility.Visible;

        //Create becomes the wrong verb: the request exists, and pressing it would only produce the
        //service's own duplicate refusal.
        CreateButton.IsEnabled = false;
    }

    /// <summary>GitLab says <c>!42</c>; the other two say <c>#42</c>.</summary>
    private static string Number(ForgeRepository forge, int number) =>
        forge.Kind == ForgeKind.GitLab ? $"!{number}" : $"#{number}";

    private async void OnGenerate(object sender, RoutedEventArgs e)
    {
        //An explicit press overrides the "do not overwrite what the user typed" rule, because it *is*
        //the user asking for it.
        _edited = false;
        await GenerateAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Streams a title and description in, splitting the answer on every fragment rather than at the
    /// end -- so the title box fills in first and the description grows underneath it, instead of the
    /// whole thing appearing in one box and jumping into two when the stream closes.
    /// </summary>
    private async Task GenerateAsync()
    {
        if (_plan is not { CanPropose: true } plan || !_ai.IsUsable)
            return;

        //One at a time. A second press, or a target change mid-stream, cancels the first -- otherwise
        //two streams write into the same two boxes.
        _generation?.Cancel();
        _generation?.Dispose();

        var generation = new CancellationTokenSource();
        _generation = generation;

        GenerateButton.IsEnabled = false;
        StatusText.Text = Strings.Get("pr.generating");

        try
        {
            GenerationOutcome outcome = await _ai.StreamPullRequestAsync(
                _repository,
                _summary.MergeBase,
                plan.SourceBranch,
                _loadedTarget,
                _summary.Commits,
                _summary.Files,
                Apply,
                generation.Token).ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                Apply(outcome.Message);
                StatusText.Text = Strings.Get("pr.hint");
            }
            else if (outcome.FailureReason is { Length: > 0 } reason)
            {
                //An ordinary editable box with a one-line notice, which is what every AI failure gets. The
                //prefilled title and description are still there.
                StatusText.Text = reason;
            }
        }
        finally
        {
            GenerateButton.IsEnabled = true;

            if (ReferenceEquals(_generation, generation))
            {
                _generation = null;
                generation.Dispose();
            }
        }
    }

    private void Apply(string answer)
    {
        //The user started typing while it was arriving. Their words win, and the rest of the stream is
        //thrown away rather than fighting them for the caret.
        if (_edited)
        {
            _generation?.Cancel();
            return;
        }

        (string title, string body) = PullRequestPrompt.Split(answer);

        _applying = true;

        try
        {
            TitleBox.Text = title;

            //Only once there is a body. Before the first newline the whole answer is the title, and blanking
            //the prefilled description meanwhile would make it flicker.
            if (body.Length > 0)
                DescriptionBox.Text = body;
        }
        finally
        {
            _applying = false;
        }
    }

    private void OnTitleChanged(object sender, RoutedEventArgs e) => NoteEdit();

    private void NoteEdit()
    {
        if (!_applying)
            _edited = true;
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (_busy || _plan is not { CanPropose: true } plan || plan.Forge is not { } forge)
            return;

        if (TitleBox.Text.Trim().Length == 0)
        {
            StatusText.Text = Strings.Get("pr.notitle");
            TitleBox.Focus();
            return;
        }

        SetBusy(true);

        try
        {
            //Re-read rather than reusing what the window opened with: the push plan plays off the
            //ahead/behind counts, and this window can have been open while the user committed in another.
            _state = await _status.GetStatusAsync(_repository, CancellationToken.None).ConfigureAwait(true);

            var draft = new PullRequestDraft(
                TitleBox.Text.Trim(),
                DescriptionBox.Text.Trim(),
                plan.SourceBranch,
                Target,
                DraftCheck.IsChecked == true,

                //Never sent to GitHub, whatever the checkbox happens to hold: it is hidden there, and a hidden
                //control's value must not reach a request.
                DeleteBranchCheck.IsChecked == true && forge.Kind != ForgeKind.GitHub);

            PullRequestFlowOutcome outcome = await _flow.CreateAsync(
                _repository,
                _state,
                forge,
                draft,
                force => Dispatcher.InvokeAsync(() =>
                    _credentials.AcquireAsync(_repository, forge, force, CancellationToken.None)).Task.Unwrap(),
                AskUpstreamAsync,
                new Progress<PullRequestStep>(Report),
                CancellationToken.None).ConfigureAwait(true);

            Finish(forge, outcome);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// The one guardrail question this surface can raise: creating an upstream publishes a branch
    /// other people read. Answered through <see cref="UpstreamConsent"/>, the same one the commit
    /// surface uses, so "once per repository" means once across both.
    /// </summary>
    private Task<bool> AskUpstreamAsync(PushPlan plan) =>
        Dispatcher.InvokeAsync(() => _consent.AnswerAsync(
            _repository,
            new CommitFlowQuestion(CommitFlowQuestionKind.CreateUpstream, plan.Branch, plan.Upstream, plan.Remote),
            (title, question, yes, no) => Task.FromResult(ConfirmWindow.Ask(this, title, question, yes, no))))
            .Task.Unwrap();

    private void Report(PullRequestStep step) =>
        StatusText.Text = Strings.Get(step switch
        {
            PullRequestStep.Pushing => "pr.step.pushing",
            PullRequestStep.Authorising => "pr.step.authorising",
            PullRequestStep.Checking => "pr.step.checking",
            _ => "pr.step.creating",
        });

    private void Finish(ForgeRepository forge, PullRequestFlowOutcome outcome)
    {
        switch (outcome.Result)
        {
            case PullRequestFlowResult.Created when outcome.Request is { } created:
                //The notification is the only trace left once this closes, which is why it carries the number.
                _notifier.Success(
                    Strings.Get("app.name"),
                    Strings.Get("pr.created", Number(forge, created.Number), created.Title));

                Open(created.WebUrl);
                Close();
                break;

            case PullRequestFlowResult.AlreadyOpen when outcome.Request is { } open:
                _existing = open;
                ShowExisting(forge, open);
                StatusText.Text = Strings.Get("pr.hint");
                break;

            case PullRequestFlowResult.Refused:
                Warn(outcome.Message ?? string.Empty);
                break;

            case PullRequestFlowResult.Failed:
                //A push that succeeded before the failure is said out loud: the branch is published and the
                //request is not, which is a state the user has to know about.
                string detail = outcome.Pushed
                    ? Strings.Get("pr.pushed", _plan?.SourceBranch ?? string.Empty) + "\n\n" + outcome.Message
                    : outcome.Message ?? string.Empty;

                new NoticeWindow(Strings.Get("pr.error.title"), detail, compact: false) { Owner = this }.ShowDialog();
                StatusText.Text = Strings.Get("pr.hint");
                break;

            default:
                StatusText.Text = Strings.Get("pr.hint");
                break;
        }
    }

    private void OnOpenExisting(object sender, RoutedEventArgs e)
    {
        if (_existing is { WebUrl.Length: > 0 } open)
            Open(open.WebUrl);
    }

    /// <summary>
    /// Opens a URL the server gave us, in the user's browser.
    ///
    /// <b>The scheme is checked first, and that is not a formality.</b> This string arrives over the
    /// network, and <c>UseShellExecute</c> will start whatever a scheme is registered to.
    /// </summary>
    private void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            _log.Warn($"Refusing to open a pull request URL that is not http(s): {url}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            //Not worth a dialog: the request exists, and the notification already said so.
            _log.Warn($"Could not open {parsed.Host} in a browser: {ex.Message}");
        }
    }

    /// <summary>Nothing can be proposed. The reason replaces the window's contents.</summary>
    private void Refuse(string reason)
    {
        NoticeText.Text = reason;
        NoticeStrip.Visibility = Visibility.Visible;
        OpenButton.Visibility = Visibility.Collapsed;

        CreateButton.IsEnabled = false;
        GenerateButton.Visibility = Visibility.Collapsed;
        TitleBox.IsEnabled = false;
        DescriptionBox.IsEnabled = false;
        TargetBox.IsEnabled = false;
        DraftCheck.IsEnabled = false;
        DeleteBranchCheck.IsEnabled = false;

        SummaryText.Text = string.Empty;
        StatusText.Text = string.Empty;
    }

    /// <summary>A refusal from the flow. Everything stays usable -- the user can retarget and retry.</summary>
    private void Warn(string reason)
    {
        NoticeText.Text = reason;
        OpenButton.Visibility = Visibility.Collapsed;
        NoticeStrip.Visibility = Visibility.Visible;
        StatusText.Text = Strings.Get("pr.hint");
    }

    private void HideNotice()
    {
        NoticeStrip.Visibility = Visibility.Collapsed;
        OpenButton.Visibility = Visibility.Collapsed;

        if (_plan is { CanPropose: true })
            CreateButton.IsEnabled = true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;

        CreateButton.IsEnabled = !busy && _existing is null && _plan is { CanPropose: true };
        GenerateButton.IsEnabled = !busy;
        TargetBox.IsEnabled = !busy;
        TitleBox.IsEnabled = !busy;
        DescriptionBox.IsEnabled = !busy;
        DraftCheck.IsEnabled = !busy;
        DeleteBranchCheck.IsEnabled = !busy;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        //A generation outlives the window otherwise, writing into boxes nobody is looking at and holding
        //a socket open.
        _generation?.Cancel();
        _generation?.Dispose();
        _generation = null;

        base.OnClosed(e);
    }
}
