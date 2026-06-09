var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.Configure<GitHubCacheWarmupOptions>(
    builder.Configuration.GetSection(GitHubCacheWarmupOptions.SectionName));
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
app.MapGet("/api/app-info", (IConfiguration configuration) =>
{
    var commitSha = configuration["GIT_COMMIT_SHA"]?.Trim() is { Length: > 0 } configuredCommitSha
        ? configuredCommitSha
        : "local";
    var shortCommitSha = commitSha[..Math.Min(7, commitSha.Length)];
    var commitUrl = commitSha == "local"
        ? null
        : $"https://github.com/davidfowl/pr-dashboard/commit/{commitSha}";

    return new AppInfoResponse(commitSha, shortCommitSha, commitUrl);
});

app.MapDefaultEndpoints();

app.UseFileServer();

app.Run();

public partial class Program;

record AppInfoResponse(string CommitSha, string ShortCommitSha, string? CommitUrl);
