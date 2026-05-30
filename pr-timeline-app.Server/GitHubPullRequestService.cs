sealed class GitHubPullRequestService(GitHubClient gitHub)
{
    public Task<IReadOnlyList<PullRequestSummary>> GetPullRequestsAsync(
        RepositoryName repositoryName,
        string state,
        bool forceRefresh,
        CancellationToken cancellationToken) =>
        gitHub.GetPullRequestsAsync(repositoryName, state, forceRefresh, cancellationToken);

    public Task<IReadOnlyList<PullRequestSummary>> GetPullRequestsByLabelAsync(
        RepositoryName repositoryName,
        string state,
        string label,
        bool forceRefresh,
        CancellationToken cancellationToken) =>
        gitHub.GetPullRequestsByLabelAsync(repositoryName, state, label, forceRefresh, cancellationToken);

    public Task<IReadOnlyList<ShipWeekIssueSummary>> GetRegressionIssuesAsync(
        RepositoryName repositoryName,
        string state,
        bool forceRefresh,
        CancellationToken cancellationToken) =>
        gitHub.GetRegressionIssuesAsync(repositoryName, state, forceRefresh, cancellationToken);

    public Task<IReadOnlyList<PullRequestChecksSummary>> GetPullRequestChecksAsync(
        RepositoryName repositoryName,
        IReadOnlyList<PullRequestChecksRequestItem> pullRequests,
        bool forceRefresh,
        CancellationToken cancellationToken) =>
        gitHub.GetPullRequestChecksAsync(repositoryName, pullRequests, forceRefresh, cancellationToken);

    public Task<ShipWeekLoadResult> GetShipWeekAsync(
        RepositoryName repositoryName,
        string milestoneTitle,
        string? releaseBranch,
        bool forceRefresh,
        CancellationToken cancellationToken) =>
        gitHub.GetShipWeekAsync(repositoryName, milestoneTitle, releaseBranch, forceRefresh, cancellationToken);

    public async Task<TimelineResponse> GetTimelineAsync(
        RepositoryName repositoryName,
        int number,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var pullRequest = await gitHub.GetPullRequestDetailsAsync(repositoryName, number, forceRefresh, cancellationToken);
        var timelineTask = gitHub.GetPullRequestTimelineAsync(repositoryName, number, forceRefresh, cancellationToken);
        var checksTask = string.IsNullOrEmpty(pullRequest.HeadSha)
            ? Task.FromResult(ChecksStatus.None)
            : gitHub.GetChecksStatusAsync(repositoryName, pullRequest.HeadSha, forceRefresh, cancellationToken);

        var timeline = await timelineTask;
        var checks = await checksTask;
        var stats = TimelineStats.Create(pullRequest, timeline);

        return new TimelineResponse(
            repositoryName.ToString(),
            number,
            stats,
            checks,
            pullRequest.MergeableState,
            timeline);
    }
}
