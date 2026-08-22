using System.Text;
using FlickGit.Models;
using FlickGit.Secrets;

namespace FlickGit.Ai;

/// <summary>
/// Decides what may leave the machine, and shrinks it until it is worth sending.
///
/// <b>This is the safety component of the AI feature.</b> Everything else about a commit message is
/// a convenience; this is the only part that can send a user's credentials to a third party. It is
/// pure — no Git, no clock, no network — precisely so it can be tested, which
/// <see cref="Build"/>'s caller cannot be.
///
/// Three rules, applied in this order and for these reasons:
///
/// <list type="number">
/// <item><description><b>Exclude by path first.</b> A file that must not be sent is never read, so
/// there is no window in which its content exists in this process.</description></item>
/// <item><description><b>Then cap.</b> CLAUDE.md: latency scales with input size and a commit
/// message does not need the whole diff. Per file first, then the whole payload — 300 files of 40
/// lines is still far past the token ceiling.</description></item>
/// <item><description><b>Redact last.</b> Path exclusion catches files that <i>are</i> secrets;
/// redaction catches a credential pasted into an ordinary source file.</description></item>
/// </list>
/// </summary>
public static class DiffPayload
{
    /// <summary>At or under this, CLAUDE.md sends the diff verbatim.</summary>
    public const int VerbatimCeilingBytes = 12 * 1024;

    /// <summary>How much of each file's hunks survives truncation. CLAUDE.md: 40 lines.</summary>
    public const int HunkLinesPerFile = 40;

    /// <summary>
    /// CLAUDE.md's hard ceiling of 4,000 input tokens, expressed in bytes.
    ///
    /// 3.5 bytes per token, rounded down — deliberately pessimistic. Overshooting costs latency on
    /// every single commit; undershooting costs a few words of context once.
    /// </summary>
    public const int TokenCeilingBytes = 4_000 * 7 / 2;

    private const string TruncationMarker = "[truncated]";

    /// <summary>
    /// Why <paramref name="change"/> must not be sent, or null when it may be.
    ///
    /// Returns a reason rather than a bool so the popup can say <i>which</i> files were held back.
    /// A user who sees "package-lock.json (lock file)" learns something; one who sees a message that
    /// ignored half their change learns nothing.
    /// </summary>
    public static string? ExclusionReason(GitFileChange change)
    {
        if (change.LooksLikeSecret || SecretDetector.LooksLikeSecretPath(change.Path))
            return "secret pattern";

        //Untracked content is in neither HEAD nor the index, so `git diff HEAD` has nothing to say
        //about it. The name still goes in the file list -- the model can use "added
        //src/PgBouncerPool.cs" even without its content.
        if (change.IsUntracked)
            return "untracked";

        if (change.IsBinary)
            return "binary";

        string path = change.Path.Replace('\\', '/');
        string name = path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();

        //Lock files are enormous, mechanical, and say nothing a commit message needs.
        if (name is "package-lock.json" or "pnpm-lock.yaml" or "yarn.lock" or "cargo.lock"
                 or "poetry.lock" or "gemfile.lock" or "composer.lock" || name.EndsWith(".lock", StringComparison.Ordinal))
        {
            return "lock file";
        }

        if (name.EndsWith(".min.js", StringComparison.Ordinal)
            || name.EndsWith(".min.css", StringComparison.Ordinal)
            || name.EndsWith(".map", StringComparison.Ordinal))
        {
            return "minified";
        }

        if (name.EndsWith(".g.cs", StringComparison.Ordinal)
            || name.EndsWith(".designer.cs", StringComparison.Ordinal)
            || name.Contains(".generated.", StringComparison.Ordinal))
        {
            return "generated";
        }

        //Build and dependency trees. Matched on a whole segment, so a file called "distances.cs"
        //is not mistaken for something under dist/.
        foreach (string segment in path.Split('/'))
        {
            if (segment is "obj" or "bin" or "node_modules" or "dist" or "vendor" or ".next")
                return "generated";
        }

        return null;
    }

