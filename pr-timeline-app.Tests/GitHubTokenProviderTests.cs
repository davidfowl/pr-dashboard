using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace pr_timeline_app.Tests;

public sealed class GitHubTokenProviderTests
{
    [Fact]
    public async Task GetTokenAsyncLogsGitHubCliFailureInDevelopment()
    {
        var logger = new ListLogger<GitHubTokenProvider>();
        var provider = new GitHubTokenProvider(
            new HttpContextAccessor(),
            new TestHostEnvironment(),
            logger,
            getEnvironmentToken: () => null,
            getGitHubCliTokenAsync: _ => Task.FromResult(GitHubCliTokenResult.Failed(127, "gh: command not found")));

        var token = await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Null(token);
        Assert.Equal("`gh auth token` exited with code 127: gh: command not found", provider.LocalAuthFailureMessage);
        Assert.Contains(logger.Entries, entry =>
            entry.LogLevel == LogLevel.Warning
            && entry.Message.Contains("GitHub CLI token fallback failed", StringComparison.Ordinal)
            && entry.Message.Contains("code 127", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthStatusReportsLocalGitHubCliFailure()
    {
        var provider = new GitHubTokenProvider(
            new HttpContextAccessor(),
            new TestHostEnvironment(),
            new ListLogger<GitHubTokenProvider>(),
            getEnvironmentToken: () => null,
            getGitHubCliTokenAsync: _ => Task.FromResult(GitHubCliTokenResult.NotFound("gh")));
        var service = new GitHubAuthService(provider, gitHub: null!, new TestHostEnvironment());

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.False(status.Authenticated);
        Assert.Contains("No GitHub token is available to the local backend.", status.Message);
        Assert.Contains("Last local token check failed: `gh` was not found", status.Message);
        Assert.Contains("If running with Aspire, restart the app after `gh auth login`", status.Message);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel LogLevel, string Message);

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
