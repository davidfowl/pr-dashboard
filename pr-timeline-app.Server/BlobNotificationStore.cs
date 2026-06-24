using System.Globalization;
using System.Net;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;

// Blob-backed INotificationStore. Uses a dedicated "notifications" container (separate from
// the public GitHub cache) so the cache "clear" command and TTL-driven eviction can never
// delete a user's subscriptions, preferences, or dedupe state.
//
// Layout (all JSON, keyed by GitHub numeric user id):
//   users/{userId}.json
//   preferences/{userId}.json
//   subscriptions/{userId}/{subscriptionId}.json
//   state/{userId}.json
sealed class BlobNotificationStore : INotificationStore
{
    public const string ConnectionName = "notifications";

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly BlobContainerClient container;

    public BlobNotificationStore(
        [FromKeyedServices(ConnectionName)] BlobContainerClient container)
    {
        this.container = container;
    }

    public async Task<IReadOnlyList<PushSubscriptionRecord>> GetSubscriptionsAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var prefix = $"subscriptions/{userId}/";
        var subscriptions = new List<PushSubscriptionRecord>();

        try
        {
            await foreach (var blob in container.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, prefix, cancellationToken))
            {
                var record = await TryReadAsync<PushSubscriptionRecord>(blob.Name, cancellationToken);
                if (record.Value is not null)
                {
                    subscriptions.Add(record.Value);
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return subscriptions;
        }
        catch (RequestFailedException ex)
        {
            throw CreateUnavailableException("reading", ex);
        }

        return subscriptions;
    }

    public Task UpsertSubscriptionAsync(
        long userId,
        PushSubscriptionRecord subscription,
        CancellationToken cancellationToken)
    {
        var blobName = $"subscriptions/{userId}/{PushSubscriptionRecord.CreateId(subscription.Endpoint)}.json";
        return OverwriteAsync(blobName, subscription, cancellationToken);
    }

    public async Task<bool> RemoveSubscriptionAsync(
        long userId,
        string endpoint,
        CancellationToken cancellationToken)
    {
        var blobName = $"subscriptions/{userId}/{PushSubscriptionRecord.CreateId(endpoint)}.json";
        try
        {
            var response = await container.GetBlobClient(blobName)
                .DeleteIfExistsAsync(cancellationToken: cancellationToken);
            return response.Value;
        }
        catch (RequestFailedException ex)
        {
            throw CreateUnavailableException("deleting", ex);
        }
    }

