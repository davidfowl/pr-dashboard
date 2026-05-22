using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<GitHubTokenProvider>();
builder.Services.AddSingleton<GitHubOAuthDeviceFlow>();

builder.Services.AddHttpClient<GitHubClient>(httpClient =>
{
    httpClient.BaseAddress = new Uri("https://api.github.com/");

    // GitHub REST API requires a User-Agent and recommends this version header.
    // https://docs.github.com/en/rest/using-the-rest-api/getting-started-with-the-rest-api
    httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("pr-timeline-app", "1.0"));
    httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
});

builder.Services.AddHttpClient<GitHubOAuthDeviceFlow>(httpClient =>
{
    httpClient.BaseAddress = new Uri("https://github.com/");
    httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("pr-timeline-app", "1.0"));
    httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});

var app = builder.Build();

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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

var api = app.MapGroup("/api/github");

api.MapGet("auth-status", async (GitHubTokenProvider tokenProvider, GitHubClient gitHub, CancellationToken cancellationToken) =>
{
    var token = await tokenProvider.GetTokenAsync(cancellationToken);
    var login = token is null ? null : await gitHub.GetCurrentUserLoginAsync(cancellationToken);
    return Results.Ok(new AuthStatusResponse(
        Authenticated: token is not null,
        Configured: GitHubOAuthDeviceFlow.IsConfigured,
        CanLogin: GitHubOAuthDeviceFlow.IsConfigured,
        Source: token?.Source,
        Login: login,
        Message: token is null
            ? GitHubOAuthDeviceFlow.IsConfigured
                ? "Sign in with GitHub to let the dashboard call the GitHub API."
                : "Set GITHUB_CLIENT_ID for GitHub login, or set GITHUB_TOKEN/GH_TOKEN, or run `gh auth login`."
            : token.Source == "oauth"
                ? "Signed in with GitHub for this local session."
                : "GitHub API token is available to the local backend."));
});

api.MapPost("login/start", async (
    HttpContext context,
    GitHubOAuthDeviceFlow deviceFlow,
    CancellationToken cancellationToken) =>
{
    if (!IsBrowserMutationRequest(context))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (!GitHubOAuthDeviceFlow.IsConfigured)
    {
        return Results.Problem(
            title: "GitHub login is not configured",
            detail: "Set GITHUB_CLIENT_ID to a GitHub OAuth App client ID with Device Flow enabled.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    return Results.Ok(await deviceFlow.StartAsync(cancellationToken));
});

api.MapPost("login/poll", async (
    HttpContext context,
    GitHubOAuthDeviceFlow deviceFlow,
    CancellationToken cancellationToken) =>
{
    if (!IsBrowserMutationRequest(context))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    return Results.Ok(await deviceFlow.PollAsync(cancellationToken));
});

api.MapPost("logout", (HttpContext context, GitHubTokenProvider tokenProvider) =>
{
    if (!IsBrowserMutationRequest(context))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    tokenProvider.Logout();
    return Results.Ok(new { authenticated = false });
});

static bool IsBrowserMutationRequest(HttpContext context)
{
    if (!context.Request.HasJsonContentType())
    {
        return false;
    }

    var origin = context.Request.Headers.Origin.ToString();
    return string.IsNullOrEmpty(origin)
        || Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || uri.Host.Equals(context.Request.Host.Host, StringComparison.OrdinalIgnoreCase));
}

api.MapGet("pulls", async (
    [FromQuery] string? repo,
    [FromQuery] string? state,
    GitHubClient gitHub,
    CancellationToken cancellationToken) =>
{
    if (!RepositoryName.TryParse(repo ?? "microsoft/aspire", out var repositoryName))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["repo"] = ["Use the owner/repo format, for example microsoft/aspire."]
        });
    }

    var normalizedState = string.IsNullOrWhiteSpace(state) ? "open" : state.Trim().ToLowerInvariant();
    if (normalizedState is not ("open" or "closed" or "all"))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["state"] = ["State must be open, closed, or all."]
        });
    }

    var pulls = await gitHub.GetPullRequestsAsync(repositoryName, normalizedState, cancellationToken);
    return Results.Ok(new PullRequestListResponse(repositoryName.ToString(), pulls));
});

