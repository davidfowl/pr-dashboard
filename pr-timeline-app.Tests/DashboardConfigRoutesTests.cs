using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace pr_timeline_app.Tests;

public sealed class DashboardConfigRoutesTests
{
    [Fact]
    public async Task DashboardConfigRouteNormalizesListsAndDropsIncompleteCheckRules()
    {
        await using var app = await CreateDashboardConfigAppAsync(new DashboardOptions
        {
            Repositories = [" example/repo ", "EXAMPLE/repo", "", "example/other"],
            ShipWeekRepositories = [" example/repo ", "example/repo"],
            CoreTeamMembers = [" octocat ", "OCTOCAT", "hubot"],
            CoreTeamMemberAliasSuffixes = [" _corp ", "_corp", ""],
            CommunityRepositories = [" community/repo ", "COMMUNITY/repo"],
            CurrentRelease = " 13.4 ",
            ShipWeekReleaseBranch = " release/13.4 ",
            DocsFromCode = new DashboardDocsFromCodeOptions
            {
                Repository = " docs/repo ",
                Label = " docs-from-code "
            },
            DoNotMergeLabels = [" no-merge ", "NO-MERGE", "needs-author-action"],
            BotAuthors = [" dependabot ", "DEPENDABOT"],
            NonBlockingCheckFailureRules =
            [
                new DashboardCheckFailureRuleOptions
                {
                    Repository = " example/repo ",
                    Label = " flaky check ",
                    CheckNames = [" Build ", "BUILD"],
                    CheckNameContains = [" proof ", ""]
                },
                new DashboardCheckFailureRuleOptions
                {
                    Repository = "example/repo",
                    Label = ""
                },
                new DashboardCheckFailureRuleOptions
                {
                    Repository = "",
                    Label = "missing repository"
                }
            ]
        });
        using var client = CreateHttpClient(app);

        var config = await client.GetFromJsonAsync<DashboardConfigResponse>(
            "/api/dashboard/config",
            TestContext.Current.CancellationToken);

        Assert.NotNull(config);
        Assert.Equal(["example/repo", "example/other"], config.Repositories);
        Assert.Equal("example/repo, example/other", config.RepositoryInput);
        Assert.Equal(["example/repo"], config.ShipWeekRepositories);
        Assert.Equal("example/repo", config.ShipWeekRepositoryInput);
        Assert.Equal(["octocat", "hubot"], config.CoreTeamMembers);
        Assert.Equal(["_corp"], config.CoreTeamMemberAliasSuffixes);
        Assert.Equal(["community/repo"], config.CommunityRepositories);
        Assert.Equal("13.4", config.CurrentRelease);
        Assert.Equal("release/13.4", config.ShipWeekReleaseBranch);
        Assert.Equal("docs/repo", config.DocsFromCodeRepository);
        Assert.Equal("docs-from-code", config.DocsFromCodeLabel);
        Assert.Equal(["no-merge", "needs-author-action"], config.DoNotMergeLabels);
        Assert.Equal(["dependabot"], config.BotAuthors);

        var rule = Assert.Single(config.NonBlockingCheckFailureRules);
        Assert.Equal("example/repo", rule.Repository);
        Assert.Equal("flaky check", rule.Label);
        Assert.Equal(["Build"], rule.CheckNames);
        Assert.Equal(["proof"], rule.CheckNameContains);
    }

    private static async Task<WebApplication> CreateDashboardConfigAppAsync(DashboardOptions options)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRouting();
        builder.Services.AddSingleton(Options.Create(options));

        var app = builder.Build();
        app.MapDashboardConfigRoutes();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HttpClient CreateHttpClient(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        var address = Assert.Single(addresses ?? []);
        return new HttpClient
        {
            BaseAddress = new Uri(address)
        };
    }
}
