#:sdk Aspire.AppHost.Sdk@13.4.0-preview.1.26271.6
#:package Aspire.Hosting.JavaScript@13.4.0-preview.1.26271.6
#:property NoWarn=ASPIRECSHARPAPPS001

// AddCSharpApp is currently guarded by ASPIRECSHARPAPPS001 for evaluation APIs.
// File-based AppHosts use it to reference project and file-based C# apps.
// https://aka.ms/aspire/diagnostics/ASPIRECSHARPAPPS001

var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddCSharpApp("server", "./pr-timeline-app.Server")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

var webfrontend = builder.AddViteApp("webfrontend", "./frontend")
    .WithHttpEndpoint(port: 5173)
    .WithExternalHttpEndpoints()
    .WithReference(server)
    .WaitFor(server);

server.PublishWithContainerFiles(webfrontend, "wwwroot");

builder.Build().Run();
