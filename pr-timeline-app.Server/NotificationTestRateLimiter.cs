using System.Collections.Concurrent;

// Per-user throttle for the manual /test endpoint so a user (or a stuck client) cannot fan
// out a burst of pushes to their own devices. In-memory is sufficient: the server pins to a
// single replica, and the worst case of a process restart is one extra allowed test.
sealed class NotificationTestRateLimiter(TimeProvider timeProvider)
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<long, DateTimeOffset> lastTestByUser = new();

    public bool TryAcquire(long userId, out TimeSpan retryAfter)
    {
        var now = timeProvider.GetUtcNow();
        retryAfter = TimeSpan.Zero;

        while (true)
        {
            if (lastTestByUser.TryGetValue(userId, out var last))
            {
                var elapsed = now - last;
                if (elapsed < MinInterval)
                {
                    retryAfter = MinInterval - elapsed;
                    return false;
                }

                if (lastTestByUser.TryUpdate(userId, now, last))
                {
                    return true;
                }
            }
            else if (lastTestByUser.TryAdd(userId, now))
            {
                return true;
            }
        }
    }
}
