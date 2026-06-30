namespace pr_timeline_app.Tests;

public sealed class GitHubSamlSsoTests
{
    [Theory]
    [InlineData("repos/microsoft/aspire/pulls?state=open", "microsoft")]
    [InlineData("orgs/microsoft/members", "microsoft")]
    [InlineData("users/octocat/repos", "octocat")]
    [InlineData("https://api.github.com/repos/contoso/widgets/issues", "contoso")]
    [InlineData("search/issues?q=repo:microsoft/aspire+is:issue+is:open", "microsoft")]
    [InlineData("search/issues?q=org:contoso+is:open", "contoso")]
    [InlineData("search/issues?q=repo%3Amicrosoft%2Faspire+is%3Aopen", "microsoft")]
    public void TryExtractOrgHint_ReturnsOwner(string url, string expected)
    {
        Assert.Equal(expected, GitHubSamlSso.TryExtractOrgHint(url));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("notifications")]
    [InlineData("")]
    [InlineData(null)]
    public void TryExtractOrgHint_ReturnsNullWhenNoOwner(string? url)
    {
        Assert.Null(GitHubSamlSso.TryExtractOrgHint(url));
    }

    [Fact]
    public void TryParseHeader_ParsesRequiredUrl()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add(
            GitHubSamlSso.HeaderName,
            "required; url=https://github.com/orgs/microsoft/sso?authorization_request=ABC");

        Assert.True(GitHubSamlSso.TryParseHeader(response, out var url, out var partial));
        Assert.Equal("https://github.com/orgs/microsoft/sso?authorization_request=ABC", url);
        Assert.False(partial);
    }

    [Fact]
    public void TryParseHeader_DetectsPartialResults()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add(GitHubSamlSso.HeaderName, "partial-results; organizations=12345,67890");

        Assert.True(GitHubSamlSso.TryParseHeader(response, out var url, out var partial));
        Assert.Null(url);
        Assert.True(partial);
    }

    [Fact]
    public void TryParseHeader_ReturnsFalseWhenAbsent()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        Assert.False(GitHubSamlSso.TryParseHeader(response, out var url, out var partial));
        Assert.Null(url);
        Assert.False(partial);
    }

    [Theory]
    [InlineData("the `microsoft` organization has enabled SAML SSO", true)]
    [InlineData("You must grant access via single sign-on", true)]
    [InlineData("Resource protected by organization SAML enforcement.", true)]
    [InlineData("Resource not accessible by integration", false)]
    [InlineData("API rate limit exceeded", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSamlMessage_DetectsSamlWording(string? message, bool expected)
    {
        Assert.Equal(expected, GitHubSamlSso.IsSamlMessage(message));
    }

    [Fact]
    public void BuildAuthorizationUrl_PrefersHeaderUrl()
    {
        var url = GitHubSamlSso.BuildAuthorizationUrl("https://github.com/orgs/x/sso?authorization_request=Z", "microsoft");
        Assert.Equal("https://github.com/orgs/x/sso?authorization_request=Z", url);
    }

    [Fact]
    public void BuildAuthorizationUrl_DerivesFromOrganizationWhenNoHeader()
    {
        Assert.Equal("https://github.com/orgs/microsoft/sso", GitHubSamlSso.BuildAuthorizationUrl(null, "microsoft"));
    }

    [Fact]
    public void BuildAuthorizationUrl_ReturnsNullWhenNothingKnown()
    {
        Assert.Null(GitHubSamlSso.BuildAuthorizationUrl(null, null));
    }
}
