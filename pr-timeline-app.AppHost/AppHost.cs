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

var server = builder.AddProject<Projects.pr_timeline_app_Server>("server")
    .WithReference(githubCache)
    .WaitFor(githubCache)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints()
    .PublishAsAzureContainerApp((_, app) =>
    {
        app.Template.Scale.MinReplicas = 0;
    });

if (builder.ExecutionContext.IsPublishMode)
{
    var githubClientId = builder.AddParameter("github-client-id");
    var githubClientSecret = builder.AddParameter("github-client-secret", secret: true);
    var gitCommitSha = builder.AddParameter("git-commit-sha");

    server
        .WithEnvironment("GITHUB_CLIENT_ID", githubClientId)
        .WithEnvironment("GITHUB_CLIENT_SECRET", githubClientSecret)
        .WithEnvironment("GIT_COMMIT_SHA", gitCommitSha);
}

if (builder.Configuration.GetValue("IncludeFrontend", true))
{
    var webfrontend = builder.AddViteApp("webfrontend", "../frontend")
        .WithReference(server)
        .WaitFor(server);

    if (builder.ExecutionContext.IsRunMode)
    {
        webfrontend.WithExternalHttpEndpoints();
    }

    server.PublishWithContainerFiles(webfrontend, "wwwroot");
}

builder.Build().Run();
