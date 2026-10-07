using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentProof.Benchmarks.Comparison;
using AgentProof.Benchmarks.Execution;
using AgentProof.Benchmarks.Formatting;
using AgentProof.Benchmarks.Model;

namespace AgentProof.UnitTests;

public sealed class BenchmarkHarnessTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void Utf8ByteMeasurementIsAccurateForAsciiAndMultibyteText()
    {
        const string ascii = "Hello, AgentProof!";
        var asciiBytes = Encoding.UTF8.GetByteCount(ascii);
        Assert.Equal(ascii.Length, asciiBytes);

        // Vietnamese text contains multi-byte UTF-8 characters (diacritics)
        const string vietnamese = "Kiểm thử hiệu năng ngữ cảnh AgentProof";
        var vnBytes = Encoding.UTF8.GetByteCount(vietnamese);
        var vnChars = vietnamese.Length;

        Assert.True(vnBytes > vnChars, $"Expected UTF-8 bytes ({vnBytes}) to be strictly greater than character count ({vnChars}) for Vietnamese text.");
        Assert.Equal(49, vnBytes);
        Assert.Equal(38, vnChars);

        // Emojis / surrogate pairs
        const string emojiText = "AgentProof 🚀🛡️";
        var emojiBytes = Encoding.UTF8.GetByteCount(emojiText);
        Assert.True(emojiBytes > emojiText.Length);
    }

    [Theory]
    [InlineData(1000, 700, 30.0)]
    [InlineData(1000, 1000, 0.0)]
    [InlineData(1000, 500, 50.0)]
    [InlineData(1000, 1200, -20.0)]
    [InlineData(0, 500, 0.0)]
    public void PercentageReductionCalculatesExpectedValues(long baseline, long candidate, double expectedReduction)
    {
        var reduction = BenchmarkRunner.CalculatePercentageReduction(baseline, candidate);
        Assert.Equal(expectedReduction, reduction, precision: 2);
    }

    [Theory]
    [InlineData(200.0, 180.0, -10.0)]
    [InlineData(200.0, 220.0, 10.0)]
    [InlineData(100.0, 100.0, 0.0)]
    [InlineData(0.0, 100.0, 0.0)]
    public void PercentageChangeCalculatesExpectedValues(double baseline, double candidate, double expectedChange)
    {
        var change = BenchmarkRunner.CalculatePercentageChange(baseline, candidate);
        Assert.Equal(expectedChange, change, precision: 2);
    }

    [Fact]
    public void MedianDurationCalculationIsAccurate()
    {
        Assert.Equal(200.0, BenchmarkRunner.CalculateMedian([100.0, 200.0, 300.0]));
        Assert.Equal(250.0, BenchmarkRunner.CalculateMedian([100.0, 200.0, 300.0, 400.0]));
        Assert.Equal(150.0, BenchmarkRunner.CalculateMedian([150.0]));
        Assert.Equal(0.0, BenchmarkRunner.CalculateMedian([]));
    }

    [Fact]
    public void ComparisonDetectsVerificationStatusRegressionAsFail()
    {
        var baseline = CreateSampleRun("Verified", payloadBytes: 1000, gaps: 0, evidence: 3);
        var candidate = CreateSampleRun("NotVerified", payloadBytes: 500, gaps: 0, evidence: 3);

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("Verification status regressed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsNewGapsAsFail()
    {
        var baseline = CreateSampleRun("Verified", payloadBytes: 1000, gaps: 0, evidence: 3);
        var candidate = CreateSampleRun("Verified", payloadBytes: 500, gaps: 2, evidence: 3);

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("new gap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsEvidenceCountRegressionAsFail()
    {
        var baseline = CreateSampleRun("Verified", payloadBytes: 1000, gaps: 0, evidence: 4);
        var candidate = CreateSampleRun("Verified", payloadBytes: 400, gaps: 0, evidence: 2);

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("Evidence count regressed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsPayloadIncreaseAsFail()
    {
        var baseline = CreateSampleRun("Verified", payloadBytes: 1000, gaps: 0, evidence: 3);
        var candidate = CreateSampleRun("Verified", payloadBytes: 1500, gaps: 0, evidence: 3);

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("payload increased", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonFlagsWorkspaceCountChangeAsWarning()
    {
        var baseline = CreateSampleRun("Verified", payloadBytes: 1000, gaps: 0, evidence: 3, workspaces: 3);
        var candidate = CreateSampleRun("Verified", payloadBytes: 600, gaps: 0, evidence: 3, workspaces: 4);

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Warning, comparison.Verdict);
        Assert.Empty(comparison.Failures);
        Assert.Contains(comparison.Warnings, w => w.Contains("Workspace count changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonPreservesPassWhenCandidateShrinksWithoutRegressions()
    {
        var baseline = CreateSampleRun("Verified", payloadBytes: 10000, gaps: 0, evidence: 4, workspaces: 2);
        var candidate = CreateSampleRun("Verified", payloadBytes: 3000, gaps: 0, evidence: 4, workspaces: 2);

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Pass, comparison.Verdict);
        Assert.Empty(comparison.Failures);
        Assert.Empty(comparison.Warnings);
        Assert.Equal(70.0, comparison.TotalPayloadReductionPercent, precision: 2);
    }

    [Fact]
    public void BenchmarkRunJsonSerializationRoundTripIsStable()
    {
        var run = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            RepositoryPath = "/path/to/TestRepo",
            ScenarioName = "FullVerification",
            TimestampUtc = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero),
            AgentProofVersion = "0.3.0-preview.2",
            GitCommitSha = "abc1234",
            Iterations = 3,
            TotalPayloadBytesUtf8 = 15000,
            TotalDurationMs = 240.5,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "analyze_repository",
                    MinDurationMs = 50.0,
                    MedianDurationMs = 55.0,
                    MaxDurationMs = 60.0,
                    PayloadBytesUtf8 = 5000,
                    PayloadCharacters = 4900,
                    JsonRpcPayloadBytes = 5200,
                    ToolResultPayloadBytes = 5000,
                    IsDeterministicPayload = true,
                    PayloadBytesHistory = [5000, 5000, 5000],
                    Success = true,
                    WorkspaceCount = 2,
                    TechnologyCount = 3,
                    FrameworkCount = 2,
                    TestFrameworkCount = 1
                },
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    MinDurationMs = 180.0,
                    MedianDurationMs = 185.5,
                    MaxDurationMs = 190.0,
                    PayloadBytesUtf8 = 10000,
                    PayloadCharacters = 9800,
                    JsonRpcPayloadBytes = 10300,
                    ToolResultPayloadBytes = 10000,
                    IsDeterministicPayload = true,
                    PayloadBytesHistory = [10000, 10000, 10000],
                    Success = true,
                    VerificationStatus = "Verified",
                    EvidenceCount = 4,
                    GapCount = 0,
                    PassedStepCount = 4,
                    FailedStepCount = 0,
                    TimedOutStepCount = 0
                }
            ]
        };

        var json = JsonSerializer.Serialize(run, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<BenchmarkRun>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(run.RepositoryName, deserialized.RepositoryName);
        Assert.Equal(run.RepositoryPath, deserialized.RepositoryPath);
        Assert.Equal(run.ScenarioName, deserialized.ScenarioName);
        Assert.Equal(run.TimestampUtc, deserialized.TimestampUtc);
        Assert.Equal(run.AgentProofVersion, deserialized.AgentProofVersion);
        Assert.Equal(run.GitCommitSha, deserialized.GitCommitSha);
        Assert.Equal(run.Iterations, deserialized.Iterations);
        Assert.Equal(run.TotalPayloadBytesUtf8, deserialized.TotalPayloadBytesUtf8);
        Assert.Equal(run.TotalDurationMs, deserialized.TotalDurationMs);
        Assert.Equal(run.Operations.Count, deserialized.Operations.Count);

        var op0 = deserialized.Operations[0];
        Assert.Equal("analyze_repository", op0.Operation);
        Assert.Equal(5000, op0.PayloadBytesUtf8);
        Assert.Equal(2, op0.WorkspaceCount);

        var op1 = deserialized.Operations[1];
        Assert.Equal("verify", op1.Operation);
        Assert.Equal("Verified", op1.VerificationStatus);
        Assert.Equal(4, op1.EvidenceCount);
        Assert.Equal(0, op1.GapCount);
    }

    [Fact]
    public void BenchmarkComparisonJsonSerializationRoundTripIsStable()
    {
        var baseline = CreateSampleRun("Verified", payloadBytes: 10000, gaps: 0, evidence: 4);
        var candidate = CreateSampleRun("Verified", payloadBytes: 3000, gaps: 0, evidence: 4);
        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        var json = JsonSerializer.Serialize(comparison, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<BenchmarkComparison>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(ComparisonVerdict.Pass, deserialized.Verdict);
        Assert.Equal(70.0, deserialized.TotalPayloadReductionPercent, precision: 2);
        Assert.Single(deserialized.Operations);
    }

    [Fact]
    public void FormatterGeneratesValidMarkdownTable()
    {
        var run = CreateSampleRun("Verified", payloadBytes: 8000, gaps: 0, evidence: 3, workspaces: 2);
        var markdown = BenchmarkFormatter.FormatRunToMarkdown(run);

        Assert.Contains("# AgentProof Context Benchmark", markdown);
        Assert.Contains("| Operation | Payload bytes | Duration ms | Workspaces | Steps | Evidence | Gaps | Status |", markdown);
        Assert.Contains("verify", markdown);
        Assert.Contains("Verified", markdown);
        Assert.Contains("Payload reduction is a context-efficiency proxy", markdown);
    }

    private static BenchmarkRun CreateSampleRun(
        string verificationStatus,
        long payloadBytes,
        int gaps,
        int evidence,
        int workspaces = 1)
    {
        return new BenchmarkRun
        {
            RepositoryName = "SampleRepo",
            RepositoryPath = "/test/SampleRepo",
            ScenarioName = "FullVerification",
            TimestampUtc = DateTimeOffset.UtcNow,
            AgentProofVersion = "0.3.0-preview.2",
            GitCommitSha = "testsha",
            Iterations = 1,
            TotalPayloadBytesUtf8 = payloadBytes,
            TotalDurationMs = 150.0,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    MinDurationMs = 150.0,
                    MedianDurationMs = 150.0,
                    MaxDurationMs = 150.0,
                    PayloadBytesUtf8 = payloadBytes,
                    PayloadCharacters = (int)payloadBytes,
                    JsonRpcPayloadBytes = payloadBytes + 100,
                    ToolResultPayloadBytes = payloadBytes,
                    IsDeterministicPayload = true,
                    PayloadBytesHistory = [payloadBytes],
                    Success = true,
                    WorkspaceCount = workspaces,
                    StepCount = 3,
                    EvidenceCount = evidence,
                    GapCount = gaps,
                    VerificationStatus = verificationStatus,
                    PassedStepCount = verificationStatus == "Verified" ? evidence : 0,
                    FailedStepCount = verificationStatus == "Verified" ? 0 : 1,
                    TimedOutStepCount = 0
                }
            ]
        };
    }
}
