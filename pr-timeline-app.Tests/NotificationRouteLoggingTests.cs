using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace pr_timeline_app.Tests;

public sealed class NotificationRouteLoggingTests
{
    private static readonly NotificationUser s_user = new(42, "octocat");

    [Fact]
    public async Task TestSendLogsInvocationAndNoSubscriptionCompletion()
    {
        var store = new InMemoryNotificationStore();
        var logger = new RecordingLogger();

        await NotificationRoutes.SendTestNotificationForUserAsync(
            new DefaultHttpContext(),
            s_user,
            store,
            new FakePushSender(),
            new NotificationTestRateLimiter(TimeProvider.System),
            logger,
            TestContext.Current.CancellationToken);

        Assert.Contains(logger.Entries, entry => entry.Message.StartsWith("Notification test send invoked.", StringComparison.Ordinal));
        var completed = Assert.Single(CompletionEntries(logger));
        Assert.Equal(LogLevel.Information, completed.Level);
        Assert.Equal("no_subscriptions", completed.Value("Outcome"));
        Assert.Equal(s_user.Id, completed.Value("UserId"));
        Assert.Equal(s_user.Login, completed.Value("UserLogin"));
        Assert.Equal(0, completed.Value("SubscriptionCount"));
        Assert.Equal(0, completed.Value("Sent"));
        Assert.Equal(0, completed.Value("Failed"));
        Assert.Equal(0, completed.Value("Expired"));
    }

    [Fact]
    public async Task TestSendLogsDeliveryCountsWithoutSubscriptionSecrets()
    {
        var store = new InMemoryNotificationStore();
        await store.UpsertSubscriptionAsync(s_user.Id, Subscription("https://push.example/sent"), TestContext.Current.CancellationToken);
        await store.UpsertSubscriptionAsync(s_user.Id, Subscription("https://push.example/expired"), TestContext.Current.CancellationToken);
        await store.UpsertSubscriptionAsync(s_user.Id, Subscription("https://push.example/failed"), TestContext.Current.CancellationToken);
        var logger = new RecordingLogger();
        var sender = new FakePushSender
        {
            Behavior = subscription => subscription.Endpoint switch
            {
                "https://push.example/sent" => new PushDeliveryResult(PushDeliveryOutcome.Sent, StatusCodes.Status201Created),
                "https://push.example/expired" => new PushDeliveryResult(PushDeliveryOutcome.Expired, StatusCodes.Status410Gone),
                _ => new PushDeliveryResult(PushDeliveryOutcome.Failed, StatusCodes.Status503ServiceUnavailable)
            }
        };

        await NotificationRoutes.SendTestNotificationForUserAsync(
            new DefaultHttpContext(),
            s_user,
            store,
            sender,
            new NotificationTestRateLimiter(TimeProvider.System),
            logger,
            TestContext.Current.CancellationToken);

        var completed = Assert.Single(CompletionEntries(logger));
        Assert.Equal("partial_success", completed.Value("Outcome"));
        Assert.Equal(3, completed.Value("SubscriptionCount"));
        Assert.Equal(1, completed.Value("Sent"));
        Assert.Equal(1, completed.Value("Failed"));
        Assert.Equal(1, completed.Value("Expired"));
        Assert.DoesNotContain(
            "https://push.example",
            string.Join('\n', logger.Entries.SelectMany(entry => entry.LoggedValues())),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "p256dh-secret",
            string.Join('\n', logger.Entries.SelectMany(entry => entry.LoggedValues())),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "auth-secret",
            string.Join('\n', logger.Entries.SelectMany(entry => entry.LoggedValues())),
            StringComparison.Ordinal);
        Assert.Equal(2, (await store.GetSubscriptionsAsync(s_user.Id, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task TestSendLogsRateLimitOutcome()
    {
        var store = new InMemoryNotificationStore();
        var logger = new RecordingLogger();
        var rateLimiter = new NotificationTestRateLimiter(TimeProvider.System);

        await NotificationRoutes.SendTestNotificationForUserAsync(
            new DefaultHttpContext(),
            s_user,
            store,
            new FakePushSender(),
            rateLimiter,
            logger,
            TestContext.Current.CancellationToken);
        logger.Entries.Clear();

        var context = new DefaultHttpContext();
        var result = await NotificationRoutes.SendTestNotificationForUserAsync(
            context,
            s_user,
            store,
            new FakePushSender(),
            rateLimiter,
            logger,
            TestContext.Current.CancellationToken);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, status.StatusCode);
        Assert.True(context.Response.Headers.ContainsKey(HeaderNames.RetryAfter));
        var completed = Assert.Single(CompletionEntries(logger));
        Assert.Equal("rate_limited", completed.Value("Outcome"));
        Assert.Equal(0, completed.Value("SubscriptionCount"));
        Assert.Equal(0, completed.Value("Sent"));
        Assert.Equal(0, completed.Value("Failed"));
        Assert.Equal(0, completed.Value("Expired"));
        Assert.Equal(15, completed.Value("RetryAfterSeconds"));
    }

    [Fact]
    public async Task TestSendLogsDisabledOutcome()
    {
        var store = new InMemoryNotificationStore();
        await store.UpsertSubscriptionAsync(s_user.Id, Subscription("https://push.example/subscribed"), TestContext.Current.CancellationToken);
        var logger = new RecordingLogger();

        var result = await NotificationRoutes.SendTestNotificationForUserAsync(
            new DefaultHttpContext(),
            s_user,
            store,
            new FakePushSender { IsEnabled = false },
            new NotificationTestRateLimiter(TimeProvider.System),
            logger,
            TestContext.Current.CancellationToken);

        var problem = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        var completed = Assert.Single(CompletionEntries(logger));
        Assert.Equal("disabled", completed.Value("Outcome"));
        Assert.Null(completed.Value("SubscriptionCount"));
        Assert.Equal(0, completed.Value("Sent"));
        Assert.Equal(0, completed.Value("Failed"));
        Assert.Equal(0, completed.Value("Expired"));
    }

    private static PushSubscriptionRecord Subscription(string endpoint) =>
        new()
        {
            Endpoint = endpoint,
            P256dh = "p256dh-secret",
            Auth = "auth-secret"
        };

    private static IEnumerable<LogEntry> CompletionEntries(RecordingLogger logger) =>
        logger.Entries.Where(entry => entry.Message.StartsWith("Notification test send completed.", StringComparison.Ordinal));

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>>;
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), values?.ToArray() ?? []));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> State)
    {
        public object? Value(string name) =>
            State.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.Ordinal)).Value;

        public IEnumerable<string> LoggedValues()
        {
            yield return Message;
            foreach (var pair in State)
            {
                if (pair.Value is not null)
                {
                    yield return pair.Value.ToString() ?? string.Empty;
                }
            }
        }
    }
}
