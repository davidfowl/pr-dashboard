using Microsoft.Extensions.Options;

public static class NotificationRoutes
{
    private const string LoggerCategoryName = nameof(NotificationRoutes);

    public static IEndpointRouteBuilder MapNotificationRoutes(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/notifications");

        // The VAPID public key is public by design. 404 signals "push is not available here"
        // so the client can hide the opt-in UI without guessing.
        api.MapGet("vapid-public-key", (IOptions<WebPushOptions> options) =>
        {
            var value = options.Value;
            return value.IsConfigured
                ? Results.Ok(new VapidPublicKeyResponse(value.PublicKey!, value.EffectiveKeyId))
                : Results.NotFound();
        });

        api.MapGet("preferences", async (
            HttpContext context,
            NotificationUserResolver resolver,
            INotificationStore store,
            CancellationToken cancellationToken) =>
        {
            var user = await resolver.ResolveAsync(cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            await UpsertProfileAsync(store, user, cancellationToken);
            var preferences = await store.GetPreferencesAsync(user.Id, cancellationToken);
            return Results.Ok(new NotificationPreferencesDto(preferences.ReviewRequested, preferences.ReadyToMerge));
        });

        api.MapPut("preferences", async (
            HttpContext context,
            NotificationUserResolver resolver,
            INotificationStore store,
            CancellationToken cancellationToken) =>
        {
            if (!IsBrowserMutationRequest(context))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var user = await resolver.ResolveAsync(cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var body = await ReadJsonAsync<NotificationPreferencesDto>(context, cancellationToken);
            if (body is null)
            {
                return Results.BadRequest();
            }

            await UpsertProfileAsync(store, user, cancellationToken);
            await store.SavePreferencesAsync(
                user.Id,
                new NotificationPreferences { ReviewRequested = body.ReviewRequested, ReadyToMerge = body.ReadyToMerge },
                cancellationToken);

            return Results.Ok(new NotificationPreferencesDto(body.ReviewRequested, body.ReadyToMerge));
        });

        api.MapPost("subscribe", async (
            HttpContext context,
            NotificationUserResolver resolver,
            INotificationStore store,
            IOptions<WebPushOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!IsBrowserMutationRequest(context))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var user = await resolver.ResolveAsync(cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var body = await ReadJsonAsync<PushSubscriptionDto>(context, cancellationToken);
            if (!TryCreateSubscription(body, options.Value, out var subscription, out var error))
            {
                return Results.ValidationProblem(error);
            }

            subscription.UserAgent = Truncate(context.Request.Headers.UserAgent.ToString(), 256);
            await UpsertProfileAsync(store, user, cancellationToken);
            await store.UpsertSubscriptionAsync(user.Id, subscription, cancellationToken);
            // Claim this endpoint exclusively for the current user so a previous account on a
            // shared device stops receiving pushes that would land on this browser.
            await store.RemoveEndpointFromOtherUsersAsync(user.Id, subscription.Endpoint, cancellationToken);

            return Results.Ok(new { subscribed = true });
        });

        api.MapPost("unsubscribe", async (
            HttpContext context,
            NotificationUserResolver resolver,
            INotificationStore store,
            CancellationToken cancellationToken) =>
        {
            if (!IsBrowserMutationRequest(context))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var user = await resolver.ResolveAsync(cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var body = await ReadJsonAsync<UnsubscribeRequest>(context, cancellationToken);
            if (string.IsNullOrWhiteSpace(body?.Endpoint))
            {
                return Results.BadRequest();
            }

            var removed = await store.RemoveSubscriptionAsync(user.Id, body.Endpoint, cancellationToken);
            return Results.Ok(new { removed });
        });

        api.MapPost("test", async (
            HttpContext context,
            NotificationUserResolver resolver,
            INotificationStore store,
            IPushSender sender,
            NotificationTestRateLimiter rateLimiter,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (!IsBrowserMutationRequest(context))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var user = await resolver.ResolveAsync(cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var logger = loggerFactory.CreateLogger(LoggerCategoryName);
            return await SendTestNotificationForUserAsync(
                context,
                user,
                store,
                sender,
                rateLimiter,
                logger,
                cancellationToken);
        });

        return endpoints;
    }

    internal static async Task<IResult> SendTestNotificationForUserAsync(
        HttpContext context,
        NotificationUser user,
        INotificationStore store,
        IPushSender sender,
        NotificationTestRateLimiter rateLimiter,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Notification test send invoked. userId={UserId} userLogin={UserLogin}.",
            user.Id,
            user.Login);

        if (!sender.IsEnabled)
        {
            LogNotificationTestCompleted(logger, user, "disabled", null, 0, 0, 0);
            return Results.Problem(
                title: "Push notifications are not configured",
                detail: "Set WebPush:PublicKey, WebPush:PrivateKey and WebPush:Subject to enable push.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var subscriptions = await store.GetSubscriptionsAsync(user.Id, cancellationToken);

        if (!rateLimiter.TryAcquire(user.Id, out var retryAfter))
        {
            var retryAfterSeconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
            context.Response.Headers.RetryAfter =
                retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            LogNotificationTestCompleted(logger, user, "rate_limited", subscriptions.Count, 0, 0, 0, retryAfterSeconds);
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // The payload is fixed server-side; the caller cannot influence title/body/url.
        var payload = NotificationPayloads.Test();

        var sent = 0;
        var failed = 0;
        var expired = 0;
        foreach (var subscription in subscriptions)
        {
            var result = await sender.SendAsync(subscription, payload, cancellationToken);
            switch (result.Outcome)
            {
                case PushDeliveryOutcome.Sent:
                    sent++;
                    break;
                case PushDeliveryOutcome.Expired:
                    expired++;
                    await store.RemoveSubscriptionAsync(user.Id, subscription.Endpoint, cancellationToken);
                    break;
                default:
                    failed++;
                    break;
            }
        }

        LogNotificationTestCompleted(
            logger,
            user,
            GetNotificationTestOutcome(subscriptions.Count, sent, failed, expired),
            subscriptions.Count,
            sent,
            failed,
            expired);

        return Results.Ok(new TestNotificationResponse(sent, failed, expired));
    }

    private static string GetNotificationTestOutcome(int subscriptionCount, int sent, int failed, int expired)
    {
        if (subscriptionCount == 0)
        {
            return "no_subscriptions";
        }

        if (sent > 0 && failed == 0 && expired == 0)
        {
            return "sent";
        }

        if (sent > 0)
        {
            return "partial_success";
        }

        if (failed > 0)
        {
            return "failed";
        }

        return expired > 0 ? "expired" : "no_subscriptions";
    }

    private static void LogNotificationTestCompleted(
        ILogger logger,
        NotificationUser user,
        string outcome,
        int? subscriptionCount,
        int sent,
        int failed,
        int expired,
        int? retryAfterSeconds = null)
    {
        logger.LogInformation(
            "Notification test send completed. outcome={Outcome} userId={UserId} userLogin={UserLogin} subscriptionCount={SubscriptionCount} sent={Sent} failed={Failed} expired={Expired} retryAfterSeconds={RetryAfterSeconds}.",
            outcome,
            user.Id,
            user.Login,
            subscriptionCount,
            sent,
            failed,
            expired,
            retryAfterSeconds);
    }

    private static async Task UpsertProfileAsync(
        INotificationStore store,
        NotificationUser user,
        CancellationToken cancellationToken) =>
        await store.UpsertUserProfileAsync(
            new NotificationUserProfile
            {
                Id = user.Id,
                Login = user.Login,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            cancellationToken);

    private static bool TryCreateSubscription(
        PushSubscriptionDto? dto,
        WebPushOptions options,
        out PushSubscriptionRecord subscription,
        out Dictionary<string, string[]> errors)
    {
        errors = [];
        subscription = null!;

        if (dto is null
            || string.IsNullOrWhiteSpace(dto.Endpoint)
            || !Uri.TryCreate(dto.Endpoint, UriKind.Absolute, out var endpointUri)
            || endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            errors["endpoint"] = ["A valid https push endpoint is required."];
            return false;
        }

        if (!PushSubscriptionEndpointValidator.IsAllowed(endpointUri))
        {
            errors["endpoint"] = ["The push endpoint must be from a supported browser push service."];
            return false;
        }

        if (string.IsNullOrWhiteSpace(dto.Keys?.P256dh) || string.IsNullOrWhiteSpace(dto.Keys?.Auth))
        {
            errors["keys"] = ["Both p256dh and auth keys are required."];
            return false;
        }

        // Reject malformed keys at the door so the detector never stores a subscription that
        // would throw inside the encryption layer (and abort a send cycle). Web Push keys are
        // base64url: p256dh is an uncompressed P-256 point (65 bytes), auth is 16 bytes.
        if (!TryValidateKeyLength(dto.Keys.P256dh, 65) || !TryValidateKeyLength(dto.Keys.Auth, 16))
        {
            errors["keys"] = ["The p256dh and auth keys must be valid base64url values of the expected length."];
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        subscription = new PushSubscriptionRecord
        {
            Endpoint = dto.Endpoint,
            P256dh = dto.Keys.P256dh,
            Auth = dto.Keys.Auth,
            ExpirationTime = dto.ExpirationTime,
            KeyId = options.IsConfigured ? options.EffectiveKeyId : null,
            CreatedAt = now,
            UpdatedAt = now
        };
        return true;
    }

    // Validates that a Web Push key is well-formed base64url decoding to exactly the expected
    // number of bytes. Accepts unpadded base64url (what browsers send).
    private static bool TryValidateKeyLength(string value, int expectedBytes)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2: normalized += "=="; break;
            case 3: normalized += "="; break;
            case 1: return false;
        }

        Span<byte> buffer = stackalloc byte[expectedBytes + 4];
        return Convert.TryFromBase64String(normalized, buffer, out var written) && written == expectedBytes;
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            return default;
        }
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

    // Mirrors the CSRF guard used by the GitHub mutation routes: require a JSON content type
    // and a same-origin (or loopback) Origin header for state-changing requests.
    private static bool IsBrowserMutationRequest(HttpContext context)
    {
        if (!context.Request.HasJsonContentType())
        {
            return false;
        }

        var origin = context.Request.Headers.Origin.ToString();
        return string.IsNullOrEmpty(origin)
            || Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.IsLoopback || uri.Host.Equals(context.Request.Host.Host, StringComparison.OrdinalIgnoreCase));
    }
}
