using System.Text.Json;
using System.Text.Json.Serialization;
using AgentProof.Application;
using AgentProof.Domain;
using AgentProof.Mcp;

namespace AgentProof.IntegrationTests;

public sealed class CompactMcpResponseTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void AnalyzeOutputPreservesEducationCmsWorkspaceIdentities()
    {
        var compact = CompactMcpResponseFactory.Create(EducationCmsProfile());

        Assert.Equal(3, compact.WorkspaceCount);
        Assert.Equal([".", "backend", "frontend"], compact.Workspaces.Select(workspace => workspace.Id).ToArray());
        Assert.Equal([".NET", "Node.js"], compact.Workspaces[0].Technologies);
        Assert.Equal("EducationCMS.slnx", compact.Workspaces[0].DotNetEntryPoint);
    }

    [Fact]
    public void AnalyzeOutputPreservesAsrpUnsupportedSubsystems()
    {
        var compact = CompactMcpResponseFactory.Create(AsrpProfile());

        Assert.Equal(5, compact.WorkspaceCount);
        Assert.Equal(["ASRP", "ASRP/web", "ai-service", "asrp_app", "fe_asrp"], compact.Workspaces.Select(workspace => workspace.Id).ToArray());
        Assert.Equal("No active verifier is available for: Python.", compact.Workspaces.Single(workspace => workspace.Id == "ai-service").UnsupportedVerifierReason);
        Assert.Equal("No active verifier is available for: Flutter.", compact.Workspaces.Single(workspace => workspace.Id == "asrp_app").UnsupportedVerifierReason);
    }

    [Fact]
    public void RecommendationOutputPreservesSkillIdentityLevelAndReason()
    {
        var compact = CompactMcpResponseFactory.Create(
        [
            new SkillRecommendation(
                SkillCatalog.Context7,
                RecommendationLevel.Recommended,
                "FRAMEWORK_API_RELEVANT",
                "Current framework APIs may matter.")
        ]);

        var recommendation = Assert.Single(compact);
        Assert.Equal("context7", recommendation.SkillId);
        Assert.Equal(RecommendationLevel.Recommended, recommendation.Level);
        Assert.Equal("FRAMEWORK_API_RELEVANT", recommendation.ReasonCode);
        Assert.Equal("Current framework APIs may matter.", recommendation.Reason);
    }

    [Fact]
    public void PlanOutputPreservesStepIdentityCommandWorkspaceAndGap()
    {
        var profile = AsrpProfile();
        var step = new VerificationStep(
            "ASRP:dotnet-build",
            "Build .NET solution",
            ".NET",
            new VerificationCommand("dotnet", ["build", "ASRP.slnx"]),
            Path.Combine(profile.RootPath, "ASRP"),
            TimeSpan.FromMinutes(5),
            true,
            [EvidenceType.Build],
            "ASRP");
        var plan = new VerificationPlan([step], [new VerificationGap("MISSING_EVIDENCE_RUNTIME", "Runtime verification is unavailable.")], ["ASRP"]);

        var compact = CompactMcpResponseFactory.Create(new RepositoryPlanningContext(profile, plan));

        Assert.Equal(1, compact.PlannedStepCount);
        var compactStep = Assert.Single(compact.Steps);
        Assert.Equal(step.Id, compactStep.Id);
        Assert.Equal(step.WorkspaceId, compactStep.WorkspaceId);
        Assert.Equal(["dotnet", "build", "ASRP.slnx"], compactStep.Command);
        Assert.Equal([EvidenceType.Build], compactStep.ProvidedEvidence);
        Assert.Equal("MISSING_EVIDENCE_RUNTIME", Assert.Single(compact.Gaps).Code);
    }

    [Fact]
    public void VerificationOutputPreservesNotVerifiedFailureAndAccounting()
    {
        var profile = AsrpProfile();
        var passed = Enumerable.Range(1, 3).Select(index => Evidence($"pass-{index}", VerificationStepStatus.Passed)).ToArray();
        var failed = Evidence("ASRP-web:node-lint", VerificationStepStatus.Failed, 1, "Process exited with code 1.", "Parsing error: Unexpected keyword or identifier");
        var notRun = Enumerable.Range(1, 5).Select(index => Evidence($"not-run-{index}", VerificationStepStatus.NotRun, null,
            "Not run because a prior required verification step did not pass.", string.Empty)).ToArray();
        var evidence = passed.Concat([failed]).Concat(notRun).ToArray();
        var plan = new VerificationPlan(evidence.Select(item => item.Step).ToArray(), [], profile.Workspaces.Select(workspace => workspace.Id).ToArray());
        var result = new VerificationResult(VerificationStatus.NotVerified, evidence, []);

        var compact = CompactMcpResponseFactory.Create(new RepositoryVerificationContext(profile, plan, result));

        Assert.Equal(VerificationStatus.NotVerified, compact.Status);
        Assert.Equal(9, compact.Summary.PlannedStepCount);
        Assert.Equal(4, compact.Summary.ExecutedStepCount);
        Assert.Equal(3, compact.Summary.PassedStepCount);
        Assert.Equal(1, compact.Summary.FailedStepCount);
        Assert.Equal(0, compact.Summary.TimedOutStepCount);
        Assert.Equal(5, compact.Summary.NotRunStepCount);
        var failure = Assert.Single(compact.FailedSteps);
        Assert.Equal("ASRP-web:node-lint", failure.StepId);
        Assert.Equal("Process exited with code 1.", failure.FailureReason);
        Assert.Contains("Parsing error", failure.OutputSummary);
        Assert.Equal(5, Assert.Single(compact.NotRun).StepIds.Count);
        Assert.Equal(
            compact.Summary.PlannedStepCount,
            compact.Summary.PassedStepCount + compact.Summary.FailedStepCount + compact.Summary.TimedOutStepCount + compact.Summary.NotRunStepCount);
    }

    [Fact]
    public void CompactSerializationIsDeterministic()
    {
        var compact = CompactMcpResponseFactory.Create(AsrpProfile());

        var first = JsonSerializer.Serialize(compact, JsonOptions);
        var second = JsonSerializer.Serialize(compact, JsonOptions);

        Assert.Equal(first, second);
        Assert.DoesNotContain("rootPath", first, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaVersionIsStableAndExplicitAcrossResponses()
    {
        var profile = EducationCmsProfile();
        var compactProfile = CompactMcpResponseFactory.Create(profile);
        Assert.Equal(1, compactProfile.SchemaVersion);

        var jsonProfile = JsonSerializer.Serialize(compactProfile, JsonOptions);
        using var docProfile = JsonDocument.Parse(jsonProfile);
        Assert.Equal(1, docProfile.RootElement.GetProperty("schemaVersion").GetInt32());

        var recs = CompactMcpResponseFactory.Create([
            new SkillRecommendation(SkillCatalog.Context7, RecommendationLevel.Recommended, "REASON", "Description")
        ]);
        Assert.Equal(1, recs[0].SchemaVersion);

        var step = new VerificationStep(
            "step-1", "Build", ".NET", new VerificationCommand("dotnet", ["build"]),
            profile.RootPath, TimeSpan.FromMinutes(5), true, [EvidenceType.Build], ".");
        var plan = new VerificationPlan([step], [], ["."]);
        var compactPlan = CompactMcpResponseFactory.Create(new RepositoryPlanningContext(profile, plan));
        Assert.Equal(1, compactPlan.SchemaVersion);

        var jsonPlan = JsonSerializer.Serialize(compactPlan, JsonOptions);
        using var docPlan = JsonDocument.Parse(jsonPlan);
        Assert.Equal(1, docPlan.RootElement.GetProperty("schemaVersion").GetInt32());

        var result = new VerificationResult(VerificationStatus.Verified, [Evidence("step-1", VerificationStepStatus.Passed)], []);
        var compactResult = CompactMcpResponseFactory.Create(new RepositoryVerificationContext(profile, plan, result));
        Assert.Equal(1, compactResult.SchemaVersion);

        var jsonResult = JsonSerializer.Serialize(compactResult, JsonOptions);
        using var docResult = JsonDocument.Parse(jsonResult);
        Assert.Equal(1, docResult.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void CompactSchemaContainsExplicitExpectedFields()
    {
        var profile = EducationCmsProfile();
        var compactProfile = CompactMcpResponseFactory.Create(profile);
        var jsonProfile = JsonSerializer.Serialize(compactProfile, JsonOptions);
        using var docProfile = JsonDocument.Parse(jsonProfile);
        var profileRoot = docProfile.RootElement;

        Assert.True(profileRoot.TryGetProperty("schemaVersion", out _));
        Assert.True(profileRoot.TryGetProperty("name", out _));
        Assert.True(profileRoot.TryGetProperty("technologies", out _));
        Assert.True(profileRoot.TryGetProperty("frameworks", out _));
        Assert.True(profileRoot.TryGetProperty("testFrameworks", out _));
        Assert.True(profileRoot.TryGetProperty("packageManagers", out _));
        Assert.True(profileRoot.TryGetProperty("hasDocker", out _));
        Assert.True(profileRoot.TryGetProperty("hasGit", out _));
        Assert.True(profileRoot.TryGetProperty("estimatedSize", out _));
        Assert.True(profileRoot.TryGetProperty("workspaceCount", out _));
        Assert.True(profileRoot.TryGetProperty("workspaces", out _));

        var step = new VerificationStep(
            "step-1", "Build", ".NET", new VerificationCommand("dotnet", ["build"]),
            profile.RootPath, TimeSpan.FromMinutes(5), true, [EvidenceType.Build], ".");
        var plan = new VerificationPlan([step], [new VerificationGap("GAP_1", "Reason")], ["."]);
        var compactPlan = CompactMcpResponseFactory.Create(new RepositoryPlanningContext(profile, plan));
        var jsonPlan = JsonSerializer.Serialize(compactPlan, JsonOptions);
        using var docPlan = JsonDocument.Parse(jsonPlan);
        var planRoot = docPlan.RootElement;

        Assert.True(planRoot.TryGetProperty("schemaVersion", out _));
        Assert.True(planRoot.TryGetProperty("plannedStepCount", out _));
        Assert.True(planRoot.TryGetProperty("workspaceIds", out _));
        Assert.True(planRoot.TryGetProperty("steps", out _));
        Assert.True(planRoot.TryGetProperty("gaps", out _));
        Assert.True(planRoot.TryGetProperty("unsupportedWorkspaces", out _));

        var result = new VerificationResult(VerificationStatus.Verified, [Evidence("step-1", VerificationStepStatus.Passed)], [new VerificationGap("GAP_1", "Reason")]);
        var compactResult = CompactMcpResponseFactory.Create(new RepositoryVerificationContext(profile, plan, result));
        var jsonResult = JsonSerializer.Serialize(compactResult, JsonOptions);
        using var docResult = JsonDocument.Parse(jsonResult);
        var resultRoot = docResult.RootElement;

        Assert.True(resultRoot.TryGetProperty("schemaVersion", out _));
        Assert.True(resultRoot.TryGetProperty("status", out _));
        Assert.True(resultRoot.TryGetProperty("summary", out _));
        Assert.True(resultRoot.TryGetProperty("passedStepIds", out _));
        Assert.True(resultRoot.TryGetProperty("failedSteps", out _));
        Assert.True(resultRoot.TryGetProperty("timedOutSteps", out _));
        Assert.True(resultRoot.TryGetProperty("notRun", out _));
        Assert.True(resultRoot.TryGetProperty("gaps", out _));
        Assert.True(resultRoot.TryGetProperty("criteria", out _));
        Assert.True(resultRoot.TryGetProperty("unsupportedWorkspaces", out _));
    }

    [Fact]
    public void WorkspaceIdsAndVerificationStatusCannotDisappear()
    {
        var profile = EducationCmsProfile();
        var compact = CompactMcpResponseFactory.Create(profile);

        Assert.NotEmpty(compact.Workspaces);
        foreach (var ws in compact.Workspaces)
        {
            Assert.False(string.IsNullOrWhiteSpace(ws.Id));
        }

        var step = new VerificationStep(
            "step-1", "Build", ".NET", new VerificationCommand("dotnet", ["build"]),
            profile.RootPath, TimeSpan.FromMinutes(5), true, [EvidenceType.Build], ".");
        var plan = new VerificationPlan([step], [], ["."]);
        var result = new VerificationResult(VerificationStatus.Verified, [Evidence("step-1", VerificationStepStatus.Passed)], []);
        var compactResult = CompactMcpResponseFactory.Create(new RepositoryVerificationContext(profile, plan, result));

        Assert.Equal(VerificationStatus.Verified, compactResult.Status);
        var jsonResult = JsonSerializer.Serialize(compactResult, JsonOptions);
        using var docResult = JsonDocument.Parse(jsonResult);
        Assert.Equal("Verified", docResult.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void CapabilityGapsRemainDistinctFromNotRun()
    {
        var profile = AsrpProfile();
        var passed = Evidence("pass-1", VerificationStepStatus.Passed);
        var failed = Evidence("fail-1", VerificationStepStatus.Failed, 1, "Exit 1", "Error message");
        var notRun = Evidence("not-run-1", VerificationStepStatus.NotRun, null, "Prior step failed", string.Empty);
        var evidence = new[] { passed, failed, notRun };
        var plan = new VerificationPlan(evidence.Select(e => e.Step).ToArray(), [], ["ASRP"]);
        var gaps = new[] { new VerificationGap("BROWSER_UNAVAILABLE", "Playwright is not configured.") };
        var result = new VerificationResult(VerificationStatus.NotVerified, evidence, gaps);

        var compact = CompactMcpResponseFactory.Create(new RepositoryVerificationContext(profile, plan, result));

        var gap = Assert.Single(compact.Gaps);
        Assert.Equal("BROWSER_UNAVAILABLE", gap.Code);
        Assert.Equal("Playwright is not configured.", gap.Reason);

        var notRunGroup = Assert.Single(compact.NotRun);
        Assert.Equal("Prior step failed", notRunGroup.Reason);
        Assert.Contains("not-run-1", notRunGroup.StepIds);
        Assert.DoesNotContain("BROWSER_UNAVAILABLE", notRunGroup.StepIds);
    }

    [Fact]
    public void FullDetailModeReturnsAdditionalDiagnosticsWithoutChangingSemantics()
    {
        var profile = AsrpProfile();
        var passed = Evidence("pass-1", VerificationStepStatus.Passed, 0, null, "Build succeeded. 0 Warning(s), 0 Error(s).");
        var failed = Evidence("ASRP-web:node-lint", VerificationStepStatus.Failed, 1, "Process exited with code 1.", "Parsing error: Unexpected keyword or identifier");
        var notRun = Evidence("not-run-1", VerificationStepStatus.NotRun, null, "Prior step failed", string.Empty);
        var evidence = new[] { passed, failed, notRun };
        var plan = new VerificationPlan(evidence.Select(e => e.Step).ToArray(), [], ["ASRP", "ASRP/web"]);
        var gaps = new[] { new VerificationGap("RUNTIME_UNAVAILABLE", "No runtime verifier.") };
        var result = new VerificationResult(VerificationStatus.NotVerified, evidence, gaps);
        var context = new RepositoryVerificationContext(profile, plan, result);

        var compact = CompactMcpResponseFactory.Create(context, "compact");
        var full = CompactMcpResponseFactory.Create(context, "full");

        // Semantics must be identical
        Assert.Equal(compact.Status, full.Status);
        Assert.Equal(compact.Summary, full.Summary);
        Assert.Equal(compact.PassedStepIds, full.PassedStepIds);
        Assert.Equal(compact.FailedSteps.Count, full.FailedSteps.Count);
        Assert.Equal(compact.TimedOutSteps.Count, full.TimedOutSteps.Count);
        Assert.Equal(compact.NotRun.Count, full.NotRun.Count);
        Assert.Equal(compact.Gaps.Count, full.Gaps.Count);
        Assert.Equal(compact.UnsupportedWorkspaces.Count, full.UnsupportedWorkspaces.Count);

        // Compact has no evidence array in JSON
        Assert.Null(compact.Evidence);
        var compactJson = JsonSerializer.Serialize(compact, JsonOptions);
        using var compactDoc = JsonDocument.Parse(compactJson);
        Assert.False(compactDoc.RootElement.TryGetProperty("evidence", out _));

        // Full has evidence array in JSON
        Assert.NotNull(full.Evidence);
        Assert.Equal(3, full.Evidence.Count);
        var fullJson = JsonSerializer.Serialize(full, JsonOptions);
        using var fullDoc = JsonDocument.Parse(fullJson);
        Assert.True(fullDoc.RootElement.TryGetProperty("evidence", out var evidenceProp));
        Assert.Equal(3, evidenceProp.GetArrayLength());

        // Full evidence contains passing step stdout and duration
        var passingEvidence = full.Evidence.Single(e => e.StepId == "pass-1");
        Assert.Equal(VerificationStepStatus.Passed, passingEvidence.Status);
        Assert.Equal(0, passingEvidence.ExitCode);
        Assert.Contains("Build succeeded", passingEvidence.OutputSummary);
        Assert.True(passingEvidence.DurationMs > 0);

        // Full evidence contains failing step diagnostic
        var failingEvidence = full.Evidence.Single(e => e.StepId == "ASRP-web:node-lint");
        Assert.Equal(VerificationStepStatus.Failed, failingEvidence.Status);
        Assert.Equal(1, failingEvidence.ExitCode);
        Assert.Contains("Parsing error", failingEvidence.OutputSummary);

        // Invalid detail level throws
        Assert.Throws<ArgumentException>(() => CompactMcpResponseFactory.Create(context, "invalid"));
    }

    [Fact]
    public void LegacyDeserializationAssumptionsDocumentedAndTested()
    {
        var profile = AsrpProfile();
        var passed = Evidence("pass-1", VerificationStepStatus.Passed, 0, null, "OK");
        var result = new VerificationResult(VerificationStatus.Verified, [passed], []);
        var plan = new VerificationPlan([passed.Step], [], ["ASRP"]);
        var compact = CompactMcpResponseFactory.Create(new RepositoryVerificationContext(profile, plan, result), "compact");

        var compactJson = JsonSerializer.Serialize(compact, JsonOptions);

        // A strict legacy consumer deserializing into domain VerificationResult will receive null/empty evidence
        var legacyDeserialized = JsonSerializer.Deserialize<VerificationResult>(compactJson, JsonOptions);
        Assert.NotNull(legacyDeserialized);
        Assert.Equal(VerificationStatus.Verified, legacyDeserialized.Status);
        // Demonstrates breaking contract change for legacy consumers expecting legacy evidence array:
        Assert.Null(legacyDeserialized.Evidence);

        // A compact consumer deserializing into CompactVerificationResult gets all summary metrics
        var compactDeserialized = JsonSerializer.Deserialize<CompactVerificationResult>(compactJson, JsonOptions);
        Assert.NotNull(compactDeserialized);
        Assert.Equal(VerificationStatus.Verified, compactDeserialized.Status);
        Assert.Equal(1, compactDeserialized.Summary.PassedStepCount);
        Assert.Equal(["pass-1"], compactDeserialized.PassedStepIds);
    }

    private static VerificationEvidence Evidence(
        string id,
        VerificationStepStatus status,
        int? exitCode = 0,
        string? failureReason = null,
        string output = "passed") =>
        new(
            new VerificationStep(
                id,
                id,
                id.StartsWith("ASRP-web", StringComparison.Ordinal) ? "Node" : ".NET",
                new VerificationCommand("dotnet", ["test"]),
                "D:/project/ASRP/ASRP",
                TimeSpan.FromMinutes(5),
                true,
                [EvidenceType.Tests],
                id.StartsWith("ASRP-web", StringComparison.Ordinal) ? "ASRP/web" : "ASRP"),
            status,
            exitCode,
            TimeSpan.FromSeconds(1),
            output,
            failureReason);

    private static RepositoryProfile EducationCmsProfile() => Profile(
        "EducationCMS",
        "D:/project/EducationCMS",
        [
            Workspace(".", [".NET", "Node.js"], dotNetEntryPoint: "EducationCMS.slnx"),
            Workspace("backend", [".NET"], dotNetEntryPoint: "Education.API.slnx"),
            Workspace("frontend", ["Node.js", "TypeScript"])
        ]);

    private static RepositoryProfile AsrpProfile() => Profile(
        "ASRP",
        "D:/project/ASRP",
        [
            Workspace("ASRP", [".NET"], dotNetEntryPoint: "ASRP.slnx"),
            Workspace("ASRP/web", ["Node.js", "TypeScript"]),
            Workspace("ai-service", ["Python"]),
            Workspace("asrp_app", ["Flutter"]),
            Workspace("fe_asrp", ["Node.js", "TypeScript"])
        ]);

    private static RepositoryProfile Profile(string name, string root, IReadOnlyList<RepositoryWorkspace> workspaces) =>
        new(name, root, [".NET", "Node.js"], ["ASP.NET Core", "Next.js"], ["xUnit"], ["npm"], false, true, 1)
        {
            Workspaces = workspaces
        };

    private static RepositoryWorkspace Workspace(string id, IReadOnlyList<string> technologies, string? dotNetEntryPoint = null) =>
        new(id, id, technologies, [], [], null, dotNetEntryPoint, new Dictionary<string, string>());
}
