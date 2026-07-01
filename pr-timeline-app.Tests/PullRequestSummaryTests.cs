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

    [Fact]
    public void FromDtoOwnerUserIdIsTheHumanAuthor()
    {
        var dto = new GitHubPullRequestDto
        {
            Number = 1,
            State = "open",
            User = new GitHubActorDto { Id = 1472, Login = "radical" }
        };

        Assert.Equal(1472, PullRequestSummary.FromDto(dto).OwnerUserId);
    }

    [Fact]
    public void FromDtoOwnerUserIdIsTheSoleHumanAssigneeForCopilotPr()
    {
        // Copilot authors the PR; the single human assignee owns it. Notifications must treat
        // that human as the owner so they are not pinged to review their own Copilot PR.
        var dto = new GitHubPullRequestDto
        {
            Number = 1,
            State = "open",
            User = new GitHubActorDto { Id = 198982749, Login = "Copilot" },
            Assignees =
            [
                new GitHubActorDto { Id = 1472, Login = "radical" },
                new GitHubActorDto { Id = 198982749, Login = "Copilot" }
            ]
        };

        Assert.Equal(1472, PullRequestSummary.FromDto(dto).OwnerUserId);
    }

    [Fact]
    public void FromDtoOwnerUserIdIsTheSoleHumanAssigneeForCopilotSweAgentPr()
    {
        var dto = new GitHubPullRequestDto
        {
            Number = 1,
            State = "open",
            User = new GitHubActorDto { Id = 198982749, Login = "copilot-swe-agent" },
            Assignees =
            [
                new GitHubActorDto { Id = 1472, Login = "radical" },
                new GitHubActorDto { Id = 198982749, Login = "copilot-swe-agent" }
            ]
        };

        Assert.Equal(1472, PullRequestSummary.FromDto(dto).OwnerUserId);
    }

    [Fact]
    public void FromDtoOwnerUserIdIsNullWhenCopilotPrHasNoSingleHuman()
    {
        var dto = new GitHubPullRequestDto
        {
            Number = 1,
            State = "open",
            User = new GitHubActorDto { Id = 198982749, Login = "Copilot" },
            Assignees =
            [
                new GitHubActorDto { Id = 1, Login = "alice" },
                new GitHubActorDto { Id = 2, Login = "bob" }
            ]
        };

        Assert.Null(PullRequestSummary.FromDto(dto).OwnerUserId);
    }

    [Fact]
    public void TimelineItemResolvesCommittedActorToGitHubLogin()
    {
        // A "committed" timeline event only carries the raw git author name; the sha->login map
        // (sourced from the commits API) is what lets us attribute it to the GitHub user.
        var committed = new GitHubTimelineItemDto
        {
            Event = "committed",
            Sha = "24ff2d1abc",
            Author = new GitHubActorDto { Name = "Ankit Jain" }
        };

        var map = new Dictionary<string, string> { ["24ff2d1abc"] = "radical" };

        Assert.Equal("radical", TimelineItem.FromDto(committed, map).Actor);
    }

    [Fact]
    public void TimelineItemFallsBackToGitNameWhenShaUnmapped()
    {
        var committed = new GitHubTimelineItemDto
        {
            Event = "committed",
            Sha = "deadbeef",
            Author = new GitHubActorDto { Name = "Ankit Jain" }
        };

        Assert.Equal("Ankit Jain", TimelineItem.FromDto(committed, new Dictionary<string, string>()).Actor);
    }
}
