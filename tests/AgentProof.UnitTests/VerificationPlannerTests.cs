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
        Assert.Equal(["backend", "frontend"], plan.WorkspaceIds);
        Assert.Equal(backend.Length + frontend.Length, plan.Steps.Select(x => x.Id).Distinct().Count());
        Assert.All(backend, x => Assert.Equal("backend", x.WorkspaceId));
        Assert.All(frontend, x => Assert.Equal("frontend", x.WorkspaceId));
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

    [Fact]
    public async Task PlansScopedBrowserVerificationForRequestedWorkspace()
    {
        using var repo = new TemporaryRepository();
        WritePlaywrightWorkspace(repo, "frontend");

        var plan = await PlanAsync(repo.Root, ScopedBrowserTask("frontend"));
        var browser = Assert.Single(plan.Steps, x => x.ProvidedEvidence.Contains(EvidenceType.Browser));

        Assert.Equal("frontend:playwright", browser.Id);
        Assert.Equal("frontend", browser.WorkspaceId);
        Assert.Equal(Path.Combine(repo.Root, "frontend"), browser.WorkingDirectory);
    }

    [Fact]
    public async Task ScopedBrowserRequirementTargetsSecondPlaywrightWorkspaceOnly()
    {
        using var repo = new TemporaryRepository();
        WritePlaywrightWorkspace(repo, "frontend");
        WritePlaywrightWorkspace(repo, "admin");

        var plan = await PlanAsync(repo.Root, ScopedBrowserTask("admin"));
        var browserSteps = plan.Steps.Where(x => x.ProvidedEvidence.Contains(EvidenceType.Browser)).ToArray();

        var browser = Assert.Single(browserSteps);
        Assert.Equal("admin:playwright", browser.Id);
        Assert.DoesNotContain(browserSteps, x => x.Id == "frontend:playwright");
    }

    [Fact]
    public async Task MultipleScopedBrowserRequirementsCreateBothProviders()
    {
        using var repo = new TemporaryRepository();
        WritePlaywrightWorkspace(repo, "frontend");
        WritePlaywrightWorkspace(repo, "admin");

        var task = new TaskContext
        {
            Contract = new TaskContract
            {
                EvidenceRequirements =
                [
                    new EvidenceRequirement { Type = EvidenceType.Browser, WorkspaceId = "frontend" },
                    new EvidenceRequirement { Type = EvidenceType.Browser, WorkspaceId = "admin" }
                ]
            }
        };
        var plan = await PlanAsync(repo.Root, task);

        Assert.Equal(["admin:playwright", "frontend:playwright"], plan.Steps
            .Where(x => x.ProvidedEvidence.Contains(EvidenceType.Browser))
            .Select(x => x.Id)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray());
    }

    [Fact]
    public async Task DuplicateScopedBrowserRequirementsCreateOneProvider()
    {
        using var repo = new TemporaryRepository();
        WritePlaywrightWorkspace(repo, "admin");
        var task = new TaskContext
        {
            Contract = new TaskContract
            {
                EvidenceRequirements =
                [
                    new EvidenceRequirement { Type = EvidenceType.Browser, WorkspaceId = "admin" },
                    new EvidenceRequirement { Type = EvidenceType.Browser, WorkspaceId = "admin" }
                ]
            }
        };

        var plan = await PlanAsync(repo.Root, task);

        Assert.Single(plan.Steps, x => x.Id == "admin:playwright");
    }

    [Fact]
    public async Task KnownWorkspaceWithoutPlaywrightProducesWorkspaceSpecificBrowserGap()
    {
        using var repo = new TemporaryRepository();
        WritePlaywrightWorkspace(repo, "frontend");
        repo.Write("admin/package.json", "{}");

        var plan = await PlanAsync(repo.Root, ScopedBrowserTask("admin"));

        Assert.DoesNotContain(plan.Steps, x => x.ProvidedEvidence.Contains(EvidenceType.Browser));
        var gap = Assert.Single(plan.Gaps, x => x.Code == "BROWSER_VERIFICATION_UNAVAILABLE");
        Assert.Contains("admin", gap.Reason);
    }

    [Fact]
    public async Task RootWorkspaceBrowserScopeUsesLegacyRootStepId()
    {
        using var repo = new TemporaryRepository();
        WritePlaywrightWorkspace(repo, ".");

        var plan = await PlanAsync(repo.Root, ScopedBrowserTask("."));
        var browser = Assert.Single(plan.Steps, x => x.ProvidedEvidence.Contains(EvidenceType.Browser));

        Assert.Equal("playwright", browser.Id);
        Assert.Equal(".", browser.WorkspaceId);
        Assert.Equal(repo.Root, Path.GetFullPath(browser.WorkingDirectory));
    }

    [Fact]
    public async Task UnscopedBrowserRequirementKeepsSingleFirstPlaywrightProviderBehavior()
    {
        using var repo = new TemporaryRepository();
        WritePlaywrightWorkspace(repo, "frontend");
        WritePlaywrightWorkspace(repo, "admin");

        var plan = await PlanAsync(repo.Root, new TaskContext
        {
            Contract = new TaskContract { RequiredEvidence = [EvidenceType.Browser] }
        });

        Assert.Single(plan.Steps, x => x.Id == "admin:playwright");
        Assert.DoesNotContain(plan.Steps, x => x.Id == "frontend:playwright");
    }

    private static async Task<VerificationPlan> PlanAsync(string root, TaskContext? task = null)
    {
        var analyzer = new LocalRepositoryAnalyzer();
        return await new DeterministicVerificationPlanner(new RepositoryConfigurationReader())
            .CreateAsync(await analyzer.AnalyzeAsync(root), task ?? new TaskContext());
    }

    private static TaskContext ScopedBrowserTask(string workspaceId) => new()
    {
        Contract = new TaskContract
        {
            EvidenceRequirements = [new EvidenceRequirement { Type = EvidenceType.Browser, WorkspaceId = workspaceId }]
        }
    };

    private static void WritePlaywrightWorkspace(TemporaryRepository repo, string relativePath)
    {
        var prefix = relativePath == "." ? string.Empty : relativePath + "/";
        repo.Write($"{prefix}package.json", "{\"devDependencies\":{\"@playwright/test\":\"1\"}}");
        repo.Write($"{prefix}package-lock.json", "{}");
        repo.Write($"{prefix}playwright.config.ts", "export default {};");
    }
}
