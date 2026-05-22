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

                if (exception is GitHubApiException gitHubException)
                {
                    context.Response.StatusCode = (int)gitHubException.StatusCode;
                    await Results.Problem(
                        title: "GitHub API request failed",
                        detail: gitHubException.Message,
                        statusCode: (int)gitHubException.StatusCode)
                        .ExecuteAsync(context);
                    return;
                }

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
