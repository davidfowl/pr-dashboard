using System.Collections.Concurrent;
using System.Text.Json;

namespace pr_timeline_app.Tests;

// In-memory INotificationStore used to exercise route/detector logic and the optimistic
// concurrency helper without Blob Storage. State carries a version token so conflicts can be
// simulated deterministically.
internal sealed class InMemoryNotificationStore : INotificationStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<long, Dictionary<string, PushSubscriptionRecord>> subscriptions = new();
    private readonly ConcurrentDictionary<long, NotificationPreferences> preferences = new();
    private readonly ConcurrentDictionary<long, NotificationUserProfile> profiles = new();
    private readonly ConcurrentDictionary<long, (string Json, int Version)> states = new();

    // Runs once inside the next TrySaveStateAsync, before the version check, so a test can
    // simulate a competing writer and force a single conflict.
    public Action? OnBeforeNextStateSave { get; set; }

    public Task<IReadOnlyList<PushSubscriptionRecord>> GetSubscriptionsAsync(long userId, CancellationToken cancellationToken)
    {
        var map = subscriptions.GetValueOrDefault(userId);
        if (map is null)
        {
            return Task.FromResult<IReadOnlyList<PushSubscriptionRecord>>([]);
        }

        // Copy under the same lock Upsert/Remove take so enumerating Values can't race a
        // concurrent write (which would throw InvalidOperationException and flake the tests).
        lock (map)
        {
            IReadOnlyList<PushSubscriptionRecord> result = [.. map.Values];
            return Task.FromResult(result);
        }
    }

    public Task UpsertSubscriptionAsync(long userId, PushSubscriptionRecord subscription, CancellationToken cancellationToken)
    {
        var map = subscriptions.GetOrAdd(userId, _ => new Dictionary<string, PushSubscriptionRecord>());
        lock (map)
        {
            map[PushSubscriptionRecord.CreateId(subscription.Endpoint)] = subscription;
        }

        return Task.CompletedTask;
    }

    public Task<bool> RemoveSubscriptionAsync(long userId, string endpoint, CancellationToken cancellationToken)
    {
        var map = subscriptions.GetValueOrDefault(userId);
        if (map is null)
        {
            return Task.FromResult(false);
        }

        lock (map)
        {
            return Task.FromResult(map.Remove(PushSubscriptionRecord.CreateId(endpoint)));
        }
    }

    public Task<int> RemoveEndpointFromOtherUsersAsync(long keepUserId, string endpoint, CancellationToken cancellationToken)
    {
        var id = PushSubscriptionRecord.CreateId(endpoint);
        var removed = 0;
        foreach (var (userId, map) in subscriptions)
        {
            if (userId == keepUserId)
            {
                continue;
            }

            lock (map)
            {
                if (map.Remove(id))
                {
                    removed++;
                }
            }
        }

        return Task.FromResult(removed);
    }

    public Task<NotificationPreferences> GetPreferencesAsync(long userId, CancellationToken cancellationToken) =>
        Task.FromResult(preferences.GetValueOrDefault(userId) ?? NotificationPreferences.CreateDefault());

    public Task SavePreferencesAsync(long userId, NotificationPreferences value, CancellationToken cancellationToken)
    {
        preferences[userId] = value;
        return Task.CompletedTask;
    }

    public Task UpsertUserProfileAsync(NotificationUserProfile profile, CancellationToken cancellationToken)
    {
        profiles[profile.Id] = profile;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NotificationUserProfile>> ListUserProfilesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NotificationUserProfile>>([.. profiles.Values]);

    public Task<NotificationDedupeStateResult> GetStateAsync(long userId, CancellationToken cancellationToken)
    {
        if (!states.TryGetValue(userId, out var entry))
        {
            return Task.FromResult(new NotificationDedupeStateResult(new NotificationDedupeState(), null));
        }

        var clone = JsonSerializer.Deserialize<NotificationDedupeState>(entry.Json, s_jsonOptions)!;
        return Task.FromResult(new NotificationDedupeStateResult(clone, entry.Version.ToString()));
    }

    public Task<bool> TrySaveStateAsync(long userId, NotificationDedupeState state, string? concurrencyToken, CancellationToken cancellationToken)
    {
        var hook = OnBeforeNextStateSave;
        if (hook is not null)
        {
            OnBeforeNextStateSave = null;
            hook();
        }

        var json = JsonSerializer.Serialize(state, s_jsonOptions);
        var exists = states.TryGetValue(userId, out var current);

        if (concurrencyToken is null)
        {
            if (exists)
            {
                return Task.FromResult(false);
            }

            states[userId] = (json, 1);
            return Task.FromResult(true);
        }

        if (!exists || current.Version.ToString() != concurrencyToken)
        {
            return Task.FromResult(false);
        }

        states[userId] = (json, current.Version + 1);
        return Task.FromResult(true);
    }

    // Test helper to bump state out-of-band, simulating a concurrent writer.
    public void ForceStateBump(long userId)
    {
        var entry = states.GetValueOrDefault(userId);
        var state = entry.Json is null
            ? new NotificationDedupeState()
            : JsonSerializer.Deserialize<NotificationDedupeState>(entry.Json, s_jsonOptions)!;
        states[userId] = (JsonSerializer.Serialize(state, s_jsonOptions), entry.Json is null ? 1 : entry.Version + 1);
    }
}

