using System.Net;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Options;

// VAPID Web Push sender built on Lib.Net.Http.WebPush. Disabled (and a graceful no-op) when
// WebPush is not fully configured, so the app runs locally without keys.
sealed class WebPushSender(
    IHttpClientFactory httpClientFactory,
    IOptions<WebPushOptions> options,
    ILogger<WebPushSender> logger) : IPushSender
{
    public const string HttpClientName = "web-push";

    private readonly Lock gate = new();
    private PushServiceClient? client;
    private string? builtForPublicKey;

    public bool IsEnabled => options.Value.IsConfigured;

    public async Task<PushDeliveryResult> SendAsync(
        PushSubscriptionRecord subscription,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var current = options.Value;
        if (!current.IsConfigured)
        {
            return new PushDeliveryResult(PushDeliveryOutcome.Disabled, null);
        }

        var pushClient = GetClient(current);

        var pushSubscription = new PushSubscription { Endpoint = subscription.Endpoint };
        pushSubscription.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
        pushSubscription.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);

        var message = new PushMessage(payloadJson) { Urgency = PushMessageUrgency.Normal };

        try
        {
            await pushClient.RequestPushMessageDeliveryAsync(pushSubscription, message, cancellationToken);
            return new PushDeliveryResult(PushDeliveryOutcome.Sent, (int)HttpStatusCode.Created);
        }
        catch (PushServiceClientException ex)
        {
            var status = (int)ex.StatusCode;

            // 404/410 mean the subscription no longer exists; the caller prunes it.
            if (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return new PushDeliveryResult(PushDeliveryOutcome.Expired, status);
            }

            // Never log the endpoint or keys — only the push service status.
            logger.LogWarning("Web push delivery failed with status {Status}.", status);
            return new PushDeliveryResult(PushDeliveryOutcome.Failed, status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Detector shutdown — let cancellation propagate rather than swallowing it.
            throw;
        }
        catch (Exception ex)
        {
            // A malformed subscription (bad p256dh/auth) throws Format/Crypto exceptions, and
            // transient network faults throw HttpRequestException/timeouts. Treat any of these
            // as a single failed delivery so one bad subscription can't abort the whole cycle.
            // Log only the exception type — never the endpoint or keys.
            logger.LogWarning("Web push delivery threw {ExceptionType}.", ex.GetType().Name);
            return new PushDeliveryResult(PushDeliveryOutcome.Failed, null);
        }
    }

    private PushServiceClient GetClient(WebPushOptions current)
    {
        lock (gate)
        {
            // Rebuild if the public key changed (key rotation requires a process restart in
            // practice, but this keeps the cached client honest).
            if (client is not null && builtForPublicKey == current.PublicKey)
            {
                return client;
            }

            client = new PushServiceClient(httpClientFactory.CreateClient(HttpClientName))
            {
                DefaultAuthentication = new VapidAuthentication(current.PublicKey!, current.PrivateKey!)
                {
                    Subject = current.Subject!
                }
            };
            builtForPublicKey = current.PublicKey;
            return client;
        }
    }
}
