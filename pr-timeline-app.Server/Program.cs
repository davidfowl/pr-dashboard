var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddGitHubApiServices(builder.Environment);

var app = builder.Build();

app.UseGitHubApiExceptionHandler();
app.UseAuthentication();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGitHubAuthRoutes();
app.MapGitHubPullRequestRoutes();

app.MapDefaultEndpoints();

app.UseFileServer();

app.Run();

public partial class Program;
