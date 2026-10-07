using System.Text.Json;
using System.Text.Json.Serialization;
using AgentProof.Application;
using AgentProof.Domain;
using AgentProof.Infrastructure;

namespace AgentProof.UnitTests;

public sealed class EvidenceEngineTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly VerificationStep BuildStep = new(
        "dotnet-build", "Build solution", ".NET",
        new VerificationCommand("dotnet", ["build", "App.sln"]),
        "repo", TimeSpan.FromMinutes(5), true, [EvidenceType.Build]);

    private static readonly VerificationStep TestStep = new(
        "dotnet-test", "Run tests", ".NET",
        new VerificationCommand("dotnet", ["test", "App.sln"]),
        "repo", TimeSpan.FromMinutes(5), true, [EvidenceType.Tests]);

    private static readonly VerificationStep BrowserStep = new(
        "playwright", "Run browser tests", "Browser",
        new VerificationCommand("npx", ["--no-install", "playwright", "test"]),
        "repo", TimeSpan.FromMinutes(10), true, [EvidenceType.Browser]);

    private static readonly VerificationStep BackendBuildStep = new(
        "backend:dotnet-build", "Build backend", ".NET",
        new VerificationCommand("dotnet", ["build", "Backend.sln"]),
        "repo/backend", TimeSpan.FromMinutes(5), true, [EvidenceType.Build], "backend");

    private static readonly VerificationStep FrontendBuildStep = new(
        "frontend:node-build", "Build frontend", "Node",
        new VerificationCommand("npm", ["run", "build"]),
        "repo/frontend", TimeSpan.FromMinutes(5), true, [EvidenceType.Build], "frontend");

    private static readonly VerificationStep FrontendTestStep = new(
        "frontend:node-test", "Test frontend", "Node",
        new VerificationCommand("npm", ["run", "test"]),
        "repo/frontend", TimeSpan.FromMinutes(5), true, [EvidenceType.Tests], "frontend");

    private static readonly VerificationStep RootBuildStep = new(
        "dotnet-build", "Build root", ".NET",
        new VerificationCommand("dotnet", ["build", "App.sln"]),
        "repo", TimeSpan.FromMinutes(5), true, [EvidenceType.Build], ".");

    private readonly DeterministicEvidenceEvaluator _evaluator = new();

    [Fact]
    public void CriterionRequiringBuildWhenBuildPassesProducesPassed()
    {
        var contract = new TaskContract
        {
            AcceptanceCriteria = [new AcceptanceCriterion { Id = "AC-BUILD", Description = "Builds", RequiredEvidence = [EvidenceType.Build] }]
        };
        var plan = new VerificationPlan([BuildStep], []);
        var executed = new[] { SuccessEvidence(BuildStep) };

        var result = _evaluator.Evaluate(contract, plan, executed, []);

        var criterion = Assert.Single(result.Criteria);
        Assert.Equal(CriterionStatus.Passed, criterion.Status);
        Assert.Equal(["dotnet-build"], criterion.EvidenceStepIds);
        Assert.Equal(VerificationStatus.Verified, result.Status);
    }

    [Fact]
    public void CriterionRequiringTestsWhenTestsPassProducesPassed()
    {
        var contract = new TaskContract
        {
            AcceptanceCriteria = [new AcceptanceCriterion { Id = "AC-TEST", Description = "Tests pass", RequiredEvidence = [EvidenceType.Tests] }]
        };
        var plan = new VerificationPlan([TestStep], []);
        var executed = new[] { SuccessEvidence(TestStep) };

        var result = _evaluator.Evaluate(contract, plan, executed, []);

        var criterion = Assert.Single(result.Criteria);
        Assert.Equal(CriterionStatus.Passed, criterion.Status);
        Assert.Equal(["dotnet-test"], criterion.EvidenceStepIds);
        Assert.Equal(VerificationStatus.Verified, result.Status);
    }

    [Fact]
    public void CriterionRequiringTestsWhenTestsFailProducesFailedAndNotVerified()
    {
        var contract = new TaskContract
        {
            AcceptanceCriteria = [new AcceptanceCriterion { Id = "AC-TEST", Description = "Tests pass", RequiredEvidence = [EvidenceType.Tests] }]
        };
        var plan = new VerificationPlan([TestStep], []);
        var executed = new[] { FailedEvidence(TestStep, "1 test failed") };

        var result = _evaluator.Evaluate(contract, plan, executed, []);

        var criterion = Assert.Single(result.Criteria);
        Assert.Equal(CriterionStatus.Failed, criterion.Status);
        Assert.Equal(["dotnet-test"], criterion.EvidenceStepIds);
        Assert.Contains("1 test failed", criterion.Reason);
        Assert.Equal(VerificationStatus.NotVerified, result.Status);
    }

    [Fact]
    public void CriterionRequiringBrowserWhenPlaywrightUnavailableProducesGapAndPartiallyVerified()
    {
        var contract = new TaskContract
        {
            AcceptanceCriteria = [new AcceptanceCriterion { Id = "AC-UI", Description = "UI works", RequiredEvidence = [EvidenceType.Browser] }]
        };
        var plan = new VerificationPlan([], [new VerificationGap("BROWSER_VERIFICATION_UNAVAILABLE", "Playwright not installed.")]);
        var executed = Array.Empty<VerificationEvidence>();

        var result = _evaluator.Evaluate(contract, plan, executed, []);

        var criterion = Assert.Single(result.Criteria);
        Assert.Equal(CriterionStatus.Gap, criterion.Status);
        Assert.Empty(criterion.EvidenceStepIds);
        Assert.Equal(VerificationStatus.PartiallyVerified, result.Status);
    }

    [Fact]
    public void CriterionRequiringBuildAndTestsWhenBothPassProducesPassed()
    {
        var contract = new TaskContract
        {
            AcceptanceCriteria = [new AcceptanceCriterion { Id = "AC-ALL", Description = "All green", RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests] }]
        };
        var plan = new VerificationPlan([BuildStep, TestStep], []);
        var executed = new[] { SuccessEvidence(BuildStep), SuccessEvidence(TestStep) };

        var result = _evaluator.Evaluate(contract, plan, executed, []);

        var criterion = Assert.Single(result.Criteria);
        Assert.Equal(CriterionStatus.Passed, criterion.Status);
        Assert.Equal(["dotnet-build", "dotnet-test"], criterion.EvidenceStepIds);
        Assert.Equal(VerificationStatus.Verified, result.Status);
    }

    [Fact]
    public void CriterionRequiringBuildAndTestsWhenBuildPassesAndTestsFailProducesFailed()
    {
        var contract = new TaskContract
        {
            AcceptanceCriteria = [new AcceptanceCriterion { Id = "AC-ALL", Description = "All green", RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests] }]
        };
        var plan = new VerificationPlan([BuildStep, TestStep], []);
        var executed = new[] { SuccessEvidence(BuildStep), FailedEvidence(TestStep, "Test execution failed.") };

        var result = _evaluator.Evaluate(contract, plan, executed, []);

        var criterion = Assert.Single(result.Criteria);
        Assert.Equal(CriterionStatus.Failed, criterion.Status);
        Assert.Equal(VerificationStatus.NotVerified, result.Status);
    }

    [Fact]
    public void PlannedEvidenceNotExecutedProducesNotEvaluated()
    {
        var contract = new TaskContract
        {
            AcceptanceCriteria = [new AcceptanceCriterion { Id = "AC-TEST", Description = "Tests pass", RequiredEvidence = [EvidenceType.Tests] }]
        };
        var plan = new VerificationPlan([BuildStep, TestStep], []);
        var executed = new[] { FailedEvidence(BuildStep, "Build failed.") };

        var result = _evaluator.Evaluate(contract, plan, executed, []);

        var criterion = Assert.Single(result.Criteria);
        Assert.Equal(CriterionStatus.NotEvaluated, criterion.Status);
        Assert.Equal(VerificationStatus.NotVerified, result.Status);
    }

    [Fact]
    public void UnrelatedEvidenceDoesNotSatisfyCriterion()
    {
        var contractTests = new TaskContract
        {
            AcceptanceCriteria = [new AcceptanceCriterion { Id = "AC-TEST", Description = "Tests pass", RequiredEvidence = [EvidenceType.Tests] }]
        };
        var planBrowser = new VerificationPlan([BrowserStep], []);
        var executedBrowser = new[] { SuccessEvidence(BrowserStep) };

        var result = _evaluator.Evaluate(contractTests, planBrowser, executedBrowser, []);
        Assert.Equal(CriterionStatus.Gap, Assert.Single(result.Criteria).Status);

        var contractRuntime = new TaskContract
        {
            AcceptanceCriteria = [new AcceptanceCriterion { Id = "AC-RUN", Description = "Runtime works", RequiredEvidence = [EvidenceType.Runtime] }]
        };
        var planBuild = new VerificationPlan([BuildStep], []);
        var executedBuild = new[] { SuccessEvidence(BuildStep) };

        var resultRuntime = _evaluator.Evaluate(contractRuntime, planBuild, executedBuild, []);
        Assert.Equal(CriterionStatus.Gap, Assert.Single(resultRuntime.Criteria).Status);
    }

    [Fact]
    public void EmptyCriterionRequiredEvidenceDoesNotPassAutomatically()
    {
        var contract = new TaskContract
        {
            RequiredEvidence = [EvidenceType.Build],
            AcceptanceCriteria = [new AcceptanceCriterion { Id = "AC-EMPTY", Description = "No evidence declared", RequiredEvidence = [] }]
        };
        var plan = new VerificationPlan([BuildStep], []);
        var executed = new[] { SuccessEvidence(BuildStep) };

        var result = _evaluator.Evaluate(contract, plan, executed, []);

        var criterion = Assert.Single(result.Criteria);
        Assert.Equal(CriterionStatus.Gap, criterion.Status);
        Assert.Contains("no required verification evidence", criterion.Reason);
        Assert.Equal(VerificationStatus.PartiallyVerified, result.Status);
        Assert.Contains(result.Gaps, g => g.Code == "UNMAPPED_ACCEPTANCE_CRITERION");
    }

    [Fact]
    public void EmptyOrWhitespaceCriterionIdProducesGapAndCannotVerify()
    {
        var contract = new TaskContract
        {
            AcceptanceCriteria = [new AcceptanceCriterion { Id = "  ", Description = "Empty ID", RequiredEvidence = [EvidenceType.Build] }]
        };
        var plan = new VerificationPlan([BuildStep], []);
        var executed = new[] { SuccessEvidence(BuildStep) };

        var result = _evaluator.Evaluate(contract, plan, executed, []);

        var criterion = Assert.Single(result.Criteria);
        Assert.Equal(CriterionStatus.Gap, criterion.Status);
        Assert.Equal(VerificationStatus.PartiallyVerified, result.Status);
        Assert.Contains(result.Gaps, g => g.Code == "INVALID_CRITERION_ID");
    }

    [Fact]
    public void DuplicateCriterionIdsProduceGapsAndCannotVerify()
    {
        var contract = new TaskContract
        {
            AcceptanceCriteria =
            [
                new AcceptanceCriterion { Id = "AC-DUP", Description = "First", RequiredEvidence = [EvidenceType.Build] },
                new AcceptanceCriterion { Id = "AC-DUP", Description = "Second", RequiredEvidence = [EvidenceType.Build] }
            ]
        };
        var plan = new VerificationPlan([BuildStep], []);
        var executed = new[] { SuccessEvidence(BuildStep) };

        var result = _evaluator.Evaluate(contract, plan, executed, []);

        Assert.Equal(2, result.Criteria.Count);
        Assert.All(result.Criteria, c => Assert.Equal(CriterionStatus.Gap, c.Status));
        Assert.Equal(VerificationStatus.PartiallyVerified, result.Status);
        Assert.Contains(result.Gaps, g => g.Code == "DUPLICATE_CRITERION_ID");
    }

    [Fact]
    public void MultipleCriteriaMayShareTheSameValidEvidenceStep()
    {
        var contract = new TaskContract
        {
            AcceptanceCriteria =
            [
                new AcceptanceCriterion { Id = "AC-1", Description = "Builds clean", RequiredEvidence = [EvidenceType.Build] },
                new AcceptanceCriterion { Id = "AC-2", Description = "Compiles outputs", RequiredEvidence = [EvidenceType.Build] }
            ]
        };
        var plan = new VerificationPlan([BuildStep], []);
        var executed = new[] { SuccessEvidence(BuildStep) };

        var result = _evaluator.Evaluate(contract, plan, executed, []);

        Assert.Equal(2, result.Criteria.Count);
        Assert.All(result.Criteria, c =>
        {
            Assert.Equal(CriterionStatus.Passed, c.Status);
            Assert.Equal(["dotnet-build"], c.EvidenceStepIds);
        });
        Assert.Equal(VerificationStatus.Verified, result.Status);
    }

    [Fact]
    public void TaskLevelEvidenceRequirementEnforcedIndependentlyWithoutCriterion()
    {
        var contract = new TaskContract
        {
            RequiredEvidence = [EvidenceType.Build, EvidenceType.Database],
            AcceptanceCriteria = [new AcceptanceCriterion { Id = "AC-1", Description = "Builds", RequiredEvidence = [EvidenceType.Build] }]
        };
        var plan = new VerificationPlan([BuildStep], [new VerificationGap("MISSING_EVIDENCE_DATABASE", "No database capability.")]);
        var executed = new[] { SuccessEvidence(BuildStep) };

        var result = _evaluator.Evaluate(contract, plan, executed, []);

        Assert.Equal(CriterionStatus.Passed, Assert.Single(result.Criteria).Status);
        Assert.Equal(VerificationStatus.PartiallyVerified, result.Status);
        Assert.Contains(result.Gaps, g => g.Code == "MISSING_EVIDENCE_DATABASE");
    }

    [Fact]
    public void EmptyAcceptanceCriteriaEvaluatesTaskRequirementsCleanly()
    {
        var contract = new TaskContract
        {
            RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests],
            AcceptanceCriteria = []
        };
        var plan = new VerificationPlan([BuildStep, TestStep], []);
        var executed = new[] { SuccessEvidence(BuildStep), SuccessEvidence(TestStep) };

        var result = _evaluator.Evaluate(contract, plan, executed, []);

        Assert.Empty(result.Criteria);
        Assert.Equal(VerificationStatus.Verified, result.Status);
    }

    [Fact]
    public void McpAndCliSerializationIncludesCriteriaArrayInJson()
    {
        var result = new VerificationResult(
            VerificationStatus.Verified,
            [SuccessEvidence(BuildStep)],
            [],
            [new CriterionResult { Id = "AC-1", Status = CriterionStatus.Passed, EvidenceStepIds = ["dotnet-build"] }]);

        var json = JsonSerializer.Serialize(result, SerializerOptions);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("criteria", out var criteriaEl));
        Assert.Equal(JsonValueKind.Array, criteriaEl.ValueKind);
        var criterion = criteriaEl[0];
        Assert.Equal("AC-1", criterion.GetProperty("id").GetString());
        Assert.Equal("Passed", criterion.GetProperty("status").GetString());
        Assert.Equal("dotnet-build", criterion.GetProperty("evidenceStepIds")[0].GetString());
    }

    [Fact]
    public void BackendBuildRequirementUsesOnlyBackendEvidence()
    {
        var contract = new TaskContract
        {
            AcceptanceCriteria = [new AcceptanceCriterion
            {
                Id = "AC-BACKEND",
                EvidenceRequirements = [new EvidenceRequirement { Type = EvidenceType.Build, WorkspaceId = "backend" }]
            }]
        };
        var plan = FullStackPlan(BackendBuildStep, FrontendBuildStep);

        var result = _evaluator.Evaluate(contract, plan, [SuccessEvidence(BackendBuildStep)], []);

        var criterion = Assert.Single(result.Criteria);
        Assert.Equal(CriterionStatus.Passed, criterion.Status);
        Assert.Equal(["backend:dotnet-build"], criterion.EvidenceStepIds);
    }

    [Fact]
    public void FrontendBuildCannotSatisfyBackendBuildRequirement()
    {
        var contract = ScopedCriterion("AC-BACKEND", EvidenceType.Build, "backend");
        var plan = new VerificationPlan([FrontendBuildStep], [], ["backend", "frontend"]);

        var result = _evaluator.Evaluate(contract, plan, [SuccessEvidence(FrontendBuildStep)], []);

        var criterion = Assert.Single(result.Criteria);
        Assert.Equal(CriterionStatus.Gap, criterion.Status);
        Assert.Empty(criterion.EvidenceStepIds);
    }

    [Fact]
    public void FrontendTestsRequirementMatchesFrontendTestStep()
    {
        var contract = ScopedCriterion("AC-FRONTEND-TESTS", EvidenceType.Tests, "frontend");
        var plan = new VerificationPlan([FrontendTestStep], [], ["frontend"]);

        var result = _evaluator.Evaluate(contract, plan, [SuccessEvidence(FrontendTestStep)], []);

        Assert.Equal(CriterionStatus.Passed, Assert.Single(result.Criteria).Status);
        Assert.Equal(["frontend:node-test"], Assert.Single(result.Criteria).EvidenceStepIds);
    }

    [Fact]
    public void SeparateWorkspaceCriteriaProduceIndependentResults()
    {
        var contract = new TaskContract
        {
            AcceptanceCriteria =
            [
                ScopedCriterionDefinition("AC-BACKEND", EvidenceType.Build, "backend"),
                ScopedCriterionDefinition("AC-FRONTEND", EvidenceType.Build, "frontend")
            ]
        };
        var plan = FullStackPlan(BackendBuildStep, FrontendBuildStep);

        var result = _evaluator.Evaluate(contract, plan,
            [SuccessEvidence(BackendBuildStep), FailedEvidence(FrontendBuildStep, "Frontend build failed.")], []);

        Assert.Equal(CriterionStatus.Passed, result.Criteria.Single(x => x.Id == "AC-BACKEND").Status);
        Assert.Equal(CriterionStatus.Failed, result.Criteria.Single(x => x.Id == "AC-FRONTEND").Status);
        Assert.Equal(["backend:dotnet-build"], result.Criteria.Single(x => x.Id == "AC-BACKEND").EvidenceStepIds);
        Assert.Equal(["frontend:node-build"], result.Criteria.Single(x => x.Id == "AC-FRONTEND").EvidenceStepIds);
        Assert.Equal(VerificationStatus.NotVerified, result.Status);
    }

    [Fact]
    public void UnknownWorkspaceProducesDeterministicGap()
    {
        var contract = ScopedCriterion("AC-MOBILE", EvidenceType.Build, "mobile");
        var plan = new VerificationPlan([BackendBuildStep], [], ["backend"]);

        var result = _evaluator.Evaluate(contract, plan, [SuccessEvidence(BackendBuildStep)], []);

        Assert.Equal(CriterionStatus.Gap, Assert.Single(result.Criteria).Status);
        Assert.Contains(result.Gaps, gap => gap.Code == "MISSING_WORKSPACE_EVIDENCE_PROVIDER");
    }

    [Fact]
    public void UnscopedBuildKeepsAllPlannedProviderSemantics()
    {
        var contract = new TaskContract
        {
            AcceptanceCriteria = [new AcceptanceCriterion { Id = "AC-ANY-BUILD", RequiredEvidence = [EvidenceType.Build] }]
        };
        var plan = FullStackPlan(BackendBuildStep, FrontendBuildStep);

        var result = _evaluator.Evaluate(contract, plan,
            [SuccessEvidence(BackendBuildStep), FailedEvidence(FrontendBuildStep, "Frontend build failed.")], []);

        Assert.Equal(CriterionStatus.Failed, Assert.Single(result.Criteria).Status);
        Assert.Equal(VerificationStatus.NotVerified, result.Status);
    }

    [Fact]
    public void RootWorkspaceRequirementMatchesRootWorkspace()
    {
        var contract = ScopedCriterion("AC-ROOT", EvidenceType.Build, ".");
        var plan = new VerificationPlan([RootBuildStep], [], ["."]);

        var result = _evaluator.Evaluate(contract, plan, [SuccessEvidence(RootBuildStep)], []);

        Assert.Equal(CriterionStatus.Passed, Assert.Single(result.Criteria).Status);
    }

    [Fact]
    public void WorkspaceRequirementWithoutExecutedEvidenceRemainsNotEvaluated()
    {
        var contract = ScopedCriterion("AC-BACKEND", EvidenceType.Build, "backend");
        var plan = new VerificationPlan([BackendBuildStep], [], ["backend"]);

        var result = _evaluator.Evaluate(contract, plan, [], []);

        Assert.Equal(CriterionStatus.NotEvaluated, Assert.Single(result.Criteria).Status);
    }

    [Fact]
    public void LegacyAndScopedEvidenceRequirementsSerializeAndDeserialize()
    {
        var contract = new TaskContract
        {
            RequiredEvidence = [EvidenceType.Build],
            EvidenceRequirements = [new EvidenceRequirement { Type = EvidenceType.Tests, WorkspaceId = "frontend" }]
        };

        var json = JsonSerializer.Serialize(contract, SerializerOptions);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Tests", document.RootElement.GetProperty("evidenceRequirements")[0].GetProperty("type").GetString());
        Assert.Equal("frontend", document.RootElement.GetProperty("evidenceRequirements")[0].GetProperty("workspaceId").GetString());

        var legacy = JsonSerializer.Deserialize<TaskContract>("{\"requiredEvidence\":[\"Build\"]}", SerializerOptions);
        Assert.Equal([EvidenceType.Build], legacy!.RequiredEvidence);
        Assert.Empty(legacy.EvidenceRequirements);
    }

    private static TaskContract ScopedCriterion(string id, EvidenceType type, string workspaceId) => new()
    {
        AcceptanceCriteria = [ScopedCriterionDefinition(id, type, workspaceId)]
    };

    private static AcceptanceCriterion ScopedCriterionDefinition(string id, EvidenceType type, string workspaceId) => new()
    {
        Id = id,
        EvidenceRequirements = [new EvidenceRequirement { Type = type, WorkspaceId = workspaceId }]
    };

    private static VerificationPlan FullStackPlan(params VerificationStep[] steps) =>
        new(steps, [], ["backend", "frontend"]);

    private static VerificationEvidence SuccessEvidence(VerificationStep step) =>
        new(step, VerificationStepStatus.Passed, 0, TimeSpan.FromSeconds(1), "Success", null);

    private static VerificationEvidence FailedEvidence(VerificationStep step, string reason) =>
        new(step, VerificationStepStatus.Failed, 1, TimeSpan.FromSeconds(1), "Failed", reason);
}