    /// <summary>
    /// The text to send.
    /// </summary>
    /// <param name="unifiedDiff">
    /// Output of <c>git diff HEAD -- &lt;included paths&gt;</c>. Empty is legitimate — an unborn
    /// HEAD, or a change consisting only of excluded files — and produces a summary with no diff.
    /// </param>
    /// <param name="included">The files whose content is in <paramref name="unifiedDiff"/>.</param>
    /// <param name="maxDiffBytes">The user's cap. Clamped to <see cref="VerbatimCeilingBytes"/>.</param>
    public static DiffPayloadResult Build(
        string unifiedDiff,
        IReadOnlyList<GitFileChange> included,
        int maxDiffBytes)
    {
        var text = new StringBuilder();

        //A summary the model can rely on even when the diff below is truncated or absent.
        //Synthesised from the counts `--numstat -z` already gave us rather than from
        //`git diff --stat`, because CLAUDE.md forbids parsing human-readable Git output -- and
        //because it is one fewer process on a latency-critical path.
        foreach (GitFileChange file in included)
            text.Append(' ').Append(file.Path).Append(" | +").Append(file.AddedLines ?? 0).Append(" -").Append(file.RemovedLines ?? 0).Append('\n');

        bool truncated = false;
        int verbatimCeiling = Math.Min(maxDiffBytes, VerbatimCeilingBytes);

        if (unifiedDiff.Length > 0)
        {
            text.Append('\n');

            if (Encoding.UTF8.GetByteCount(unifiedDiff) <= verbatimCeiling)
            {
                text.Append(unifiedDiff);
            }
            else
            {
                text.Append(Shorten(unifiedDiff));
                truncated = true;
            }
        }

        string payload = text.ToString();

        //After the per-file cap, not instead of it: three hundred files of forty lines each is far
        //past the token ceiling, so the per-file rule alone does not honour it.
        if (Encoding.UTF8.GetByteCount(payload) > TokenCeilingBytes)
        {
            payload = ClampToLineBoundary(payload, TokenCeilingBytes);
            truncated = true;
        }

        //Last of all, and unconditionally.
        return new DiffPayloadResult(SecretDetector.Redact(payload), truncated);
    }

    /// <summary>
    /// Keeps each file's header and the first <see cref="HunkLinesPerFile"/> lines of its body.
    ///
    /// Split on <c>diff --git </c> at column zero, which is unambiguous: every line of a hunk body
    /// begins with a space, <c>+</c>, <c>-</c> or <c>\</c>.
    /// </summary>
    private static string Shorten(string unifiedDiff)
    {
        var result = new StringBuilder();
        int bodyLines = 0;
        bool markerPending = false;

        foreach (string line in unifiedDiff.Split('\n'))
        {
            bool startsFile = line.StartsWith("diff --git ", StringComparison.Ordinal);

            if (startsFile)
            {
                if (markerPending)
                    result.Append(TruncationMarker).Append('\n');

                bodyLines = 0;
                markerPending = false;
            }

            //The file's own headers always survive: without them the model cannot tell which file a
            //hunk belongs to, which is the one thing the summary above cannot supply.
            bool isHeader = startsFile
                            || line.StartsWith("index ", StringComparison.Ordinal)
                            || line.StartsWith("--- ", StringComparison.Ordinal)
                            || line.StartsWith("+++ ", StringComparison.Ordinal)
                            || line.StartsWith("new file", StringComparison.Ordinal)
                            || line.StartsWith("deleted file", StringComparison.Ordinal)
                            || line.StartsWith("rename ", StringComparison.Ordinal)
                            || line.StartsWith("similarity ", StringComparison.Ordinal);

            if (isHeader)
            {
                result.Append(line).Append('\n');
                continue;
            }

            if (bodyLines < HunkLinesPerFile)
            {
                result.Append(line).Append('\n');
                bodyLines++;
                continue;
            }

            markerPending = true;
        }

        if (markerPending)
            result.Append(TruncationMarker).Append('\n');

        return result.ToString();
    }

    /// <summary>
    /// Cuts to <paramref name="maxBytes"/> on a line boundary, so the tail is never half a line.
    /// </summary>
    private static string ClampToLineBoundary(string payload, int maxBytes)
    {
        var kept = new StringBuilder();
        int bytes = 0;

        foreach (string line in payload.Split('\n'))
        {
            int cost = Encoding.UTF8.GetByteCount(line) + 1;

            if (bytes + cost > maxBytes)
                break;

            kept.Append(line).Append('\n');
            bytes += cost;
        }

        return kept.Append(TruncationMarker).Append('\n').ToString();
    }
}

/// <param name="Text">Exactly what will be sent. Already capped, filtered and redacted.</param>
/// <param name="Truncated">True when the model is seeing less than the whole change.</param>
public sealed record DiffPayloadResult(string Text, bool Truncated);
