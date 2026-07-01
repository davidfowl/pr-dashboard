namespace pr_timeline_app.Tests;

public sealed class GitHubExceptionHandlingExtensionsTests
{
    [Fact]
    public void NormalizeQueryValueForLogTrimsLineEndingsAndTruncatesLongValues()
    {
        var value = $"  owner{Environment.NewLine}repo/{new string('x', 200)}  ";

        var normalized = GitHubExceptionHandlingExtensions.NormalizeQueryValueForLog(value);

        Assert.NotNull(normalized);
        Assert.Equal(160, normalized.Length);
        Assert.DoesNotContain(Environment.NewLine, normalized);
        Assert.StartsWith("owner repo/", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeQueryValueForLogReturnsNullForEmptyValues()
    {
        Assert.Null(GitHubExceptionHandlingExtensions.NormalizeQueryValueForLog(null));
        Assert.Null(GitHubExceptionHandlingExtensions.NormalizeQueryValueForLog("   "));
    }
}