api.MapGet("pulls/{number:int}/timeline", async (
    int number,
    [FromQuery] string? repo,
    GitHubClient gitHub,
    CancellationToken cancellationToken) =>
{
    if (number <= 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["number"] = ["Pull request number must be greater than zero."]
        });
    }

    if (!RepositoryName.TryParse(repo ?? "microsoft/aspire", out var repositoryName))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["repo"] = ["Use the owner/repo format, for example microsoft/aspire."]
        });
    }

    var pullRequest = await gitHub.GetPullRequestDetailsAsync(repositoryName, number, cancellationToken);
    var timeline = await gitHub.GetPullRequestTimelineAsync(repositoryName, number, cancellationToken);
    var stats = TimelineStats.Create(pullRequest, timeline);

    return Results.Ok(new TimelineResponse(repositoryName.ToString(), number, stats, timeline));
});

app.MapDefaultEndpoints();

app.UseFileServer();

app.Run();

public partial class Program;

sealed partial class GitHubClient(HttpClient httpClient, GitHubTokenProvider tokenProvider, IMemoryCache cache)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(45);

    public async Task<string?> GetCurrentUserLoginAsync(CancellationToken cancellationToken)
    {
        return await cache.GetOrCreateAsync($"current-user:{tokenProvider.AuthGeneration}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            using var document = await SendGitHubRequestAsync("user", cancellationToken);
            return document.RootElement.TryGetProperty("login", out var login)
                && login.ValueKind == JsonValueKind.String
                ? login.GetString()
                : null;
        });
    }

    public async Task<IReadOnlyList<PullRequestSummary>> GetPullRequestsAsync(
        RepositoryName repositoryName,
        string state,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"pulls:{tokenProvider.AuthGeneration}:{repositoryName}:{state}";
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls?state={Uri.EscapeDataString(state)}&sort=updated&direction=desc&per_page=30";
            using var document = await SendGitHubRequestAsync(url, cancellationToken);

            var pullRequests = document.RootElement.EnumerateArray()
                .Select(PullRequestSummary.FromJson)
                .ToArray();

            var reviewTasks = pullRequests.ToDictionary(
                pullRequest => pullRequest.Number,
                pullRequest => GetReviewStatusAsync(repositoryName, pullRequest.Number, cancellationToken));

            await Task.WhenAll(reviewTasks.Values);

            return pullRequests
                .Select(pullRequest => pullRequest with { Review = reviewTasks[pullRequest.Number].Result })
                .ToArray();
        }) ?? [];
    }

    public async Task<ReviewStatus> GetReviewStatusAsync(
        RepositoryName repositoryName,
        int number,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"reviews:{tokenProvider.AuthGeneration}:{repositoryName}:{number}";
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            using var document = await SendGitHubRequestAsync(
                $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls/{number}/reviews?per_page=100",
                cancellationToken);

            var humanReviews = document.RootElement.EnumerateArray()
                .Select(ReviewEvent.FromJson)
                .Where(review => !IsBotActor(review.Actor))
                .OrderBy(review => review.SubmittedAt)
                .ToArray();

            var latestByReviewer = humanReviews
                .GroupBy(review => review.Actor, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.MaxBy(review => review.SubmittedAt)!)
                .ToArray();

            // GitHub's review conclusion is based on each reviewer's latest review state, not the
            // newest review event globally. Example raw states: APPROVED, CHANGES_REQUESTED,
            // COMMENTED. A later COMMENTED review should not erase another reviewer's approval.
            var state =
                latestByReviewer.Any(review => review.State == "CHANGES_REQUESTED") ? "changes_requested" :
                latestByReviewer.Any(review => review.State == "APPROVED") ? "approved" :
                latestByReviewer.Any(review => review.State == "COMMENTED") ? "reviewed" :
                "waiting";

            return new ReviewStatus(
                State: state,
                LatestState: humanReviews.LastOrDefault()?.State,
                ReviewerCount: humanReviews.Select(review => review.Actor).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ApprovalCount: humanReviews.Count(review => review.State == "APPROVED"),
                ChangesRequestedCount: humanReviews.Count(review => review.State == "CHANGES_REQUESTED"),
                CommentedReviewCount: humanReviews.Count(review => review.State == "COMMENTED"),
                LastReviewedAt: humanReviews.LastOrDefault()?.SubmittedAt);
        }) ?? ReviewStatus.Waiting;
    }

    public async Task<IReadOnlyList<TimelineItem>> GetPullRequestTimelineAsync(
        RepositoryName repositoryName,
        int number,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"timeline:{tokenProvider.AuthGeneration}:{repositoryName}:{number}";
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            // PRs are issues in the GitHub REST API timeline model, so this endpoint returns
            // the mixed event stream behind the GitHub.com PR timeline UI.
            // https://docs.github.com/en/rest/issues/timeline
            var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/issues/{number}/timeline?per_page=100";
            var elements = new List<JsonElement>();

            for (var page = 0; page < 3 && url is not null; page++)
            {
                using var response = await SendAuthorizedRequestAsync(url, cancellationToken);
                var document = await ReadGitHubJsonAsync(response, cancellationToken);

                foreach (var element in document.RootElement.EnumerateArray())
                {
                    elements.Add(element.Clone());
                }

                document.Dispose();
                url = GetNextPageUrl(response);
            }

            return elements
                .Select(TimelineItem.FromJson)
                .OrderBy(item => item.OccurredAt)
                .ToArray();
        }) ?? [];
    }

    public async Task<PullRequestDetails> GetPullRequestDetailsAsync(
        RepositoryName repositoryName,
        int number,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"pull:{tokenProvider.AuthGeneration}:{repositoryName}:{number}";
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            using var document = await SendGitHubRequestAsync(
                $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls/{number}",
                cancellationToken);

            return PullRequestDetails.FromJson(document.RootElement);
        }) ?? throw new GitHubApiException(HttpStatusCode.NotFound, $"Pull request #{number} was not found.");
    }

    private async Task<JsonDocument> SendGitHubRequestAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedRequestAsync(url, cancellationToken);
        return await ReadGitHubJsonAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAuthorizedRequestAsync(string url, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetTokenAsync(cancellationToken);
        if (token is null)
        {
            throw new GitHubApiException(
                HttpStatusCode.Unauthorized,
                "GitHub authentication is required. Set GITHUB_TOKEN or GH_TOKEN, or run `gh auth login`.");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<JsonDocument> ReadGitHubJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var message = await ReadGitHubErrorMessageAsync(response, cancellationToken);
            throw new GitHubApiException(response.StatusCode, message);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task<string> ReadGitHubErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (document.RootElement.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String
            && messageElement.GetString() is { Length: > 0 } message)
        {
            return $"GitHub API returned {(int)response.StatusCode}: {message}";
        }

        return $"GitHub API returned {(int)response.StatusCode}.";
    }

    private static string? GetNextPageUrl(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Link", out var values) is false)
        {
            return null;
        }

        foreach (var value in values)
        {
            // GitHub Link header example:
            // <https://api.github.com/repositories/1/issues/2/timeline?page=2>; rel="next",
            // <https://api.github.com/repositories/1/issues/2/timeline?page=4>; rel="last"
            // https://docs.github.com/en/rest/using-the-rest-api/using-pagination-in-the-rest-api
            foreach (Match match in LinkHeaderRegex().Matches(value))
            {
                if (match.Groups["rel"].Value.Equals("next", StringComparison.OrdinalIgnoreCase))
                {
                    var absoluteUrl = match.Groups["url"].Value;
                    return absoluteUrl.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase)
                        ? absoluteUrl["https://api.github.com/".Length..]
                        : null;
                }
            }
        }

        return null;
    }

    [GeneratedRegex("<(?<url>[^>]+)>;\\s*rel=\"(?<rel>[^\"]+)\"")]
    private static partial Regex LinkHeaderRegex();

    private static bool IsBotActor(string actor) =>
        actor.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)
        || s_knownBotActors.Contains(actor);

    private static readonly HashSet<string> s_knownBotActors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Copilot",
        "dependabot",
        "dependabot-preview",
        "github-actions",
        "renovate"
    };
}

