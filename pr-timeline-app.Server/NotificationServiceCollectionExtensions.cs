using Microsoft.Extensions.DependencyInjection.Extensions;

public static class NotificationServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<INotificationStore, BlobNotificationStore>();
        services.AddScoped<NotificationUserResolver>();
        services.AddHttpClient(WebPushSender.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<IPushSender, WebPushSender>();
        services.AddSingleton<NotificationTestRateLimiter>();
        services.AddHostedService<NotificationDetectorService>();
        return services;
    }
}
