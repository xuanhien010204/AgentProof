using AgentProof.Application;
using AgentProof.Domain;
using AgentProof.Mcp;

namespace AgentProof.UnitTests;

public sealed class VerificationCoverageSemanticsTests
{
    [Fact]
    public async Task ScopedDotNetVerificationPassesDespiteUnrelatedUnsupportedFlutterWorkspace()
    {
        var (service, _) = CreateServiceWithWorkspaces(
            [
                new RepositoryWorkspace("backend", "backend", [".NET"], [], ["xUnit"], null, "App.sln", new Dictionary<string, string>()),
                new RepositoryWorkspace("mobile", "mobile", ["Flutter"], [], [], null, null, new Dictionary<string, string>())
            ],
            failingStepIds: []);

        var task = new TaskContext
        {
            Description = "Fix backend validation logic",
            Contract = new TaskContract
            {
                Goal = "Backend validation logic fix",
                EvidenceRequirements =
                [
                    new EvidenceRequirement { Type = EvidenceType.Build, WorkspaceId = "backend" },
                    new EvidenceRequirement { Type = EvidenceType.Tests, WorkspaceId = "backend" }
                ]
            }
        };

        var planningContext = await service.CreateVerificationPlanWithProfileAsync("D:/dummy/repo", task);
        var verificationContext = await service.VerifyRepositoryWithContextAsync("D:/dummy/repo", task);

        // Planner only plans backend
        Assert.Single(planningContext.Plan.WorkspaceIds);
        Assert.Equal("backend", planningContext.Plan.WorkspaceIds[0]);
        Assert.Empty(planningContext.Plan.Gaps);

        // Verification evaluates to Verified because scoped requirements all passed
        Assert.Equal(VerificationStatus.Verified, verificationContext.Result.Status);
        Assert.Empty(verificationContext.Result.Gaps);

        // In MCP compact output, out-of-scope unsupported workspace remains visible
        var mcpResult = CompactMcpResponseFactory.Create(verificationContext);
        Assert.Equal(VerificationStatus.Verified, mcpResult.Status);
        Assert.Empty(mcpResult.Gaps);
        var unsupported = Assert.Single(mcpResult.UnsupportedWorkspaces);
        Assert.Equal("mobile", unsupported.WorkspaceId);
        Assert.Contains("Flutter", unsupported.Technologies);
    }

    [Fact]
    public async Task RepositoryWideVerificationExplicitlyReportsMissingRequiredCoverageForUnsupportedWorkspaces()
    {
        var (service, _) = CreateServiceWithWorkspaces(
            [
                new RepositoryWorkspace("backend", "backend", [".NET"], [], ["xUnit"], null, "App.sln", new Dictionary<string, string>()),
                new RepositoryWorkspace("ai-service", "ai-service", ["Python"], [], [], null, null, new Dictionary<string, string>())
            ],
            failingStepIds: []);

        // Repository-wide verification (unscoped contract)
        var task = new TaskContext
        {
            Description = "Verify the entire application",
            Contract = new TaskContract
            {
                Goal = "Verify entire application",
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests]
            }
        };

        var planningContext = await service.CreateVerificationPlanWithProfileAsync("D:/dummy/repo", task);
        var verificationContext = await service.VerifyRepositoryWithContextAsync("D:/dummy/repo", task);

        // Planning includes gap for in-scope unsupported workspace
        Assert.Equal(2, planningContext.Plan.WorkspaceIds.Count);
        var planGap = Assert.Single(planningContext.Plan.Gaps);
        Assert.Equal("UNSUPPORTED_WORKSPACE_VERIFIER", planGap.Code);
        Assert.Contains("ai-service", planGap.Reason);
        Assert.Contains("Python", planGap.Reason);

        // Status CANNOT be Verified; must be PartiallyVerified due to in-scope missing capability
        Assert.Equal(VerificationStatus.PartiallyVerified, verificationContext.Result.Status);
        var resultGap = Assert.Single(verificationContext.Result.Gaps);
        Assert.Equal("UNSUPPORTED_WORKSPACE_VERIFIER", resultGap.Code);