sealed class GitHubTokenProvider
{
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private TokenResult? oauthToken;
    private TokenResult? cachedGitHubCliToken;
    private bool attemptedGitHubCli;
    private bool suppressFallback;

    public long AuthGeneration { get; private set; }

    public void SetOAuthToken(string token)
    {
        oauthToken = new TokenResult(token, "oauth");
        suppressFallback = false;
        AuthGeneration++;
    }

    public void Logout()
    {
        oauthToken = null;
        suppressFallback = true;
        AuthGeneration++;
    }

    public async Task<TokenResult?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (oauthToken is not null)
        {
            return oauthToken;
        }

        if (suppressFallback)
        {
            return null;
        }

        var environmentToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            return new TokenResult(environmentToken.Trim(), "environment");
        }

        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (cachedGitHubCliToken is not null)
            {
                return cachedGitHubCliToken;
            }

            if (attemptedGitHubCli)
            {
                return null;
            }

            attemptedGitHubCli = true;
            var ghToken = await GetGitHubCliTokenAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(ghToken))
            {
                cachedGitHubCliToken = new TokenResult(ghToken.Trim(), "gh");
                return cachedGitHubCliToken;
            }

            return null;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task<string?> GetGitHubCliTokenAsync(CancellationToken cancellationToken)
    {
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = "gh",
                ArgumentList = { "auth", "token" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
        }
        catch (Win32Exception)
        {
            return null;
        }

        using (process)
        {
            if (process is null)
            {
                return null;
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            return await process.StandardOutput.ReadToEndAsync(cancellationToken);
        }
    }
}

