static class PushSubscriptionEndpointValidator
{
    // Push endpoints come from the browser, but the server later POSTs to them when sending a
    // notification. Keep that URL constrained to documented/current browser push services so an
    // authenticated user cannot turn /subscribe + /test into a blind SSRF primitive. These
    // origins come from the vendor push services returned by PushManager.subscribe() and tracked
    // by https://github.com/pushpad/known-push-services: Google FCM (Chrome/Chromium), Mozilla
    // Autopush (Firefox), Apple Web Push (Safari/iOS), and Microsoft WNS endpoints for Edge on
    // Windows.
    private static readonly HashSet<string> s_allowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "fcm.googleapis.com",
        "updates.push.services.mozilla.com",
        "web.push.apple.com"
    };

    public static bool IsAllowed(Uri endpoint)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps
            || !endpoint.IsDefaultPort
            || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            return false;
        }

        var host = endpoint.IdnHost;
        return s_allowedHosts.Contains(host)
            || host.EndsWith(".notify.windows.com", StringComparison.OrdinalIgnoreCase);
    }
}
