// Persistent storage for push notification data. Kept behind an interface so routes, the
// detector, and tests depend on the contract rather than Blob Storage specifics.
//
// Concurrency: subscriptions, preferences, and user profiles are last-writer-wins upserts
// (a per-user, per-device write that rarely races). Dedupe state uses optimistic concurrency
// because the single detector reads-modifies-writes it every cycle; callers retry on a
// ConcurrencyConflict.
interface INotificationStore
{
    Task<IReadOnlyList<PushSubscriptionRecord>> GetSubscriptionsAsync(long userId, CancellationToken cancellationToken);

    Task UpsertSubscriptionAsync(long userId, PushSubscriptionRecord subscription, CancellationToken cancellationToken);

    Task<bool> RemoveSubscriptionAsync(long userId, string endpoint, CancellationToken cancellationToken);

    // Removes the given push endpoint from every user other than keepUserId. Called when a user
    // subscribes so a shared browser endpoint belongs to exactly one (the most recent) account,
    // preventing one user's review notifications from reaching whoever signed in next on the
    // same device. Returns the number of stale records deleted.
    Task<int> RemoveEndpointFromOtherUsersAsync(long keepUserId, string endpoint, CancellationToken cancellationToken);

    Task<NotificationPreferences> GetPreferencesAsync(long userId, CancellationToken cancellationToken);

    Task SavePreferencesAsync(long userId, NotificationPreferences preferences, CancellationToken cancellationToken);

    Task UpsertUserProfileAsync(NotificationUserProfile profile, CancellationToken cancellationToken);

    Task<IReadOnlyList<NotificationUserProfile>> ListUserProfilesAsync(CancellationToken cancellationToken);

    Task<NotificationDedupeStateResult> GetStateAsync(long userId, CancellationToken cancellationToken);

    // Returns true when the state was written, false when another writer changed it first
    // (the caller should re-read via GetStateAsync and retry).
    Task<bool> TrySaveStateAsync(
        long userId,
        NotificationDedupeState state,
        string? concurrencyToken,
        CancellationToken cancellationToken);
}
