namespace pr_timeline_app.Tests;

public sealed class RepositoryNameTests
{
    [Theory]
    [InlineData("https://github.com/devdiv-microsoft/aspire-1p")]
    [InlineData("https://github.com/devdiv-microsoft/aspire-1p/")]
    [InlineData("https://github.com/devdiv-microsoft/aspire-1p.git")]
    [InlineData("https://github.com/devdiv-microsoft/aspire-1p/pulls")]
    public void TryParseAcceptsGitHubRepositoryUrls(string value)
    {
        var parsed = RepositoryName.TryParse(value, out var repositoryName);

        Assert.True(parsed);
        Assert.Equal("devdiv-microsoft", repositoryName.Owner);
        Assert.Equal("aspire-1p", repositoryName.Name);
    }

    [Fact]
    public void TryParsePreservesOwnerSlashRepository()
    {
        var parsed = RepositoryName.TryParse("devdiv-microsoft/aspire-1p", out var repositoryName);

        Assert.True(parsed);
        Assert.Equal("devdiv-microsoft/aspire-1p", repositoryName.ToString());
    }
}