sealed class GitHubOAuthDeviceFlow(HttpClient httpClient, GitHubTokenProvider tokenProvider)
{
    private const string Scope = "repo read:org";
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private DeviceLoginState? current;

    public static string? ClientId => Environment.GetEnvironmentVariable("GITHUB_CLIENT_ID");

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    public async Task<DeviceLoginResponse> StartAsync(CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (current is { ExpiresAt: var expiresAt } && expiresAt > DateTimeOffset.UtcNow)
            {
                return current.ToResponse("pending", "Enter this code at GitHub to finish signing in.");
            }

            var clientId = ClientId ?? throw new InvalidOperationException("GitHub login is not configured.");
            using var response = await httpClient.PostAsync(
                "login/device/code",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["scope"] = Scope
                }),
                cancellationToken);
            using var document = await ReadOAuthJsonAsync(response, cancellationToken);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                return new DeviceLoginResponse(
                    Status: "error",
                    UserCode: null,
                    VerificationUri: null,
                    VerificationUriComplete: null,
                    IntervalSeconds: 5,
                    ExpiresAt: null,
                    Message: OAuthErrorMessage(error.GetString(), root));
            }

            current = new DeviceLoginState(
                DeviceCode: root.GetProperty("device_code").GetString() ?? "",
                UserCode: root.GetProperty("user_code").GetString() ?? "",
                VerificationUri: root.GetProperty("verification_uri").GetString() ?? "https://github.com/login/device",
                VerificationUriComplete: root.TryGetProperty("verification_uri_complete", out var complete) ? complete.GetString() : null,
                IntervalSeconds: root.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 5,
                ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()));

            return current.ToResponse("pending", "Enter this code at GitHub to finish signing in.");
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<DeviceLoginResponse> PollAsync(CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (current is null)
            {
                return new DeviceLoginResponse("idle", null, null, null, 5, null, "Start GitHub login first.");
            }

            if (current.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                current = null;
                return new DeviceLoginResponse("expired", null, null, null, 5, null, "The GitHub login code expired.");
            }

            if (current.NextPollAt > DateTimeOffset.UtcNow)
            {
                return current.ToResponse("pending", "Waiting for GitHub authorization.");
            }

            current = current with { NextPollAt = DateTimeOffset.UtcNow.AddSeconds(current.IntervalSeconds) };
            var clientId = ClientId ?? throw new InvalidOperationException("GitHub login is not configured.");
            using var response = await httpClient.PostAsync(
                "login/oauth/access_token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["device_code"] = current.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                }),
                cancellationToken);
            using var document = await ReadOAuthJsonAsync(response, cancellationToken);
            var root = document.RootElement;

            if (root.TryGetProperty("access_token", out var accessToken)
                && accessToken.GetString() is { Length: > 0 } token)
            {
                tokenProvider.SetOAuthToken(token);
                current = null;
                return new DeviceLoginResponse("authorized", null, null, null, 5, null, "Signed in with GitHub.");
            }

            var error = root.TryGetProperty("error", out var errorProperty) ? errorProperty.GetString() : "authorization_pending";
            if (error == "slow_down")
            {
                current = current with { IntervalSeconds = current.IntervalSeconds + 5 };
            }

            if (error is "expired_token" or "access_denied")
            {
                current = null;
            }

            var status = error switch
            {
                "authorization_pending" => "pending",
                "expired_token" => "expired",
                _ => error ?? "pending"
            };
            return current?.ToResponse(status, OAuthErrorMessage(error, root))
                ?? new DeviceLoginResponse(status, null, null, null, 5, null, OAuthErrorMessage(error, root));
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task<JsonDocument> ReadOAuthJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string OAuthErrorMessage(string? error, JsonElement root) =>
        error switch
        {
            "authorization_pending" => "Waiting for GitHub authorization.",
            "slow_down" => "GitHub asked us to poll more slowly.",
            "expired_token" => "The GitHub login code expired.",
            "access_denied" => "GitHub login was canceled.",
            "device_flow_disabled" => "Enable Device Flow on the GitHub OAuth App used by GITHUB_CLIENT_ID.",
            _ => root.TryGetProperty("error_description", out var description)
                ? description.GetString() ?? "GitHub login failed."
                : "GitHub login failed."
        };

    private sealed record DeviceLoginState(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        string? VerificationUriComplete,
        int IntervalSeconds,
        DateTimeOffset ExpiresAt)
    {
        public DateTimeOffset NextPollAt { get; init; } = DateTimeOffset.MinValue;

        public DeviceLoginResponse ToResponse(string status, string message) =>
            new(
                Status: status,
                UserCode: UserCode,
                VerificationUri: VerificationUri,
                VerificationUriComplete: VerificationUriComplete,
                IntervalSeconds: IntervalSeconds,
                ExpiresAt: ExpiresAt,
                Message: message);
    }
}

