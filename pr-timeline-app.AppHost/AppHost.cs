using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("aca");

var storage = builder.AddAzureStorage("storage");
storage = storage.RunAsEmulator(azurite =>
{
    azurite.WithDataVolume();
});

var githubCache = storage
    .AddBlobContainer("github-cache", blobContainerName: "github-cache")
    .WithClearCacheCommand();

// Dedicated container for push notification data (subscriptions, preferences, dedupe state).
// Kept separate from github-cache so the cache "clear" command and TTL eviction can never
// delete a user's subscriptions.
var notifications = storage
    .AddBlobContainer("notifications", blobContainerName: "notifications");

var server = builder.AddProject<Projects.pr_timeline_app_Server>("server")
    .WithReference(githubCache)
    .WaitFor(githubCache)
    .WithReference(notifications)
    .WaitFor(notifications)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints()
    .PublishAsAzureContainerApp((_, app) =>
    {
        // The notification detector is an in-process BackgroundService that does a
        // read-modify-write of per-user dedupe state in blob storage. v1 pins the server to
        // exactly one replica so that loop is a single writer and we never send duplicate
        // pushes from competing replicas. ETag/If-Match writes are defense-in-depth, not a
        // substitute for this. To scale horizontally later, add single-leader election
        // (e.g. an Azure Blob lease) so only the leader detects + sends, then relax these.
        app.Template.Scale.MinReplicas = 1;
        app.Template.Scale.MaxReplicas = 1;
    });

if (builder.ExecutionContext.IsPublishMode)
{
    var githubClientId = builder.AddParameter("github-client-id");
    var githubClientSecret = builder.AddParameter("github-client-secret", secret: true);
    var gitCommitSha = builder.AddParameter("git-commit-sha");

    // VAPID configuration for Web Push. Generate a one-time P-256 key pair; the public key is
    // shipped to the browser, the private key signs pushes and must stay secret. Subject is a
    // mailto:/https contact for the push service. KeyId lets the client detect a key rotation
    // and re-subscribe. Bound into the "WebPush" options section via WebPush__* env vars.
    var webPushPublicKey = builder.AddParameter("web-push-public-key");
    var webPushPrivateKey = builder.AddParameter("web-push-private-key", secret: true);
    var webPushSubject = builder.AddParameter("web-push-subject");
    var webPushKeyId = builder.AddParameter("web-push-key-id");

    server
        .WithEnvironment("GITHUB_CLIENT_ID", githubClientId)
        .WithEnvironment("GITHUB_CLIENT_SECRET", githubClientSecret)
        .WithEnvironment("GIT_COMMIT_SHA", gitCommitSha)
        .WithEnvironment("WebPush__Enabled", "true")
        .WithEnvironment("WebPush__PublicKey", webPushPublicKey)
        .WithEnvironment("WebPush__PrivateKey", webPushPrivateKey)
        .WithEnvironment("WebPush__Subject", webPushSubject)
        .WithEnvironment("WebPush__KeyId", webPushKeyId);
}

if (builder.Configuration.GetValue("IncludeFrontend", true))
{
    var webfrontend = builder.AddViteApp("webfrontend", "../frontend")
        .WithReference(server)
        .WaitFor(server)
        .WithBrowserLogs();

    if (builder.ExecutionContext.IsRunMode)
    {
        webfrontend.WithExternalHttpEndpoints();
    }

    server.PublishWithContainerFiles(webfrontend, "wwwroot");
}

builder.Build().Run();
