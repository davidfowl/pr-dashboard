using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Caching.Memory;

sealed partial class GitHubClient(HttpClient httpClient, GitHubTokenProvider tokenProvider, IMemoryCache cache, IHostEnvironment environment)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(45);
    private const int MaxLinkedIssuesPerPullRequest = 10;

    public async Task<string?> GetCurrentUserLoginAsync(CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        return await cache.GetOrCreateAsync($"current-user:{authCacheKey}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var user = await SendGitHubRequestAsync(
                "user",
                GitHubJsonSerializerContext.Default.GitHubActorDto,
                cancellationToken);
            return user.Login;
        });
    }

    public async Task<IReadOnlyList<PullRequestSummary>> GetPullRequestsAsync(
        RepositoryName repositoryName,
        string state,
        CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"pulls:{authCacheKey}:{repositoryName}:{state}";
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls?state={Uri.EscapeDataString(state)}&sort=updated&direction=desc&per_page=30";
            var pullRequestDtos = await SendGitHubRequestAsync(
                url,
                GitHubJsonSerializerContext.Default.GitHubPullRequestDtoArray,
                cancellationToken);
            var activePullRequestDtos = pullRequestDtos
                .Where(pullRequest => !pullRequest.Draft)
                .ToArray();

            var pullRequests = activePullRequestDtos
                .Select(PullRequestSummary.FromDto)
                .ToArray();

            var reviewTasks = pullRequests.ToDictionary(
                pullRequest => pullRequest.Number,
                pullRequest => GetReviewStatusAsync(repositoryName, pullRequest.Number, cancellationToken));
            var linkedIssueTasks = activePullRequestDtos.ToDictionary(
                pullRequest => pullRequest.Number,
                pullRequest => GetLinkedIssuesAsync(repositoryName, pullRequest.Body, cancellationToken));

            await Task.WhenAll(reviewTasks.Values);
            await Task.WhenAll(linkedIssueTasks.Values);

            return pullRequests
                .Select(pullRequest => pullRequest with
                {
                    LinkedIssues = linkedIssueTasks[pullRequest.Number].Result,
                    Review = reviewTasks[pullRequest.Number].Result
                })
                .ToArray();
        }) ?? [];
    }

    public async Task<ReviewStatus> GetReviewStatusAsync(
        RepositoryName repositoryName,
        int number,
        CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"reviews:{authCacheKey}:{repositoryName}:{number}";
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            GitHubReviewDto[] reviews;
            try
            {
                reviews = await SendGitHubRequestAsync(
                    $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls/{number}/reviews?per_page=100",
                    GitHubJsonSerializerContext.Default.GitHubReviewDtoArray,
                    cancellationToken);
            }
            catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Review status is enrichment-only. GitHub can return 404 when a PR becomes
                // unavailable between the list and enrichment calls, so keep the PR visible.
                return ReviewStatus.Waiting;
            }

            var humanReviews = reviews
                .Select(ReviewEvent.FromDto)
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
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"timeline:{authCacheKey}:{repositoryName}:{number}";
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            // PRs are issues in the GitHub REST API timeline model, so this endpoint returns
            // the mixed event stream behind the GitHub.com PR timeline UI.
            // https://docs.github.com/en/rest/issues/timeline
            var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/issues/{number}/timeline?per_page=100";
            var items = new List<GitHubTimelineItemDto>();

            for (var page = 0; page < 3 && url is not null; page++)
            {
                using var response = await SendAuthorizedRequestAsync(url, cancellationToken);
                var pageItems = await ReadGitHubJsonAsync(
                    response,
                    GitHubJsonSerializerContext.Default.GitHubTimelineItemDtoArray,
                    cancellationToken);

                items.AddRange(pageItems);
                url = GetNextPageUrl(response);
            }

            return items
                .Select(TimelineItem.FromDto)
                .OrderBy(item => item.OccurredAt)
                .ToArray();
        }) ?? [];
    }

    public async Task<PullRequestDetails> GetPullRequestDetailsAsync(
        RepositoryName repositoryName,
        int number,
        CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"pull:{authCacheKey}:{repositoryName}:{number}";
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            var pullRequest = await SendGitHubRequestAsync(
                $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls/{number}",
                GitHubJsonSerializerContext.Default.GitHubPullRequestDto,
                cancellationToken);

            return PullRequestDetails.FromDto(pullRequest);
        }) ?? throw new GitHubApiException(HttpStatusCode.NotFound, $"Pull request #{number} was not found.");
    }

    private async Task<IReadOnlyList<LinkedIssueSummary>> GetLinkedIssuesAsync(
        RepositoryName repositoryName,
        string? body,
        CancellationToken cancellationToken)
    {
        var references = FindLinkedIssueReferences(repositoryName, body);
        if (references.Count == 0)
        {
            return [];
        }

        var issueTasks = references
            .Select(reference => GetIssueAsync(reference.RepositoryName, reference.Number, cancellationToken))
            .ToArray();

        await Task.WhenAll(issueTasks);

        return issueTasks
            .Select(task => task.Result)
            .Where(issue => issue is not null)
            .Select(issue => issue!)
            .ToArray();
    }

    private async Task<LinkedIssueSummary?> GetIssueAsync(
        RepositoryName repositoryName,
        int number,
        CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"issue:{authCacheKey}:{repositoryName}:{number}";
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            GitHubIssueDto issue;
            try
            {
                issue = await SendGitHubRequestAsync(
                    $"repos/{repositoryName.Owner}/{repositoryName.Name}/issues/{number}",
                    GitHubJsonSerializerContext.Default.GitHubIssueDto,
                    cancellationToken);
            }
            catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Linked issues are enrichment-only. GitHub returns 404 for deleted, private, or
                // accidentally parsed same-repo references, so skip them instead of failing the list.
                return null;
            }

            return issue.PullRequest is null
                ? LinkedIssueSummary.FromDto(repositoryName, issue)
                : null;
        });
    }

    private async Task<T> SendGitHubRequestAsync<T>(
        string url,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedRequestAsync(url, cancellationToken);
        return await ReadGitHubJsonAsync(response, jsonTypeInfo, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAuthorizedRequestAsync(string url, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetTokenAsync(cancellationToken);
        if (token is null)
        {
            throw new GitHubApiException(
                HttpStatusCode.Unauthorized,
                environment.IsDevelopment()
                    ? "GitHub authentication is required. Set GITHUB_TOKEN or GH_TOKEN, run `gh auth login`, or sign in with GitHub."
                    : "GitHub authentication is required. Sign in with GitHub.");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<T> ReadGitHubJsonAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var message = await ReadGitHubErrorMessageAsync(response, cancellationToken);
            throw new GitHubApiException(response.StatusCode, message);
        }

        return await response.Content.ReadFromJsonAsync(jsonTypeInfo, cancellationToken)
            ?? throw new GitHubApiException(response.StatusCode, "GitHub API returned an empty response.");
    }

    private static async Task<string> ReadGitHubErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var error = await response.Content.ReadFromJsonAsync(
            GitHubJsonSerializerContext.Default.GitHubErrorDto,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(error?.Message))
        {
            return $"GitHub API returned {(int)response.StatusCode}: {error.Message}";
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

    private static IReadOnlyList<IssueReference> FindLinkedIssueReferences(RepositoryName repositoryName, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        var references = new Dictionary<string, IssueReference>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in GitHubIssueUrlRegex().Matches(body))
        {
            AddIssueReference(references, repositoryName, match);
        }

        foreach (Match match in IssueReferenceRegex().Matches(body))
        {
            AddIssueReference(references, repositoryName, match);
        }

        return references.Values
            .Take(MaxLinkedIssuesPerPullRequest)
            .ToArray();
    }

    private static void AddIssueReference(
        IDictionary<string, IssueReference> references,
        RepositoryName defaultRepositoryName,
        Match match)
    {
        if (!int.TryParse(match.Groups["number"].Value, out var number) || number <= 0)
        {
            return;
        }

        var repositoryName = defaultRepositoryName;
        if (match.Groups["owner"] is { Success: true } owner
            && match.Groups["repo"] is { Success: true } repo
            && !string.IsNullOrWhiteSpace(owner.Value)
            && !string.IsNullOrWhiteSpace(repo.Value)
            && RepositoryName.TryParse($"{owner.Value}/{repo.Value}", out var parsedRepositoryName))
        {
            repositoryName = parsedRepositoryName;
        }

        references[$"{repositoryName}#{number}"] = new IssueReference(repositoryName, number);
    }

    [GeneratedRegex("<(?<url>[^>]+)>;\\s*rel=\"(?<rel>[^\"]+)\"")]
    private static partial Regex LinkHeaderRegex();

    [GeneratedRegex("https://github\\.com/(?<owner>[A-Za-z0-9._-]+)/(?<repo>[A-Za-z0-9._-]+)/issues/(?<number>[1-9][0-9]*)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubIssueUrlRegex();

    [GeneratedRegex("(?<![A-Za-z0-9._/-])(?:(?<owner>[A-Za-z0-9._-]+)/(?<repo>[A-Za-z0-9._-]+))?#(?<number>[1-9][0-9]*)\\b")]
    private static partial Regex IssueReferenceRegex();

    private readonly record struct IssueReference(RepositoryName RepositoryName, int Number);

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
