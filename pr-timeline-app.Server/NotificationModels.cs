using System.Security.Cryptography;
using System.Text;

// The signed-in GitHub identity. Subscriptions, preferences, and dedupe state are keyed by
// the numeric Id (stable across login renames). Login is a mutable matching field used to
// pair the user with a PR's requested reviewers and is refreshed on every authenticated call.
sealed record NotificationUser(long Id, string Login);

// A single browser push subscription. Mirrors the shape returned by the browser
// PushSubscription.toJSON(), plus server-side bookkeeping. Only the minimum required to send
// is stored; full endpoints/keys are never logged.
sealed class PushSubscriptionRecord
{
    public required string Endpoint { get; set; }

    public required string P256dh { get; set; }

    public required string Auth { get; set; }

    public long? ExpirationTime { get; set; }

    // The VAPID key id active when this subscription was created. A mismatch with the current
    // server key id tells the client it must re-subscribe.
    public string? KeyId { get; set; }

    public string? UserAgent { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    // Stable id derived from the endpoint so the same device updates one blob instead of
    // accumulating duplicates. The endpoint itself is never used as a blob path segment.
    public static string CreateId(string endpoint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint))).ToLowerInvariant();
}

// Per-user trigger toggles. v1 ships only review_requested; the shape is intentionally open
// so the deferred v2 triggers can be added without a storage migration.
sealed class NotificationPreferences
{
    // Default ON: the whole point of opting in is to learn when you are asked to review.
    public bool ReviewRequested { get; set; } = true;

    public static NotificationPreferences CreateDefault() => new();
}

// Lightweight directory entry the detector enumerates to learn which users have opted in and
// what login to match against requested reviewers.
sealed class NotificationUserProfile
{
    public long Id { get; set; }

    public string Login { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}

// Transition-dedupe state. eventKey -> last observed fingerprint + when we last notified.
// A notification only fires when the fingerprint changes (e.g. the user newly becomes a
// requested reviewer), which suppresses repeats while allowing re-notify after removal.
sealed class NotificationDedupeState
{
    public Dictionary<string, NotificationEventState> Events { get; set; } =
        new(StringComparer.Ordinal);
}

sealed class NotificationEventState
{
    public string Fingerprint { get; set; } = string.Empty;

    public DateTimeOffset LastNotifiedAt { get; set; }
}

// State plus the concurrency token used to guard the read-modify-write. ConcurrencyToken is
// null when no state blob exists yet (create-only write).
readonly record struct NotificationDedupeStateResult(
    NotificationDedupeState State,
    string? ConcurrencyToken);

// ---- Endpoint request/response contracts ----

sealed record VapidPublicKeyResponse(string PublicKey, string KeyId);

sealed record NotificationPreferencesDto(bool ReviewRequested);

sealed record PushSubscriptionKeysDto(string? P256dh, string? Auth);

sealed record PushSubscriptionDto(string? Endpoint, long? ExpirationTime, PushSubscriptionKeysDto? Keys);

sealed record UnsubscribeRequest(string? Endpoint);

sealed record TestNotificationResponse(int Sent, int Failed, int Expired);
