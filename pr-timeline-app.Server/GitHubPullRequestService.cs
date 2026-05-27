sealed class GitHubPullRequestService(GitHubClient gitHub)
{
    public Task<IReadOnlyList<PullRequestSummary>> GetPullRequestsAsync(
        RepositoryName repositoryName,
        string state,
        CancellationToken cancellationToken) =>
        gitHub.GetPullRequestsAsync(repositoryName, state, cancellationToken);

    public Task<IReadOnlyList<PullRequestChecksSummary>> GetPullRequestChecksAsync(
        RepositoryName repositoryName,
        IReadOnlyList<PullRequestChecksRequestItem> pullRequests,
        CancellationToken cancellationToken) =>
        gitHub.GetPullRequestChecksAsync(repositoryName, pullRequests, cancellationToken);

    public async Task<TimelineResponse> GetTimelineAsync(
        RepositoryName repositoryName,
        int number,
        CancellationToken cancellationToken)
    {
        var pullRequest = await gitHub.GetPullRequestDetailsAsync(repositoryName, number, cancellationToken);
        var timelineTask = gitHub.GetPullRequestTimelineAsync(repositoryName, number, cancellationToken);
        var checksTask = string.IsNullOrEmpty(pullRequest.HeadSha)
            ? Task.FromResult(ChecksStatus.None)
            : gitHub.GetChecksStatusAsync(repositoryName, pullRequest.HeadSha, cancellationToken);

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
