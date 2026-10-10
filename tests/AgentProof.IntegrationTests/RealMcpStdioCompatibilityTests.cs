using System.Text.Json;
using System.Text.Json.Serialization;
using AgentProof.Benchmarks.Execution;
using AgentProof.Domain;
using AgentProof.Mcp;

namespace AgentProof.IntegrationTests;

public sealed class RealMcpStdioCompatibilityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [DogfoodFact("AGENTPROOF_EDUCATION_REPO")]
    public async Task EducationCmsRealMcpStdioWorkflowVerifiesSuccessfully()
    {
        var educationRepoPath = GetEducationRepoPath();
        if (string.IsNullOrWhiteSpace(educationRepoPath) || !Directory.Exists(educationRepoPath))
        {
            Assert.Fail("AGENTPROOF_EDUCATION_REPO environment variable was not provided or points to a non-existent path.");
            return;
        }

        var taskJsonPath = ResolveTaskJsonPath("benchmarks/tasks/education-fullstack.json");
        var taskContent = await File.ReadAllTextAsync(taskJsonPath);
        var taskContext = JsonSerializer.Deserialize<TaskContext>(taskContent, JsonOptions)
            ?? throw new InvalidOperationException("Failed to load education task context.");

        await using var client = await McpStdioClient.StartAsync();

        // 1 & 2. initialize & tools/list
        var listResult = await client.BenchmarkInitializeAndListToolsAsync();
        Assert.True(listResult.Success);

        // 3. analyze_repository
        var analyze = await client.CallToolAsync("analyze_repository", new { repositoryPath = educationRepoPath });
        Assert.True(analyze.Success);
        var profile = JsonSerializer.Deserialize<CompactRepositoryProfile>(analyze.ToolResultText, JsonOptions)!;
        Assert.Equal(1, profile.SchemaVersion);
        Assert.Equal(3, profile.WorkspaceCount);
        Assert.Equal([".", "backend", "frontend"], profile.Workspaces.Select(w => w.Id).ToArray());

        // 4. recommend_skills
        var recsResult = await client.CallToolAsync("recommend_skills", new { repositoryPath = educationRepoPath, taskContext });
        Assert.True(recsResult.Success);
        var recs = JsonSerializer.Deserialize<List<CompactSkillRecommendation>>(recsResult.ToolResultText, JsonOptions)!;
        Assert.NotEmpty(recs);
        Assert.All(recs, r => Assert.Equal(1, r.SchemaVersion));

        // 5. create_verification_plan
        var planResult = await client.CallToolAsync("create_verification_plan", new { repositoryPath = educationRepoPath, taskContext });
        Assert.True(planResult.Success);
        var plan = JsonSerializer.Deserialize<CompactVerificationPlan>(planResult.ToolResultText, JsonOptions)!;
        Assert.Equal(1, plan.SchemaVersion);
        Assert.Equal(14, plan.PlannedStepCount);
        Assert.Equal(3, plan.WorkspaceIds.Count);

        // 6. verify (compact default)
        var verifyResult = await client.CallToolAsync("verify", new { repositoryPath = educationRepoPath, taskContext });
        Assert.True(verifyResult.Success);
        var vResult = JsonSerializer.Deserialize<CompactVerificationResult>(verifyResult.ToolResultText, JsonOptions)!;
        Assert.Equal(1, vResult.SchemaVersion);
        Assert.Equal(VerificationStatus.Verified, vResult.Status);
        Assert.Equal(14, vResult.Summary.PlannedStepCount);
        Assert.Equal(14, vResult.Summary.PassedStepCount);
        Assert.Equal(0, vResult.Summary.FailedStepCount);
        Assert.Equal(14, vResult.PassedStepIds.Count);
        Assert.Null(vResult.Evidence);

        using var verifyDoc = JsonDocument.Parse(verifyResult.ToolResultText);
        Assert.False(verifyDoc.RootElement.TryGetProperty("evidence", out _));

        // 7. verify (full mode detail retrieval)
        var verifyFullResult = await client.CallToolAsync("verify", new { repositoryPath = educationRepoPath, taskContext, detailLevel = "full" });
        Assert.True(verifyFullResult.Success);
        var vFullResult = JsonSerializer.Deserialize<CompactVerificationResult>(verifyFullResult.ToolResultText, JsonOptions)!;
        Assert.Equal(1, vFullResult.SchemaVersion);
        Assert.Equal(VerificationStatus.Verified, vFullResult.Status);
        Assert.Equal(14, vFullResult.Summary.PlannedStepCount);
        Assert.NotNull(vFullResult.Evidence);
        Assert.Equal(14, vFullResult.Evidence.Count);
        Assert.All(vFullResult.Evidence, e =>
        {
            Assert.Equal(VerificationStepStatus.Passed, e.Status);
            Assert.Equal(0, e.ExitCode);
            Assert.True(e.DurationMs >= 0);
        });
    }

    [DogfoodFact("AGENTPROOF_ASRP_REPO")]
    public async Task AsrpRealMcpStdioWorkflowPreservesWorkspacesFailuresAndUnsupportedVerifiers()
    {
        var asrpRepoPath = GetAsrpRepoPath();
        if (string.IsNullOrWhiteSpace(asrpRepoPath) || !Directory.Exists(asrpRepoPath))
        {
            Assert.Fail("AGENTPROOF_ASRP_REPO environment variable was not provided or points to a non-existent path.");
            return;
        }

        var taskJsonPath = ResolveTaskJsonPath("benchmarks/tasks/asrp-fullstack.json");
        var taskContent = await File.ReadAllTextAsync(taskJsonPath);
        var taskContext = JsonSerializer.Deserialize<TaskContext>(taskContent, JsonOptions)
            ?? throw new InvalidOperationException("Failed to load ASRP task context.");

        await using var client = await McpStdioClient.StartAsync();

        // 1 & 2. initialize & tools/list
        var listResult = await client.BenchmarkInitializeAndListToolsAsync();
        Assert.True(listResult.Success);

        // 3. analyze_repository
        var analyze = await client.CallToolAsync("analyze_repository", new { repositoryPath = asrpRepoPath });
        Assert.True(analyze.Success);
        var profile = JsonSerializer.Deserialize<CompactRepositoryProfile>(analyze.ToolResultText, JsonOptions)!;
        Assert.Equal(1, profile.SchemaVersion);
        Assert.Equal(5, profile.WorkspaceCount);
        Assert.Equal(["ASRP", "ASRP/web", "ai-service", "asrp_app", "fe_asrp"], profile.Workspaces.Select(w => w.Id).ToArray());
        Assert.Equal("No active verifier is available for: Python.", profile.Workspaces.Single(w => w.Id == "ai-service").UnsupportedVerifierReason);
        Assert.Equal("No active verifier is available for: Flutter.", profile.Workspaces.Single(w => w.Id == "asrp_app").UnsupportedVerifierReason);

        // 4. recommend_skills
        var recsResult = await client.CallToolAsync("recommend_skills", new { repositoryPath = asrpRepoPath, taskContext });
        Assert.True(recsResult.Success);
        var recs = JsonSerializer.Deserialize<List<CompactSkillRecommendation>>(recsResult.ToolResultText, JsonOptions)!;
        Assert.NotEmpty(recs);

        // 5. create_verification_plan
        var planResult = await client.CallToolAsync("create_verification_plan", new { repositoryPath = asrpRepoPath, taskContext });
        Assert.True(planResult.Success);
        var plan = JsonSerializer.Deserialize<CompactVerificationPlan>(planResult.ToolResultText, JsonOptions)!;
        Assert.Equal(1, plan.SchemaVersion);
        Assert.Equal(9, plan.PlannedStepCount);
        Assert.Equal(2, plan.UnsupportedWorkspaces.Count);
        Assert.Contains(plan.UnsupportedWorkspaces, u => u.WorkspaceId == "ai-service" && u.Technologies.Contains("Python"));
        Assert.Contains(plan.UnsupportedWorkspaces, u => u.WorkspaceId == "asrp_app" && u.Technologies.Contains("Flutter"));

        // 6. verify (compact default)
        var verifyResult = await client.CallToolAsync("verify", new { repositoryPath = asrpRepoPath, taskContext });
        Assert.True(verifyResult.Success);
        var vResult = JsonSerializer.Deserialize<CompactVerificationResult>(verifyResult.ToolResultText, JsonOptions)!;
        Assert.Equal(1, vResult.SchemaVersion);
        Assert.Equal(VerificationStatus.NotVerified, vResult.Status);
        Assert.Equal(9, vResult.Summary.PlannedStepCount);
        Assert.Equal(4, vResult.Summary.ExecutedStepCount);
        Assert.Equal(3, vResult.Summary.PassedStepCount);
        Assert.Equal(1, vResult.Summary.FailedStepCount);
        Assert.Equal(0, vResult.Summary.TimedOutStepCount);
        Assert.Equal(5, vResult.Summary.NotRunStepCount);

        var failed = Assert.Single(vResult.FailedSteps);
        Assert.Equal("ASRP-web:node-lint", failed.StepId);
        Assert.Equal(1, failed.ExitCode);
        Assert.Contains("code 1", failed.FailureReason);
        Assert.Contains("Parsing error: Unexpected keyword or identifier", failed.OutputSummary);

        var notRun = Assert.Single(vResult.NotRun);
        Assert.Equal(5, notRun.StepIds.Count);

        Assert.Equal(2, vResult.UnsupportedWorkspaces.Count);
        Assert.Contains(vResult.UnsupportedWorkspaces, u => u.WorkspaceId == "ai-service");
        Assert.Contains(vResult.UnsupportedWorkspaces, u => u.WorkspaceId == "asrp_app");
        Assert.Null(vResult.Evidence);

        // 7. verify (full mode detail retrieval)
        var verifyFullResult = await client.CallToolAsync("verify", new { repositoryPath = asrpRepoPath, taskContext, detailLevel = "full" });
        Assert.True(verifyFullResult.Success);
        var vFullResult = JsonSerializer.Deserialize<CompactVerificationResult>(verifyFullResult.ToolResultText, JsonOptions)!;
        Assert.Equal(VerificationStatus.NotVerified, vFullResult.Status);
        Assert.Equal(vResult.Summary, vFullResult.Summary);
        Assert.NotNull(vFullResult.Evidence);
        Assert.Equal(9, vFullResult.Evidence.Count);

        var failedEvidence = vFullResult.Evidence.Single(e => e.StepId == "ASRP-web:node-lint");
        Assert.Equal(VerificationStepStatus.Failed, failedEvidence.Status);
        Assert.Equal(1, failedEvidence.ExitCode);
        Assert.Contains("Parsing error", failedEvidence.OutputSummary);
    }

    private static string? GetEducationRepoPath()
    {
        var env = Environment.GetEnvironmentVariable("AGENTPROOF_EDUCATION_REPO");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        return null;
    }

    private static string? GetAsrpRepoPath()
    {
        var env = Environment.GetEnvironmentVariable("AGENTPROOF_ASRP_REPO");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        return null;
    }

    private static string ResolveTaskJsonPath(string relativePath)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var candidate = Path.Combine(current, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }
        return Path.GetFullPath(relativePath);
    }
}
