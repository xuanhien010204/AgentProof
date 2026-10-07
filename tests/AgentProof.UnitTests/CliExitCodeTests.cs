using System.Text.Json;
using AgentProof.Application;
using AgentProof.Cli;
using AgentProof.Domain;

namespace AgentProof.UnitTests;

public sealed class CliExitCodeTests
{
    [Fact]
    public async Task MissingArgumentsReturns1()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await Program.RunAsync([], output, error);
        Assert.Equal(1, exitCode);
        Assert.Contains("AgentProof CLI", error.ToString());
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public async Task HelpOptionReturns0(string helpArg)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await Program.RunAsync([helpArg], output, error);
        Assert.Equal(0, exitCode);
        Assert.Contains("AgentProof CLI", output.ToString());
    }

    [Fact]
    public async Task UnknownCommandReturns1()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await Program.RunAsync(["unknown", "."], output, error);
        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown command", error.ToString());
    }

    [Fact]
    public async Task MissingTaskContextOptionReturns1()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await Program.RunAsync(["verify", "."], output, error);
        Assert.Equal(1, exitCode);
        Assert.Contains("--task-context", error.ToString());
    }

    [Fact]
    public async Task NonExistentTaskContextFileReturns1()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await Program.RunAsync(["verify", ".", "--task-context", "does-not-exist.json"], output, error);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task InvalidTaskContextJsonReturns1()
    {
        using var repo = new TemporaryRepository();
        var badJson = repo.Write("bad.json", "not valid json {");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await Program.RunAsync(["verify", repo.Root, "--task-context", badJson], output, error);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task VerifyReturningVerifiedProducesExitCode0()
    {
        var service = CreateService(new VerificationResult(VerificationStatus.Verified, [], []));
        var exitCode = await RunVerifyWithResult(service);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task VerifyReturningNotVerifiedProducesExitCode2()
    {
        var service = CreateService(new VerificationResult(VerificationStatus.NotVerified, [], []));
        var exitCode = await RunVerifyWithResult(service);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task VerifyReturningPartiallyVerifiedProducesExitCode3()
    {
        var service = CreateService(new VerificationResult(
            VerificationStatus.PartiallyVerified, [], [new VerificationGap("GAP", "Verification gap exists")]));
        var exitCode = await RunVerifyWithResult(service);
        Assert.Equal(3, exitCode);
    }

    private static async Task<int> RunVerifyWithResult(AgentProofService service)
    {
        using var repo = new TemporaryRepository();
        var taskFile = repo.Write("task.json", JsonSerializer.Serialize(new TaskContext { Description = "Test" }));
        using var output = new StringWriter();
        using var error = new StringWriter();
        return await Program.RunAsync(["verify", repo.Root, "--task-context", taskFile], output, error, service);
    }

    private static AgentProofService CreateService(VerificationResult verificationResult)
    {
        return new AgentProofService(
            new StubAnalyzer(),
            new StubRecommender(),
            new StubPlanner(),
            new StubRunner(),
            new StubEvaluator(verificationResult));
    }

    private sealed class StubAnalyzer : IRepositoryAnalyzer
    {
        public Task<RepositoryProfile> AnalyzeAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RepositoryProfile("TestRepo", repositoryPath, [".NET"], [], ["xUnit"], [], false, true, 100));
    }

    private sealed class StubRecommender : ISkillRecommender
    {
        public IReadOnlyList<SkillRecommendation> Recommend(RepositoryProfile repository, TaskContext task) => [];
    }

    private sealed class StubPlanner : IVerificationPlanner
    {
        public Task<VerificationPlan> CreateAsync(RepositoryProfile repository, TaskContext task, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationPlan([], []));
    }

    private sealed class StubRunner : IVerificationRunner
    {
        public Task<VerificationExecutionResult> RunAsync(string repositoryPath, VerificationPlan plan, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationExecutionResult([], []));
    }

    private sealed class StubEvaluator(VerificationResult result) : IEvidenceEvaluator
    {
        public VerificationResult Evaluate(TaskContract contract, VerificationPlan plan, IReadOnlyList<VerificationEvidence> executedEvidence, IReadOnlyList<VerificationGap> executionGaps) =>
            result;
    }
}
