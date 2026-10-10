using AgentProof.Application;
using AgentProof.Domain;
using AgentProof.Mcp;

namespace AgentProof.UnitTests;

public sealed class McpInputValidationTests
{
    [Theory]
    [InlineData("compact", "compact")]
    [InlineData("COMPACT", "compact")]
    [InlineData("Compact", "compact")]
    [InlineData("  compact  ", "compact")]
    [InlineData("full", "full")]
    [InlineData("FULL", "full")]
    [InlineData("Full", "full")]
    [InlineData("  full  ", "full")]
    public void ValidateDetailLevelAcceptsCaseInsensitiveValidInputs(string input, string expected)
    {
        var result = AgentProofTools.ValidateDetailLevel(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid")]
    [InlineData("detailed")]
    [InlineData("summary")]
    [InlineData("verbose")]
    public void ValidateDetailLevelRejectsInvalidOrEmptyInput(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => AgentProofTools.ValidateDetailLevel(input));
        Assert.Contains("Expected 'compact' or 'full'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDetailLevelRejectsNullInput()
    {
        Assert.Throws<ArgumentNullException>(() => AgentProofTools.ValidateDetailLevel(null));
    }

    [Fact]
    public async Task InvalidDetailLevelThrowsBeforeVerificationRunnerIsInvoked()
    {
        var (tools, fakeRunner) = CreateToolsWithFakeRunner();
        var task = new TaskContext { Description = "Test task" };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => tools.VerifyAsync("D:/dummy/repo", task, detailLevel: "unsupported_level"));

        Assert.Equal(0, fakeRunner.ExecutionCount);
        Assert.Contains("Unsupported detail level", ex.Message);
    }

    [Fact]
    public async Task EmptyOrWhitespaceDetailLevelThrowsBeforeVerificationRunnerIsInvoked()
    {
        var (tools, fakeRunner) = CreateToolsWithFakeRunner();
        var task = new TaskContext { Description = "Test task" };

        await Assert.ThrowsAsync<ArgumentException>(
            () => tools.VerifyAsync("D:/dummy/repo", task, detailLevel: ""));
        Assert.Equal(0, fakeRunner.ExecutionCount);

        await Assert.ThrowsAsync<ArgumentException>(
            () => tools.VerifyAsync("D:/dummy/repo", task, detailLevel: "   "));
        Assert.Equal(0, fakeRunner.ExecutionCount);
    }

    [Fact]
    public async Task NullDetailLevelThrowsBeforeVerificationRunnerIsInvoked()
    {
        var (tools, fakeRunner) = CreateToolsWithFakeRunner();
        var task = new TaskContext { Description = "Test task" };

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => tools.VerifyAsync("D:/dummy/repo", task, detailLevel: null!));
        Assert.Equal(0, fakeRunner.ExecutionCount);
    }

    [Fact]
    public async Task DefaultCompactModeExecutesRunnerAndReturnsCompactPayload()
    {
        var (tools, fakeRunner) = CreateToolsWithFakeRunner();
        var task = new TaskContext { Description = "Test task" };

        var result = await tools.VerifyAsync("D:/dummy/repo", task);

        Assert.Equal(1, fakeRunner.ExecutionCount);
        Assert.Equal(1, result.SchemaVersion);
        Assert.Equal(VerificationStatus.Verified, result.Status);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task FullModeExecutesRunnerAndPopulatesEvidenceWithoutAlteringSemantics()
    {
        var (tools, fakeRunner) = CreateToolsWithFakeRunner();
        var task = new TaskContext { Description = "Test task" };

        var compactResult = await tools.VerifyAsync("D:/dummy/repo", task, detailLevel: "compact");
        var fullResult = await tools.VerifyAsync("D:/dummy/repo", task, detailLevel: "full");

        Assert.Equal(2, fakeRunner.ExecutionCount);

        // Core semantics must be identical
        Assert.Equal(compactResult.Status, fullResult.Status);
        Assert.Equal(compactResult.Summary, fullResult.Summary);
        Assert.Equal(compactResult.PassedStepIds, fullResult.PassedStepIds);
        Assert.Equal(compactResult.FailedSteps.Count, fullResult.FailedSteps.Count);
        Assert.Equal(compactResult.TimedOutSteps.Count, fullResult.TimedOutSteps.Count);
        Assert.Equal(compactResult.NotRun.Count, fullResult.NotRun.Count);
        Assert.Equal(compactResult.Gaps.Count, fullResult.Gaps.Count);

        // Compact has null evidence, full has populated evidence
        Assert.Null(compactResult.Evidence);
        Assert.NotNull(fullResult.Evidence);
        Assert.Equal(fullResult.Summary.EvidenceCount, fullResult.Evidence.Count);
    }

    private static (AgentProofTools Tools, FakeVerificationRunner Runner) CreateToolsWithFakeRunner()
    {
        var step = new VerificationStep(
            "step-1", "Build", ".NET",
            new VerificationCommand("dotnet", ["build"]),
            "D:/dummy/repo", TimeSpan.FromMinutes(1), true, [EvidenceType.Build], ".");
        var plan = new VerificationPlan([step], [], ["."]);
        var profile = new RepositoryProfile("dummy", "D:/dummy/repo", [".NET"], [], [], [], false, false, 0)
        {
            Workspaces = [new RepositoryWorkspace(".", ".", [".NET"], [], [], null, null, new Dictionary<string, string>())]
        };

        var fakeAnalyzer = new FakeRepositoryAnalyzer(profile);
        var fakePlanner = new FakeVerificationPlanner(plan);
        var fakeRunner = new FakeVerificationRunner(step);
        var service = new AgentProofService(
            fakeAnalyzer,
            new DeterministicSkillRecommender(),
            fakePlanner,
            fakeRunner);

        return (new AgentProofTools(service), fakeRunner);
    }

    private sealed class FakeRepositoryAnalyzer(RepositoryProfile profile) : IRepositoryAnalyzer
    {
        public Task<RepositoryProfile> AnalyzeAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(profile);
    }

    private sealed class FakeVerificationPlanner(VerificationPlan plan) : IVerificationPlanner
    {
        public Task<VerificationPlan> CreateAsync(RepositoryProfile repository, TaskContext task, CancellationToken cancellationToken = default) =>
            Task.FromResult(plan);
    }

    private sealed class FakeVerificationRunner(VerificationStep step) : IVerificationRunner
    {
        public int ExecutionCount { get; private set; }

        public Task<VerificationExecutionResult> RunAsync(string repositoryPath, VerificationPlan plan, CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            var evidence = new VerificationEvidence(
                step, VerificationStepStatus.Passed, 0, TimeSpan.FromSeconds(1), "Build succeeded.", null);
            return Task.FromResult(new VerificationExecutionResult([evidence], []));
        }
    }
}