        // MCP representation conveys Partial verification with explicit gap and unsupported list
        var mcpResult = CompactMcpResponseFactory.Create(verificationContext);
        Assert.Equal(VerificationStatus.PartiallyVerified, mcpResult.Status);
        Assert.Equal("UNSUPPORTED_WORKSPACE_VERIFIER", Assert.Single(mcpResult.Gaps).Code);
        Assert.Equal("ai-service", Assert.Single(mcpResult.UnsupportedWorkspaces).WorkspaceId);
    }

    [Fact]
    public async Task UnsupportedInScopeCapabilityCannotProduceMisleadingFullVerification()
    {
        var (service, _) = CreateServiceWithWorkspaces(
            [
                new RepositoryWorkspace("backend", "backend", [".NET"], [], ["xUnit"], null, "App.sln", new Dictionary<string, string>()),
                new RepositoryWorkspace("mobile", "mobile", ["Flutter"], [], [], null, null, new Dictionary<string, string>())
            ],
            failingStepIds: []);

        // Explicitly scoped to the unsupported workspace
        var task = new TaskContext
        {
            Description = "Verify mobile build",
            Contract = new TaskContract
            {
                Goal = "Verify mobile application",
                EvidenceRequirements =
                [
                    new EvidenceRequirement { Type = EvidenceType.Build, WorkspaceId = "mobile" }
                ],
                AcceptanceCriteria =
                [
                    new AcceptanceCriterion
                    {
                        Id = "AC-Mobile",
                        Description = "Mobile app compiles",
                        EvidenceRequirements = [new EvidenceRequirement { Type = EvidenceType.Build, WorkspaceId = "mobile" }]
                    }
                ]
            }
        };

        var verificationContext = await service.VerifyRepositoryWithContextAsync("D:/dummy/repo", task);

        // Must not be Verified
        Assert.NotEqual(VerificationStatus.Verified, verificationContext.Result.Status);
        Assert.Equal(VerificationStatus.PartiallyVerified, verificationContext.Result.Status);

        // Criterion must report Gap
        var criterion = Assert.Single(verificationContext.Result.Criteria);
        Assert.Equal("AC-Mobile", criterion.Id);
        Assert.Equal(CriterionStatus.Gap, criterion.Status);
        Assert.Contains("mobile", criterion.Reason ?? string.Empty);
    }

    [Fact]
    public async Task UnsupportedOutOfScopeCapabilitiesRemainVisibleInMcpResponse()
    {
        var (service, profile) = CreateServiceWithWorkspaces(
            [
                new RepositoryWorkspace("backend", "backend", [".NET"], [], ["xUnit"], null, "App.sln", new Dictionary<string, string>()),
                new RepositoryWorkspace("ml-model", "ml-model", ["Python"], [], [], null, null, new Dictionary<string, string>()),
                new RepositoryWorkspace("mobile-app", "mobile-app", ["Flutter"], [], [], null, null, new Dictionary<string, string>())
            ],
            failingStepIds: []);

        var task = new TaskContext
        {
            Description = "Backend fix only",
            Contract = new TaskContract
            {
                Goal = "Fix backend bug",
                EvidenceRequirements = [new EvidenceRequirement { Type = EvidenceType.Build, WorkspaceId = "backend" }]
            }
        };

        var verificationContext = await service.VerifyRepositoryWithContextAsync("D:/dummy/repo", task);
        var mcpResult = CompactMcpResponseFactory.Create(verificationContext);

        Assert.Equal(VerificationStatus.Verified, mcpResult.Status);
        Assert.Equal(2, mcpResult.UnsupportedWorkspaces.Count);
        Assert.Contains(mcpResult.UnsupportedWorkspaces, u => u.WorkspaceId == "ml-model" && u.Technologies.Contains("Python"));
        Assert.Contains(mcpResult.UnsupportedWorkspaces, u => u.WorkspaceId == "mobile-app" && u.Technologies.Contains("Flutter"));
    }

    [Fact]
    public async Task KnownFailuresStillReturnNotVerifiedDespiteGaps()
    {
        var (service, _) = CreateServiceWithWorkspaces(
            [
                new RepositoryWorkspace("backend", "backend", [".NET"], [], ["xUnit"], null, "App.sln", new Dictionary<string, string>()),
                new RepositoryWorkspace("mobile", "mobile", ["Flutter"], [], [], null, null, new Dictionary<string, string>())
            ],
            failingStepIds: ["backend:dotnet-build"]);

        var task = new TaskContext
        {
            Description = "Repository verification with failure",
            Contract = new TaskContract
            {
                Goal = "Verify repository",
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests]
            }
        };

        var verificationContext = await service.VerifyRepositoryWithContextAsync("D:/dummy/repo", task);

        // A required step failed -> NotVerified must trump PartiallyVerified
        Assert.Equal(VerificationStatus.NotVerified, verificationContext.Result.Status);
        Assert.Contains(verificationContext.Result.Evidence, e => e.Status == VerificationStepStatus.Failed);
        Assert.Contains(verificationContext.Result.Gaps, g => g.Code == "UNSUPPORTED_WORKSPACE_VERIFIER");

        var mcpResult = CompactMcpResponseFactory.Create(verificationContext);
        Assert.Equal(VerificationStatus.NotVerified, mcpResult.Status);
        Assert.NotEmpty(mcpResult.FailedSteps);
    }

    [Fact]
    public async Task CapabilityGapsRemainDistinctFromNotRun()
    {
        var (service, _) = CreateServiceWithWorkspaces(
            [
                new RepositoryWorkspace("backend", "backend", [".NET"], [], ["xUnit"], null, "App.sln", new Dictionary<string, string>()),
                new RepositoryWorkspace("mobile", "mobile", ["Flutter"], [], [], null, null, new Dictionary<string, string>())
            ],
            failingStepIds: ["backend:dotnet-build"]);

        var task = new TaskContext
        {
            Description = "Repository verification",
            Contract = new TaskContract
            {
                Goal = "Verify all",
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests]
            }
        };

        var verificationContext = await service.VerifyRepositoryWithContextAsync("D:/dummy/repo", task);
        var mcpResult = CompactMcpResponseFactory.Create(verificationContext);

        // Gap is in Gaps, NOT in NotRun
        Assert.Contains(mcpResult.Gaps, g => g.Code == "UNSUPPORTED_WORKSPACE_VERIFIER");
        var notRunStepIds = mcpResult.NotRun.SelectMany(g => g.StepIds).ToList();
        Assert.DoesNotContain("mobile", notRunStepIds);
        Assert.DoesNotContain("UNSUPPORTED_WORKSPACE_VERIFIER", notRunStepIds);
        Assert.Contains("backend:dotnet-test", notRunStepIds);
    }

    private static (AgentProofService Service, RepositoryProfile Profile) CreateServiceWithWorkspaces(
        IReadOnlyList<RepositoryWorkspace> workspaces,
        IReadOnlyList<string> failingStepIds)
    {
        var tech = workspaces.SelectMany(w => w.Technologies).Distinct().ToArray();
        var profile = new RepositoryProfile("test-repo", "D:/dummy/repo", tech, [], [], [], false, false, 0)
        {
            Workspaces = workspaces
        };

        var analyzer = new FakeRepositoryAnalyzer(profile);
        var configReader = new FakeConfigReader();
        var planner = new DeterministicVerificationPlanner(configReader);
        var runner = new FakeRunner(failingStepIds);
        var service = new AgentProofService(analyzer, new DeterministicSkillRecommender(), planner, runner);

        return (service, profile);
    }

    private sealed class FakeRepositoryAnalyzer(RepositoryProfile profile) : IRepositoryAnalyzer
    {
        public Task<RepositoryProfile> AnalyzeAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(profile);
    }

    private sealed class FakeConfigReader : IRepositoryConfigurationReader
    {
        public Task<IReadOnlyDictionary<string, string>> ReadPackageScriptsAsync(string directory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public string? FindDotNetEntryPoint(string directory) => null;
    }

    private sealed class FakeRunner(IReadOnlyList<string> failingStepIds) : IVerificationRunner
    {
        public Task<VerificationExecutionResult> RunAsync(string repositoryPath, VerificationPlan plan, CancellationToken cancellationToken = default)
        {
            var evidence = new List<VerificationEvidence>();
            var failingSet = new HashSet<string>(failingStepIds, StringComparer.Ordinal);
            var hadFailure = false;

            foreach (var step in plan.Steps)
            {
                if (hadFailure)
                {
                    evidence.Add(new VerificationEvidence(
                        step, VerificationStepStatus.NotRun, null, TimeSpan.Zero, string.Empty,
                        "Not run because a prior required verification step did not pass."));
                    continue;
                }

                if (failingSet.Contains(step.Id))
                {
                    hadFailure = true;
                    evidence.Add(new VerificationEvidence(
                        step, VerificationStepStatus.Failed, 1, TimeSpan.FromSeconds(1), "Compile error", "Build failed with exit code 1."));
                }
                else
                {
                    evidence.Add(new VerificationEvidence(
                        step, VerificationStepStatus.Passed, 0, TimeSpan.FromSeconds(1), "Passed", null));
                }
            }

            return Task.FromResult(new VerificationExecutionResult(evidence, plan.Gaps));
        }
    }
}
