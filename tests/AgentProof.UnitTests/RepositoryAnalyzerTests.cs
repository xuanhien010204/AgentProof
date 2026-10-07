using AgentProof.Infrastructure;

namespace AgentProof.UnitTests;

public sealed class RepositoryAnalyzerTests
{
    private readonly LocalRepositoryAnalyzer _analyzer = new();

    [Fact]
    public async Task CsprojDetectsDotNet()
    {
        using var repo = new TemporaryRepository();
        repo.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var result = await _analyzer.AnalyzeAsync(repo.Root);
        Assert.Contains(".NET", result.Technologies);
    }

    [Fact]
    public async Task WebSdkDetectsAspNetCore()
    {
        using var repo = new TemporaryRepository();
        repo.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        Assert.Contains("ASP.NET Core", (await _analyzer.AnalyzeAsync(repo.Root)).Frameworks);
    }

    [Fact]
    public async Task PackageReferencesDetectEfCoreAndTestFrameworks()
    {
        using var repo = new TemporaryRepository();
        repo.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.0" />
              <PackageReference Include="xunit" Version="2.9.3" />
            </ItemGroup></Project>
            """);
        var profile = await _analyzer.AnalyzeAsync(repo.Root);
        Assert.Contains("EF Core", profile.Frameworks);
        Assert.Contains("xUnit", profile.TestFrameworks);
    }

    [Theory]
    [InlineData("react", "React")]
    [InlineData("next", "Next.js")]
    [InlineData("tailwindcss", "Tailwind CSS")]
    [InlineData("vite", "Vite")]
    public async Task PackageDependenciesDetectFrameworks(string dependency, string expected)
    {
        using var repo = new TemporaryRepository();
        repo.Write("package.json", "{\"dependencies\":{\"" + dependency + "\":\"1.0.0\"}}");
        Assert.Contains(expected, (await _analyzer.AnalyzeAsync(repo.Root)).Frameworks);
    }

    [Fact]
    public async Task DetectsTypeScriptAndPlaywright()
    {
        using var repo = new TemporaryRepository();
        repo.Write("package.json", """{"devDependencies":{"typescript":"1","@playwright/test":"1"}}""");
        var profile = await _analyzer.AnalyzeAsync(repo.Root);
        Assert.Contains("TypeScript", profile.Technologies);
        Assert.Contains("Playwright", profile.TestFrameworks);
    }

    [Fact]
    public async Task DetectsVitestDependency()
    {
        using var repo = new TemporaryRepository();
        repo.Write("package.json", """{"devDependencies":{"vitest":"1"}}""");

        Assert.Contains("Vitest", (await _analyzer.AnalyzeAsync(repo.Root)).TestFrameworks);
    }

    [Theory]
    [InlineData("vitest.config.ts")]
    [InlineData("vitest.config.js")]
    [InlineData("vitest.config.mts")]
    [InlineData("vitest.config.mjs")]
    public async Task DetectsVitestConfig(string configName)
    {
        using var repo = new TemporaryRepository();
        repo.Write("package.json", "{}");
        repo.Write(configName, "export default {};");

        Assert.Contains("Vitest", (await _analyzer.AnalyzeAsync(repo.Root)).TestFrameworks);
    }

    [Fact]
    public async Task IgnoresGeneratedDirectories()
    {
        using var repo = new TemporaryRepository();
        repo.Write("bin/Generated.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        var profile = await _analyzer.AnalyzeAsync(repo.Root);
        Assert.DoesNotContain(".NET", profile.Technologies);
        Assert.DoesNotContain("ASP.NET Core", profile.Frameworks);
    }

    [Fact]
    public async Task DetectsDockerCompose()
    {
        using var repo = new TemporaryRepository();
        repo.Write("compose.yml", "services: {}" );
        var profile = await _analyzer.AnalyzeAsync(repo.Root);
        Assert.True(profile.HasDocker);
        Assert.Contains("Docker", profile.Technologies);
        Assert.Contains("Docker Compose", profile.Technologies);
    }

    [Fact]
    public async Task DiscoversNestedDotNetWorkspaceAndSolutionEntryPoint()
    {
        using var repo = new TemporaryRepository();
        repo.Write("backend/App.sln", "Microsoft Visual Studio Solution File, Format Version 12.00");

        var workspace = Assert.Single((await _analyzer.AnalyzeAsync(repo.Root)).Workspaces);

        Assert.Equal("backend", workspace.Id);
        Assert.Equal("App.sln", workspace.DotNetEntryPoint);
        Assert.Contains(".NET", workspace.Technologies);
    }

    [Fact]
    public async Task DiscoversNodeWorkspacesWithLocalPackageManagers()
    {
        using var repo = new TemporaryRepository();
        repo.Write("frontend/package.json", "{\"scripts\":{\"build\":\"vite build\"}}");
        repo.Write("frontend/package-lock.json", "{}");
        repo.Write("tools/package.json", "{\"scripts\":{\"test\":\"vitest\"}}");
        repo.Write("tools/pnpm-lock.yaml", "lockfileVersion: 9");

        var workspaces = (await _analyzer.AnalyzeAsync(repo.Root)).Workspaces;

        Assert.Equal(["frontend", "tools"], workspaces.Select(x => x.Id).ToArray());
        Assert.Equal("npm", workspaces.Single(x => x.Id == "frontend").PackageManager);
        Assert.Equal("pnpm", workspaces.Single(x => x.Id == "tools").PackageManager);
    }

    [Fact]
    public async Task IgnoresAllConfiguredGeneratedDirectoriesDuringWorkspaceDiscovery()
    {
        using var repo = new TemporaryRepository();
        foreach (var directory in new[] { "node_modules", "bin", "obj", ".next", "dist", "build", "coverage", "TestResults" })
            repo.Write($"{directory}/nested/package.json", "{\"scripts\":{\"build\":\"build\"}}");

        Assert.Empty((await _analyzer.AnalyzeAsync(repo.Root)).Workspaces);
    }
}
