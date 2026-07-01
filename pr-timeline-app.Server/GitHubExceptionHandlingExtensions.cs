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
                        "GitHub API request failed. Path={RequestPath}, Repository={Repository}, StatusCode={GitHubStatusCode}, ExceptionType={ExceptionType}.",
                        context.Request.Path.Value,
                        string.IsNullOrWhiteSpace(repository) ? null : repository,
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
}
