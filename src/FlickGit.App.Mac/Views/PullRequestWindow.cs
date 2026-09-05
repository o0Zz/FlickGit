using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FlickGit.Ai;
using FlickGit.App.Ai;
using FlickGit.App.CommandLine;
using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.Commits;
using FlickGit.Forges;
using FlickGit.History;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Remotes;
using FlickGit.Status;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// Propose the current branch, on GitHub, GitLab or Azure DevOps.
///
/// Thin on purpose. Which forge, which target and what is in the branch come from
/// <see cref="PullRequestService"/>; the order the network is spoken to in is
/// <see cref="PullRequestFlow"/>; the description is <see cref="AiTextService"/>. What is left is
/// presentation and one rule that genuinely belongs to a window: <b>never overwrite what the user
/// has typed.</b>
///
/// The commit window's shape, deliberately: a header saying what is about to happen, the text the
/// user is writing in the middle with the AI filling it in, and one primary button. What is missing
/// compared with that window is a file list, and the summary line is the point of that — a pull
/// request is reviewed on the server, so the question here is "is this the right branch going to the
/// right place", not "which of these files".
/// </summary>
public sealed class PullRequestWindow : Window
{
    private readonly RepositoryInfo _repository;
    private readonly PullRequestService _pullRequests;
    private readonly PullRequestFlow _flow;
    private readonly ForgeCredentials _credentials;
    private readonly AiTextService _ai;
    private readonly StatusService _status;
    private readonly UpstreamConsent _consent;
    private readonly INotifier _notifier;
    private readonly FlickSettings _settings;
    private readonly ILog _log;

