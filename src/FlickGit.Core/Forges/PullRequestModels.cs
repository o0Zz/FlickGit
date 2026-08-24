namespace FlickGit.Forges;

/// <summary>
/// What the user is asking for, in the two words every forge agrees on and the two flags two of
/// them do.
///
/// <b>Four fields, and the list is closed on purpose.</b> Reviewers, labels and work items were
/// considered and left out: each needs a lookup call with a different shape and a different id type
/// per service — a login, a numeric user id, an Azure DevOps descriptor — and a typed field with no
/// completion behind it is worse than no field at all. What is here maps cleanly onto all three
/// APIs, or onto two with the third saying so.
/// </summary>
/// <param name="Title">The first line. Never empty — the flow refuses before it reaches a client.</param>
/// <param name="Description">Markdown. May be empty; a request with no description is legal everywhere.</param>
/// <param name="SourceBranch">The branch being proposed, short — <c>feature/x</c>, not a ref path.</param>
/// <param name="TargetBranch">Where it is proposed into, short.</param>
/// <param name="IsDraft">
/// Draft on GitHub, "mark as draft" on GitLab, <c>isDraft</c> on Azure DevOps. One checkbox, three
/// native spellings.
/// </param>
/// <param name="DeleteSourceBranch">
/// Delete the branch when the request merges. GitLab and Azure DevOps carry it on the request
/// itself; <b>GitHub has no per-request equivalent</b> — it is a repository setting there, so the
/// checkbox is hidden rather than sent and ignored.
/// </param>
public sealed record PullRequestDraft(
    string Title,
    string Description,
    string SourceBranch,
    string TargetBranch,
    bool IsDraft,
    bool DeleteSourceBranch);

/// <summary>
/// A pull request that exists on the server, whether it was just created or was already open.
///
/// One type for both, because the window does the same thing with either: name it and offer to open
/// it. A second type would differ only in which verb the caller used to get it.
/// </summary>
/// <param name="Number">
/// What the service calls it to a human: <c>#42</c>, <c>!42</c>, <c>PR 42</c>. A number rather than
/// the id, which for Azure DevOps happens to be the same and for the other two is not.
/// </param>
/// <param name="WebUrl">Where it lives. What the notification opens.</param>
/// <param name="Title">Its title, for an existing one the user did not just type.</param>
public sealed record PullRequestRef(int Number, string WebUrl, string Title);

/// <param name="Succeeded">The request exists on the server.</param>
/// <param name="Request">What was created, when it succeeded.</param>
/// <param name="Error">
/// Why not, in a sentence the user can act on. Already redacted — a forge that echoes a bad request
/// back would otherwise put the token in the log.
/// </param>
/// <param name="Unauthorised">
/// True when the service refused the credential rather than the request. The one failure with a
/// specific remedy, so the window offers to store a token instead of only reporting.
/// </param>
public sealed record PullRequestOutcome(
    bool Succeeded,
    PullRequestRef? Request,
    string? Error,
    bool Unauthorised = false)
{
    public static PullRequestOutcome Ok(PullRequestRef request) => new(true, request, null);

    public static PullRequestOutcome Failed(string error) => new(false, null, error);

    public static PullRequestOutcome Rejected(string error) => new(false, null, error, Unauthorised: true);
}

/// <summary>
/// One forge's pull-request API.
///
/// An interface with three implementations rather than two, which is what earns it under Hard
/// Requirement 2 — and the three share almost nothing but <see cref="ForgeApi"/>: three request
/// shapes, three response shapes, three ways of saying "already open", and one of them uses Basic
/// auth where the others use Bearer. A base class here would hold the two lines they agree on.
///
/// The token arrives per call rather than in a constructor. It is acquired lazily — from Git's own
/// credential helper first, and only then from a prompt — and a client built at startup would have
/// to be rebuilt every time that answer changed.
/// </summary>
public interface IPullRequestClient
{
    ForgeKind Kind { get; }

    /// <summary>Creates the request.</summary>
    Task<PullRequestOutcome> CreateAsync(
        ForgeRepository repository,
        PullRequestDraft draft,
        string token,
        CancellationToken cancellationToken);

    /// <summary>
    /// The open request for this exact source and target, or null when there is none.
    ///
    /// Asked before creating, and its value is not saving a round trip — it is that all three
    /// services refuse a duplicate with a status code and a sentence, and none of them says
    /// <i>where</i> the existing one is. "!12 is already open for this branch, open it?" is an
    /// answer; "409 Conflict" is a puzzle.
    ///
    /// A failure here is <b>not</b> a failure to report: it returns null, and creating then either
    /// works or produces the service's own duplicate error. This call is an improvement to a message,
    /// and it must never be the reason a request cannot be opened.
    /// </summary>
    Task<PullRequestRef?> FindOpenAsync(
        ForgeRepository repository,
        string sourceBranch,
        string targetBranch,
        string token,
        CancellationToken cancellationToken);
}