readonly partial record struct RepositoryName(string Owner, string Name)
{
    public static bool TryParse(string value, out RepositoryName repositoryName)
    {
        repositoryName = default;

        if (RepositoryRegex().Match(value.Trim()) is not { Success: true } match)
        {
            return false;
        }

        repositoryName = new RepositoryName(match.Groups["owner"].Value, match.Groups["repo"].Value);
        return true;
    }

    public override string ToString() => $"{Owner}/{Name}";

    [GeneratedRegex("^(?<owner>[A-Za-z0-9._-]+)/(?<repo>[A-Za-z0-9._-]+)$")]
    private static partial Regex RepositoryRegex();
}

sealed class GitHubApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

record TokenResult(string Value, string Source);

record AuthStatusResponse(bool Authenticated, bool Configured, bool CanLogin, string? Source, string? Login, string Message);

record DeviceLoginResponse(
    string Status,
    string? UserCode,
    string? VerificationUri,
    string? VerificationUriComplete,
    int IntervalSeconds,
    DateTimeOffset? ExpiresAt,
    string Message);

record PullRequestListResponse(string Repository, IReadOnlyList<PullRequestSummary> PullRequests);

record PullRequestSummary(
    int Number,
    string Title,
    string State,
    bool Draft,
    string Author,
    string HtmlUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> RequestedReviewers,
    ReviewStatus Review)
{
    public static PullRequestSummary FromJson(JsonElement element) =>
        new(
            element.GetProperty("number").GetInt32(),
            element.GetProperty("title").GetString() ?? "",
            element.GetProperty("state").GetString() ?? "",
            element.GetProperty("draft").GetBoolean(),
            GetNestedString(element, "user", "login") ?? "unknown",
            element.GetProperty("html_url").GetString() ?? "",
            element.GetProperty("created_at").GetDateTimeOffset(),
            element.GetProperty("updated_at").GetDateTimeOffset(),
            element.GetProperty("labels")
                .EnumerateArray()
                .Select(label => label.GetProperty("name").GetString())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(label => label!)
                .ToArray(),
            element.GetProperty("requested_reviewers")
                .EnumerateArray()
                .Select(reviewer => reviewer.GetProperty("login").GetString())
                .Concat(element.GetProperty("requested_teams")
                    .EnumerateArray()
                    .Select(team => team.GetProperty("name").GetString()))
                .Where(reviewer => !string.IsNullOrWhiteSpace(reviewer))
                .Select(reviewer => reviewer!)
                .ToArray(),
            ReviewStatus.Waiting);

    private static string? GetNestedString(JsonElement element, string propertyName, string nestedPropertyName) =>
        element.TryGetProperty(propertyName, out var nested)
        && nested.ValueKind == JsonValueKind.Object
        && nested.TryGetProperty(nestedPropertyName, out var nestedValue)
        && nestedValue.ValueKind == JsonValueKind.String
            ? nestedValue.GetString()
            : null;
}

