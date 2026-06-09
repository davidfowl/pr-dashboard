static class GitHubHttpRedirects
{
    public const int MaxRedirects = 3;

    public static bool TryGetRedirectUrl(HttpResponseMessage response, out string redirectUrl)
    {
        redirectUrl = "";
        var statusCode = (int)response.StatusCode;
        if (statusCode < 300 || statusCode >= 400 || response.Headers.Location is not { } location)
        {
            return false;
        }

        if (location.IsAbsoluteUri)
        {
            if (!location.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                || !location.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            redirectUrl = location.PathAndQuery.TrimStart('/');
            return true;
        }

        redirectUrl = location.OriginalString.TrimStart('/');
        return !string.IsNullOrWhiteSpace(redirectUrl);
    }
}
