using System.Text.Json;

// Outcome of attempting to deliver one push message. Expired means the push service reported
// the subscription is gone (404/410) and the caller should delete it.
enum PushDeliveryOutcome
{
    Sent,
    Expired,
    Failed,
    Disabled
}

readonly record struct PushDeliveryResult(PushDeliveryOutcome Outcome, int? StatusCode);

// Sends an already-built JSON payload to a single subscription. Behind an interface so the
// detector and the /test endpoint share one implementation and tests can substitute a fake.
interface IPushSender
{
    bool IsEnabled { get; }

    Task<PushDeliveryResult> SendAsync(
        PushSubscriptionRecord subscription,
        string payloadJson,
        CancellationToken cancellationToken);
}

// The notification payload contract shared with the service worker (src/sw.ts). Property
// names serialize to lowercase, matching the fields the SW reads in its `push` handler.
sealed record NotificationPayload(string Title, string Body, string Url, string Tag, string Icon);

static class NotificationPayloads
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private const string DefaultIcon = "/pwa-192x192.png";

    public static string Test() =>
        Serialize(new NotificationPayload(
            Title: "PR Focus",
            Body: "Test notification — push is working. \uD83C\uDF89",
            Url: "/",
            Tag: "pr-focus-test",
            Icon: DefaultIcon));

    public static string ReviewRequested(string repository, int number, string title, string url) =>
        Serialize(new NotificationPayload(
            Title: $"Review requested · {repository}#{number}",
            Body: string.IsNullOrWhiteSpace(title) ? "You were added as a reviewer." : title,
            Url: url,
            // Coalesce repeats for the same PR into one notification slot.
            Tag: $"review-requested:{repository}#{number}",
            Icon: DefaultIcon));

    private static string Serialize(NotificationPayload payload) =>
        JsonSerializer.Serialize(payload, s_jsonOptions);
}
