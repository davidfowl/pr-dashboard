namespace pr_timeline_app.Tests;

public sealed class PullRequestSummaryTests
{
    [Fact]
    public void FromDtoMapsIndividualReviewersButExcludesTeams()
    {
        var dto = new GitHubPullRequestDto
        {
            Number = 42,
            Title = "Add feature",
            State = "open",
            User = new GitHubActorDto { Login = "author" },
            RequestedReviewers =
            [
                new GitHubActorDto { Id = 10, Login = "alice" },
                new GitHubActorDto { Id = 20, Login = "bob" }
            ],
            // A team named like a login must NOT be merged into RequestedReviewers, or it would
            // produce a false-positive reviewer match for a user of that name.
            RequestedTeams =
            [
                new GitHubTeamDto { Name = "alice" },
                new GitHubTeamDto { Name = "reviewers" }
            ]
        };

        var summary = PullRequestSummary.FromDto(dto);

        Assert.Equal(["alice", "bob"], summary.RequestedReviewers);
        Assert.Equal([10, 20], summary.RequestedReviewerIds);
    }
}
