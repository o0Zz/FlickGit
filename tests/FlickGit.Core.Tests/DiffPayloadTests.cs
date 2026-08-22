using System.Text;
using FlickGit.Ai;
using FlickGit.Models;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// What may leave the machine, and how much of it.
///
/// In scope under Hard Requirement 4 as <b>the safety rules</b>. "What may be sent to a third
/// party" is the same class of rule as "what may be staged": every other part of the AI feature is
/// a convenience, and this is the only one that can send a user's credentials to somebody else.
/// </summary>
public class DiffPayloadTests
{
    private static GitFileChange File(
        string path,
        int added = 3,
        int removed = 1,
        bool untracked = false,
        bool binary = false,
        bool secret = false) =>
        new()
        {
            Path = path,
            WorkTreeStatus = untracked ? GitChangeType.Untracked : GitChangeType.Modified,
            AddedLines = binary ? null : added,
            RemovedLines = binary ? null : removed,
            IsBinary = binary,
            IsUntracked = untracked,
            LooksLikeSecret = secret,
            IsSelected = true,
        };

    [Theory]
    [InlineData("package-lock.json", "lock file")]
    [InlineData("web/pnpm-lock.yaml", "lock file")]
    [InlineData("Cargo.lock", "lock file")]
    [InlineData("wwwroot/app.min.js", "minified")]
    [InlineData("wwwroot/site.min.css", "minified")]
    [InlineData("wwwroot/app.js.map", "minified")]
    [InlineData("src/Options.g.cs", "generated")]
    [InlineData("src/Form.Designer.cs", "generated")]
    [InlineData("src/obj/Debug/thing.cs", "generated")]
    [InlineData("node_modules/left-pad/index.js", "generated")]
    public void Lock_files_generated_code_and_minified_assets_are_never_included(string path, string reason)
    {
        Assert.Equal(reason, DiffPayload.ExclusionReason(File(path)));
    }

    /// <summary>
    /// Both routes to "this is a secret" are checked: the flag <c>StatusService</c> already set, and
    /// the path pattern itself. The second matters because this is a pure function and a caller
    /// might hand it a change it built by hand.
    /// </summary>
    [Fact]
    public void Secret_matching_paths_are_never_included()
    {
        Assert.Equal("secret pattern", DiffPayload.ExclusionReason(File(".env")));
        Assert.Equal("secret pattern", DiffPayload.ExclusionReason(File("config/appsettings.Development.json")));
        Assert.Equal("secret pattern", DiffPayload.ExclusionReason(File("keys/id_rsa")));

        //Flagged by the status refresh rather than by its name.
        Assert.Equal("secret pattern", DiffPayload.ExclusionReason(File("src/Options.cs", secret: true)));
    }

    /// <summary>Ordinary source is not excluded, or the feature would send nothing.</summary>
    [Fact]
    public void Ordinary_source_files_are_included()
    {
        Assert.Null(DiffPayload.ExclusionReason(File("src/GatewayClient.cs")));
        Assert.Null(DiffPayload.ExclusionReason(File("appsettings.json")));

        //"distances.cs" is not under dist/. The segment match is what keeps that true.
        Assert.Null(DiffPayload.ExclusionReason(File("src/distances.cs")));
    }

    /// <summary>
    /// Untracked content is in neither HEAD nor the index, so <c>git diff HEAD</c> has nothing to
    /// say about it — and binary content has nothing a commit message can use.
    /// </summary>
    [Fact]
    public void Untracked_and_binary_files_contribute_no_content()
    {
        Assert.Equal("untracked", DiffPayload.ExclusionReason(File("scratch/dump.json", untracked: true)));
        Assert.Equal("binary", DiffPayload.ExclusionReason(File("assets/logo.png", binary: true)));
    }

    [Fact]
    public void A_diff_under_the_ceiling_is_sent_verbatim()
    {
        const string diff = "diff --git a/src/A.cs b/src/A.cs\n@@ -1 +1 @@\n-old\n+new\n";

        DiffPayloadResult result = DiffPayload.Build(diff, [File("src/A.cs")], DiffPayload.VerbatimCeilingBytes);

        Assert.False(result.Truncated);
        Assert.Contains("-old", result.Text);
        Assert.Contains("+new", result.Text);

        //The summary is always there, so the model can rely on it even when the diff is not.
        Assert.Contains("src/A.cs | +3 -1", result.Text);
    }

