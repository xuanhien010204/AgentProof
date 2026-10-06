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

    private static async Task<VerificationPlan> PlanAsync(string root)
    {
        var analyzer = new LocalRepositoryAnalyzer();
        return await new DeterministicVerificationPlanner(new RepositoryConfigurationReader())
            .CreateAsync(await analyzer.AnalyzeAsync(root), new TaskContext());
    }
}