    private readonly TextBlock _source = new()
    {
        Classes = { "mono" },
        FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly AutoCompleteBox _target = new()
    {
        Width = 200,
        FilterMode = AutoCompleteFilterMode.Contains,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly TextBlock _forge = new()
    {
        Classes = { "muted" },
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(12, 0, 0, 0),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly TextBlock _summaryText = new()
    {
        Classes = { "muted" },
        Margin = new Thickness(12, 8, 12, 0),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly TextBlock _noticeText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _open = new() { MinWidth = 110, Classes = { "strip" }, IsVisible = false };
    private readonly Border _noticeStrip;

    private readonly TextBox _title = new() { FontSize = 13.5 };

    private readonly TextBox _description = new()
    {
        AcceptsReturn = true,
        AcceptsTab = false,
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly CheckBox _draft = new() { VerticalAlignment = VerticalAlignment.Center };
    //Hidden until the plan names a forge, because that is what decides both its label and whether it
    //applies at all. Shown from the start it is an unlabelled box beside Draft on every refusal.
    private readonly CheckBox _deleteBranch = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        IsVisible = false,
    };
    private readonly Button _generate = new() { MinWidth = 150, Classes = { "strip" }, IsVisible = false };

    private readonly TextBlock _status_ = new()
    {
        Classes = { "muted" },
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly Button _create = new() { MinWidth = 150, Classes = { "primary" }, IsEnabled = false };
    private readonly Button _close = new() { MinWidth = 90 };

    private PullRequestPlan? _plan;
    private PullRequestSummary _summary = PullRequestSummary.Empty;
    private RepositoryStatus? _state;
    private PullRequestRef? _existing;

    private CancellationTokenSource? _generation;

    /// <summary>
    /// The target the summary and the description were built for, so a box event that changed nothing
    /// does not re-run three Git commands and a generation.
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
        INotifier notifier,
        FlickSettings settings,
        ILog log)
    {
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
        Width = 720;
        Height = 620;
        MinWidth = 560;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _draft.Content = Strings.Get("pr.draft");
        _generate.Content = Strings.Get("pr.generate");
        _create.Content = Strings.Get("pr.create");
        _close.Content = Strings.Get("common.close");
        _open.Content = Strings.Get("pr.open");
        _status_.Text = Strings.Get("pr.hint");

        _noticeStrip = new Border
        {
            IsVisible = false,
            Margin = new Thickness(12, 10, 12, 0),
            Padding = new Thickness(10, 8),
            Background = Resource("WarnBackground"),
            BorderBrush = Resource("WarnBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children = { Column(_noticeText, 0), Column(_open, 1) },
            },
        };

        _generate.Click += (_, _) =>
        {
            //An explicit press overrides the "do not overwrite what the user typed" rule, because it
            //*is* the user asking for it.
            _edited = false;
            _ = GenerateAsync();
        };

        _create.Click += (_, _) => _ = CreateAsync();
        _close.Click += (_, _) => Close();
        _open.Click += (_, _) => OpenExisting();

        _target.LostFocus += (_, _) => _ = TargetChangedAsync();
        _target.SelectionChanged += (_, _) => _ = TargetChangedAsync();
        _title.TextChanged += (_, _) => NoteEdit();
        _description.TextChanged += (_, _) => NoteEdit();

        Content = Build();
    }

    private Control Build()
    {
        //Where it comes from and where it goes. The target is editable rather than fixed: the
        //resolved answer is right nearly always, and when it is not, retyping it here beats finding
        //the config key.
        var header = new Border
        {
            Background = Resource("SurfaceAlt"),
            BorderBrush = Resource("Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 10),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,200,*"),
                Children =
                {
                    Column(_source, 0),
                    Column(
                        new TextBlock
                        {
                            Text = "→",
                            Margin = new Thickness(10, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = Resource("Accent"),
                            FontWeight = FontWeight.Bold,
                        },
                        1),
                    Column(_target, 2),
                    Column(_forge, 3),
                },
            },
        };

        var titleBlock = new StackPanel
        {
            Margin = new Thickness(12, 12, 12, 0),
            Children =
            {
                new TextBlock { Text = Strings.Get("pr.field.title"), Classes = { "section" } },
                Spaced(_title, 4),
            },
        };

        var descriptionGrid = new Grid
        {
            Margin = new Thickness(12, 12, 12, 0),
            RowDefinitions = new RowDefinitions("Auto,*"),
        };

        descriptionGrid.Children.Add(
            Row(new TextBlock { Text = Strings.Get("pr.field.description"), Classes = { "section" } }, 0));
        descriptionGrid.Children.Add(Row(Spaced(_description, 4), 1));

        //The two flags, and the button that writes the description. Delete-on-merge hides itself on
        //GitHub, where there is no per-request setting for it — a checkbox that silently did nothing
        //would be worse than its absence.
        var flags = new Grid
        {
            Margin = new Thickness(12, 10, 12, 0),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 18,
                    Children = { _draft, _deleteBranch },
                },
                _generate,
            },
        };

        _generate.HorizontalAlignment = HorizontalAlignment.Right;

        var footer = new Border
        {
            Margin = new Thickness(0, 12, 0, 0),
            Background = Resource("SurfaceAlt"),
            BorderBrush = Resource("Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 10),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    Column(_status_, 0),
                    Column(
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 10,
                            Children = { _create, _close },
                        },
                        1),
                },
            },
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto") };

        grid.Children.Add(Row(header, 0));
        grid.Children.Add(Row(_summaryText, 1));
        grid.Children.Add(Row(_noticeStrip, 2));
        grid.Children.Add(Row(titleBlock, 3));
        grid.Children.Add(Row(descriptionGrid, 4));
        grid.Children.Add(Row(flags, 5));
        grid.Children.Add(Row(footer, 6));

        return grid;
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

        _source.Text = _plan.SourceBranch;
        _forge.Text = $"{forge.Kind} · {forge.Display}";

        //GitHub has no per-request "delete the branch on merge" -- it is a repository setting there,
        //so the checkbox is absent rather than present and ignored.
        _deleteBranch.IsVisible = forge.Kind != ForgeKind.GitHub;
        _deleteBranch.Content = Strings.Get("pr.deletebranch", _plan.SourceBranch);

        _applying = true;
        _target.ItemsSource = _plan.TargetCandidates;
        _target.Text = _plan.TargetBranch;
        _applying = false;

        _generate.IsVisible = _ai.IsUsable;
        _create.IsEnabled = true;

        await ReloadTargetAsync(_plan.TargetBranch).ConfigureAwait(true);

        //The caret in the title box: it is the one field that must not be empty, and the AI is about
        //to fill it in.
        _title.Focus();
    }

    private string Target => (_target.Text ?? string.Empty).Trim();

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
    /// All three, because all three are answers about a specific pair of branches — a description
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

        _summaryText.Text = _summary.Commits.Count == 0
            ? Strings.Get("pr.summary.empty", plan.SourceBranch, $"{plan.Remote}/{target}")
            : Strings.Get(
                "pr.summary",
                _summary.Commits.Count,
                _summary.Files.Count,
                _summary.Added,
                _summary.Removed);

        Prefill();

        //Both fire and forget, both harmless if they never finish. Awaiting the generation here would
        //lose the point of the window: LoadAsync is what the verb times as "populated", so a second
        //of model latency would become a second before the window could be typed in, for text the
        //user is free to ignore.
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
            _title.Text = _summary.Commits.Count == 1
                ? _summary.Commits[0].Subject
                : Humanise(_plan.SourceBranch);

            _description.Text = _summary.Commits.Count > 1
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
    /// wrong first impression of the feature — so with nothing stored this simply does not run, and
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
            ShowExisting(open);
        }
        catch (Exception ex)
        {
            //A check that improves a message must never be able to break the window.
            _log.Debug($"Looking for an existing pull request failed: {ex.Message}");
        }
    }

    private void ShowExisting(PullRequestRef open)
    {
        _noticeText.Text = Strings.Get("pr.alreadyopen", Number(open.Number), open.Title);
        _open.IsVisible = true;
        _noticeStrip.IsVisible = true;

        //Create becomes the wrong verb: the request exists, and pressing it would only produce the
        //service's own duplicate refusal.
        _create.IsEnabled = false;
    }

    /// <summary>All three services say <c>#42</c>.</summary>
    private static string Number(int number) => $"#{number}";

    /// <summary>
    /// Streams a title and description in, splitting the answer on every fragment rather than at the
    /// end — so the title box fills in first and the description grows underneath it, instead of the
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

        _generate.IsEnabled = false;
        _status_.Text = Strings.Get("pr.generating");

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
                _status_.Text = Strings.Get("pr.hint");
            }
            else if (outcome.FailureReason is { Length: > 0 } reason)
            {
                //An ordinary editable box with a one-line notice, which is what every AI failure gets.
                //The prefilled title and description are still there.
                _status_.Text = reason;
            }
        }
        finally
        {
            _generate.IsEnabled = true;

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
            _title.Text = title;

            //Only once there is a body. Before the first newline the whole answer is the title, and
            //blanking the prefilled description meanwhile would make it flicker.
            if (body.Length > 0)
                _description.Text = body;
        }
        finally
        {
            _applying = false;
        }
    }

    private void NoteEdit()
    {
        if (!_applying)
            _edited = true;
    }

    private async Task CreateAsync()
    {
        if (_busy || _plan is not { CanPropose: true } plan || plan.Forge is not { } forge)
            return;

        if ((_title.Text ?? string.Empty).Trim().Length == 0)
        {
            _status_.Text = Strings.Get("pr.notitle");
            _title.Focus();

            return;
        }

        SetBusy(true);

        try
        {
            //Re-read rather than reusing what the window opened with: the push plan plays off the
            //ahead/behind counts, and this window can have been open while the user committed in
            //another.
            _state = await _status.GetStatusAsync(_repository, CancellationToken.None).ConfigureAwait(true);

            var draft = new PullRequestDraft(
                (_title.Text ?? string.Empty).Trim(),
                (_description.Text ?? string.Empty).Trim(),
                plan.SourceBranch,
                Target,
                _draft.IsChecked == true,

                //Never sent to GitHub, whatever the checkbox happens to hold: it is hidden there, and
                //a hidden control's value must not reach a request.
                _deleteBranch.IsChecked == true && forge.Kind != ForgeKind.GitHub);

            PullRequestFlowOutcome outcome = await _flow.CreateAsync(
                _repository,
                _state,
                forge,
                draft,
                force => Dispatcher.UIThread.InvokeAsync(() =>
                    _credentials.AcquireAsync(_repository, forge, force, CancellationToken.None)),
                AskUpstreamAsync,
                new Progress<PullRequestStep>(Report),
                CancellationToken.None).ConfigureAwait(true);

            Finish(outcome);
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
        Dispatcher.UIThread.InvokeAsync(() => _consent.AnswerAsync(
            _repository,
            new CommitFlowQuestion(CommitFlowQuestionKind.CreateUpstream, plan.Branch, plan.Upstream, plan.Remote),
            (title, question, yes, no) => MessageWindow.AskAsync(title, question, yes, no, destructive: false)));

    private void Report(PullRequestStep step) =>
        _status_.Text = Strings.Get(step switch
        {
            PullRequestStep.Pushing => "pr.step.pushing",
            PullRequestStep.Authorising => "pr.step.authorising",
            PullRequestStep.Checking => "pr.step.checking",
            _ => "pr.step.creating",
        });

    private void Finish(PullRequestFlowOutcome outcome)
    {
        switch (outcome.Result)
        {
            case PullRequestFlowResult.Created when outcome.Request is { } created:
                //The notification is the only trace left once this closes, which is why it carries
                //the number.
                _notifier.Success(
                    Strings.Get("app.name"),
                    Strings.Get("pr.created", Number(created.Number), created.Title));

                Open(created.WebUrl);
                Close();

                break;

            case PullRequestFlowResult.AlreadyOpen when outcome.Request is { } open:
                _existing = open;
                ShowExisting(open);
                _status_.Text = Strings.Get("pr.hint");

                break;

            case PullRequestFlowResult.Refused:
                Warn(outcome.Message ?? string.Empty);

                break;

            case PullRequestFlowResult.Failed:
                //A push that succeeded before the failure is said out loud: the branch is published
                //and the request is not, which is a state the user has to know about.
                MessageWindow.Notice(
                    Strings.Get("pr.error.title"),
                    outcome.Pushed
                        ? Strings.Get("pr.pushed", _plan?.SourceBranch ?? string.Empty)
                          + Environment.NewLine + Environment.NewLine + outcome.Message
                        : outcome.Message ?? string.Empty);

                _status_.Text = Strings.Get("pr.hint");

                break;

            default:
                _status_.Text = Strings.Get("pr.hint");

                break;
        }
    }

    private void OpenExisting()
    {
        if (_existing is { WebUrl.Length: > 0 } open)
            Open(open.WebUrl);
    }

    /// <summary>
    /// Opens a URL the server gave us, in the user's browser.
    ///
    /// <b>The scheme is checked first, and that is not a formality.</b> This string arrives over the
    /// network, and the shell will start whatever a scheme is registered to.
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
        _noticeText.Text = reason;
        _noticeStrip.IsVisible = true;
        _open.IsVisible = false;

        _create.IsEnabled = false;
        _generate.IsVisible = false;
        _title.IsEnabled = false;
        _description.IsEnabled = false;
        _target.IsEnabled = false;
        _draft.IsEnabled = false;
        _deleteBranch.IsEnabled = false;

        _summaryText.Text = string.Empty;
        _status_.Text = string.Empty;
    }

    /// <summary>A refusal from the flow. Everything stays usable — the user can retarget and retry.</summary>
    private void Warn(string reason)
    {
        _noticeText.Text = reason;
        _open.IsVisible = false;
        _noticeStrip.IsVisible = true;
        _status_.Text = Strings.Get("pr.hint");
    }

    private void HideNotice()
    {
        _noticeStrip.IsVisible = false;
        _open.IsVisible = false;

        if (_plan is { CanPropose: true })
            _create.IsEnabled = true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;

        _create.IsEnabled = !busy && _existing is null && _plan is { CanPropose: true };
        _generate.IsEnabled = !busy;
        _target.IsEnabled = !busy;
        _title.IsEnabled = !busy;
        _description.IsEnabled = !busy;
        _draft.IsEnabled = !busy;
        _deleteBranch.IsEnabled = !busy;
    }

    /// <summary>
    /// Ctrl/Cmd+Enter creates, from anywhere in the window including the description box.
    ///
    /// <b>A default button is not enough here.</b> A multi-line TextBox consumes Enter to insert a
    /// newline and never lets it reach one, and it does not special-case a modifier either — so once
    /// the caret is in the description, the only way to create would be the mouse. The commit window
    /// carries the same chord for the same reason.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (e.Key is Key.Enter or Key.Return && command)
        {
            //Through the button's own guard: it is disabled until the plan says a request can be
            //proposed, and a chord must not reach past a refusal the click cannot.
            if (!_create.IsEnabled)
                return;

            e.Handled = true;
            _ = CreateAsync();

            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();

            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        //A generation outlives the window otherwise, writing into boxes nobody is looking at and
        //holding a socket open.
        _generation?.Cancel();
        _generation?.Dispose();
        _generation = null;

        base.OnClosed(e);
    }

    private static T Spaced<T>(T control, double top)
        where T : Control
    {
        control.Margin = new Thickness(0, top, 0, 0);

        return control;
    }

    private static T Row<T>(T control, int row)
        where T : Control
    {
        control.SetValue(Grid.RowProperty, row);

        return control;
    }

    private static T Column<T>(T control, int column)
        where T : Control
    {
        control.SetValue(Grid.ColumnProperty, column);

        return control;
    }

    private static IBrush? Resource(string key) => Application.Current?.FindResource(key) as IBrush;
}