record ReviewStatus(
    string State,
    string? LatestState,
    int ReviewerCount,
    int ApprovalCount,
    int ChangesRequestedCount,
    int CommentedReviewCount,
    DateTimeOffset? LastReviewedAt)
{
    public static ReviewStatus Waiting { get; } = new(
        State: "waiting",
        LatestState: null,
        ReviewerCount: 0,
        ApprovalCount: 0,
        ChangesRequestedCount: 0,
        CommentedReviewCount: 0,
        LastReviewedAt: null);
}

record ReviewEvent(string Actor, string State, DateTimeOffset SubmittedAt)
{
    public static ReviewEvent FromJson(JsonElement element) =>
        new(
            Actor: GetNestedString(element, "user", "login") ?? "unknown",
            State: element.GetProperty("state").GetString() ?? "UNKNOWN",
            SubmittedAt: element.GetProperty("submitted_at").GetDateTimeOffset());

    private static string? GetNestedString(JsonElement element, string propertyName, string nestedPropertyName) =>
        element.TryGetProperty(propertyName, out var nested)
        && nested.ValueKind == JsonValueKind.Object
        && nested.TryGetProperty(nestedPropertyName, out var nestedValue)
        && nestedValue.ValueKind == JsonValueKind.String
            ? nestedValue.GetString()
            : null;
}

record PullRequestDetails(
    DateTimeOffset CreatedAt,
    string Author,
    DateTimeOffset? MergedAt,
    int CommitCount)
{
    public static PullRequestDetails FromJson(JsonElement element) =>
        new(
            element.GetProperty("created_at").GetDateTimeOffset(),
            GetNestedString(element, "user", "login") ?? "unknown",
            GetNullableDate(element, "merged_at"),
            element.GetProperty("commits").GetInt32());

    private static DateTimeOffset? GetNullableDate(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out var date)
            ? date
            : null;

    private static string? GetNestedString(JsonElement element, string propertyName, string nestedPropertyName) =>
        element.TryGetProperty(propertyName, out var nested)
        && nested.ValueKind == JsonValueKind.Object
        && nested.TryGetProperty(nestedPropertyName, out var nestedValue)
        && nestedValue.ValueKind == JsonValueKind.String
            ? nestedValue.GetString()
            : null;
}

record TimelineResponse(string Repository, int Number, TimelineStats Stats, IReadOnlyList<TimelineItem> Items);

