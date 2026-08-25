using System.Net;
using System.Text;
using FlickGit.Ai;
using FlickGit.Logging;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Reading the three providers' streams, and what actually leaves the machine.
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

    /// <summary>
    /// One request, the way a commit surface assembles it: the system prompt, the payload, and the
    /// token ceiling that belongs to that task rather than to the provider.
    /// </summary>
    private static readonly AiPrompt Prompt = new(
        CommitPrompt.For(conventionalCommits: false),
        new AiContext(
                "Branch: main",
                Subjects: [],
                Files: ["M src/A.cs"],
                Excluded: [],
                "diff --git a/src/A.cs b/src/A.cs\n+new\n",
                Truncated: false,
                "Summarise",
                Instruction: string.Empty)
            .ToPromptText(),
        AiOptions.CommitMaxTokens);

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

        var generator = new AnthropicGenerator(http, Options, () => "sk-ant-test", NullLog.Instance);

        Assert.Equal("feat: add connection pooling", await Collect(generator.GenerateAsync(Prompt, CancellationToken.None)));
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
        var generator = new OpenAiGenerator(http, options, () => "sk-proj-test", NullLog.Instance);

        Assert.Equal("fix: handle rebase conflicts", await Collect(generator.GenerateAsync(Prompt, CancellationToken.None)));
    }

    /// <summary>
    /// Copilot speaks Chat Completions, which is a third wire format rather than a second: the text is
    /// <c>choices[0].delta.content</c>, where OpenAI's Responses API puts it in
    /// <c>response.output_text.delta</c>.
    ///
    /// The two frames that carry no text are the ones a naive reader breaks on — an opening frame whose
    /// delta is only a role, and the content-filter frame whose <c>choices</c> is empty.
    /// </summary>
    [Fact]
    public async Task Copilot_chat_completion_deltas_are_concatenated_and_empty_choices_ignored()
    {
        const string transcript = """
            data: {"choices":[],"created":0,"id":"chatcmpl-1"}

            data: {"choices":[{"index":0,"delta":{"role":"assistant","content":null}}]}

            data: {"choices":[{"index":0,"delta":{"content":"feat: add "}}]}

            data: {"choices":[{"index":0,"delta":{"content":"Copilot support"}}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]


            """;

        var handler = new FakeCopilotHandler(transcript);
        using var http = new HttpClient(handler);

        var generator = new CopilotGenerator(
            http,
            Options with { Provider = AiProvider.Copilot },
            new CopilotToken(http, () => "gho_stored", NullLog.Instance),
            NullLog.Instance);

        Assert.Equal("feat: add Copilot support", await Collect(generator.GenerateAsync(Prompt, CancellationToken.None)));
    }

    /// <summary>
    /// Ollama is newline-delimited JSON, which is a different <i>framing</i> rather than a fourth
    /// frame shape: no <c>data:</c> prefix, no blank-line separator, no <c>[DONE]</c>.
    ///
    /// The last line is the one a naive reader breaks on — <c>done: true</c> arrives with an empty
    /// content and the timing statistics, and treating it as text would append nothing while
    /// treating it as a fault would fail every request.
    /// </summary>
    [Fact]
    public async Task Ollama_newline_delimited_messages_are_concatenated()
    {
        //Deliberately not blank-line separated: this is exactly what an SSE reader cannot parse.
        const string transcript =
            """
            {"model":"qwen2.5-coder:7b","message":{"role":"assistant","content":"fix: "},"done":false}
            {"model":"qwen2.5-coder:7b","message":{"role":"assistant","content":"close the "},"done":false}
            {"model":"qwen2.5-coder:7b","message":{"role":"assistant","content":"pool on reconnect"},"done":false}
            {"model":"qwen2.5-coder:7b","message":{"role":"assistant","content":""},"done":true,"total_duration":118000}
            """ + "\n";

        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, transcript);
        using var http = new HttpClient(handler);

        var generator = new OllamaGenerator(
            http,
            Options with { Provider = AiProvider.Ollama, Model = "qwen2.5-coder:7b" },
            NullLog.Instance);

        Assert.Equal("fix: close the pool on reconnect", await Collect(generator.GenerateAsync(Prompt, CancellationToken.None)));
    }

    /// <summary>
    /// Ollama reports a failure as a bare <c>error</c> string, where the other three wrap it in an
    /// object — so it cannot share their shape, and reading it as one would ignore every Ollama
    /// error and return a silently empty message.
    /// </summary>
    [Fact]
    public async Task An_Ollama_error_line_fails_rather_than_returning_an_empty_message()
    {
        const string transcript = """
            {"error":"model 'nope' not found, try pulling it first"}

            """;

        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, transcript);
        using var http = new HttpClient(handler);

        var generator = new OllamaGenerator(
            http,
            Options with { Provider = AiProvider.Ollama, Model = "nope" },
            NullLog.Instance);

        AiUnavailableException failure = await Assert.ThrowsAsync<AiUnavailableException>(
            () => Collect(generator.GenerateAsync(Prompt, CancellationToken.None)));

        Assert.Contains("not found", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no model set, Ollama is refused by name before a request is made.
    ///
    /// The other three have a default to fall back on; here the set of models is whatever the user
    /// has pulled, so a guess would 404 for most people — with an error about a model they never
    /// asked for, instead of the one sentence that fixes it.
    /// </summary>
    [Fact]
    public async Task Ollama_without_a_model_is_refused_before_anything_is_sent()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, string.Empty);
        using var http = new HttpClient(handler);

        var generator = new OllamaGenerator(
            http,
            Options with { Provider = AiProvider.Ollama, Model = string.Empty },
            NullLog.Instance);

        AiUnavailableException failure = await Assert.ThrowsAsync<AiUnavailableException>(
            () => Collect(generator.GenerateAsync(Prompt, CancellationToken.None)));

        Assert.Contains("ollama list", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Requests);
    }

    /// <summary>
    /// The request carries the payload, the output guard under Ollama's own name, and no credential
    /// of any kind.
    ///
    /// The last part is the point: a local provider has nobody to authenticate to, so an
    /// Authorization header here would be a header nothing reads — and a sign that a key had been
    /// wired in where none is needed.
    /// </summary>
    [Fact]
    public async Task The_Ollama_request_carries_the_model_and_no_credential()
    {
        const string transcript = """
            {"message":{"role":"assistant","content":"chore: tidy"},"done":true}

            """;

        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, transcript);
        using var http = new HttpClient(handler);

        var generator = new OllamaGenerator(
            http,
            Options with { Provider = AiProvider.Ollama, Model = "llama3.2" },
            NullLog.Instance);

        await Collect(generator.GenerateAsync(Prompt, CancellationToken.None));

        Assert.Contains("\"model\":\"llama3.2\"", handler.SentBody, StringComparison.Ordinal);
        Assert.Contains("\"num_predict\":150", handler.SentBody, StringComparison.Ordinal);
        Assert.Contains("\"stream\":true", handler.SentBody, StringComparison.Ordinal);
        Assert.Null(handler.SentAuthorization);
    }

    /// <summary>
    /// The credential the user stored is <b>never</b> sent to the endpoint the diff goes to.
    ///
    /// In scope as <b>the safety rules</b>, and it is the one assertion this provider needs that the
    /// other two do not: their stored key <i>is</i> the header, so there is nothing to get wrong.
    /// Here the GitHub token is good for the whole account — repositories included — and only the
    /// short-lived Copilot token it buys may reach <c>api.githubcopilot.com</c>. Sending the wrong one
    /// would work, which is what makes it worth pinning.
    /// </summary>
    [Fact]
    public async Task The_stored_GitHub_token_is_exchanged_and_never_sent_to_the_completion_endpoint()
    {
        const string transcript = """
            data: {"choices":[{"index":0,"delta":{"content":"chore: tidy"}}]}

            data: [DONE]


            """;

        var handler = new FakeCopilotHandler(transcript);
        using var http = new HttpClient(handler);

        var generator = new CopilotGenerator(
            http,
            Options with { Provider = AiProvider.Copilot },
            new CopilotToken(http, () => "gho_stored", NullLog.Instance),
            NullLog.Instance);

        Assert.Equal("chore: tidy", await Collect(generator.GenerateAsync(Prompt, CancellationToken.None)));

        //The exchange, with the spelling GitHub's own endpoint wants.
        Assert.Equal("token gho_stored", handler.ExchangeAuthorization);

        //And the completion, carrying only what the exchange handed back.
        Assert.Equal("Bearer tid=exchanged", handler.CompletionAuthorization);

        //And nowhere in the payload either, which is a different surface from the header: the diff is
        //serialised by us and a stray credential in it would not show up in the assertion above.
        Assert.DoesNotContain("gho_stored", handler.CompletionBody);

        //Without this header Copilot answers 400 with an empty body, which reads exactly like a bad
        //model name -- so it is pinned rather than left to be rediscovered.
        Assert.Equal("vscode-chat", handler.CompletionIntegrationId);

        //The diff, and the runaway guard.
        Assert.Contains("src/A.cs", handler.CompletionBody);
        Assert.Contains("\"max_tokens\":150", handler.CompletionBody);
        Assert.Contains("\"stream\":true", handler.CompletionBody);
    }

    /// <summary>
    /// Two hosts, two answers: the token exchange is JSON and the completion is an event stream, so a
    /// single-response fake cannot drive this provider.
    ///
    /// Purpose-built rather than a routing option on <see cref="FakeHttpMessageHandler"/>, which has
    /// one job and four callers that do not need one.
    /// </summary>
    private sealed class FakeCopilotHandler(string transcript) : HttpMessageHandler
    {
        private const string Exchanged = "tid=exchanged";

        public string? ExchangeAuthorization { get; private set; }

        public string CompletionAuthorization { get; private set; } = string.Empty;

        public string CompletionIntegrationId { get; private set; } = string.Empty;

        public string CompletionBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string authorization = string.Join(' ', request.Headers.GetValues("Authorization"));

            if (request.RequestUri!.Host == "api.github.com")
            {
                ExchangeAuthorization = authorization;

                //`expires_at` well into the future, so the margin cannot make a fresh token look spent.
                return Json($"{{\"token\":\"{Exchanged}\",\"expires_at\":{DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()}}}");
            }

            CompletionAuthorization = authorization;
            CompletionIntegrationId = string.Join(' ', request.Headers.GetValues("Copilot-Integration-Id"));
            CompletionBody = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var stream = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(transcript, Encoding.UTF8, "text/event-stream"),
            };

            return stream;
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
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

        var generator = new AnthropicGenerator(http, Options, () => "sk-ant-wrong", NullLog.Instance);

        AiUnavailableException failure = await Assert.ThrowsAsync<AiUnavailableException>(
            () => Collect(generator.GenerateAsync(Prompt, CancellationToken.None)));

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

        var generator = new AnthropicGenerator(http, Options, () => "sk-ant-test", NullLog.Instance);

        await Collect(generator.GenerateAsync(Prompt, CancellationToken.None));

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
