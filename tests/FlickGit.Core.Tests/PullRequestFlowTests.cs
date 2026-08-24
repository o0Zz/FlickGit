using FlickGit.Ai;
using FlickGit.Forges;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Remotes;
using FlickGit.Repositories;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Publishing the branch, then opening the request.
///
/// In scope under Hard Requirement 4 as <b>the sequence</b> and as <b>the safety rules</b>. The order
/// is the whole of the first: a request opened before the push is a request against commits the
/// server has never seen. The second is that this surface reaches a remote through
/// <c>PushService</c> and nothing else, so a diverged branch is refused here exactly as it is from
/// the commit window and force-push is not reachable from either.
/// </summary>
public class PullRequestFlowTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\repo", "repo", HasSubmodules: false, IsBare: false);

    private static readonly ForgeRepository Forge = new(
        ForgeKind.GitHub,
        "github.com",
        new Uri("https://api.github.com/"),
        "o0Zz",
        string.Empty,
        "FlickGit");

    private static readonly PullRequestDraft Draft = new(
        "feat: add connection pooling",
        "Adds a pool.",
        "feature/pool",
        "main",
        IsDraft: false,
        DeleteSourceBranch: false);

    private static RepositoryStatus Status(int ahead = 1, int behind = 0, string? upstream = "origin/feature/pool") =>
        new()
        {
            Repository = Repository,
            Branch = "feature/pool",
            Upstream = upstream,
            Ahead = ahead,
            Behind = behind,
            HeadCommit = "abc1234",
        };

    private static PullRequestFlow Create(FakeGitRunner git, FakePullRequestClient client) =>
        new(
            new PushService(git, new RepositoryService(git)),
            new PullRequestClients([client]),
            new RepositoryService(git),
            NullLog.Instance);

    private static Task<PullRequestFlowOutcome> RunAsync(
        FakeGitRunner git,
        FakePullRequestClient client,
        RepositoryStatus? status = null,
        PullRequestDraft? draft = null,
        Func<bool, Task<string?>>? token = null,
        bool consent = true) =>
        Create(git, client).CreateAsync(
            Repository,
            status ?? Status(),
            Forge,
            draft ?? Draft,
            token ?? (_ => Task.FromResult<string?>("token")),
            _ => Task.FromResult(consent),
            null,
            CancellationToken.None);

    /// <summary>
    /// The push happens first, and the create sees it has happened.
    ///
    /// Asserted by recording how many Git calls had run when the client was asked to create: the
    /// push has to be among them. Ordering by wall clock would pass on a flow that pushed
    /// afterwards.
    /// </summary>
    [Fact]
    public async Task The_branch_is_pushed_before_the_request_is_created()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n").Returns(["push"]);
        var client = new FakePullRequestClient();

        //Recorded at the moment the client is asked, so the assertion is about order rather than
        //about wall clock.
        client.OnCreate = () => client.GitCallsBeforeCreate = git.Invocations.Count;

        PullRequestFlowOutcome outcome = await RunAsync(git, client);

        Assert.Equal(PullRequestFlowResult.Created, outcome.Result);
        Assert.True(outcome.Pushed);
        Assert.Equal(1, client.Creates);

        //The push is in the calls that had already run when the create was issued.
        Assert.Contains(
            git.Invocations.Take(client.GitCallsBeforeCreate),
            call => call.Args.Contains("push", StringComparer.Ordinal));
    }

    /// <summary>
    /// A diverged branch stops the flow, and nothing is pushed or created.
    ///
    /// The refusal is <c>PushService</c>'s, unchanged — reconciling means a rebase or a force-push,
    /// and CLAUDE.md's "Safety Rules" forbid offering the second from any surface.
    /// </summary>
    [Fact]
    public async Task A_diverged_branch_is_refused_with_nothing_pushed_and_nothing_created()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n");
        var client = new FakePullRequestClient();

        PullRequestFlowOutcome outcome = await RunAsync(git, client, Status(ahead: 2, behind: 3));

        Assert.Equal(PullRequestFlowResult.Refused, outcome.Result);
        Assert.False(outcome.Pushed);
        Assert.Equal(0, client.Creates);
        Assert.True(git.NeverCalledWith("push"));
    }

    /// <summary>
    /// Behind its own upstream means somebody else pushed to this branch. Proposing without their
    /// commits would open a request that is missing work already published under the same name.
    /// </summary>
    [Fact]
    public async Task A_branch_behind_its_upstream_is_refused_rather_than_pushed()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n");
        var client = new FakePullRequestClient();

        PullRequestFlowOutcome outcome = await RunAsync(git, client, Status(ahead: 0, behind: 2));

        Assert.Equal(PullRequestFlowResult.Refused, outcome.Result);
        Assert.Equal(0, client.Creates);
        Assert.True(git.NeverCalledWith("push"));
    }

    /// <summary>
    /// Creating an upstream publishes a branch other people read, so declining it stops the flow.
    ///
    /// <b>This caught a real bug.</b> The publish step reported "no error" for a declined consent and
    /// the flow read that as "carry on", so saying no to publishing the branch opened the pull
    /// request anyway — against a branch the user had just refused to push.
    /// </summary>
    [Fact]
    public async Task Declining_to_create_an_upstream_stops_the_flow()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n").Returns(["push"]);
        var client = new FakePullRequestClient();

        PullRequestFlowOutcome outcome = await RunAsync(git, client, Status(upstream: null), consent: false);

        //Cancelled rather than Refused: the user answered a question, which is not a failure to
        //report back to them.
        Assert.Equal(PullRequestFlowResult.Cancelled, outcome.Result);
        Assert.Null(outcome.Message);
        Assert.Equal(0, client.Creates);
        Assert.True(git.NeverCalledWith("push"));
    }

    /// <summary>
    /// An already-open request is reported rather than duplicated.
    ///
    /// All three services refuse a duplicate with a status code and none of them says where the
    /// existing one is, so this is what turns "409 Conflict" into a number the user can open.
    /// </summary>
    [Fact]
    public async Task An_open_request_is_reported_instead_of_creating_a_second()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n").Returns(["push"]);
        var client = new FakePullRequestClient { Open = new PullRequestRef(42, "https://example/42", "Existing") };

        PullRequestFlowOutcome outcome = await RunAsync(git, client);

        Assert.Equal(PullRequestFlowResult.AlreadyOpen, outcome.Result);
        Assert.Equal(42, outcome.Request?.Number);
        Assert.Equal(0, client.Creates);
    }

    /// <summary>
    /// A refused credential is retried once with a fresh one, and only for that reason.
    ///
    /// A token from Git's credential helper can be stale in a way nothing local can detect, and
    /// asking the user is what the flow would do next time anyway.
    /// </summary>
    [Fact]
    public async Task A_rejected_credential_is_asked_for_once_more()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n").Returns(["push"]);
        var client = new FakePullRequestClient { RejectFirstCredential = true };

        var asked = new List<bool>();

        PullRequestFlowOutcome outcome = await RunAsync(
            git,
            client,
            token: force =>
            {
                asked.Add(force);
                return Task.FromResult<string?>("token");
            });

        Assert.Equal(PullRequestFlowResult.Created, outcome.Result);
        Assert.Equal(2, client.Creates);

        //The second ask demands a fresh credential rather than handing back the one just refused.
        Assert.Equal([false, true], asked);
    }

    /// <summary>Any other failure is reported as it stands, rather than retried against a server that explained itself.</summary>
    [Fact]
    public async Task An_ordinary_failure_is_not_retried()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n").Returns(["push"]);
        var client = new FakePullRequestClient { Failure = "GitHub refused the request." };

        PullRequestFlowOutcome outcome = await RunAsync(git, client);

        Assert.Equal(PullRequestFlowResult.Failed, outcome.Result);
        Assert.Equal(1, client.Creates);
        Assert.Equal("GitHub refused the request.", outcome.Message);

        //The push happened and the request did not, which is a state the caller has to be able to report.
        Assert.True(outcome.Pushed);
    }

    /// <summary>An empty title is refused before any Git command or request runs.</summary>
    [Fact]
    public async Task An_empty_title_is_refused_before_anything_happens()
    {
        var git = new FakeGitRunner();
        var client = new FakePullRequestClient();

        PullRequestFlowOutcome outcome = await RunAsync(git, client, draft: Draft with { Title = "   " });

        Assert.Equal(PullRequestFlowResult.Refused, outcome.Result);
        Assert.Empty(git.Invocations);
        Assert.Equal(0, client.Creates);
    }

    /// <summary>
    /// Nothing on this path can force-push.
    ///
    /// CLAUDE.md, "Safety Rules": force-push is never offered from any surface, and the fast surfaces
    /// are not shortcuts around the rules. This flow builds no argument list of its own at all — the
    /// assertion pins that it stays that way.
    /// </summary>
    [Fact]
    public async Task Nothing_in_the_flow_can_force_push()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n").Returns(["push"]);
        var client = new FakePullRequestClient();

        await RunAsync(git, client, Status(upstream: null));

        Assert.True(git.NeverCalledWith("--force"));
        Assert.True(git.NeverCalledWith("-f"));
        Assert.True(git.NeverCalledWith("--force-with-lease"));
    }

    /// <summary>
    /// The answer is one piece of text with the title on its first line, and the split is Git's own
    /// rule: first line, blank line, rest.
    ///
    /// A pure function beside a parser, and the failure it guards against is visible: a title box
    /// holding the whole description, or a description whose first line is repeated above it.
    /// </summary>
    [Theory]
    [InlineData("feat: pool connections\n\nAdds a pool.\n\n- one\n- two", "feat: pool connections", "Adds a pool.\n\n- one\n- two")]

    //No blank line. Still a title on its own first line, and refusing that would throw away a good
    //answer over whitespace.
    [InlineData("feat: pool connections\nAdds a pool.", "feat: pool connections", "Adds a pool.")]

    //A model asked for a title sometimes writes a Markdown heading, or bolds it.
    [InlineData("# feat: pool connections\n\nBody.", "feat: pool connections", "Body.")]
    [InlineData("**feat: pool connections**\n\nBody.", "feat: pool connections", "Body.")]

    //A fence around the whole answer, which is what the prompt asks it not to do.
    [InlineData("```\nfeat: pool connections\n\nBody.\n```", "feat: pool connections", "Body.")]

    //A title and nothing else is a legitimate answer for a one-commit branch.
    [InlineData("feat: pool connections", "feat: pool connections", "")]
    public void A_generated_answer_splits_into_a_title_and_a_body(string answer, string title, string body)
    {
        (string actualTitle, string actualBody) = PullRequestPrompt.Split(answer);

        Assert.Equal(title, actualTitle);
        Assert.Equal(body.Replace("\r\n", "\n"), actualBody.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// A client that records what it was asked instead of opening a socket.
    ///
    /// This is what <see cref="PullRequestClients"/> taking an <c>IEnumerable</c> buys: the sequence
    /// is assertable with no HTTP at all, which is the same trade <c>FakeGitRunner</c> makes for Git.
    /// </summary>
    private sealed class FakePullRequestClient : IPullRequestClient
    {
        public ForgeKind Kind => ForgeKind.GitHub;

        /// <summary>Answered by <see cref="FindOpenAsync"/>. Null means there is no duplicate.</summary>
        public PullRequestRef? Open { get; init; }

        /// <summary>Refuse the first credential, the way a stale token from the helper would.</summary>
        public bool RejectFirstCredential { get; init; }

        /// <summary>Fail every attempt, for a reason that is not the credential.</summary>
        public string? Failure { get; init; }

        public int Creates { get; private set; }

        /// <summary>Run at the moment a create is issued, so a test can sample the Git calls so far.</summary>
        public Action? OnCreate { get; set; }

        /// <summary>How many Git commands had run by the time the create was issued.</summary>
        public int GitCallsBeforeCreate { get; set; }

        public Task<PullRequestOutcome> CreateAsync(
            ForgeRepository repository,
            PullRequestDraft draft,
            string token,
            CancellationToken cancellationToken)
        {
            Creates++;
            OnCreate?.Invoke();

            if (Failure is { } reason)
                return Task.FromResult(PullRequestOutcome.Failed(reason));

            if (RejectFirstCredential && Creates == 1)
                return Task.FromResult(PullRequestOutcome.Rejected("Rejected."));

            return Task.FromResult(
                PullRequestOutcome.Ok(new PullRequestRef(7, "https://example/7", draft.Title)));
        }

        public Task<PullRequestRef?> FindOpenAsync(
            ForgeRepository repository,
            string sourceBranch,
            string targetBranch,
            string token,
            CancellationToken cancellationToken) =>
            Task.FromResult(Open);
    }
}
