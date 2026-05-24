using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("aca");

var server = builder.AddProject<Projects.pr_timeline_app_Server>("server")
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

    server
        .WithEnvironment("GITHUB_CLIENT_ID", githubClientId)
        .WithEnvironment("GITHUB_CLIENT_SECRET", githubClientSecret);
}

if (builder.Configuration.GetValue("IncludeFrontend", true))
{
    var webfrontend = builder.AddViteApp("webfrontend", "../frontend")
        .WithHttpEndpoint(port: 5173)
        .WithReference(server)
        .WaitFor(server);

    if (builder.ExecutionContext.IsRunMode)
    {
        webfrontend.WithExternalHttpEndpoints();
    }

    server.PublishWithContainerFiles(webfrontend, "wwwroot");
}

builder.Build().Run();
