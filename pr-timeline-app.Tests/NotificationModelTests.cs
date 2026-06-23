using System.Text.Json;

namespace pr_timeline_app.Tests;

public sealed class NotificationModelTests
{
    [Fact]
    public void WebPushOptionsRequiresEnabledAndAllKeys()
    {
        Assert.False(new WebPushOptions { Enabled = true, PublicKey = "pk", PrivateKey = "sk" }.IsConfigured);
        Assert.False(new WebPushOptions { Enabled = false, PublicKey = "pk", PrivateKey = "sk", Subject = "mailto:a@b.com" }.IsConfigured);
        Assert.True(new WebPushOptions { Enabled = true, PublicKey = "pk", PrivateKey = "sk", Subject = "mailto:a@b.com" }.IsConfigured);
    }

    [Fact]
    public void EffectiveKeyIdPrefersExplicitOtherwiseDerivesFromPublicKey()
    {
        Assert.Equal("v3", new WebPushOptions { KeyId = "v3", PublicKey = "pk" }.EffectiveKeyId);

        var derived = new WebPushOptions { PublicKey = "some-public-key" }.EffectiveKeyId;
        var derivedAgain = new WebPushOptions { PublicKey = "some-public-key" }.EffectiveKeyId;
        var different = new WebPushOptions { PublicKey = "another-public-key" }.EffectiveKeyId;

        Assert.Equal(derived, derivedAgain);
        Assert.NotEqual(derived, different);
    }

    [Fact]
    public void SubscriptionIdIsStablePerEndpoint()
    {
        const string endpoint = "https://fcm.googleapis.com/fcm/send/abc";
        Assert.Equal(PushSubscriptionRecord.CreateId(endpoint), PushSubscriptionRecord.CreateId(endpoint));
        Assert.NotEqual(PushSubscriptionRecord.CreateId(endpoint), PushSubscriptionRecord.CreateId(endpoint + "z"));
    }

    [Fact]
    public void DefaultPreferencesEnableReviewRequested()
    {
        Assert.True(NotificationPreferences.CreateDefault().ReviewRequested);
    }

    [Fact]
    public void PayloadsUseServiceWorkerContractFields()
    {
        foreach (var json in new[] { NotificationPayloads.Test(), NotificationPayloads.ReviewRequested("microsoft/aspire", 42, "Fix bug", "/#pr/microsoft%2Faspire/42") })
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            foreach (var field in new[] { "title", "body", "url", "tag", "icon" })
            {
                Assert.True(root.TryGetProperty(field, out var value), $"missing {field}");
                Assert.False(string.IsNullOrWhiteSpace(value.GetString()));
            }
        }
    }

    [Fact]
    public void ReviewRequestedPayloadDeepLinksToThePullRequest()
    {
        var json = NotificationPayloads.ReviewRequested("microsoft/aspire", 42, "Fix bug", "/#pr/microsoft%2Faspire/42");
        using var document = JsonDocument.Parse(json);
        Assert.Equal("/#pr/microsoft%2Faspire/42", document.RootElement.GetProperty("url").GetString());
        Assert.Equal("review-requested:microsoft/aspire#42", document.RootElement.GetProperty("tag").GetString());
    }
}
