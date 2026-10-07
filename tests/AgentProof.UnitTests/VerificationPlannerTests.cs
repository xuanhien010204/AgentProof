using AgentProof.Application;
using AgentProof.Domain;
using AgentProof.Infrastructure;

namespace AgentProof.UnitTests;

public sealed class VerificationPlannerTests
{
    private static readonly string[] BuildArguments = ["run", "build"];

    [Fact]
    public async Task DoesNotInventMissingNpmScripts()
    {
        using var repo = new TemporaryRepository();
        repo.Write("package.json", """{"scripts":{"build":"vite build"}}""");
        var plan = await PlanAsync(repo.Root);
        Assert.Contains(plan.Steps, x => x.Command.Arguments.SequenceEqual(BuildArguments));
        Assert.DoesNotContain(plan.Steps, x => x.Command.Arguments.Contains("lint"));
    }

    [Fact]
    public async Task ReportsBrowserGapWhenPlaywrightIsUnavailable()
    {
        using var repo = new TemporaryRepository();
        repo.Write("package.json", "{}" );
        var analyzer = new LocalRepositoryAnalyzer();
        var planner = new DeterministicVerificationPlanner(new RepositoryConfigurationReader());
        var plan = await planner.CreateAsync(await analyzer.AnalyzeAsync(repo.Root),
            new TaskContext { HasBrowserVisibleChanges = true });
        Assert.Contains(plan.Gaps, x => x.Code == "BROWSER_VERIFICATION_UNAVAILABLE");
    }

    [Fact]
    public async Task PlansFullStackVerificationPerWorkspaceWithUniqueIdsAndDirectories()
    {
        using var repo = new TemporaryRepository();
        repo.Write("backend/App.sln", "solution");
        repo.Write("frontend/package.json", "{\"scripts\":{\"build\":\"vite build\",\"test\":\"vitest\"}}");
        repo.Write("frontend/package-lock.json", "{}");

        var plan = await PlanAsync(repo.Root);
        var backend = plan.Steps.Where(x => x.Id.StartsWith("backend:", StringComparison.Ordinal)).ToArray();
        var frontend = plan.Steps.Where(x => x.Id.StartsWith("frontend:", StringComparison.Ordinal)).ToArray();

        Assert.Equal(3, backend.Length);
        Assert.Equal(2, frontend.Length);
        Assert.Equal(backend.Length + frontend.Length, plan.Steps.Select(x => x.Id).Distinct().Count());
        Assert.All(backend, x => Assert.Equal(Path.Combine(repo.Root, "backend"), x.WorkingDirectory));
        Assert.All(frontend, x => Assert.Equal(Path.Combine(repo.Root, "frontend"), x.WorkingDirectory));
        Assert.Contains(backend, x => x.Command.Arguments.SequenceEqual(["build", "App.sln"]));
        Assert.Contains(frontend, x => x.Command.Arguments.SequenceEqual(["run", "build"]));
    }

    [Fact]
    public async Task PlansBrowserVerificationFromPlaywrightWorkspace()
    {
        using var repo = new TemporaryRepository();
        repo.Write("frontend/package.json", "{\"devDependencies\":{\"@playwright/test\":\"1\"}}");
        repo.Write("frontend/package-lock.json", "{}");
        repo.Write("frontend/playwright.config.ts", "export default {};");

        var plan = await PlanAsync(repo.Root, new TaskContext { HasBrowserVisibleChanges = true });
        var browser = Assert.Single(plan.Steps, x => x.ProvidedEvidence.Contains(EvidenceType.Browser));

        Assert.Equal(Path.Combine(repo.Root, "frontend"), browser.WorkingDirectory);
        Assert.Equal("frontend:playwright", browser.Id);
        Assert.DoesNotContain(plan.Gaps, x => x.Code == "BROWSER_VERIFICATION_UNAVAILABLE");
    }

    private static async Task<VerificationPlan> PlanAsync(string root, TaskContext? task = null)
    {
        var analyzer = new LocalRepositoryAnalyzer();
        return await new DeterministicVerificationPlanner(new RepositoryConfigurationReader())
            .CreateAsync(await analyzer.AnalyzeAsync(root), task ?? new TaskContext());
    }
}
