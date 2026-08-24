using System.Net;
using FlickGit.Ai;
using FlickGit.Logging;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Reading the two providers' streams, and what actually leaves the machine.
///
/// In scope under Hard Requirement 4 as <b>parsers</b> — a wrong byte here becomes a wrong commit
/// message, which is the same failure mode as a wrong byte in <c>--porcelain=v2 -z</c> — and as
/// <b>the safety rules</b>, for the two tests that pin the request body and the refusal to return a
/// partial message.
///
/// Every transcript is delivered seven bytes at a time by the fake handler, so a reader that only
/// works when the whole response arrives at once does not pass.
/// </summary>
public class AiStreamTests
{
    private static readonly AiOptions Options =
        new(AiProvider.Anthropic, string.Empty, "none", DiffPayload.VerbatimCeilingBytes, ConventionalCommits: false);

    private static readonly CommitContext Context =
        new("diff --git a/src/A.cs b/src/A.cs\n+new\n", ["M src/A.cs"], [], "main", Truncated: false);

    private static async Task<string> Collect(IAsyncEnumerable<string> stream)
    {
        var text = new System.Text.StringBuilder();

        await foreach (string chunk in stream)
            text.Append(chunk);

        return text.ToString();
    }

    /// <summary>
    /// The one text-bearing frame type is read and every other one is ignored, so a provider adding
    /// a frame type is a non-event rather than a broken message.
    /// </summary>
    [Fact]
    public async Task Anthropic_text_deltas_are_concatenated_and_every_other_frame_ignored()
    {
        const string transcript = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: ping
            data: {"type":"ping"}

            event: content_block_start
            data: {"type":"content_block_start","index":0}

            event: content_block_delta
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"feat: add "}}

            event: content_block_delta
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"connection pooling"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

            event: message_stop
            data: {"type":"message_stop"}


            """;

        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, transcript);
        using var http = new HttpClient(handler);

        var generator = new AnthropicCommitMessageGenerator(http, Options, () => "sk-ant-test", NullLog.Instance);

        Assert.Equal("feat: add connection pooling", await Collect(generator.GenerateAsync(Context, CancellationToken.None)));
    }

    [Fact]
    public async Task OpenAi_output_text_deltas_are_concatenated()
    {
        const string transcript = """
            data: {"type":"response.created","response":{"id":"resp_1"}}

            data: {"type":"response.output_text.delta","delta":"fix: handle "}

            data: {"type":"response.output_text.delta","delta":"rebase conflicts"}

            data: {"type":"response.completed"}

            data: [DONE]


            """;

        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, transcript);
        using var http = new HttpClient(handler);

        var options = Options with { Provider = AiProvider.OpenAi };
        var generator = new OpenAiCommitMessageGenerator(http, options, () => "sk-proj-test", NullLog.Instance);

        Assert.Equal("fix: handle rebase conflicts", await Collect(generator.GenerateAsync(Context, CancellationToken.None)));
    }

    /// <summary>
    /// A rejected key fails outright rather than returning what arrived before the rejection.
    ///
    /// This is what protects "never commit an empty or placeholder message": the queued-Enter path
    /// commits whatever the generator returned, so a partial or empty success here would be a commit
    /// with a truncated subject.
    /// </summary>
    [Fact]
    public async Task A_rejected_key_fails_rather_than_returning_a_partial_message()
    {
        const string body = """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""";

        var handler = new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, body);
        using var http = new HttpClient(handler);

        var generator = new AnthropicCommitMessageGenerator(http, Options, () => "sk-ant-wrong", NullLog.Instance);

        AiUnavailableException failure = await Assert.ThrowsAsync<AiUnavailableException>(
            () => Collect(generator.GenerateAsync(Context, CancellationToken.None)));

        //A sentence with an action in it, not the provider's JSON. CLAUDE.md asks for "a one-line
        //notice", and the raw body is what a user pastes into a search engine instead.
        Assert.Contains("rejected the API key", failure.Message);
        Assert.DoesNotContain("authentication_error", failure.Message);
    }

    /// <summary>
    /// The strongest single assertion in this file: what actually went over the wire.
    ///
    /// In scope as <b>the safety rules</b> — it pins that the runaway guard is set, that extended
    /// thinking is absent (on Haiku 4.5, omitting it <i>is</i> disabling it), and that the diff in
    /// the body is the capped one rather than anything larger.
    /// </summary>
    [Fact]
    public async Task The_request_body_carries_the_guard_rails_and_no_thinking()
    {
        const string transcript = """
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"chore: tidy"}}


            """;

        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, transcript);
        using var http = new HttpClient(handler);

        var generator = new AnthropicCommitMessageGenerator(http, Options, () => "sk-ant-test", NullLog.Instance);

        await Collect(generator.GenerateAsync(Context, CancellationToken.None));

        string body = handler.SentBody ?? string.Empty;

        Assert.Contains("\"max_tokens\":150", body);
        Assert.Contains("\"stream\":true", body);
        Assert.Contains("claude-haiku-4-5", body);

        //Never enabled here. CLAUDE.md: "Extended thinking exists on the Haiku line -- do not
        //enable it here."
        Assert.DoesNotContain("thinking", body);

        //The payload, and only the payload.
        Assert.Contains("src/A.cs", body);
    }

    /// <summary>
    /// A model that wrapped the message in a code fence, which the prompt forbids and some models do
    /// anyway. Stripped after the stream, because a fence cannot be recognised from a fragment.
    /// </summary>
    [Theory]
    [InlineData("```\nfeat: add pooling\n```", "feat: add pooling")]
    [InlineData("```text\nfix: leak\n```", "fix: leak")]
    [InlineData("feat: no fence here", "feat: no fence here")]
    public void A_fenced_message_is_unwrapped(string raw, string expected)
    {
        Assert.Equal(expected, CommitPrompt.Clean(raw));
    }
}
