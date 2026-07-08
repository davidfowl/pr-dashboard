namespace pr_timeline_app.Tests;

public sealed class TeamIdentityMapTests
{
    private static TeamIdentityMap CreateMap(string? currentDeveloper = "radical") =>
        new(new TeamIdentityOptions
        {
            DefaultKind = "public",
            RepositoryKinds = new Dictionary<string, string> { ["devdiv-microsoft/aspire-1p"] = "microsoft" },
            CurrentDeveloper = currentDeveloper,
            Members =
            [
                new TeamMemberOptions
                {
                    Name = "radical",
                    Identities =
                    [
                        new TeamMemberIdentityOptions { Login = "radical", Kind = "public" },
                        new TeamMemberIdentityOptions { Login = "ankj_microsoft", Kind = "microsoft" }
                    ]
                }
            ]
        });

    [Fact]
    public void ResolveKindUsesRepositoryKindThenDefault()
    {
        var map = CreateMap();
        Assert.True(RepositoryName.TryParse("devdiv-microsoft/aspire-1p", out var microsoftRepo));
        Assert.True(RepositoryName.TryParse("microsoft/aspire", out var publicRepo));

        Assert.Equal("microsoft", map.ResolveKind(microsoftRepo));
        Assert.Equal("public", map.ResolveKind(publicRepo));
        Assert.Equal("public", map.ResolveKind(repositoryName: null));
    }

    [Fact]
    public void TryResolveLoginForRepositoryRoutesByKind()
    {
        var map = CreateMap();
        Assert.True(RepositoryName.TryParse("devdiv-microsoft/aspire-1p", out var microsoftRepo));
        Assert.True(RepositoryName.TryParse("microsoft/aspire", out var publicRepo));

        Assert.True(map.TryResolveLoginForRepository("radical", microsoftRepo, out var microsoftLogin, out var microsoftFallback));
        Assert.True(map.TryResolveLoginForRepository("radical", publicRepo, out var publicLogin, out var publicFallback));

        Assert.Equal("ankj_microsoft", microsoftLogin);
        Assert.False(microsoftFallback);
        Assert.Equal("radical", publicLogin);
        Assert.False(publicFallback);
    }

    [Fact]
    public void TryResolveLoginForRepositoryFallsBackToDefaultKindWhenMemberLacksKind()
    {
        var map = new TeamIdentityMap(new TeamIdentityOptions
        {
            DefaultKind = "public",
            RepositoryKinds = new Dictionary<string, string> { ["devdiv-microsoft/aspire-1p"] = "microsoft" },
            CurrentDeveloper = "radical",
            Members =
            [
                new TeamMemberOptions
                {
                    Name = "radical",
                    Identities = [new TeamMemberIdentityOptions { Login = "radical", Kind = "public" }]
                }
            ]
        });
        Assert.True(RepositoryName.TryParse("devdiv-microsoft/aspire-1p", out var microsoftRepo));

        Assert.True(map.TryResolveLoginForRepository("radical", microsoftRepo, out var login, out var usedFallback));

        Assert.Equal("radical", login);
        Assert.True(usedFallback);
    }

    [Fact]
    public void CanonicalizeLoginFoldsAliasesToMemberName()
    {
        var map = CreateMap();

        Assert.Equal("radical", map.CanonicalizeLogin("ankj_microsoft"));
        Assert.Equal("radical", map.CanonicalizeLogin("radical"));
        // Unmapped logins are returned unchanged.
        Assert.Equal("someone-else", map.CanonicalizeLogin("someone-else"));
    }

    [Fact]
    public void CurrentDeveloperDefaultsToSoleMemberWhenUnset()
    {
        var map = CreateMap(currentDeveloper: null);

        Assert.Equal("radical", map.CurrentDeveloper);
    }

    [Fact]
    public void EmptyMapHasNoMembersAndPublicDefaultKind()
    {
        Assert.False(TeamIdentityMap.Empty.HasMembers);
        Assert.Equal("public", TeamIdentityMap.Empty.DefaultKind);
        Assert.Null(TeamIdentityMap.Empty.CurrentDeveloper);
    }
}
