/// <summary>
/// Helpers for detecting GitHub's SAML single sign-on (SSO) enforcement signal and turning it into a
/// user-actionable authorization URL.
///
/// When a member of a SAML-protected organization (for example <c>microsoft</c>) calls the API with an
/// OAuth token that has not been SSO-authorized for that org, GitHub does not error in the usual way —
/// it withholds the organization's data and signals the requirement out-of-band:
/// <list type="bullet">
/// <item><description>REST: an <c>X-GitHub-SSO</c> response header (<c>required; url=...</c> on a 403, or
/// <c>partial-results; organizations=...</c> on an otherwise-successful 200 where org rows were silently
/// dropped).</description></item>
/// <item><description>GraphQL: a <c>FORBIDDEN</c> error whose message mentions SAML / single sign-on.</description></item>
/// </list>
/// </summary>
static class GitHubSamlSso
{
    public const string HeaderName = "X-GitHub-SSO";

    private static readonly string[] s_ownerPathPrefixes = ["repos/", "orgs/", "users/"];
    private static readonly string[] s_searchQualifierKeys = ["repo:", "org:", "user:"];
    private static readonly char[] s_segmentTerminators = ['/', '?', '&', '#'];
    private static readonly char[] s_ownerTokenTerminators = ['/', ' ', '+', '&', '#', ',', '"'];

    /// <summary>
    /// Reads the <c>X-GitHub-SSO</c> response header. Returns <see langword="true"/> when present.
    /// </summary>
    /// <param name="authorizationUrl">The <c>url=</c> directive when GitHub returned a <c>required</c> challenge.</param>
    /// <param name="partialResults">True when the header is the <c>partial-results</c> form, meaning org rows were filtered out of a successful response.</param>
    public static bool TryParseHeader(HttpResponseMessage? response, out string? authorizationUrl, out bool partialResults)
    {
        authorizationUrl = null;
        partialResults = false;

        if (response is null || !response.Headers.TryGetValues(HeaderName, out var values))
        {
            return false;
        }

        var raw = string.Join(", ", values);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        partialResults = raw.Contains("partial-results", StringComparison.OrdinalIgnoreCase);
        authorizationUrl = ExtractDirectiveUrl(raw);
        return true;
    }

    /// <summary>
    /// True when an error message is GitHub's SAML enforcement wording. Intentionally specific so that
    /// generic 403s (for example "Resource not accessible by integration") are not misclassified.
    /// </summary>
    public static bool IsSamlMessage(string? message) =>
        !string.IsNullOrEmpty(message)
        && (message.Contains("SAML", StringComparison.OrdinalIgnoreCase)
            || message.Contains("single sign-on", StringComparison.OrdinalIgnoreCase)
            || message.Contains("single sign on", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Prefers the authorization URL GitHub handed back in the header; otherwise derives the org SSO URL
    /// from the organization name when one is known.
    /// </summary>
    public static string? BuildAuthorizationUrl(string? headerUrl, string? organization)
    {
        if (!string.IsNullOrWhiteSpace(headerUrl))
        {
            return headerUrl;
        }

        return string.IsNullOrWhiteSpace(organization)
            ? null
            : $"https://github.com/orgs/{Uri.EscapeDataString(organization)}/sso";
    }

    public static string BuildMessage(string? organization)
    {
        var org = string.IsNullOrWhiteSpace(organization)
            ? "this GitHub organization"
            : $"the {organization} organization";

        return $"GitHub single sign-on (SSO) authorization is required to access {org}. "
            + "Authorize this app for the organization on GitHub, then sign in again.";
    }

    /// <summary>
    /// Best-effort extraction of an organization (owner) name from a GitHub REST request URL so a
    /// <c>partial-results</c> signal (which carries no org name) can still be turned into a usable
    /// authorization URL. Handles <c>repos/{owner}/...</c>, <c>orgs/{owner}/...</c>,
    /// <c>users/{owner}/...</c>, and search queries (<c>?q=repo:{owner}/{repo}</c>, <c>org:{owner}</c>).
    /// </summary>
    public static string? TryExtractOrgHint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var path = url;

        var schemeIndex = path.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            var firstSlash = path.IndexOf('/', schemeIndex + 3);
            path = firstSlash >= 0 ? path[(firstSlash + 1)..] : string.Empty;
        }

        path = path.TrimStart('/');

        foreach (var prefix in s_ownerPathPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var owner = ReadToken(path[prefix.Length..], s_segmentTerminators);
                if (!string.IsNullOrEmpty(owner))
                {
                    return owner;
                }
            }
        }

        var queryIndex = path.IndexOf("?q=", StringComparison.OrdinalIgnoreCase);
        if (queryIndex < 0)
        {
            queryIndex = path.IndexOf("&q=", StringComparison.OrdinalIgnoreCase);
        }

        if (queryIndex >= 0)
        {
            var query = Uri.UnescapeDataString(path[(queryIndex + 3)..]);
            foreach (var key in s_searchQualifierKeys)
            {
                var keyIndex = query.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (keyIndex >= 0)
                {
                    var owner = ReadToken(query[(keyIndex + key.Length)..], s_ownerTokenTerminators);
                    if (!string.IsNullOrEmpty(owner))
                    {
                        return owner;
                    }
                }
            }
        }

        return null;
    }

    private static string? ExtractDirectiveUrl(string headerValue)
    {
        const string marker = "url=";
        var index = headerValue.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        var end = headerValue.IndexOf(';', start);
        var url = (end < 0 ? headerValue[start..] : headerValue[start..end]).Trim();
        if (url.Length == 0)
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out _) ? url : null;
    }

    private static string ReadToken(string value, char[] terminators)
    {
        var end = value.IndexOfAny(terminators);
        var token = end < 0 ? value : value[..end];
        return token.Trim();
    }
}