record TimelineStats(
    int CommitCount,
    int HumanCommenterCount,
    int HumanCommentCount,
    int ReviewCount,
    int ApprovalCount,
    double? FirstHumanCommentDelayMs,
    double? FirstReviewDelayMs,
    double? FirstApprovalDelayMs,
    double? ApprovalToMergeDelayMs,
    double? CreatedToMergeDelayMs,
    double? AverageHumanCommentGapMs,
    double? LongestHumanCommentGapMs,
    DateTimeOffset? MergedAt,
    IReadOnlyList<DeveloperStats> Developers)
{
    public static TimelineStats Create(PullRequestDetails pullRequest, IReadOnlyList<TimelineItem> timeline)
    {
        var humanComments = timeline
            .Where(item => item.Event == "commented"
                && IsHuman(item.Actor)
                && !SameActor(item.Actor, pullRequest.Author))
            .OrderBy(item => item.OccurredAt)
            .ToArray();

        var humanReviews = timeline
            .Where(item => item.Event == "reviewed" && IsHuman(item.Actor))
            .OrderBy(item => item.OccurredAt)
            .ToArray();

        var approvals = humanReviews
            .Where(item => item.State?.Equals("APPROVED", StringComparison.OrdinalIgnoreCase) is true)
            .ToArray();

        var mergedAt = pullRequest.MergedAt
            ?? timeline.FirstOrDefault(item => item.Event == "merged")?.OccurredAt;
        var lastApprovalBeforeMerge = mergedAt is null
            ? null
            : approvals.LastOrDefault(item => item.OccurredAt <= mergedAt.Value);

        return new TimelineStats(
            CommitCount: pullRequest.CommitCount,
            HumanCommenterCount: humanComments.Select(item => NormalizeActorIdentity(item.Actor)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            HumanCommentCount: humanComments.Length,
            ReviewCount: humanReviews.Length,
            ApprovalCount: approvals.Length,
            FirstHumanCommentDelayMs: DelayMs(pullRequest.CreatedAt, humanComments.FirstOrDefault()?.OccurredAt),
            FirstReviewDelayMs: DelayMs(pullRequest.CreatedAt, humanReviews.FirstOrDefault()?.OccurredAt),
            FirstApprovalDelayMs: DelayMs(pullRequest.CreatedAt, approvals.FirstOrDefault()?.OccurredAt),
            ApprovalToMergeDelayMs: DelayMs(lastApprovalBeforeMerge?.OccurredAt, mergedAt),
            CreatedToMergeDelayMs: DelayMs(pullRequest.CreatedAt, mergedAt),
            AverageHumanCommentGapMs: AverageGapMs(humanComments),
            LongestHumanCommentGapMs: LongestGapMs(humanComments),
            MergedAt: mergedAt,
            Developers: CreateDeveloperStats(timeline));
    }

    private static IReadOnlyList<DeveloperStats> CreateDeveloperStats(IReadOnlyList<TimelineItem> timeline) =>
        timeline
            .Where(item => IsHuman(item.Actor))
            .GroupBy(item => NormalizeActorIdentity(item.Actor), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderBy(item => item.OccurredAt).ToArray();
                return new DeveloperStats(
                    Actor: PreferredActorName(ordered.Select(item => item.Actor)),
                    ActivityCount: ordered.Length,
                    CommitCount: ordered.Count(item => item.Event == "committed"),
                    CommentCount: ordered.Count(item => item.Event == "commented"),
                    ReviewCount: ordered.Count(item => item.Event == "reviewed"),
                    ApprovalCount: ordered.Count(item => item.Event == "reviewed" && item.State?.Equals("APPROVED", StringComparison.OrdinalIgnoreCase) is true),
                    ChangesRequestedCount: ordered.Count(item => item.Event == "reviewed" && item.State?.Equals("CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase) is true),
                    FirstActivityAt: ordered.First().OccurredAt,
                    LastActivityAt: ordered.Last().OccurredAt);
            })
            .OrderByDescending(developer => developer.ActivityCount)
            .ThenBy(developer => developer.Actor)
            .ToArray();

    private static bool IsHuman(string actor) =>
        !string.IsNullOrWhiteSpace(actor)
        && !actor.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)
        && !s_knownBotActors.Contains(actor);

    private static bool SameActor(string first, string second) =>
        NormalizeActorIdentity(first).Equals(NormalizeActorIdentity(second), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeActorIdentity(string actor) =>
        string.Concat(actor.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string PreferredActorName(IEnumerable<string> actors) =>
        actors
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(actor => actor.Any(char.IsWhiteSpace))
            .ThenBy(actor => actor.Length)
            .First();

    private static double? DelayMs(DateTimeOffset? start, DateTimeOffset? end) =>
        start is null || end is null ? null : Math.Max(0, (end.Value - start.Value).TotalMilliseconds);

    private static double? AverageGapMs(IReadOnlyList<TimelineItem> items)
    {
        if (items.Count < 2)
        {
            return null;
        }

        return items.Zip(items.Skip(1), (first, second) => (second.OccurredAt - first.OccurredAt).TotalMilliseconds)
            .Average();
    }

    private static double? LongestGapMs(IReadOnlyList<TimelineItem> items)
    {
        if (items.Count < 2)
        {
            return null;
        }

        return items.Zip(items.Skip(1), (first, second) => (second.OccurredAt - first.OccurredAt).TotalMilliseconds)
            .Max();
    }

    private static readonly HashSet<string> s_knownBotActors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Copilot",
        "dependabot",
        "dependabot-preview",
        "github-actions",
        "renovate"
    };
}

record DeveloperStats(
    string Actor,
    int ActivityCount,
    int CommitCount,
    int CommentCount,
    int ReviewCount,
    int ApprovalCount,
    int ChangesRequestedCount,
    DateTimeOffset FirstActivityAt,
    DateTimeOffset LastActivityAt);

record TimelineItem(
    string Id,
    string Event,
    string Actor,
    DateTimeOffset OccurredAt,
    string? State,
    string Summary,
    string? Body,
    string? HtmlUrl)
{
    public static TimelineItem FromJson(JsonElement element)
    {
        var eventName = GetString(element, "event") ?? "event";
        var occurredAt = GetDate(element, "created_at")
            ?? GetDate(element, "submitted_at")
            ?? GetDate(element, "committed_at")
            ?? GetNestedDate(element, "author", "date")
            ?? GetNestedDate(element, "committer", "date")
            ?? DateTimeOffset.MinValue;
        var actor = GetNestedString(element, "actor", "login")
            ?? GetNestedString(element, "user", "login")
            ?? GetNestedString(element, "author", "login")
            ?? GetNestedString(element, "author", "name")
            ?? GetNestedString(element, "committer", "login")
            ?? GetNestedString(element, "committer", "name")
            ?? "unknown";

        return new TimelineItem(
            Id: GetString(element, "id") ?? GetString(element, "sha") ?? $"{eventName}-{occurredAt.ToUnixTimeMilliseconds()}",
            Event: eventName,
            Actor: actor,
            OccurredAt: occurredAt,
            State: GetString(element, "state"),
            Summary: BuildSummary(element, eventName, actor),
            Body: GetString(element, "body"),
            HtmlUrl: GetString(element, "html_url"));
    }

    private static string BuildSummary(JsonElement element, string eventName, string actor)
    {
        var normalizedEvent = eventName.Replace('_', ' ');
        return eventName switch
        {
            "commented" => $"{actor} commented",
            "committed" => $"{actor} pushed commit {ShortSha(GetString(element, "sha") ?? GetString(element, "commit_id"))}",
            "reviewed" => $"{actor} reviewed with state {GetString(element, "state") ?? "unknown"}",
            "review_requested" => $"{actor} requested review from {GetNestedString(element, "requested_reviewer", "login") ?? GetNestedString(element, "requested_team", "name") ?? "someone"}",
            "ready_for_review" => $"{actor} marked the PR ready for review",
            "converted_to_draft" => $"{actor} converted the PR to draft",
            "labeled" => $"{actor} added label {GetNestedString(element, "label", "name") ?? "unknown"}",
            "unlabeled" => $"{actor} removed label {GetNestedString(element, "label", "name") ?? "unknown"}",
            "assigned" => $"{actor} assigned {GetNestedString(element, "assignee", "login") ?? "someone"}",
            "unassigned" => $"{actor} unassigned {GetNestedString(element, "assignee", "login") ?? "someone"}",
            "cross-referenced" => $"{actor} cross-referenced another issue or PR",
            "renamed" => $"{actor} renamed the title",
            "closed" => $"{actor} closed the PR",
            "reopened" => $"{actor} reopened the PR",
            "merged" => $"{actor} merged the PR",
            _ => $"{actor} {normalizedEvent}"
        };
    }

    private static string ShortSha(string? sha) => string.IsNullOrWhiteSpace(sha)
        ? "unknown"
        : sha[..Math.Min(7, sha.Length)];

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static DateTimeOffset? GetDate(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out var date)
            ? date
            : null;

    private static DateTimeOffset? GetNestedDate(JsonElement element, string propertyName, string nestedPropertyName) =>
        element.TryGetProperty(propertyName, out var nested)
        && nested.ValueKind == JsonValueKind.Object
        && nested.TryGetProperty(nestedPropertyName, out var nestedValue)
        && nestedValue.ValueKind == JsonValueKind.String
        && nestedValue.TryGetDateTimeOffset(out var date)
            ? date
            : null;

    private static string? GetNestedString(JsonElement element, string propertyName, string nestedPropertyName) =>
        element.TryGetProperty(propertyName, out var nested)
        && nested.ValueKind == JsonValueKind.Object
        && nested.TryGetProperty(nestedPropertyName, out var nestedValue)
        && nestedValue.ValueKind == JsonValueKind.String
            ? nestedValue.GetString()
            : null;
}
