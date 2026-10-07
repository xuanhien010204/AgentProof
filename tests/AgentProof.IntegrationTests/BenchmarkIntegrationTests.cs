using AgentProof.Benchmarks.Execution;
using AgentProof.Domain;

namespace AgentProof.IntegrationTests;

public sealed class BenchmarkIntegrationTests
{
    [Fact]
    public async Task BenchmarkRunnerExecutesAnalyzeOnlyOverStdioMcp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AgentProof.BenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "package.json"), "{\"name\": \"test-bench\", \"scripts\": {\"test\": \"echo pass\"}}");

            var run = await BenchmarkRunner.RunAsync(
                tempDir,
                scenarioName: "AnalyzeOnly",
                iterations: 1);

            Assert.NotNull(run);
            Assert.Equal("AnalyzeOnly", run.ScenarioName);
            Assert.Single(run.Operations);

            var op = run.Operations[0];
            Assert.Equal("analyze_repository", op.Operation);
            Assert.True(op.Success);
            Assert.True(op.PayloadBytesUtf8 > 0);
            Assert.Equal(op.ToolResultPayloadBytes, op.PayloadBytesUtf8);
            Assert.True(op.MedianDurationMs > 0);
            Assert.Equal(1, op.WorkspaceCount);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task BenchmarkRunnerExecutesPlanningScenarioOverStdioMcp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AgentProof.BenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "package.json"), "{\"name\": \"test-bench\", \"scripts\": {\"test\": \"echo pass\"}}");

            var task = new TaskContext
            {
                Description = "Run benchmark planning",
                Contract = new TaskContract
                {
                    Goal = "Run test script",
                    RequiredEvidence = [EvidenceType.Tests]
                }
            };

            var run = await BenchmarkRunner.RunAsync(
                tempDir,
                scenarioName: "Planning",
                taskContext: task,
                iterations: 1);

            Assert.NotNull(run);
            Assert.Equal("Planning", run.ScenarioName);
            Assert.Equal(2, run.Operations.Count);

            var analyzeOp = run.Operations[0];
            Assert.Equal("analyze_repository", analyzeOp.Operation);
            Assert.True(analyzeOp.Success);

            var planOp = run.Operations[1];
            Assert.Equal("create_verification_plan", planOp.Operation);
            Assert.True(planOp.Success);
            Assert.True(planOp.StepCount >= 1);
            Assert.Equal(0, planOp.GapCount);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