    public async Task<int> RemoveEndpointFromOtherUsersAsync(
        long keepUserId,
        string endpoint,
        CancellationToken cancellationToken)
    {
        // subscriptionId is a deterministic hash of the endpoint, so the same endpoint stored
        // under a different user has the blob name subscriptions/{otherUserId}/{id}.json. Scan
        // the subscriptions prefix and delete any matching blob that isn't the keep user's.
        //
        // This is O(total subscriptions) per subscribe. That's an intentional v1 tradeoff: this
        // path only runs when a device is claimed by a new account, the server is pinned to a
        // single replica, and the audience is a small team, so the scan stays cheap. If the
        // container grows large, add an endpoint->owner index (e.g. subscriptions-by-endpoint/
        // {subscriptionId}.json or blob tags) to make this O(1) instead of enumerating the prefix.
        var subscriptionId = PushSubscriptionRecord.CreateId(endpoint);
        var suffix = $"/{subscriptionId}.json";
        var keepPrefix = $"subscriptions/{keepUserId}/";
        var removed = 0;

        try
        {
            await foreach (var blob in container.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, "subscriptions/", cancellationToken))
            {
                if (!blob.Name.EndsWith(suffix, StringComparison.Ordinal)
                    || blob.Name.StartsWith(keepPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var response = await container.GetBlobClient(blob.Name)
                    .DeleteIfExistsAsync(cancellationToken: cancellationToken);
                if (response.Value)
                {
                    removed++;
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return removed;
        }
        catch (RequestFailedException ex)
        {
            throw CreateUnavailableException("deleting", ex);
        }

        return removed;
    }

    public async Task<NotificationPreferences> GetPreferencesAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var record = await TryReadAsync<NotificationPreferences>($"preferences/{userId}.json", cancellationToken);
        return record.Value ?? NotificationPreferences.CreateDefault();
    }

    public Task SavePreferencesAsync(
        long userId,
        NotificationPreferences preferences,
        CancellationToken cancellationToken) =>
        OverwriteAsync($"preferences/{userId}.json", preferences, cancellationToken);

    public Task UpsertUserProfileAsync(
        NotificationUserProfile profile,
        CancellationToken cancellationToken) =>
        OverwriteAsync($"users/{profile.Id}.json", profile, cancellationToken);

    public async Task<IReadOnlyList<NotificationUserProfile>> ListUserProfilesAsync(
        CancellationToken cancellationToken)
    {
        var profiles = new List<NotificationUserProfile>();
        try
        {
            await foreach (var blob in container.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, "users/", cancellationToken))
            {
                var record = await TryReadAsync<NotificationUserProfile>(blob.Name, cancellationToken);
                if (record.Value is not null)
                {
                    profiles.Add(record.Value);
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return profiles;
        }
        catch (RequestFailedException ex)
        {
            throw CreateUnavailableException("reading", ex);
        }

        return profiles;
    }

    public async Task<NotificationDedupeStateResult> GetStateAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var blob = container.GetBlobClient($"state/{userId}.json");
        try
        {
            var download = (await blob.DownloadContentAsync(cancellationToken)).Value;
            var state = download.Content.ToObjectFromJson<NotificationDedupeState>(s_jsonOptions)
                ?? new NotificationDedupeState();
            return new NotificationDedupeStateResult(state, download.Details.ETag.ToString());
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return new NotificationDedupeStateResult(new NotificationDedupeState(), null);
        }
        catch (RequestFailedException ex)
        {
            throw CreateUnavailableException("reading", ex);
        }
    }

    public async Task<bool> TrySaveStateAsync(
        long userId,
        NotificationDedupeState state,
        string? concurrencyToken,
        CancellationToken cancellationToken)
    {
        var blob = container.GetBlobClient($"state/{userId}.json");
        // A null token means "create only": if another writer created the blob in the
        // meantime, IfNoneMatch=* fails and we report a conflict. Otherwise require the ETag
        // to be unchanged since we read it.
        var conditions = concurrencyToken is null
            ? new BlobRequestConditions { IfNoneMatch = ETag.All }
            : new BlobRequestConditions { IfMatch = new ETag(concurrencyToken) };

        try
        {
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            await blob.UploadAsync(
                BinaryData.FromObjectAsJson(state, s_jsonOptions),
                new BlobUploadOptions { Conditions = conditions },
                cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (
            ex.Status == (int)HttpStatusCode.PreconditionFailed
            || ex.Status == (int)HttpStatusCode.Conflict)
        {
            return false;
        }
        catch (RequestFailedException ex)
        {
            throw CreateUnavailableException("writing", ex);
        }
    }

    private async Task OverwriteAsync<T>(string blobName, T value, CancellationToken cancellationToken)
    {
        try
        {
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            await container.GetBlobClient(blobName).UploadAsync(
                BinaryData.FromObjectAsJson(value, s_jsonOptions),
                overwrite: true,
                cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            throw CreateUnavailableException("writing", ex);
        }
    }

    private async Task<(bool Found, T? Value)> TryReadAsync<T>(string blobName, CancellationToken cancellationToken)
    {
        try
        {
            var download = (await container.GetBlobClient(blobName)
                .DownloadContentAsync(cancellationToken)).Value;
            return (true, download.Content.ToObjectFromJson<T>(s_jsonOptions));
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return (false, default);
        }
        catch (RequestFailedException ex)
        {
            throw CreateUnavailableException("reading", ex);
        }
    }

    private static GitHubApiException CreateUnavailableException(
        string operation,
        RequestFailedException exception) =>
        new(
            HttpStatusCode.ServiceUnavailable,
            $"Notification storage is temporarily unavailable while {operation} blob storage: {exception.Message}");
}

// Marker thrown when a state save loses every concurrency retry. Surfaced as a transient
// failure rather than silently dropping a notification decision.
sealed class NotificationConcurrencyException(string message) : Exception(message);

internal static class NotificationStoreExtensions
{
    private const int MaxStateSaveAttempts = 5;

    // Read-modify-write helper that applies a mutation under optimistic concurrency and
    // retries on conflict. Returns false when the mutation chose not to change anything.
    public static async Task<bool> UpdateStateAsync(
        this INotificationStore store,
        long userId,
        Func<NotificationDedupeState, bool> mutate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxStateSaveAttempts; attempt++)
        {
            var current = await store.GetStateAsync(userId, cancellationToken);
            if (!mutate(current.State))
            {
                return false;
            }

            if (await store.TrySaveStateAsync(userId, current.State, current.ConcurrencyToken, cancellationToken))
            {
                return true;
            }
        }

        throw new NotificationConcurrencyException(
            $"Failed to persist notification state for user {userId} after {MaxStateSaveAttempts} attempts.");
    }
}
