using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Caching.Memory;

sealed partial class GitHubClient(HttpClient httpClient, GitHubTokenProvider tokenProvider, IMemoryCache cache, IHostEnvironment environment)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(45);
    private const int PullRequestPageSize = 100;
    private const int MaxLinkedIssuesPerPullRequest = 10;
    private const int MaxGitHubRedirects = 3;
    private const int MaxConcurrentChecksFetches = 4;
    private static readonly SemaphoreSlim s_checksFetchThrottle = new(MaxConcurrentChecksFetches, MaxConcurrentChecksFetches);

    private void RemoveCacheEntry(string cacheKey, bool forceRefresh)
    {
        if (forceRefresh)
        {
            cache.Remove(cacheKey);
        }
    }

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
        RemoveCacheEntry(cacheKey, forceRefresh);
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
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

            return await CreatePullRequestSummariesAsync(repositoryName, activePullRequestDtos, forceRefresh, cancellationToken);
        }) ?? [];
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
        RemoveCacheEntry(cacheKey, forceRefresh);
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            var sort = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "created" : "updated";
            var direction = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
            var issues = await SendPagedGitHubRequestAsync(
                $"repos/{repositoryName.Owner}/{repositoryName.Name}/issues?state={Uri.EscapeDataString(state)}&labels={Uri.EscapeDataString(normalizedLabel)}&sort={sort}&direction={direction}&per_page={PullRequestPageSize}",
                GitHubJsonSerializerContext.Default.GitHubIssueDtoArray,
                cancellationToken);
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
                forceRefresh,
                cancellationToken);
        }) ?? [];
    }

    public async Task<IReadOnlyList<ShipWeekIssueSummary>> GetRegressionIssuesAsync(
        RepositoryName repositoryName,
        string state,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"regression-issues:{authCacheKey}:{repositoryName}:{state}";
        RemoveCacheEntry(cacheKey, forceRefresh);
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            var regressionLabels = await GetRegressionLabelNamesAsync(repositoryName, forceRefresh, cancellationToken);
            if (regressionLabels.Count == 0)
            {
                return [];
            }

            var issueTasks = regressionLabels
                .ToDictionary(
                    label => label,
                    label => GetIssuesByLabelAsync(repositoryName, state, label, cancellationToken));

            await Task.WhenAll(issueTasks.Values);

            return issueTasks.Values
                .SelectMany(task => task.Result)
                .Where(issue => issue.PullRequest is null && HasRegressionLabel(issue.Labels))
                .GroupBy(issue => issue.Number)
                .Select(group => group.First())
                .OrderByDescending(issue => issue.UpdatedAt)
                .Select(issue => ShipWeekIssueSummary.FromDto(repositoryName, issue, []))
                .ToArray();
        }) ?? [];
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
        RemoveCacheEntry(cacheKey, forceRefresh);
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

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
                forceRefresh,
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
            var shipWeekIssues = nonPullRequestIssues
                .Select(issue =>
                {
                    var linkedOpenPullRequests = linkedOpenPullRequestsByIssue.TryGetValue(issue.Number, out var linked)
                        ? linked
                        : [];
                    return ShipWeekIssueSummary.FromDto(repositoryName, issue, linkedOpenPullRequests);
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
        }) ?? ShipWeekLoadResult.ValidationProblem("shipWeek", "Unable to load ship-week data.");
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
            .Where(pullRequest => reviewsByPullRequest[pullRequest.Number].State == "waiting")
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

        return pullRequests
            .Select(pullRequest =>
            {
                detailsByPullRequest.TryGetValue(pullRequest.Number, out var details);
                lastCommitByPullRequest.TryGetValue(pullRequest.Number, out var lastCommitAt);
                return pullRequest with
                {
                    LinkedIssues = linkedIssuesByPullRequest[pullRequest.Number],
                    CommitCount = details?.CommitCount ?? pullRequest.CommitCount,
                    Additions = details?.Additions ?? pullRequest.Additions,
                    Deletions = details?.Deletions ?? pullRequest.Deletions,
                    ChangedFiles = details?.ChangedFiles ?? pullRequest.ChangedFiles,
                    LastCommitAt = lastCommitAt,
                    Review = reviewsByPullRequest[pullRequest.Number],
                    Checks = pullRequest.Checks
                };
            })
            .ToArray();
    }

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
        await SendPagedGitHubRequestAsync(
            $"repos/{repositoryName.Owner}/{repositoryName.Name}/issues?state=open&milestone={milestoneNumber}&per_page=100",
            GitHubJsonSerializerContext.Default.GitHubIssueDtoArray,
            cancellationToken);

    private async Task<IReadOnlyList<string>> GetRegressionLabelNamesAsync(
        RepositoryName repositoryName,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = $"regression-labels:{authCacheKey}:{repositoryName}";
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
                .Where(label => label.Contains("regression", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }) ?? [];
    }

    private async Task<IReadOnlyList<GitHubIssueDto>> GetIssuesByLabelAsync(
        RepositoryName repositoryName,
        string state,
        string label,
        CancellationToken cancellationToken) =>
        await SendPagedGitHubRequestAsync(
            $"repos/{repositoryName.Owner}/{repositoryName.Name}/issues?state={Uri.EscapeDataString(state)}&labels={Uri.EscapeDataString(label)}&sort=updated&direction=desc&per_page=100",
            GitHubJsonSerializerContext.Default.GitHubIssueDtoArray,
            cancellationToken);

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

    private static bool HasRegressionLabel(IEnumerable<GitHubLabelDto> labels) =>
        labels.Any(label => label.Name?.Contains("regression", StringComparison.OrdinalIgnoreCase) is true);

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
                using var response = await SendAuthorizedRequestAsync(url, cancellationToken);
                var page = await ReadGitHubJsonAsync(
                    response,
                    GitHubJsonSerializerContext.Default.GitHubCheckRunsResponseDto,
                    cancellationToken);

                if (page.CheckRuns is { Length: > 0 } pageRuns)
                {
                    runs.AddRange(pageRuns);
                }

                url = GetNextPageUrl(response);
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
                using var response = await SendAuthorizedRequestAsync(url, cancellationToken);
                var page = await ReadGitHubJsonAsync(
                    response,
                    GitHubJsonSerializerContext.Default.GitHubCombinedStatusDto,
                    cancellationToken);

                if (page.Statuses is { Length: > 0 } pageStatuses)
                {
                    statuses.AddRange(pageStatuses);
                }

                url = GetNextPageUrl(response);
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
                using var response = await SendAuthorizedRequestAsync(url, cancellationToken);

                try
                {
                    pageCommits = await ReadGitHubJsonAsync(
                        response,
                        GitHubJsonSerializerContext.Default.GitHubPullRequestCommitDtoArray,
                        cancellationToken);
                }
                catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Commit recency is enrichment-only. Keep the PR in the list without it.
                    return null;
                }

                commits.AddRange(pageCommits);
                url = GetNextPageUrl(response);
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
        using var response = await SendAuthorizedRequestAsync(url, cancellationToken);
        return await ReadGitHubJsonAsync(response, jsonTypeInfo, cancellationToken);
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
            using var response = await SendAuthorizedRequestAsync(nextUrl, cancellationToken);
            var pageItems = await ReadGitHubJsonAsync(response, jsonTypeInfo, cancellationToken);

            items.AddRange(pageItems);
            nextUrl = GetNextPageUrl(response);
        }

        return items;
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
