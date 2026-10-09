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
        var workspace = Assert.Single(result.Workspaces);
        Assert.Equal(".", workspace.Id);
        Assert.Equal("App.csproj", workspace.DotNetEntryPoint);
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

    [Fact]
    public async Task IgnoresNestedHiddenVendorAndToolDirectoriesButKeepsRootConfiguration()
    {
        using var repo = new TemporaryRepository();
        repo.Write("package.json", "{\"scripts\":{\"build\":\"build\"}}");
        repo.Write(".phase2-upstream/context7/package.json", "{\"scripts\":{\"build\":\"build\"}}");
        repo.Write(".agents/skills/package.json", "{\"scripts\":{\"test\":\"test\"}}");
        repo.Write(".cache/nested/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var workspaces = (await _analyzer.AnalyzeAsync(repo.Root)).Workspaces;

        var workspace = Assert.Single(workspaces);
        Assert.Equal(".", workspace.Id);
        Assert.Equal(["Node.js"], workspace.Technologies);
    }

    [Fact]
    public async Task SolutionContainingMultipleProjectsProducesOneDotNetWorkspace()
    {
        using var repo = new TemporaryRepository();
        repo.Write("backend/App.slnx", """
            <Solution>
              <Project Path="App/App.csproj" />
              <Project Path="Lib/Lib.csproj" />
            </Solution>
            """);
        repo.Write("backend/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        repo.Write("backend/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var workspaces = (await _analyzer.AnalyzeAsync(repo.Root)).Workspaces;

        var workspace = Assert.Single(workspaces);
        Assert.Equal("backend", workspace.Id);
        Assert.Equal("App.slnx", workspace.DotNetEntryPoint);
        Assert.Equal([".NET"], workspace.Technologies);
    }

    [Fact]
    public async Task RelatedStandaloneProjectsShareAWorkspaceButUnrelatedProjectsDoNot()
    {
        using var repo = new TemporaryRepository();
        repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup>
            </Project>
            """);
        repo.Write("src/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        repo.Write("src/Other/Other.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var workspaces = (await _analyzer.AnalyzeAsync(repo.Root)).Workspaces;

        Assert.Equal(["src/App", "src/Other"], workspaces.Select(x => x.Id).ToArray());
        Assert.Equal("App.csproj", workspaces.Single(x => x.Id == "src/App").DotNetEntryPoint);
    }

    [Fact]
    public async Task SeparateNodeAndDotNetSubsystemsRemainSeparateWorkspaces()
    {
        using var repo = new TemporaryRepository();
        repo.Write("backend/App.slnx", "<Solution><Project Path=\"App.csproj\" /></Solution>");
        repo.Write("backend/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        repo.Write("frontend/package.json", "{\"dependencies\":{\"next\":\"1\"}}");

        var workspaces = (await _analyzer.AnalyzeAsync(repo.Root)).Workspaces;

        Assert.Equal(["backend", "frontend"], workspaces.Select(x => x.Id).ToArray());
        Assert.Equal([".NET"], workspaces[0].Technologies);
        Assert.Equal(["Node.js"], workspaces[1].Technologies);
    }

    [Fact]
    public async Task EducationCmsStyleNestedSolutionsAndFrontendRemainDiscoverable()
    {
        using var repo = new TemporaryRepository();
        var projects = new[] { "Education.Core", "Education.Infrastructure", "Education.API", "Education.Tests" };
        repo.Write("EducationCMS.slnx", """
            <Solution>
              <Project Path="backend/Education.Core/Education.Core.csproj" />
              <Project Path="backend/Education.Infrastructure/Education.Infrastructure.csproj" />
              <Project Path="backend/Education.API/Education.API.csproj" />
              <Project Path="backend/Education.Tests/Education.Tests.csproj" />
            </Solution>
            """);
        repo.Write("backend/Education.API.slnx", string.Join(Environment.NewLine,
            ["<Solution>", .. projects.Select(project => $"  <Project Path=\"{project}/{project}.csproj\" />"), "</Solution>"]));
        foreach (var project in projects) repo.Write($"backend/{project}/{project}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        repo.Write("package.json", "{\"scripts\":{\"build\":\"npm --prefix frontend run build\"}}");
        repo.Write("frontend/package.json", "{\"dependencies\":{\"next\":\"1\"}}");

        var workspaces = (await _analyzer.AnalyzeAsync(repo.Root)).Workspaces;

        Assert.Equal([".", "backend", "frontend"], workspaces.Select(x => x.Id).ToArray());
        Assert.Equal([".NET", "Node.js"], workspaces[0].Technologies);
        Assert.Equal([".NET"], workspaces[1].Technologies);
        Assert.Equal(["Node.js"], workspaces[2].Technologies);
    }

    [Fact]
    public async Task DetectsPythonAndFlutterSubsystems()
    {
        using var repo = new TemporaryRepository();
        repo.Write("ai-service/requirements.txt", "fastapi==1.0\n");
        repo.Write("asrp_app/pubspec.yaml", "dependencies:\n  flutter:\n    sdk: flutter\n");

        var workspaces = (await _analyzer.AnalyzeAsync(repo.Root)).Workspaces;

        Assert.Equal(["ai-service", "asrp_app"], workspaces.Select(x => x.Id).ToArray());
        Assert.Equal(["Python"], workspaces[0].Technologies);
        Assert.Equal(["FastAPI"], workspaces[0].Frameworks);
        Assert.Equal(["Flutter"], workspaces[1].Technologies);
    }
}
