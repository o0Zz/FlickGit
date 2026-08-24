using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FlickGit.Secrets;

namespace FlickGit.Forges;

/// <param name="Sent">False when the request never reached a server at all.</param>
/// <param name="Status">The HTTP status, meaningless unless <paramref name="Sent"/>.</param>
/// <param name="Body">The response body, verbatim. Each client reads its own error shape out of it.</param>
/// <param name="TransportError">DNS, TLS, a proxy, a timeout. Already redacted.</param>
internal readonly record struct ForgeResponse(bool Sent, HttpStatusCode Status, string Body, string? TransportError)
{
    public bool Succeeded => Sent && (int)Status is >= 200 and < 300;

    /// <summary>
    /// The one failure with a remedy the window can offer: store a token.
    ///
    /// 403 counts as well as 401, and that is not a mistake — GitHub answers 403 for a token whose
    /// scopes do not cover pull requests, and Azure DevOps answers 401 with an HTML sign-in page.
    /// Both mean "this credential will not do", which is the same question to put to the user.
    /// </summary>
    public bool Unauthorised => Sent && Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

/// <summary>
/// Everything the three forge clients do identically: send the request, and explain a refusal.
///
/// The same split <see cref="Ai.AiEndpoint"/> makes, for the same reason — what differs between the
/// three is the URL, the authorisation header, the request body and which field of the answer
/// matters, and those are arguments. Without it, the timeout, the user agent, the redaction and the
/// status-code wording would each exist three times.
/// </summary>
internal static class ForgeApi
{
    /// <summary>
    /// How long one call may take.
    ///
    /// Generous compared with the AI's eight seconds, and deliberately so: this is not on a latency
    /// budget in CLAUDE.md's table, it is a one-off action the user has already committed to by
    /// pressing a button, and a corporate Azure DevOps Server behind a proxy is genuinely slow. It
    /// exists to stop a hung socket from leaving the window busy forever, not to pace anything.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// GitHub rejects a request with no user agent outright, and the other two log it. One value,
    /// set in one place, so a client cannot forget it.
    /// </summary>
    private static readonly ProductInfoHeaderValue UserAgent = new("FlickGit", "1.0");

    public static void Bearer(HttpRequestMessage request, string token) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    /// <summary>
    /// Azure DevOps' scheme: the token as the <i>password</i>, with an empty user name.
    ///
    /// Not a quirk worth working around — it is what the service documents, and it is why a personal
    /// access token pasted into a Bearer header there fails with a sign-in page rather than a clean
    /// 401.
    /// </summary>
    public static void Basic(HttpRequestMessage request, string token)
    {
        string encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + token));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }

    /// <summary>
    /// Sends one request and reads the whole answer.
    ///
    /// Buffered rather than streamed, unlike the AI path: these responses are one JSON object and
    /// there is nothing to render as it arrives.
    /// </summary>
    /// <param name="json">The request body, already serialised. Null for a GET.</param>
    /// <param name="authorise">Adds the credential header. The one thing that must not be logged.</param>
    public static async Task<ForgeResponse> SendAsync(
        HttpClient http,
        HttpMethod method,
        Uri url,
        string? json,
        Action<HttpRequestMessage> authorise,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        using var request = new HttpRequestMessage(method, url);

        if (json is not null)
        {
            //Serialised by the caller, never interpolated. A description is arbitrary Markdown full
            //of quotes, backslashes and newlines -- the same reason no Git command in this product
            //is built as a string.
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.UserAgent.Add(UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        authorise(request);

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);

            return new ForgeResponse(true, response.StatusCode, body, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ForgeResponse(
                false,
                default,
                string.Empty,
                $"The server did not answer within {Timeout.TotalSeconds:F0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            return new ForgeResponse(false, default, string.Empty, SecretDetector.Redact(ex.Message));
        }
    }

    /// <summary>
    /// Turns a refusal into a sentence naming the next action.
    ///
    /// CLAUDE.md, "Error Handling": the operation, the error, and a suggested next action, never
    /// "something went wrong". The status code carries the actionable half; the service's own
    /// message carries the specific half, which is why <paramref name="apiMessage"/> is pulled out of
    /// the body by the client that knows its shape rather than guessed at here.
    /// </summary>
    /// <param name="forge">What to call the service in the sentence.</param>
    /// <param name="host">Named in the 401, because the stored token is keyed by it.</param>
    public static string Describe(string forge, string host, ForgeResponse response, string? apiMessage)
    {
        if (!response.Sent)
            return $"{forge} could not be reached. {response.TransportError}";

        string detail = apiMessage is { Length: > 0 } ? " " + Summarise(apiMessage) : string.Empty;

        return response.Status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"{forge} refused the credential for {host}. It may be expired, or it may not carry "
                + $"permission to open pull requests.{detail}",

            //A 404 from an authenticated API is almost never "no such URL": all three hide a
            //repository the token cannot see behind one, so saying "not found" alone would send the
            //user looking for a typo that is not there.
            HttpStatusCode.NotFound =>
                $"{forge} has no repository at that address, or the credential cannot see it.{detail}",

            HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity =>
                $"{forge} refused the request.{detail}",

            HttpStatusCode.BadRequest => $"{forge} refused the request.{detail}",

            >= HttpStatusCode.InternalServerError =>
                $"{forge} is having trouble ({(int)response.Status}). Try again in a moment.{detail}",

            _ => $"{forge} returned {(int)response.Status}.{detail}".TrimEnd(),
        };
    }

    /// <summary>
    /// The sentence out of an error body, whichever of the six shapes the three services use.
    ///
    /// One lenient reader rather than a DTO per variant, because the variants are not a design —
    /// they are what each service happens to emit for each class of failure:
    ///
    /// <list type="bullet">
    /// <item><description>GitHub: <c>{"message": "Validation Failed", "errors": [{"message": "A pull
    /// request already exists…"}]}</c> — the useful half is in the array.</description></item>
    /// <item><description>GitLab: <c>message</c> is a string, an array of strings, <i>or</i> an
    /// object whose values are arrays, depending on whether it is a rejection or a validation
    /// failure.</description></item>
    /// <item><description>Azure DevOps: <c>{"message": "TF401179: An active pull request … already
    /// exists."}</c>, and an HTML sign-in page when the credential is wrong.</description></item>
    /// </list>
    ///
    /// Returns null for anything it cannot read, including HTML — <see cref="Describe"/> then says
    /// what the status code means, which is better than quoting a page of markup at the user.
    /// </summary>
    public static string? MessageFrom(string body)
    {
        if (body.Length == 0 || body.TrimStart().FirstOrDefault() is not ('{' or '['))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            //`errors` first: when both are present it is the specific one, and GitHub's `message`
            //beside it is the useless "Validation Failed".
            if (root.TryGetProperty("errors", out JsonElement errors) && Flatten(errors) is { Length: > 0 } detailed)
                return detailed;

            foreach (string key in (string[])["message", "error", "detail"])
            {
                if (root.TryGetProperty(key, out JsonElement value) && Flatten(value) is { Length: > 0 } text)
                    return text;
            }

            return null;
        }
        catch (JsonException)
        {
            //A body that is not the JSON its content type claimed. The status code still says
            //something useful, so this is not worth reporting as a failure of its own.
            return null;
        }
    }

    /// <summary>
    /// Whatever sentences are in <paramref name="element"/>, joined.
    ///
    /// Recursive because two of the shapes nest: an array of objects with a <c>message</c> each, and
    /// an object whose values are arrays of strings. Depth is bounded by the document, and a body
    /// this is called on is an error the server just wrote.
    /// </summary>
    private static string Flatten(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;

            case JsonValueKind.Array:
                return string.Join(' ', element.EnumerateArray().Select(Flatten).Where(t => t.Length > 0));

            case JsonValueKind.Object:
                //A `message` inside wins over the whole object: that is GitHub's error array, and
                //flattening the rest would emit field names and resource types as prose.
                if (element.TryGetProperty("message", out JsonElement message))
                    return Flatten(message);

                return string.Join(
                    ' ',
                    element.EnumerateObject().Select(p => Flatten(p.Value)).Where(t => t.Length > 0));

            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// A short, redacted fragment of whatever the service said.
    ///
    /// Redacted first and truncated second, exactly as the AI path does it: a token echoed back in a
    /// long body must not survive by being past the cut, and must not survive by being before it
    /// either.
    /// </summary>
    public static string Summarise(string text)
    {
        string clean = SecretDetector.Redact(text).Replace('\n', ' ').Replace('\r', ' ').Trim();

        return clean.Length <= 300 ? clean : clean[..300] + "…";
    }
}
