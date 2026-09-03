using System.Net;
using FlickGit.Ai;
using FlickGit.App.Settings;
using FlickGit.Logging;

namespace FlickGit.App.Ai;

/// <summary>
/// The two things every host has to build before the AI is usable: the connection, and the right
/// generator for whatever provider is configured.
///
/// Here rather than in each composition root because there is nothing platform-specific about
/// either, and two copies of the provider switch is a provider that works on one platform and
/// silently falls through to <c>DisabledAiGenerator</c> on the other. Both are pure functions of
/// their arguments, which is the kind of static Hard Requirement 3 keeps.
/// </summary>
public static class AiHost
{
    /// <summary>
    /// One <see cref="HttpClient"/> for the process, kept warm.
    ///
    /// CLAUDE.md, "Latency": a cold TLS handshake is 100–300 ms, a third of the first-token budget,
    /// so the pool is told to keep connections far longer than it would by default. The timeout is
    /// infinite because the generation's own silence budget is what bounds a request — eight seconds
    /// without a frame — and an <see cref="HttpClient"/> timeout would cut a healthy long answer off
    /// mid-sentence instead.
    /// </summary>
    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
            EnableMultipleHttp2Connections = true,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,

            //RequestVersionOrLower, so ALPN negotiates h2 and falls back to 1.1 rather than failing
            //outright against a proxy that does not speak it.
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

    /// <summary>
    /// The generator for the configured provider.
    ///
    /// The key arrives as a delegate rather than as the store itself: the credential store is a
    /// platform facility — Credential Manager, Keychain — and <c>FlickGit.Core</c> deliberately
    /// cannot reach one.
    /// </summary>
    public static IAiGenerator For(AiConfiguration configuration, HttpClient http, ILog log) =>
        configuration.Provider switch
        {
            AiProvider.Anthropic => new AnthropicGenerator(
                http, configuration.Options, configuration.ReadKey, log),

            AiProvider.OpenAi => new OpenAiGenerator(
                http, configuration.Options, configuration.ReadKey, log),

            //Copilot is the one provider whose stored credential is not what gets sent, so it takes a
            //CopilotToken rather than the key delegate.
            AiProvider.Copilot => new CopilotGenerator(
                http,
                configuration.Options,
                new CopilotToken(http, configuration.ReadKey, log),
                log),

            //The local one. No key delegate at all: there is nobody to authenticate to.
            AiProvider.Ollama => new OllamaGenerator(http, configuration.Options, log),

            _ => new DisabledAiGenerator(),
        };
}
