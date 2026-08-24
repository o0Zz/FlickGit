using System.Text.Json;
using FlickGit.Logging;
using FlickGit.Secrets;

namespace FlickGit.Ai;

/// <summary>
/// The short-lived Copilot token, exchanged from the stored GitHub token and cached until it expires.
///
/// This class is the whole difference between Copilot and the other two providers. Anthropic and
/// OpenAI take the credential the user stored and put it in a header; Copilot will not accept that
/// credential at all. The GitHub token is only good for one thing — asking
/// <c>/copilot_internal/v2/token</c> for a token that <c>api.githubcopilot.com</c> does accept, which
/// then expires in under half an hour.
///
/// Separate from the generator rather than folded into it, against "do not split a class that is
/// easier to read whole": the generator reads like its two siblings precisely because the exchange is
/// not in the middle of it, and the expiry and the concurrency below are the only state anywhere in
/// the AI code.
/// </summary>
/// <param name="http">The pooled client, so the exchange shares the warm connection.</param>
/// <param name="gitHubToken">
/// The stored credential, read per call. Never held in a field — see <c>ApiKeyStore</c>: the only
/// thing this code can honestly control about a secret's lifetime is how long one exists.
/// </param>
public sealed class CopilotToken(HttpClient http, Func<string?> gitHubToken, ILog log)
{
    private const string Endpoint = "https://api.github.com/copilot_internal/v2/token";

    /// <summary>
    /// How early to treat the token as spent.
    ///
    /// A token that expires while the request is in flight fails the commit message, and the request
    /// itself is allowed eight seconds. Two minutes is comfortably clear of that and still leaves a
    /// typical token good for most of its life.
    /// </summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Serialises the exchange.
    ///
    /// The resident service can have a commit window and a speculative refresh asking at once, and
    /// two simultaneous exchanges would be two round trips to spend one token. Around the exchange
    /// only: a cache hit does not wait.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;
    private DateTime _expiresUtc;

    /// <summary>
    /// A usable Copilot token, from the cache when there is one.
    ///
    /// <see cref="DateTime.UtcNow"/> directly rather than an injected clock, following
    /// <c>RepositoryService</c> and <c>RepositoryOverviewCache</c>, which are the other two TTL caches
    /// in the product.
    /// </summary>
    public async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        if (_token is { Length: > 0 } cached && DateTime.UtcNow < _expiresUtc)
            return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            //Checked again inside the gate: whoever was ahead in the queue has just filled it.
            if (_token is { Length: > 0 } filled && DateTime.UtcNow < _expiresUtc)
                return filled;

            (string token, DateTime expiresUtc) = await ExchangeAsync(cancellationToken).ConfigureAwait(false);

            _token = token;
            _expiresUtc = expiresUtc;

            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets the cached token, so the next request exchanges a fresh one.</summary>
    public void Invalidate() => _expiresUtc = DateTime.MinValue;

    private async Task<(string Token, DateTime ExpiresUtc)> ExchangeAsync(CancellationToken cancellationToken)
    {
        string stored = gitHubToken()
            ?? throw new AiUnavailableException("No GitHub token is stored for Copilot.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(AiOptions.HardTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);

        //`token`, not `Bearer`. GitHub's own API accepts both spellings for most endpoints and this
        //one is documented nowhere, so it gets the spelling the editors send.
        request.Headers.Add("Authorization", $"token {stored}");
        request.Headers.Add("Accept", "application/json");
        CopilotHeaders.Identify(request);

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiUnavailableException(
                $"GitHub did not answer within {AiOptions.HardTimeout.TotalSeconds:F0} s.");
        }
        catch (HttpRequestException ex)
        {
            throw new AiUnavailableException(SecretDetector.Redact(ex.Message));
        }

        using (response)
        {
            //A 401 here means the *stored* token is wrong, and a 403 usually means the account has no
            //Copilot subscription. Those are different problems with different fixes, and
            //AiEndpoint.DescribeAsync would report both as a rejected API key -- which for this
            //provider would send the user to re-paste a token that was never the issue.
            if (!response.IsSuccessStatusCode)
                throw new AiUnavailableException(Describe(response.StatusCode));

            string body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);

            CopilotTokenResponse? parsed;

            try
            {
                parsed = JsonSerializer.Deserialize(body, AiJson.Default.CopilotTokenResponse);
            }
            catch (JsonException ex)
            {
                //Redacted: the body is a credential.
                log.Debug($"Unparseable Copilot token response: {ex.Message}");
                throw new AiUnavailableException("GitHub returned a Copilot token FlickGit could not read.");
            }

            if (parsed?.Token is not { Length: > 0 } token)
                throw new AiUnavailableException("GitHub returned no Copilot token for this account.");

            //`expires_at` is epoch seconds. Absent -- which it should never be -- is read as the
            //shortest life any real token has had, so a missing field costs an extra exchange rather
            //than a cached token that is already dead.
            DateTime expiresUtc = parsed.ExpiresAt > 0
                ? DateTimeOffset.FromUnixTimeSeconds(parsed.ExpiresAt).UtcDateTime - Margin
                : DateTime.UtcNow + TimeSpan.FromMinutes(5);

            log.Debug($"Exchanged a Copilot token, good for {(expiresUtc - DateTime.UtcNow).TotalMinutes:F0} min.");

            return (token, expiresUtc);
        }
    }

    private static string Describe(System.Net.HttpStatusCode status) => (int)status switch
    {
        401 => "GitHub rejected the stored token. Store a current one with `flick ai key set`.",

        403 => "This GitHub account has no Copilot subscription, or the token is not allowed to use it.",

        404 => "GitHub does not offer Copilot to this account.",

        >= 500 => $"GitHub is having trouble ({(int)status}). Try again in a moment.",

        _ => $"GitHub returned {(int)status} when asked for a Copilot token.",
    };
}

/// <summary>
/// The headers that tell GitHub which editor is asking.
///
/// Copilot's endpoints are built for editor integrations and refuse a request that does not say it is
/// one — a 400 with no useful body, which reads exactly like a bad model name. Both the exchange and
/// the completion carry them, so they are here rather than written twice.
///
/// A static because it is the thinnest possible wrapper over "the same three constants every time",
/// with nothing to substitute in a test.
/// </summary>
internal static class CopilotHeaders
{
    /// <summary>
    /// The integration this claims to be.
    ///
    /// <c>vscode-chat</c> rather than a name of our own: the id has to be one GitHub has issued, and
    /// an unrecognised one is refused outright. There is no registration process open to a per-user
    /// tool, so this is the honest state of the art rather than a shortcut — and it is why this
    /// provider is documented as riding an undocumented API.
    /// </summary>
    private const string IntegrationId = "vscode-chat";

    private const string EditorVersion = "vscode/1.99.0";

    private const string PluginVersion = "FlickGit/1.0.0";

    public static void Identify(HttpRequestMessage request)
    {
        request.Headers.Add("Copilot-Integration-Id", IntegrationId);
        request.Headers.Add("Editor-Version", EditorVersion);
        request.Headers.Add("Editor-Plugin-Version", PluginVersion);
        request.Headers.Add("User-Agent", PluginVersion);
    }
}
