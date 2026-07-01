using Microsoft.AspNetCore.Diagnostics;

public static class GitHubExceptionHandlingExtensions
{
    public static WebApplication UseGitHubApiExceptionHandler(this WebApplication app)
    {
        app.UseExceptionHandler(exceptionApp =>
        {
            exceptionApp.Run(async context =>
            {
                var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                var logger = context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("GitHubExceptionHandler");

                if (exception is GitHubApiException gitHubException)
                {
                    var repository = context.Request.Query["repo"].ToString();
                    logger.LogWarning(
                        gitHubException,
                        "GitHub API request failed. Method={RequestMethod}, Path={RequestPath}, Repository={Repository}, State={GitHubState}, MilestoneProvided={GitHubMilestoneProvided}, Refresh={GitHubRefreshRequested}, StatusCode={GitHubStatusCode}, ExceptionType={ExceptionType}.",
                        context.Request.Method,
                        context.Request.Path.Value,
                        string.IsNullOrWhiteSpace(repository) ? null : repository,
                        EmptyToNull(context.Request.Query["state"].ToString()),
                        !string.IsNullOrWhiteSpace(context.Request.Query["milestone"].ToString()),
                        string.Equals(context.Request.Query["refresh"].ToString(), "true", StringComparison.OrdinalIgnoreCase),
                        (int)gitHubException.StatusCode,
                        gitHubException.GetType().Name);
                    context.Response.StatusCode = (int)gitHubException.StatusCode;
                    await Results.Problem(
                        title: "GitHub API request failed",
                        detail: gitHubException.Message,
                        statusCode: (int)gitHubException.StatusCode)
                        .ExecuteAsync(context);
                    return;
                }

                logger.LogError(
                    exception,
                    "Unexpected server error while handling request. Path={RequestPath}, ExceptionType={ExceptionType}.",
                    context.Request.Path.Value,
                    exception?.GetType().Name ?? "unknown");
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await Results.Problem(
                    title: "Unexpected server error",
                    detail: "The local backend hit an unexpected error while processing the request.",
                    statusCode: StatusCodes.Status500InternalServerError)
                    .ExecuteAsync(context);
            });
        });

        return app;
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