public sealed class NotificationStoreTests
{
    [Fact]
    public async Task SubscriptionsRoundTripAndRemove()
    {
        var store = new InMemoryNotificationStore();
        var record = new PushSubscriptionRecord
        {
            Endpoint = "https://push.example/abc",
            P256dh = "p",
            Auth = "a"
        };

        await store.UpsertSubscriptionAsync(7, record, TestContext.Current.CancellationToken);
        Assert.Single(await store.GetSubscriptionsAsync(7, TestContext.Current.CancellationToken));

        // Same endpoint upserts in place rather than duplicating.
        await store.UpsertSubscriptionAsync(7, record, TestContext.Current.CancellationToken);
        Assert.Single(await store.GetSubscriptionsAsync(7, TestContext.Current.CancellationToken));

        Assert.True(await store.RemoveSubscriptionAsync(7, record.Endpoint, TestContext.Current.CancellationToken));
        Assert.Empty(await store.GetSubscriptionsAsync(7, TestContext.Current.CancellationToken));
        Assert.False(await store.RemoveSubscriptionAsync(7, record.Endpoint, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveEndpointFromOtherUsersClaimsSharedDeviceForCurrentUser()
    {
        var store = new InMemoryNotificationStore();
        var shared = new PushSubscriptionRecord { Endpoint = "https://push.example/shared", P256dh = "p", Auth = "a" };
        var otherEndpoint = new PushSubscriptionRecord { Endpoint = "https://push.example/other", P256dh = "p", Auth = "a" };

        // User 1 (previous account) and user 2 (just signed in) both hold the same device endpoint;
        // user 1 also has an unrelated subscription that must survive.
        await store.UpsertSubscriptionAsync(1, shared, TestContext.Current.CancellationToken);
        await store.UpsertSubscriptionAsync(1, otherEndpoint, TestContext.Current.CancellationToken);
        await store.UpsertSubscriptionAsync(2, shared, TestContext.Current.CancellationToken);

        var removed = await store.RemoveEndpointFromOtherUsersAsync(2, shared.Endpoint, TestContext.Current.CancellationToken);

        Assert.Equal(1, removed);
        var user1 = await store.GetSubscriptionsAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal("https://push.example/other", Assert.Single(user1).Endpoint);
        Assert.Equal("https://push.example/shared", Assert.Single(await store.GetSubscriptionsAsync(2, TestContext.Current.CancellationToken)).Endpoint);
    }

    [Fact]
    public async Task UpdateStateRetriesAfterAConflict()
    {
        var store = new InMemoryNotificationStore();
        await store.UpdateStateAsync(5, state =>
        {
            state.Events["seed"] = new NotificationEventState { Fingerprint = "f0" };
            return true;
        }, TestContext.Current.CancellationToken);

        // Force exactly one concurrent write between the read and the save.
        store.OnBeforeNextStateSave = () => store.ForceStateBump(5);

        var invocations = 0;
        var changed = await store.UpdateStateAsync(5, state =>
        {
            invocations++;
            state.Events["seed"] = new NotificationEventState { Fingerprint = "f1" };
            return true;
        }, TestContext.Current.CancellationToken);

        Assert.True(changed);
        Assert.Equal(2, invocations); // first attempt lost the race, second succeeded
        var result = await store.GetStateAsync(5, TestContext.Current.CancellationToken);
        Assert.Equal("f1", result.State.Events["seed"].Fingerprint);
    }

    [Fact]
    public async Task UpdateStateReturnsFalseWhenMutationMakesNoChange()
    {
        var store = new InMemoryNotificationStore();
        var changed = await store.UpdateStateAsync(9, _ => false, TestContext.Current.CancellationToken);
        Assert.False(changed);
    }
}

public sealed class NotificationTestRateLimiterTests
{
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan delta) => now += delta;
    }

    [Fact]
    public void AllowsFirstThenThrottlesUntilIntervalElapses()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var limiter = new NotificationTestRateLimiter(time);

        Assert.True(limiter.TryAcquire(1, out _));
        Assert.False(limiter.TryAcquire(1, out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);

        // A different user is independent.
        Assert.True(limiter.TryAcquire(2, out _));

        time.Advance(TimeSpan.FromSeconds(20));
        Assert.True(limiter.TryAcquire(1, out _));
    }
}