    /// <summary>
    /// Above the ceiling each file keeps its headers, forty body lines and a marker. The headers
    /// matter: without them the model cannot tell which file a hunk belongs to.
    /// </summary>
    [Fact]
    public void Above_the_ceiling_each_file_keeps_forty_lines_and_a_truncated_marker()
    {
        var diff = new StringBuilder();

        foreach (string name in new[] { "A", "B" })
        {
            diff.Append($"diff --git a/src/{name}.cs b/src/{name}.cs\n");
            diff.Append("index 1111111..2222222 100644\n");
            diff.Append($"--- a/src/{name}.cs\n+++ b/src/{name}.cs\n");

            for (int i = 0; i < 300; i++)
                diff.Append($"+line {name} {i} padded out so the whole thing is comfortably over twelve kilobytes\n");
        }

        DiffPayloadResult result = DiffPayload.Build(
            diff.ToString(),
            [File("src/A.cs"), File("src/B.cs")],
            DiffPayload.VerbatimCeilingBytes);

        Assert.True(result.Truncated);

        //Both files still identified, and both truncated.
        Assert.Contains("a/src/A.cs", result.Text);
        Assert.Contains("a/src/B.cs", result.Text);
        Assert.Equal(2, CountOccurrences(result.Text, "[truncated]"));

        //Forty body lines each: line 39 survives, line 40 does not.
        Assert.Contains("+line A 39 ", result.Text);
        Assert.DoesNotContain("+line A 40 ", result.Text);
    }

    /// <summary>
    /// The token ceiling is applied <i>after</i> the per-file cap, not instead of it.
    ///
    /// Three hundred files of forty lines each is far past 4,000 tokens, so the per-file rule alone
    /// does not honour the ceiling CLAUDE.md sets — and latency is what the ceiling protects.
    /// </summary>
    [Fact]
    public void The_token_ceiling_is_applied_after_the_per_file_cap()
    {
        var diff = new StringBuilder();
        var files = new List<GitFileChange>();

        for (int f = 0; f < 300; f++)
        {
            files.Add(File($"src/File{f}.cs"));
            diff.Append($"diff --git a/src/File{f}.cs b/src/File{f}.cs\n");

            for (int i = 0; i < 40; i++)
                diff.Append($"+something changed on line {i} of file {f}\n");
        }

        DiffPayloadResult result = DiffPayload.Build(diff.ToString(), files, DiffPayload.VerbatimCeilingBytes);

        Assert.True(result.Truncated);
        Assert.True(
            Encoding.UTF8.GetByteCount(result.Text) <= DiffPayload.TokenCeilingBytes,
            $"payload was {Encoding.UTF8.GetByteCount(result.Text)} bytes, ceiling is {DiffPayload.TokenCeilingBytes}");
    }

    /// <summary>
    /// A credential pasted into an ordinary source file.
    ///
    /// Path exclusion cannot catch this — the file is called <c>Options.cs</c> and looks entirely
    /// innocent — so redaction runs over the whole payload as the last step before it is sent.
    /// </summary>
    [Fact]
    public void A_content_secret_inside_an_included_file_is_redacted()
    {
        const string leaked = "AKIAIOSFODNN7EXAMPLE";
        string diff = $"diff --git a/src/Options.cs b/src/Options.cs\n@@ -1 +1 @@\n+var key = \"{leaked}\";\n";

        DiffPayloadResult result = DiffPayload.Build(diff, [File("src/Options.cs")], DiffPayload.VerbatimCeilingBytes);

        Assert.DoesNotContain(leaked, result.Text);
        Assert.Contains("[redacted]", result.Text);
    }

    /// <summary>The user's own cap wins when it is lower than CLAUDE.md's 12 KB.</summary>
    [Fact]
    public void A_lower_user_cap_is_honoured()
    {
        var diff = new StringBuilder("diff --git a/src/A.cs b/src/A.cs\n");

        for (int i = 0; i < 200; i++)
            diff.Append($"+line {i}\n");

        DiffPayloadResult result = DiffPayload.Build(diff.ToString(), [File("src/A.cs")], maxDiffBytes: 256);

        Assert.True(result.Truncated);
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;

        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
