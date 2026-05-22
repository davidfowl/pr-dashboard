using System.Net.Http.Headers;

public static class GitHubServiceCollectionExtensions
{
    public static IServiceCollection AddGitHubApiServices(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<GitHubTokenProvider>();
        services.AddSingleton<GitHubOAuthDeviceFlow>();

        services.AddHttpClient<GitHubClient>(httpClient =>
        {
            httpClient.BaseAddress = new Uri("https://api.github.com/");

            // GitHub REST API requires a User-Agent and recommends this version header.
            // https://docs.github.com/en/rest/using-the-rest-api/getting-started-with-the-rest-api
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("pr-timeline-app", "1.0"));
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        });

        services.AddHttpClient<GitHubOAuthDeviceFlow>(httpClient =>
        {
            httpClient.BaseAddress = new Uri("https://github.com/");
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("pr-timeline-app", "1.0"));
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        return services;
    }
}
