using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Caching.Memory;

sealed partial class GitHubClient(HttpClient httpClient, GitHubTokenProvider tokenProvider, IMemoryCache cache, IHostEnvironment environment)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan LastGoodCacheDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan ForceRefreshCooldown = TimeSpan.FromMinutes(2);
    private const int PullRequestPageSize = 100;
    private const int PullRequestStreamBatchSize = 20;
    private const int MaxLinkedIssuesPerPullRequest = 10;
    private const int MaxGitHubRedirects = 3;
    internal const int MaxConcurrentGitHubRequests = 8;
    private const int MaxConcurrentChecksFetches = 4;
    private const string RegressionLabelMarker = "regression";
    private const string CtiTeamTitleMarker = "[AspireE2E]";
    private const string CtiTeamSearchTerm = "AspireE2E";
    private static readonly SemaphoreSlim s_githubRequestThrottle = new(MaxConcurrentGitHubRequests, MaxConcurrentGitHubRequests);
    private static readonly SemaphoreSlim s_checksFetchThrottle = new(MaxConcurrentChecksFetches, MaxConcurrentChecksFetches);

    private bool RemoveCacheEntry(string cacheKey, bool forceRefresh)
    {
        if (!forceRefresh)
        {
            return false;
        }

        var forceRefreshKey = $"force-refresh:{cacheKey}";
        if (cache.TryGetValue(forceRefreshKey, out _))
        {
            return false;
        }

        cache.Set(forceRefreshKey, true, ForceRefreshCooldown);
        cache.Remove(cacheKey);
        return true;
    }

    private static string GetLastGoodCacheKey(string cacheKey) => $"last-good:{cacheKey}";

    private void SetLastGood<T>(string cacheKey, T value)
        where T : class =>
        cache.Set(GetLastGoodCacheKey(cacheKey), value, LastGoodCacheDuration);

    private bool TryGetLastGood<T>(string cacheKey, out T? value)
        where T : class =>
        cache.TryGetValue(GetLastGoodCacheKey(cacheKey), out value) && value is not null;

    private bool TryUseLastGoodFallback<T>(
        string cacheKey,
        Exception exception,
        CancellationToken cancellationToken,
        out T? value)
        where T : class
    {
        value = null;
        return IsTransientGitHubFailure(exception, cancellationToken)
            && TryGetLastGood(cacheKey, out value);
    }

    private async Task<T> GetOrCreateWithLastGoodFallbackAsync<T>(
        string cacheKey,
        TimeSpan cacheDuration,
        Func<Task<T>> factory,
        CancellationToken cancellationToken,
        Func<T, bool>? storeLastGood = null)
        where T : class
    {
        if (cache.TryGetValue(cacheKey, out T? cachedValue) && cachedValue is not null)
        {
            return cachedValue;
        }

        try
        {
            var value = await factory();
            if (storeLastGood?.Invoke(value) ?? true)
            {
                SetLastGood(cacheKey, value);
            }

            cache.Set(cacheKey, value, cacheDuration);
            return value;
        }
        catch (Exception ex) when (TryUseLastGoodFallback(cacheKey, ex, cancellationToken, out T? lastGood))
        {
            return lastGood!;
        }
    }

    private static bool IsTransientGitHubFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && exception switch
        {
            GitHubApiException ex => IsTransientGitHubStatusCode(ex.StatusCode)
                || ex.StatusCode == HttpStatusCode.Forbidden && IsRateLimitMessage(ex.Message),
            _ when IsTransientGitHubTransportFailure(exception) => true,
            _ => false
        };

    private static bool IsTransientGitHubTransportFailure(Exception exception) =>
        exception is HttpRequestException
           or TimeoutException
           or OperationCanceledException
        || IsBrokenCircuitException(exception);

    private static bool IsBrokenCircuitException(Exception exception) =>
        exception.GetType().FullName?.StartsWith("Polly.CircuitBreaker.BrokenCircuitException", StringComparison.Ordinal) is true;

    private static bool IsTransientGitHubStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static bool IsRateLimitMessage(string message) =>
        message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("secondary rate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("abuse detection", StringComparison.OrdinalIgnoreCase);

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
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"pulls:{authCacheKey}:{repositoryName}:{state}";
        var bypassedCache = RemoveCacheEntry(cacheKey, forceRefresh);
        return await GetOrCreateWithLastGoodFallbackAsync(
            cacheKey,
            CacheDuration,
            async () =>
        {
            var sort = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "created" : "updated";
            var direction = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
            var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls?state={Uri.EscapeDataString(state)}&sort={sort}&direction={direction}&per_page={PullRequestPageSize}";
            var pullRequestDtos = await SendPagedGitHubRequestAsync(
                url,
                GitHubJsonSerializerContext.Default.GitHubPullRequestDtoArray,
                cancellationToken);
            var activePullRequestDtos = pullRequestDtos
                .Where(pullRequest => !pullRequest.Draft)
                .ToArray();

            return await CreatePullRequestSummariesAsync(repositoryName, activePullRequestDtos, bypassedCache, cancellationToken);
        },
            cancellationToken);
    }

    public async IAsyncEnumerable<PullRequestSummary> StreamPullRequestsAsync(
        RepositoryName repositoryName,
        string state,
        bool forceRefresh,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"pulls:{authCacheKey}:{repositoryName}:{state}";
        var bypassedCache = RemoveCacheEntry(cacheKey, forceRefresh);

        if (!bypassedCache && cache.TryGetValue(cacheKey, out IReadOnlyList<PullRequestSummary>? cachedPullRequests) && cachedPullRequests is not null)
        {
            foreach (var pullRequest in cachedPullRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return pullRequest;
            }

            yield break;
        }

        IReadOnlyList<PullRequestSummary>? stalePullRequests = null;
        var streamedPullRequests = new List<PullRequestSummary>();
        await using var enumerator = CreatePullRequestSummariesInBatchesAsync(
            repositoryName,
            StreamPullRequestDtosAsync(repositoryName, state, cancellationToken),
            bypassedCache,
            cancellationToken).GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            PullRequestSummary? pullRequest = null;
            bool hasPullRequest;
            try
            {
                hasPullRequest = await enumerator.MoveNextAsync();
                if (hasPullRequest)
                {
                    pullRequest = enumerator.Current;
                }
            }
            catch (Exception ex) when (streamedPullRequests.Count == 0
                && TryUseLastGoodFallback(cacheKey, ex, cancellationToken, out stalePullRequests))
            {
                hasPullRequest = false;
            }

            if (stalePullRequests is not null)
            {
                foreach (var stalePullRequest in stalePullRequests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return stalePullRequest;
                }

                yield break;
            }

            if (!hasPullRequest)
            {
                break;
            }

            if (pullRequest is not null)
            {
                streamedPullRequests.Add(pullRequest);
                yield return pullRequest;
            }
        }

        var completedPullRequests = streamedPullRequests.ToArray();
        SetLastGood(cacheKey, completedPullRequests);
        cache.Set(cacheKey, completedPullRequests, CacheDuration);
    }

    public async Task<IReadOnlyList<PullRequestSummary>> GetPullRequestsByLabelAsync(
        RepositoryName repositoryName,
        string state,
        string label,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var normalizedLabel = label.Trim();
        if (string.IsNullOrWhiteSpace(normalizedLabel))
        {
            return await GetPullRequestsAsync(repositoryName, state, forceRefresh, cancellationToken);
        }

        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"pulls:{authCacheKey}:{repositoryName}:{state}:label:{normalizedLabel}";
        var bypassedCache = RemoveCacheEntry(cacheKey, forceRefresh);
        return await GetOrCreateWithLastGoodFallbackAsync(
            cacheKey,
            CacheDuration,
            async () =>
        {
            var sort = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "created" : "updated";
            var direction = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
            var issues = await GetIssuesAsync(
                repositoryName,
                state,
                label: normalizedLabel,
                milestoneNumber: null,
                sort: sort,
                direction: direction,
                cancellationToken: cancellationToken);
            var pullRequestTasks = issues
                .Where(issue => issue.PullRequest is not null)
                .ToDictionary(
                    issue => issue.Number,
                    issue => GetPullRequestDtoOrNullAsync(repositoryName, issue.Number, cancellationToken));

            await Task.WhenAll(pullRequestTasks.Values);

            var pullRequestDtos = new List<GitHubPullRequestDto>(pullRequestTasks.Count);
            foreach (var task in pullRequestTasks.Values)
            {
                if (await task is { Draft: false } pullRequest)
                {
                    pullRequestDtos.Add(pullRequest);
                }
            }

            return await CreatePullRequestSummariesAsync(
                repositoryName,
                pullRequestDtos
                    .OrderBy(pullRequest => pullRequest.CreatedAt)
                    .ToArray(),
                bypassedCache,
                cancellationToken);
        },
            cancellationToken);
    }

    public async IAsyncEnumerable<PullRequestSummary> StreamPullRequestsByLabelAsync(
        RepositoryName repositoryName,
        string state,
        string label,
        bool forceRefresh,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var normalizedLabel = label.Trim();
        if (string.IsNullOrWhiteSpace(normalizedLabel))
        {
            await foreach (var pullRequest in StreamPullRequestsAsync(repositoryName, state, forceRefresh, cancellationToken))
            {
                yield return pullRequest;
            }

            yield break;
        }

        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"pulls:{authCacheKey}:{repositoryName}:{state}:label:{normalizedLabel}";
        var bypassedCache = RemoveCacheEntry(cacheKey, forceRefresh);

        if (!bypassedCache && cache.TryGetValue(cacheKey, out IReadOnlyList<PullRequestSummary>? cachedPullRequests) && cachedPullRequests is not null)
        {
            foreach (var pullRequest in cachedPullRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return pullRequest;
            }

            yield break;
        }

        IReadOnlyList<PullRequestSummary>? stalePullRequests = null;
        var streamedPullRequests = new List<PullRequestSummary>();
        await using var enumerator = CreatePullRequestSummariesInBatchesAsync(
            repositoryName,
            StreamPullRequestDtosByLabelAsync(repositoryName, state, normalizedLabel, cancellationToken),
            bypassedCache,
            cancellationToken).GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            PullRequestSummary? pullRequest = null;
            bool hasPullRequest;
            try
            {
                hasPullRequest = await enumerator.MoveNextAsync();
                if (hasPullRequest)
                {
                    pullRequest = enumerator.Current;
                }
            }
            catch (Exception ex) when (streamedPullRequests.Count == 0
                && TryUseLastGoodFallback(cacheKey, ex, cancellationToken, out stalePullRequests))
            {
                hasPullRequest = false;
            }

            if (stalePullRequests is not null)
            {
                foreach (var stalePullRequest in stalePullRequests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return stalePullRequest;
                }

                yield break;
            }

            if (!hasPullRequest)
            {
                break;
            }

            if (pullRequest is not null)
            {
                streamedPullRequests.Add(pullRequest);
                yield return pullRequest;
            }
        }

        var completedPullRequests = streamedPullRequests.ToArray();
        SetLastGood(cacheKey, completedPullRequests);
        cache.Set(cacheKey, completedPullRequests, CacheDuration);
    }

    public async Task<IReadOnlyList<ShipWeekIssueSummary>> GetFocusIssuesAsync(
        RepositoryName repositoryName,
        string state,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"focus-issues:{authCacheKey}:{repositoryName}:{state}";
        var bypassedCache = RemoveCacheEntry(cacheKey, forceRefresh);
        return await GetOrCreateWithLastGoodFallbackAsync(
            cacheKey,
            CacheDuration,
            async () =>
        {
            var regressionIssuesTask = GetIssuesMatchingLabelMarkerAsync(
                repositoryName,
                state,
                RegressionLabelMarker,
                bypassedCache,
                cancellationToken);
            var ctiTeamIssuesTask = SearchIssuesByTitleMarkerAsync(
                repositoryName,
                state,
                CtiTeamTitleMarker,
                CtiTeamSearchTerm,
                cancellationToken);

            await Task.WhenAll(regressionIssuesTask, ctiTeamIssuesTask);

            var fetchedAt = DateTimeOffset.UtcNow;
            return regressionIssuesTask.Result
                .Concat(ctiTeamIssuesTask.Result)
                .Where(issue => issue.PullRequest is null)
                .GroupBy(issue => issue.Number)
                .Select(group => group.First())
                .OrderByDescending(issue => issue.UpdatedAt)
                .Select(issue => ShipWeekIssueSummary.FromDto(repositoryName, issue, []) with
                {
                    FetchedAt = fetchedAt
                })
                .ToArray();
        },
            cancellationToken);
    }

    public async Task<ShipWeekLoadResult> GetShipWeekAsync(
        RepositoryName repositoryName,
        string milestoneTitle,
        string? releaseBranch,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var normalizedMilestoneTitle = milestoneTitle.Trim();
        var requestedReleaseBranch = releaseBranch?.Trim();
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"ship-week:{authCacheKey}:{repositoryName}:{normalizedMilestoneTitle}:{requestedReleaseBranch ?? "latest-release"}";
        var bypassedCache = RemoveCacheEntry(cacheKey, forceRefresh);
        return await GetOrCreateWithLastGoodFallbackAsync(
            cacheKey,
            CacheDuration,
            async () =>
        {
            var milestone = await GetMilestoneByTitleAsync(repositoryName, normalizedMilestoneTitle, cancellationToken);
            if (milestone is null)
            {
                return ShipWeekLoadResult.ValidationProblem(
                    "milestone",
                    $"Milestone '{normalizedMilestoneTitle}' was not found in {repositoryName}.");
            }

            var normalizedReleaseBranch = string.IsNullOrWhiteSpace(requestedReleaseBranch)
                ? await GetLatestReleaseBranchAsync(repositoryName, cancellationToken)
                : requestedReleaseBranch;

            if (string.IsNullOrWhiteSpace(normalizedReleaseBranch))
            {
                return ShipWeekLoadResult.ValidationProblem(
                    "releaseBranch",
                    $"No release/* branches were found in {repositoryName}.");
            }

            if (!string.IsNullOrWhiteSpace(requestedReleaseBranch)
                && !await BranchExistsAsync(repositoryName, normalizedReleaseBranch, cancellationToken))
            {
                return ShipWeekLoadResult.ValidationProblem(
                    "releaseBranch",
                    $"Branch '{normalizedReleaseBranch}' was not found in {repositoryName}.");
            }

            var milestoneIssuesTask = GetOpenMilestoneIssuesAsync(repositoryName, milestone.Number, cancellationToken);
            var releaseBranchPullRequestsTask = GetOpenPullRequestsByBaseAsync(repositoryName, normalizedReleaseBranch, cancellationToken);
            await Task.WhenAll(milestoneIssuesTask, releaseBranchPullRequestsTask);

            var milestoneIssues = await milestoneIssuesTask;
            var releaseBranchPullRequestDtos = await releaseBranchPullRequestsTask;
            var pullRequestDtosByNumber = releaseBranchPullRequestDtos
                .GroupBy(pullRequest => pullRequest.Number)
                .ToDictionary(group => group.Key, group => group.First());
            var releaseBranchPullRequestNumbers = pullRequestDtosByNumber.Keys.ToHashSet();
            var milestonePullRequestNumbers = milestoneIssues
                .Where(issue => issue.PullRequest is not null)
                .Select(issue => issue.Number)
                .ToHashSet();

            var missingMilestonePullRequestTasks = milestonePullRequestNumbers
                .Where(number => !pullRequestDtosByNumber.ContainsKey(number))
                .ToDictionary(
                    number => number,
                    number => GetPullRequestDtoOrNullAsync(repositoryName, number, cancellationToken));

            await Task.WhenAll(missingMilestonePullRequestTasks.Values);

            foreach (var (number, task) in missingMilestonePullRequestTasks)
            {
                if (await task is { } pullRequest)
                {
                    pullRequestDtosByNumber[number] = pullRequest;
                }
            }

            var pullRequestSummaries = await CreatePullRequestSummariesAsync(
                repositoryName,
                pullRequestDtosByNumber.Values
                    .OrderBy(pullRequest => pullRequest.CreatedAt)
                    .ToArray(),
                bypassedCache,
                cancellationToken);

            var nonPullRequestIssues = milestoneIssues
                .Where(issue => issue.PullRequest is null)
                .OrderByDescending(issue => issue.UpdatedAt)
                .ToArray();
            var milestoneIssueNumbers = nonPullRequestIssues
                .Select(issue => issue.Number)
                .ToHashSet();
            var linkedOpenPullRequestsByIssue = CreateLinkedOpenPullRequestsByIssue(
                repositoryName,
                normalizedMilestoneTitle,
                pullRequestSummaries,
                milestoneIssueNumbers);
            var fetchedAt = DateTimeOffset.UtcNow;
            var shipWeekIssues = nonPullRequestIssues
                .Select(issue =>
                {
                    var linkedOpenPullRequests = linkedOpenPullRequestsByIssue.TryGetValue(issue.Number, out var linked)
                        ? linked
                        : [];
                    return ShipWeekIssueSummary.FromDto(repositoryName, issue, linkedOpenPullRequests) with
                    {
                        FetchedAt = fetchedAt
                    };
                })
                .ToArray();

            var shipWeekPullRequests = pullRequestSummaries
                .Select(pullRequest =>
                {
                    var linkedMilestoneIssueNumbers = pullRequest.LinkedIssues
                        .Where(issue => RepositoryMatches(issue.Repository, repositoryName)
                            && (MilestoneTitleMatches(issue.Milestone, normalizedMilestoneTitle)
                                || milestoneIssueNumbers.Contains(issue.Number)))
                        .Select(issue => issue.Number)
                        .Distinct()
                        .OrderBy(number => number)
                        .ToArray();
                    var inMilestone = milestonePullRequestNumbers.Contains(pullRequest.Number)
                        || MilestoneTitleMatches(pullRequest.Milestone, normalizedMilestoneTitle)
                        || linkedMilestoneIssueNumbers.Length > 0;
                    var targetsReleaseBranch = releaseBranchPullRequestNumbers.Contains(pullRequest.Number)
                        || string.Equals(pullRequest.BaseRef, normalizedReleaseBranch, StringComparison.OrdinalIgnoreCase);

                    return new ShipWeekPullRequestSummary(
                        pullRequest,
                        new ShipWeekReleaseScope(
                            InMilestone: inMilestone,
                            TargetsReleaseBranch: targetsReleaseBranch,
                            ReleaseBranchException: targetsReleaseBranch && !inMilestone,
                            MilestoneIssueNumbers: linkedMilestoneIssueNumbers));
                })
                .OrderByDescending(item => item.ReleaseScope.ReleaseBranchException)
                .ThenBy(item => item.PullRequest.CreatedAt)
                .ToArray();

            return ShipWeekLoadResult.Success(new ShipWeekResponse(
                repositoryName.ToString(),
                normalizedMilestoneTitle,
                normalizedReleaseBranch,
                shipWeekPullRequests,
                shipWeekIssues));
        },
            cancellationToken,
            result => result.Response is not null);
    }

    private async IAsyncEnumerable<PullRequestSummary> CreatePullRequestSummariesInBatchesAsync(
        RepositoryName repositoryName,
        IAsyncEnumerable<GitHubPullRequestDto> pullRequestDtos,
        bool forceRefresh,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seenPullRequests = new HashSet<int>();
        var batch = new List<GitHubPullRequestDto>(PullRequestStreamBatchSize);

        await foreach (var pullRequestDto in pullRequestDtos.WithCancellation(cancellationToken))
        {
            if (!seenPullRequests.Add(pullRequestDto.Number))
            {
                continue;
            }

            batch.Add(pullRequestDto);
            if (batch.Count < PullRequestStreamBatchSize)
            {
                continue;
            }

            foreach (var pullRequest in await CreatePullRequestSummariesAsync(repositoryName, batch, forceRefresh, cancellationToken))
            {
                yield return pullRequest;
            }

            batch.Clear();
        }

        if (batch.Count == 0)
        {
            yield break;
        }

        foreach (var pullRequest in await CreatePullRequestSummariesAsync(repositoryName, batch, forceRefresh, cancellationToken))
        {
            yield return pullRequest;
        }
    }

    private async IAsyncEnumerable<GitHubPullRequestDto> StreamPullRequestDtosAsync(
        RepositoryName repositoryName,
        string state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sort = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "created" : "updated";
        var direction = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls?state={Uri.EscapeDataString(state)}&sort={sort}&direction={direction}&per_page={PullRequestPageSize}";

        await foreach (var pullRequest in SendPagedGitHubRequestStreamAsync(
            url,
            GitHubJsonSerializerContext.Default.GitHubPullRequestDtoArray,
            cancellationToken))
        {
            if (!pullRequest.Draft)
            {
                yield return pullRequest;
            }
        }
    }

    private async IAsyncEnumerable<GitHubPullRequestDto> StreamPullRequestDtosByLabelAsync(
        RepositoryName repositoryName,
        string state,
        string label,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sort = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "created" : "updated";
        var direction = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        var issueBatch = new List<int>(PullRequestStreamBatchSize);

        await foreach (var issue in StreamIssuesAsync(
            repositoryName,
            state,
            label: label,
            sort: sort,
            direction: direction,
            cancellationToken: cancellationToken))
        {
            if (issue.PullRequest is null)
            {
                continue;
            }

            issueBatch.Add(issue.Number);
            if (issueBatch.Count < PullRequestStreamBatchSize)
            {
                continue;
            }

            foreach (var pullRequest in await GetPullRequestDtosByNumberAsync(repositoryName, issueBatch, cancellationToken))
            {
                yield return pullRequest;
            }

            issueBatch.Clear();
        }

        if (issueBatch.Count == 0)
        {
            yield break;
        }

        foreach (var pullRequest in await GetPullRequestDtosByNumberAsync(repositoryName, issueBatch, cancellationToken))
        {
            yield return pullRequest;
        }
    }

    private async Task<IReadOnlyList<GitHubPullRequestDto>> GetPullRequestDtosByNumberAsync(
        RepositoryName repositoryName,
        IReadOnlyList<int> numbers,
        CancellationToken cancellationToken)
    {
        var pullRequestTasks = numbers
            .Distinct()
            .ToDictionary(
                number => number,
                number => GetPullRequestDtoOrNullAsync(repositoryName, number, cancellationToken));

        await Task.WhenAll(pullRequestTasks.Values);

        var pullRequests = new List<GitHubPullRequestDto>(pullRequestTasks.Count);
        foreach (var task in pullRequestTasks.Values)
        {
            if (await task is { Draft: false } pullRequest)
            {
                pullRequests.Add(pullRequest);
            }
        }

        return pullRequests
            .OrderBy(pullRequest => pullRequest.CreatedAt)
            .ToArray();
    }

    private async Task<IReadOnlyList<PullRequestSummary>> CreatePullRequestSummariesAsync(
        RepositoryName repositoryName,
        IReadOnlyList<GitHubPullRequestDto> pullRequestDtos,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var uniquePullRequestDtos = pullRequestDtos
            .GroupBy(pullRequest => pullRequest.Number)
            .Select(group => group.First())
            .ToArray();
        var pullRequests = uniquePullRequestDtos
            .Select(PullRequestSummary.FromDto)
            .ToArray();

        if (pullRequests.Length == 0)
        {
            return [];
        }

        var reviewTasks = pullRequests.ToDictionary(
            pullRequest => pullRequest.Number,
            pullRequest => GetReviewStatusAsync(repositoryName, pullRequest.Number, forceRefresh, cancellationToken));
        var linkedIssueTasks = uniquePullRequestDtos.ToDictionary(
            pullRequest => pullRequest.Number,
            pullRequest => GetLinkedIssuesAsync(repositoryName, pullRequest.Body, forceRefresh, cancellationToken));

        await Task.WhenAll(reviewTasks.Values);
        await Task.WhenAll(linkedIssueTasks.Values);

        var reviewsByPullRequest = reviewTasks.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Result);
        var detailTasks = pullRequests
            .Where(pullRequest => NeedsPullRequestDetails(pullRequest, reviewsByPullRequest[pullRequest.Number]))
            .ToDictionary(
                pullRequest => pullRequest.Number,
                pullRequest => GetPullRequestDetailsOrNullAsync(repositoryName, pullRequest.Number, forceRefresh, cancellationToken));
        var lastCommitTasks = pullRequests
            .Where(pullRequest =>
                reviewsByPullRequest[pullRequest.Number] is { LastReviewedAt: not null } review
                && (review.State == "reviewed" || review.State == "changes_requested"))
            .ToDictionary(
                pullRequest => pullRequest.Number,
                pullRequest => GetLastCommitAtAsync(repositoryName, pullRequest.Number, forceRefresh, cancellationToken));

        await Task.WhenAll(detailTasks.Values);
        await Task.WhenAll(lastCommitTasks.Values);

        var detailsByPullRequest = new Dictionary<int, PullRequestDetails?>(detailTasks.Count);
        foreach (var (number, task) in detailTasks)
        {
            detailsByPullRequest[number] = await task;
        }

        var lastCommitByPullRequest = new Dictionary<int, DateTimeOffset?>(lastCommitTasks.Count);
        foreach (var (number, task) in lastCommitTasks)
        {
            lastCommitByPullRequest[number] = await task;
        }

        var linkedIssuesByPullRequest = new Dictionary<int, IReadOnlyList<LinkedIssueSummary>>(linkedIssueTasks.Count);
        foreach (var (number, task) in linkedIssueTasks)
        {
            linkedIssuesByPullRequest[number] = await task;
        }

        var fetchedAt = DateTimeOffset.UtcNow;
        return pullRequests
            .Select(pullRequest =>
            {
                detailsByPullRequest.TryGetValue(pullRequest.Number, out var details);
                lastCommitByPullRequest.TryGetValue(pullRequest.Number, out var lastCommitAt);
                return pullRequest with
                {
                    FetchedAt = fetchedAt,
                    LinkedIssues = linkedIssuesByPullRequest[pullRequest.Number],
                    CommitCount = details?.CommitCount ?? pullRequest.CommitCount,
                    Additions = details?.Additions ?? pullRequest.Additions,
                    Deletions = details?.Deletions ?? pullRequest.Deletions,
                    ChangedFiles = details?.ChangedFiles ?? pullRequest.ChangedFiles,
                    LastCommitAt = lastCommitAt,
                    MergeableState = details?.MergeableState ?? pullRequest.MergeableState,
                    Review = reviewsByPullRequest[pullRequest.Number],
                    Checks = pullRequest.Checks
                };
            })
            .ToArray();
    }

    private static bool NeedsPullRequestDetails(PullRequestSummary pullRequest, ReviewStatus review) =>
        pullRequest.State.Equals("open", StringComparison.OrdinalIgnoreCase)
            || review.State == "waiting";

    private async Task<GitHubMilestoneDto?> GetMilestoneByTitleAsync(
        RepositoryName repositoryName,
        string milestoneTitle,
        CancellationToken cancellationToken)
    {
        var milestones = await SendPagedGitHubRequestAsync(
            $"repos/{repositoryName.Owner}/{repositoryName.Name}/milestones?state=all&per_page=100",
            GitHubJsonSerializerContext.Default.GitHubMilestoneDtoArray,
            cancellationToken);
        return milestones.FirstOrDefault(milestone =>
            string.Equals(milestone.Title?.Trim(), milestoneTitle, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> GetLatestReleaseBranchAsync(
        RepositoryName repositoryName,
        CancellationToken cancellationToken)
    {
        var references = await SendGitHubRequestAsync(
            $"repos/{repositoryName.Owner}/{repositoryName.Name}/git/matching-refs/heads/release/",
            GitHubJsonSerializerContext.Default.GitHubGitReferenceDtoArray,
            cancellationToken);

        return references
            .Select(reference => TryGetBranchName(reference.Ref))
            .Where(branch => branch is not null)
            .Select(branch => branch!)
            .OrderByDescending(ReleaseBranchSortKey)
            .FirstOrDefault();
    }

    private static string? TryGetBranchName(string? gitReference)
    {
        const string branchPrefix = "refs/heads/";
        return gitReference?.StartsWith(branchPrefix, StringComparison.OrdinalIgnoreCase) is true
            ? gitReference[branchPrefix.Length..]
            : null;
    }

    private static (int Major, int Minor, int Patch, string Name) ReleaseBranchSortKey(string branch)
    {
        var suffix = branch["release/".Length..];
        var versionParts = suffix.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return (
            Major: ParseReleaseBranchVersionPart(versionParts, 0),
            Minor: ParseReleaseBranchVersionPart(versionParts, 1),
            Patch: ParseReleaseBranchVersionPart(versionParts, 2),
            Name: branch);
    }

    private static int ParseReleaseBranchVersionPart(string[] versionParts, int index) =>
        index < versionParts.Length && int.TryParse(versionParts[index], out var value) ? value : -1;

    private async Task<bool> BranchExistsAsync(
        RepositoryName repositoryName,
        string branch,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendGitHubRequestAsync(
                $"repos/{repositoryName.Owner}/{repositoryName.Name}/branches/{Uri.EscapeDataString(branch)}",
                GitHubJsonSerializerContext.Default.GitHubBranchDto,
                cancellationToken);
            return true;
        }
        catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<GitHubIssueDto>> GetOpenMilestoneIssuesAsync(
        RepositoryName repositoryName,
        int milestoneNumber,
        CancellationToken cancellationToken) =>
        await GetIssuesAsync(
            repositoryName,
            "open",
            label: null,
            milestoneNumber: milestoneNumber,
            sort: null,
            direction: null,
            cancellationToken: cancellationToken);

    private async Task<IReadOnlyList<GitHubIssueDto>> GetIssuesMatchingLabelMarkerAsync(
        RepositoryName repositoryName,
        string state,
        string labelMarker,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var matchingLabels = await GetLabelNamesContainingAsync(repositoryName, labelMarker, forceRefresh, cancellationToken);
        if (matchingLabels.Count == 0)
        {
            return [];
        }

        var issueTasks = matchingLabels
            .ToDictionary(
                label => label,
                label => GetIssuesByLabelAsync(repositoryName, state, label, cancellationToken));

        await Task.WhenAll(issueTasks.Values);

        return issueTasks.Values
            .SelectMany(task => task.Result)
            .Where(issue => issue.PullRequest is null && HasLabelContaining(issue.Labels, labelMarker))
            .GroupBy(issue => issue.Number)
            .Select(group => group.First())
            .OrderByDescending(issue => issue.UpdatedAt)
            .ToArray();
    }

    private async Task<IReadOnlyList<GitHubIssueDto>> SearchIssuesByTitleMarkerAsync(
        RepositoryName repositoryName,
        string state,
        string titleMarker,
        string searchTerm,
        CancellationToken cancellationToken)
    {
        var issues = await SendPagedGitHubIssueSearchRequestAsync(
            CreateIssueSearchUrl(repositoryName, state, searchTerm),
            cancellationToken);

        return issues
            .Where(issue => issue.PullRequest is null
                && issue.Title?.Contains(titleMarker, StringComparison.OrdinalIgnoreCase) is true)
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> GetLabelNamesContainingAsync(
        RepositoryName repositoryName,
        string labelMarker,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"matching-labels:{authCacheKey}:{repositoryName}:{labelMarker}";
        RemoveCacheEntry(cacheKey, forceRefresh);
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var labels = await SendPagedGitHubRequestAsync(
                $"repos/{repositoryName.Owner}/{repositoryName.Name}/labels?per_page=100",
                GitHubJsonSerializerContext.Default.GitHubLabelDtoArray,
                cancellationToken);
            return labels
                .Select(label => label.Name)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(label => label!)
                .Where(label => label.Contains(labelMarker, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }) ?? [];
    }

    private async Task<IReadOnlyList<GitHubIssueDto>> GetIssuesByLabelAsync(
        RepositoryName repositoryName,
        string state,
        string label,
        CancellationToken cancellationToken) =>
        await GetIssuesAsync(
            repositoryName,
            state,
            label: label,
            milestoneNumber: null,
            sort: "updated",
            direction: "desc",
            cancellationToken: cancellationToken);

    private async Task<IReadOnlyList<GitHubIssueDto>> GetIssuesAsync(
        RepositoryName repositoryName,
        string state,
        string? label,
        int? milestoneNumber,
        string? sort,
        string? direction,
        CancellationToken cancellationToken) =>
        await SendPagedGitHubRequestAsync(
            CreateIssuesUrl(repositoryName, state, label, milestoneNumber, sort, direction),
            GitHubJsonSerializerContext.Default.GitHubIssueDtoArray,
            cancellationToken);

    private static string CreateIssueSearchUrl(
        RepositoryName repositoryName,
        string state,
        string searchTerm)
    {
        var queryTerms = new List<string>
        {
            $"repo:{repositoryName.Owner}/{repositoryName.Name}",
            "is:issue"
        };

        if (!state.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            queryTerms.Add($"state:{state}");
        }

        queryTerms.Add("in:title");
        queryTerms.Add(searchTerm);

        return $"search/issues?q={Uri.EscapeDataString(string.Join(" ", queryTerms))}&sort=updated&order=desc&per_page={PullRequestPageSize}";
    }

    private async IAsyncEnumerable<GitHubIssueDto> StreamIssuesAsync(
        RepositoryName repositoryName,
        string state,
        string? label,
        string? sort,
        string? direction,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var issue in SendPagedGitHubRequestStreamAsync(
            CreateIssuesUrl(
                repositoryName,
                state,
                label,
                milestoneNumber: null,
                sort,
                direction),
            GitHubJsonSerializerContext.Default.GitHubIssueDtoArray,
            cancellationToken))
        {
            yield return issue;
        }
    }

    private static string CreateIssuesUrl(
        RepositoryName repositoryName,
        string state,
        string? label,
        int? milestoneNumber,
        string? sort,
        string? direction)
    {
        var queryParts = new List<string>
        {
            $"state={Uri.EscapeDataString(state)}"
        };

        if (!string.IsNullOrWhiteSpace(label))
        {
            queryParts.Add($"labels={Uri.EscapeDataString(label)}");
        }

        if (milestoneNumber is { } milestone)
        {
            queryParts.Add($"milestone={milestone}");
        }

        if (!string.IsNullOrWhiteSpace(sort))
        {
            queryParts.Add($"sort={Uri.EscapeDataString(sort)}");
        }

        if (!string.IsNullOrWhiteSpace(direction))
        {
            queryParts.Add($"direction={Uri.EscapeDataString(direction)}");
        }

        queryParts.Add($"per_page={PullRequestPageSize}");

        return $"repos/{repositoryName.Owner}/{repositoryName.Name}/issues?{string.Join("&", queryParts)}";
    }

    private async Task<IReadOnlyList<GitHubPullRequestDto>> GetOpenPullRequestsByBaseAsync(
        RepositoryName repositoryName,
        string baseRef,
        CancellationToken cancellationToken) =>
        await SendPagedGitHubRequestAsync(
            $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls?state=open&base={Uri.EscapeDataString(baseRef)}&sort=created&direction=asc&per_page={PullRequestPageSize}",
            GitHubJsonSerializerContext.Default.GitHubPullRequestDtoArray,
            cancellationToken);

    private async Task<GitHubPullRequestDto?> GetPullRequestDtoOrNullAsync(
        RepositoryName repositoryName,
        int number,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendGitHubRequestAsync(
                $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls/{number}",
                GitHubJsonSerializerContext.Default.GitHubPullRequestDto,
                cancellationToken);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> CreateLinkedOpenPullRequestsByIssue(
        RepositoryName repositoryName,
        string milestoneTitle,
        IReadOnlyList<PullRequestSummary> pullRequests,
        IReadOnlySet<int> milestoneIssueNumbers)
    {
        var linkedOpenPullRequestsByIssue = new Dictionary<int, List<int>>();

        foreach (var pullRequest in pullRequests)
        {
            foreach (var issue in pullRequest.LinkedIssues)
            {
                if (!RepositoryMatches(issue.Repository, repositoryName)
                    || (!MilestoneTitleMatches(issue.Milestone, milestoneTitle)
                        && !milestoneIssueNumbers.Contains(issue.Number)))
                {
                    continue;
                }

                if (!linkedOpenPullRequestsByIssue.TryGetValue(issue.Number, out var linkedPullRequests))
                {
                    linkedPullRequests = [];
                    linkedOpenPullRequestsByIssue[issue.Number] = linkedPullRequests;
                }

                linkedPullRequests.Add(pullRequest.Number);
            }
        }

        return linkedOpenPullRequestsByIssue.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<int>)pair.Value
                .Distinct()
                .OrderBy(number => number)
                .ToArray());
    }

    private static bool RepositoryMatches(string repository, RepositoryName repositoryName) =>
        repository.Equals(repositoryName.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool HasLabelContaining(IEnumerable<GitHubLabelDto> labels, string marker) =>
        labels.Any(label => label.Name?.Contains(marker, StringComparison.OrdinalIgnoreCase) is true);

    private static bool MilestoneTitleMatches(string? milestoneTitle, string expectedMilestoneTitle) =>
        string.Equals(milestoneTitle?.Trim(), expectedMilestoneTitle, StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<PullRequestChecksSummary>> GetPullRequestChecksAsync(
        RepositoryName repositoryName,
        IReadOnlyList<PullRequestChecksRequestItem> pullRequests,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var requestedPullRequests = pullRequests
            .Where(pullRequest => pullRequest.Number > 0 && !string.IsNullOrWhiteSpace(pullRequest.HeadSha))
            .GroupBy(
                pullRequest => (pullRequest.Number, HeadSha: pullRequest.HeadSha!.Trim()),
                pullRequest => pullRequest,
                EqualityComparer<(int Number, string HeadSha)>.Default)
            .Select(group => group.Key)
            .ToArray();

        if (requestedPullRequests.Length == 0)
        {
            return [];
        }

        return await Task.WhenAll(requestedPullRequests.Select(
            pullRequest => GetPullRequestChecksWithThrottleAsync(
                repositoryName,
                pullRequest.Number,
                pullRequest.HeadSha,
                forceRefresh,
                cancellationToken)));
    }

    private async Task<PullRequestChecksSummary> GetPullRequestChecksWithThrottleAsync(
        RepositoryName repositoryName,
        int number,
        string headSha,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        await s_checksFetchThrottle.WaitAsync(cancellationToken);
        try
        {
            return new PullRequestChecksSummary(
                number,
                headSha,
                await GetChecksStatusAsync(repositoryName, headSha, forceRefresh, cancellationToken));
        }
        finally
        {
            s_checksFetchThrottle.Release();
        }
    }

    public async Task<ChecksStatus> GetChecksStatusAsync(
        RepositoryName repositoryName,
        string headSha,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(headSha))
        {
            return ChecksStatus.None;
        }

        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        // Key by SHA so a fresh push naturally invalidates stale check state once
        // GitHub posts results for the new commit.
        var cacheKey = $"checks:{authCacheKey}:{repositoryName}:{headSha}";
        RemoveCacheEntry(cacheKey, forceRefresh);
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            var checkRunsTask = TryGetCheckRunsAsync(repositoryName, headSha, cancellationToken);
            var combinedStatusTask = TryGetCombinedStatusesAsync(repositoryName, headSha, cancellationToken);
            await Task.WhenAll(checkRunsTask, combinedStatusTask);

            var rollup = MergeChecks(await checkRunsTask, await combinedStatusTask);

            // Terminal states are cached for the full window; pending/failure get a much shorter
            // TTL so the dashboard reflects CI transitions promptly without waiting for the head
            // SHA to change.
            entry.AbsoluteExpirationRelativeToNow = rollup.State switch
            {
                "pending" => PendingChecksCacheDuration,
                "failure" => FailingChecksCacheDuration,
                _ => CacheDuration,
            };

            return rollup;
        }) ?? ChecksStatus.None;
    }

    private static readonly TimeSpan PendingChecksCacheDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FailingChecksCacheDuration = TimeSpan.FromSeconds(20);
    private const int MaxFailingChecksTracked = 5;

    private static ChecksStatus MergeChecks(IReadOnlyList<GitHubCheckRunDto> checkRuns, IReadOnlyList<GitHubStatusDto> statuses)
    {
        var success = 0;
        var failure = 0;
        var pending = 0;
        var neutral = 0;
        var skipped = 0;
        DateTimeOffset? latestCompletedAt = null;
        // Cap the failing list while iterating so a matrix build with thousands of failing
        // contexts cannot cause unbounded allocation just to be trimmed at the end.
        var failingChecks = new List<FailingCheck>(MaxFailingChecksTracked);

        foreach (var run in checkRuns)
        {
            var status = run.Status?.ToLowerInvariant();
            var conclusion = run.Conclusion?.ToLowerInvariant();

            // Treat any not-yet-completed run as pending. status == "completed" is the only state
            // for which conclusion is guaranteed meaningful.
            if (status != "completed")
            {
                pending++;
                continue;
            }

            switch (conclusion)
            {
                case "success":
                    success++;
                    break;
                case "failure" or "timed_out" or "action_required" or "cancelled" or "startup_failure":
                    failure++;
                    if (failingChecks.Count < MaxFailingChecksTracked)
                    {
                        failingChecks.Add(new FailingCheck(
                            Name: run.Name ?? "(unnamed check)",
                            Conclusion: conclusion,
                            HtmlUrl: run.HtmlUrl));
                    }
                    break;
                case "neutral":
                    neutral++;
                    break;
                case "skipped" or "stale":
                    skipped++;
                    break;
                default:
                    // Unknown / null conclusion on a completed run — count as neutral so it
                    // does not drag the rollup down or up.
                    neutral++;
                    break;
            }

            if (run.CompletedAt is { } completedAt
                && (latestCompletedAt is null || completedAt > latestCompletedAt))
            {
                latestCompletedAt = completedAt;
            }
        }

        if (statuses.Count > 0)
        {
            foreach (var contextStatus in statuses)
            {
                var state = contextStatus.State?.ToLowerInvariant();
                switch (state)
                {
                    case "success":
                        success++;
                        break;
                    case "failure" or "error":
                        failure++;
                        if (failingChecks.Count < MaxFailingChecksTracked)
                        {
                            failingChecks.Add(new FailingCheck(
                                Name: contextStatus.Context ?? "(unnamed status)",
                                Conclusion: state,
                                HtmlUrl: contextStatus.TargetUrl));
                        }
                        break;
                    case "pending":
                        pending++;
                        break;
                    default:
                        neutral++;
                        break;
                }

                if (contextStatus.UpdatedAt is { } updatedAt
                    && (latestCompletedAt is null || updatedAt > latestCompletedAt))
                {
                    latestCompletedAt = updatedAt;
                }
            }
        }

        var total = success + failure + pending + neutral + skipped;

        // Mirrors GitHub's own check-suite conclusion: a suite with only neutral/skipped runs is
        // considered passing, so a PR with all-neutral CI should still qualify as "success" here.
        var rolledUpState =
            total == 0 ? "none" :
            failure > 0 ? "failure" :
            pending > 0 ? "pending" :
            success > 0 || neutral > 0 || skipped > 0 ? "success" :
            "none";

        return new ChecksStatus(
            State: rolledUpState,
            TotalCount: total,
            SuccessCount: success,
            FailureCount: failure,
            PendingCount: pending,
            NeutralCount: neutral,
            SkippedCount: skipped,
            CompletedAt: latestCompletedAt,
            FailingChecks: failingChecks);
    }

    private async Task<IReadOnlyList<GitHubCheckRunDto>> TryGetCheckRunsAsync(
        RepositoryName repositoryName,
        string headSha,
        CancellationToken cancellationToken)
    {
        // The check-runs response is a wrapper object ({ total_count, check_runs[] }) rather than a
        // bare array, so we follow Link-header pagination manually instead of using
        // SendPagedGitHubRequestAsync. Without this, PRs with > 100 check runs (matrix builds in
        // monorepos) silently truncate and can produce a false "green" rollup.
        var runs = new List<GitHubCheckRunDto>();
        string? url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/commits/{Uri.EscapeDataString(headSha)}/check-runs?filter=latest&per_page=100";

        try
        {
            while (url is not null)
            {
                var pageResponse = await SendGitHubPageAsync(
                    url,
                    GitHubJsonSerializerContext.Default.GitHubCheckRunsResponseDto,
                    cancellationToken);
                var page = pageResponse.Value;

                if (page.CheckRuns is { Length: > 0 } pageRuns)
                {
                    runs.AddRange(pageRuns);
                }

                url = pageResponse.NextUrl;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Checks are enrichment-only. Any failure (GitHub API errors like 404/403/5xx,
            // JsonException from unexpected payload shapes, socket errors, etc.) must degrade
            // gracefully to "no checks" instead of tearing down the entire PR list response.
            // Cancellation is intentionally re-thrown so the caller can honor it.
            return [];
        }

        return runs;
    }

    private async Task<IReadOnlyList<GitHubStatusDto>> TryGetCombinedStatusesAsync(
        RepositoryName repositoryName,
        string headSha,
        CancellationToken cancellationToken)
    {
        // Combined status defaults to per_page=30 and paginates via Link headers, identical in
        // shape to check-runs (wrapper object with a `statuses[]` array). Without paging, repos
        // that post many third-party statuses (Azure Pipelines, AppVeyor, Travis, etc.) silently
        // truncate and skew the rollup.
        var statuses = new List<GitHubStatusDto>();
        string? url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/commits/{Uri.EscapeDataString(headSha)}/status?per_page=100";

        try
        {
            while (url is not null)
            {
                var pageResponse = await SendGitHubPageAsync(
                    url,
                    GitHubJsonSerializerContext.Default.GitHubCombinedStatusDto,
                    cancellationToken);
                var page = pageResponse.Value;

                if (page.Statuses is { Length: > 0 } pageStatuses)
                {
                    statuses.AddRange(pageStatuses);
                }

                url = pageResponse.NextUrl;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same enrichment-only stance as TryGetCheckRunsAsync — never let a single PR's
            // checks call break the whole list. Cancellation is intentionally re-thrown.
            return [];
        }

        return statuses;
    }

    public async Task<ReviewStatus> GetReviewStatusAsync(
        RepositoryName repositoryName,
        int number,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"reviews:{authCacheKey}:{repositoryName}:{number}";
        RemoveCacheEntry(cacheKey, forceRefresh);
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
                LastApprovedAt: humanReviews.LastOrDefault(review => review.State == "APPROVED")?.SubmittedAt,
                LastReviewedAt: humanReviews.LastOrDefault()?.SubmittedAt);
        }) ?? ReviewStatus.Waiting;
    }

    public async Task<IReadOnlyList<TimelineItem>> GetPullRequestTimelineAsync(
        RepositoryName repositoryName,
        int number,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"timeline:{authCacheKey}:{repositoryName}:{number}";
        RemoveCacheEntry(cacheKey, forceRefresh);
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
                var pageResponse = await SendGitHubPageAsync(
                    url,
                    GitHubJsonSerializerContext.Default.GitHubTimelineItemDtoArray,
                    cancellationToken);

                items.AddRange(pageResponse.Value);
                url = pageResponse.NextUrl;
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
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"pull:{authCacheKey}:{repositoryName}:{number}";
        RemoveCacheEntry(cacheKey, forceRefresh);
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

    private async Task<PullRequestDetails?> GetPullRequestDetailsOrNullAsync(
        RepositoryName repositoryName,
        int number,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetPullRequestDetailsAsync(repositoryName, number, forceRefresh, cancellationToken);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Detail metrics are enrichment-only. Keep the PR visible if it disappears mid-refresh.
            return null;
        }
    }

    private async Task<DateTimeOffset?> GetLastCommitAtAsync(
        RepositoryName repositoryName,
        int number,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"commits:last:{authCacheKey}:{repositoryName}:{number}";
        RemoveCacheEntry(cacheKey, forceRefresh);
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls/{number}/commits?per_page=100";
            var commits = new List<GitHubPullRequestCommitDto>();

            for (var page = 0; page < 3 && url is not null; page++)
            {
                GitHubPullRequestCommitDto[] pageCommits;

                try
                {
                    var pageResponse = await SendGitHubPageAsync(
                        url,
                        GitHubJsonSerializerContext.Default.GitHubPullRequestCommitDtoArray,
                        cancellationToken);
                    pageCommits = pageResponse.Value;
                    url = pageResponse.NextUrl;
                }
                catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Commit recency is enrichment-only. Keep the PR in the list without it.
                    return null;
                }
                commits.AddRange(pageCommits);
            }

            return commits
                .Select(commit => commit.Commit?.Committer?.Date ?? commit.Commit?.Author?.Date)
                .Where(date => date is not null)
                .OrderByDescending(date => date)
                .FirstOrDefault();
        });
    }

    private async Task<IReadOnlyList<LinkedIssueSummary>> GetLinkedIssuesAsync(
        RepositoryName repositoryName,
        string? body,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var references = FindLinkedIssueReferences(repositoryName, body);
        if (references.Count == 0)
        {
            return [];
        }

        var issueTasks = references
            .Select(reference => GetIssueAsync(reference.RepositoryName, reference.Number, forceRefresh, cancellationToken))
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
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"issue:{authCacheKey}:{repositoryName}:{number}";
        RemoveCacheEntry(cacheKey, forceRefresh);
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

            // Redirected issue lookups can start from old repo names like dotnet/aspire but return
            // canonical repository metadata. Surface the canonical org/repo when GitHub provides it.
            var issueRepositoryName = TryParseGitHubRepositoryApiUrl(issue.RepositoryUrl, out var parsedRepositoryName)
                ? parsedRepositoryName
                : repositoryName;

            return issue.PullRequest is null
                ? LinkedIssueSummary.FromDto(issueRepositoryName, issue)
                : null;
        });
    }

    private async Task<T> SendGitHubRequestAsync<T>(
        string url,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        var page = await SendGitHubPageAsync(url, jsonTypeInfo, cancellationToken);
        return page.Value;
    }

    private async Task<IReadOnlyList<T>> SendPagedGitHubRequestAsync<T>(
        string url,
        JsonTypeInfo<T[]> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? nextUrl = url;
        while (nextUrl is not null)
        {
            var page = await SendGitHubPageAsync(nextUrl, jsonTypeInfo, cancellationToken);

            items.AddRange(page.Value);
            nextUrl = page.NextUrl;
        }

        return items;
    }

    private async Task<IReadOnlyList<GitHubIssueDto>> SendPagedGitHubIssueSearchRequestAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var items = new List<GitHubIssueDto>();
        string? nextUrl = url;
        while (nextUrl is not null)
        {
            var page = await SendGitHubPageAsync(
                nextUrl,
                GitHubJsonSerializerContext.Default.GitHubIssueSearchResponseDto,
                cancellationToken);

            items.AddRange(page.Value.Items);
            nextUrl = page.NextUrl;
        }

        return items;
    }

    private async IAsyncEnumerable<T> SendPagedGitHubRequestStreamAsync<T>(
        string url,
        JsonTypeInfo<T[]> jsonTypeInfo,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? nextUrl = url;

        while (nextUrl is not null)
        {
            var page = await SendGitHubPageAsync(nextUrl, jsonTypeInfo, cancellationToken);

            foreach (var item in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            nextUrl = page.NextUrl;
        }
    }

    private async Task<GitHubPage<T>> SendGitHubPageAsync<T>(
        string url,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        await s_githubRequestThrottle.WaitAsync(cancellationToken);
        try
        {
            using var response = await SendAuthorizedRequestAsync(url, cancellationToken);
            var value = await ReadGitHubJsonAsync(response, jsonTypeInfo, cancellationToken);
            return new GitHubPage<T>(value, GetNextPageUrl(response));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsTransientGitHubTransportFailure(ex))
        {
            throw new GitHubApiException(
                HttpStatusCode.ServiceUnavailable,
                "GitHub API is temporarily unavailable from the local backend. Try again shortly.");
        }
        finally
        {
            s_githubRequestThrottle.Release();
        }
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

        // Follow GitHub API redirects ourselves so every redirected request gets the bearer token.
        for (var redirectCount = 0; redirectCount <= MaxGitHubRedirects; redirectCount++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!TryGetGitHubRedirectUrl(response, out var redirectUrl))
            {
                return response;
            }

            response.Dispose();
            url = redirectUrl;
        }

        throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub API returned too many redirects.");
    }

    private readonly record struct GitHubPage<T>(T Value, string? NextUrl);

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

    private static bool TryGetGitHubRedirectUrl(HttpResponseMessage response, out string redirectUrl)
    {
        redirectUrl = "";
        var statusCode = (int)response.StatusCode;
        if (statusCode < 300 || statusCode >= 400 || response.Headers.Location is not { } location)
        {
            return false;
        }

        if (location.IsAbsoluteUri)
        {
            if (!location.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                || !location.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            redirectUrl = location.PathAndQuery.TrimStart('/');
            return true;
        }

        redirectUrl = location.OriginalString.TrimStart('/');
        return !string.IsNullOrWhiteSpace(redirectUrl);
    }

    private static bool TryParseGitHubRepositoryApiUrl(string? repositoryUrl, out RepositoryName repositoryName)
    {
        repositoryName = default;
        if (string.IsNullOrWhiteSpace(repositoryUrl)
            || !Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath.Trim('/');
        const string reposPrefix = "repos/";
        if (!path.StartsWith(reposPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var repositoryPath = path[reposPrefix.Length..];
        return RepositoryName.TryParse(repositoryPath, out repositoryName);
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
