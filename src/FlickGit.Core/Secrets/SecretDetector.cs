using System.Text.RegularExpressions;

namespace FlickGit.Secrets;

/// <summary>
/// Two jobs, deliberately kept apart because they run at different moments and can
/// afford very different costs.
///
/// <b>Path matching</b> is cheap and runs on every status refresh, so a file whose
/// *name* says "secret" is unticked and flagged in the list before the user has read a
/// single row. CLAUDE.md, "Staging Defaults": "Files matching secret-detection patterns
/// are unchecked and flagged in red, even if tracked."
///
/// <b>Content matching</b> runs before a diff leaves the machine and again before a
/// commit — CLAUDE.md, "Privacy and secrets": "Run the secret detector before sending
/// **and** before committing."
///
/// This is a heuristic, and its failure mode is chosen: over-flagging costs the user one
/// click, under-flagging pushes a credential to a remote. Nothing here silently drops a
/// file from a commit — it only ever unticks a box the user can tick again.
/// </summary>
public static class SecretDetector
{
    /// <summary>
    /// File names and extensions that are secrets by convention rather than by content.
    /// Matched case-insensitively against the file name and, for the directory rules,
    /// against the whole repository-relative path.
    /// </summary>
    private static readonly Regex PathPattern = new(
        """
        (?ix)                                   # case-insensitive, verbose
        (?:^|/)                                 # start of path or a path segment
        (?:
            [\w-]*\.env(?:\.[\w.-]+)?           # .env, .env.local, secret.env, prod.env.local
          | secrets?\.(?:json|ya?ml|toml|config)
          | credentials?(?:\.\w+)?
          | appsettings\.(?!json$)[\w.-]*\.json # appsettings.Development.json, not the base file
          | id_(?:rsa|dsa|ecdsa|ed25519)        # private keys
          | \.npmrc | \.pypirc | \.netrc | _netrc
          | .*\.(?:pfx|p12|pem|key|keystore|jks|ppk)
        )$
        """,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Content patterns, each named so the warning can say what was found without
    /// quoting the value. Ordered cheapest-first is pointless here — every one of them
    /// runs — but naming them is not: "AWS access key ID in src/Options.cs" is
    /// actionable, "possible secret" is not.
    /// </summary>
    private static readonly (string Name, Regex Pattern)[] ContentPatterns =
    [
        ("AWS access key ID",
            new Regex(@"\b(?:AKIA|ASIA|ABIA|ACCA)[0-9A-Z]{16}\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),

        ("GitHub token",
            new Regex(@"\b(?:ghp|gho|ghu|ghs|ghr|github_pat)_[A-Za-z0-9_]{20,}\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),

        ("Anthropic API key",
            new Regex(@"\bsk-ant-[A-Za-z0-9_-]{20,}\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),

        ("OpenAI API key",
            new Regex(@"\bsk-(?:proj-)?[A-Za-z0-9]{32,}\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),

        ("Slack token",
            new Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),

        ("private key block",
            new Regex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY(?: BLOCK)?-----",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),

        ("connection string password",
            new Regex(@"(?i)\b(?:password|pwd)\s*=\s*[^\s;""']{4,}",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),

        ("credential in a URL",
            new Regex(@"\b[a-z][a-z0-9+.-]*://[^/\s:@]+:[^/\s:@]+@",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),

        ("assigned API key or token",
            //Deliberately narrow: an assignment, to a key-shaped name, of a long
            //literal. A broad /token/ match would flag half of any codebase that has an
            //auth module and train the user to ignore the flag.
            new Regex("""(?i)\b(?:api[_-]?key|secret[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret)\b\s*[:=]\s*["']?[A-Za-z0-9_\-./+]{16,}""",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),
    ];

    /// <summary>
    /// True when the path alone is enough to say "do not commit this by accident".
    /// </summary>
    /// <param name="repositoryRelativePath">Forward-slashed, as Git reports it.</param>
    public static bool LooksLikeSecretPath(string repositoryRelativePath)
    {
        if (string.IsNullOrEmpty(repositoryRelativePath))
            return false;

        return PathPattern.IsMatch(repositoryRelativePath.Replace('\\', '/'));
    }

    /// <summary>
    /// Every content pattern that matched, by name and position. Empty when clean.
    ///
    /// Returns positions rather than the matched text so a caller can highlight or
    /// redact without ever having to hold the secret itself in a log line or a message.
    /// </summary>
    public static IReadOnlyList<SecretMatch> Find(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var found = new List<SecretMatch>();

        foreach ((string name, Regex pattern) in ContentPatterns)
        {
            foreach (Match match in pattern.Matches(text))
                found.Add(new SecretMatch(name, match.Index, match.Length));
        }

        return found;
    }

    /// <summary>
    /// Replaces every match with a fixed marker, keeping the surrounding text intact.
    ///
    /// Used on anything about to be sent to an AI provider and on anything about to be
    /// written to the log. The marker is a constant, not the original length: padding a
    /// redaction to the secret's length leaks the secret's length.
    /// </summary>
    public static string Redact(string text)
    {
        IReadOnlyList<SecretMatch> matches = Find(text);
        if (matches.Count == 0)
            return text;

        //Right to left, so each replacement leaves earlier offsets valid. Overlaps are
        //skipped rather than merged -- two patterns matching the same credential is
        //common (a URL credential is also a password assignment), and replacing twice
        //would corrupt the string around it.
        var ordered = matches.OrderByDescending(m => m.Index).ToArray();
        var builder = new System.Text.StringBuilder(text);
        int lowestReplaced = int.MaxValue;

        foreach (SecretMatch match in ordered)
        {
            if (match.Index + match.Length > lowestReplaced)
                continue;

            builder.Remove(match.Index, match.Length);
            builder.Insert(match.Index, "[redacted]");
            lowestReplaced = match.Index;
        }

        return builder.ToString();
    }
}

/// <param name="Kind">Human-readable name of the pattern that matched, safe to display.</param>
/// <param name="Index">Character offset of the match in the scanned text.</param>
/// <param name="Length">Length of the match. Never shown to the user.</param>
public sealed record SecretMatch(string Kind, int Index, int Length);
