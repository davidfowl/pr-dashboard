using Microsoft.Extensions.DependencyInjection.Extensions;

public static class NotificationServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<INotificationStore, BlobNotificationStore>();
        services.AddScoped<NotificationUserResolver>();
        services.AddHttpClient(WebPushSender.HttpClientName);
        services.AddSingleton<IPushSender, WebPushSender>();
        services.AddSingleton<NotificationTestRateLimiter>();
        return services;
    }
}
