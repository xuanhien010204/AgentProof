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

    [Fact]
    public void ComparisonDetectsDifferentWorkspaceIdsWithSameCountAsFailure()
    {
        var baseline = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.2",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "analyze_repository",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    WorkspaceCount = 2,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        Workspaces =
                        [
                            new WorkspaceSemanticSnapshot("backend", "src/Backend", ["C#"], [".NET"], "dotnet", "Backend.csproj", null),
                            new WorkspaceSemanticSnapshot("frontend", "src/Frontend", ["TypeScript"], ["React"], "npm", null, null)
                        ]
                    }
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.3",
            TotalPayloadBytesUtf8 = 800,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "analyze_repository",
                    PayloadBytesUtf8 = 800,
                    Success = true,
                    WorkspaceCount = 2,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        Workspaces =
                        [
                            new WorkspaceSemanticSnapshot("backend", "src/Backend", ["C#"], [".NET"], "dotnet", "Backend.csproj", null),
                            new WorkspaceSemanticSnapshot("worker", "src/Worker", ["C#"], [".NET"], "dotnet", "Worker.csproj", null)
                        ]
                    }
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("Workspace IDs mismatch", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(comparison.Failures, f => f.Contains("frontend", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsChangedCommandWithSameStepCountAsFailure()
    {
        var baseline = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.2",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "create_verification_plan",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    StepCount = 1,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        PlannedSteps =
                        [
                            new PlanStepSemanticSnapshot("step-1", "backend", "Build", ["dotnet", "build"], true, ["Build"])
                        ]
                    }
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.3",
            TotalPayloadBytesUtf8 = 800,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "create_verification_plan",
                    PayloadBytesUtf8 = 800,
                    Success = true,
                    StepCount = 1,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        PlannedSteps =
                        [
                            new PlanStepSemanticSnapshot("step-1", "backend", "Build", ["dotnet", "test"], true, ["Build"])
                        ]
                    }
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("command changed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(comparison.Failures, f => f.Contains("dotnet test", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsMissingFailureReasonWithSameStatusAsFailure()
    {
        var baseline = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.2",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    VerificationStatus = "NotVerified",
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        VerificationStatus = "NotVerified",
                        FailedSteps =
                        [
                            new StepFailureSemanticSnapshot("step-1", "backend", "Build", ["dotnet", "build"], 1, "Compilation error CS1002")
                        ]
                    }
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.3",
            TotalPayloadBytesUtf8 = 800,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 800,
                    Success = true,
                    VerificationStatus = "NotVerified",
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        VerificationStatus = "NotVerified",
                        FailedSteps =
                        [
                            new StepFailureSemanticSnapshot("step-1", "backend", "Build", ["dotnet", "build"], 1, null)
                        ]
                    }
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("failure reason changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonPreservesEquivalenceWhenWorkspacesAndEvidenceAreReordered()
    {
        var baseline = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.2",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "analyze_repository",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    WorkspaceCount = 2,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        Workspaces =
                        [
                            new WorkspaceSemanticSnapshot("backend", "src/Backend", ["C#"], [".NET"], "dotnet", "Backend.csproj", null),
                            new WorkspaceSemanticSnapshot("frontend", "src/Frontend", ["TypeScript"], ["React"], "npm", null, null)
                        ]
                    }
                },
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    VerificationStatus = "Verified",
                    EvidenceCount = 2,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        VerificationStatus = "Verified",
                        PassedStepIds = ["step-1", "step-2"],
                        Summary = new AgentProof.Mcp.CompactVerificationSummary(2, 2, 2, 2, 0, 0, 0)
                    }
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.3",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "analyze_repository",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    WorkspaceCount = 2,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        Workspaces =
                        [
                            new WorkspaceSemanticSnapshot("frontend", "src/Frontend", ["TypeScript"], ["React"], "npm", null, null),
                            new WorkspaceSemanticSnapshot("backend", "src/Backend", ["C#"], [".NET"], "dotnet", "Backend.csproj", null)
                        ]
                    }
                },
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    VerificationStatus = "Verified",
                    EvidenceCount = 2,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        VerificationStatus = "Verified",
                        PassedStepIds = ["step-2", "step-1"],
                        Summary = new AgentProof.Mcp.CompactVerificationSummary(2, 2, 2, 2, 0, 0, 0)
                    }
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Pass, comparison.Verdict);
        Assert.Empty(comparison.Failures);
    }

    [Fact]
    public void ComparisonDetectsIncreasedGapCountOrMissingEvidenceAsFailure()
    {
        var baseline = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.2",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    VerificationStatus = "Verified",
                    EvidenceCount = 3,
                    GapCount = 0,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        VerificationStatus = "Verified",
                        PassedStepIds = ["step-1", "step-2", "step-3"],
                        VerificationGaps = [],
                        Summary = new AgentProof.Mcp.CompactVerificationSummary(3, 3, 3, 3, 0, 0, 0)
                    }
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.3",
            TotalPayloadBytesUtf8 = 800,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 800,
                    Success = true,
                    VerificationStatus = "Verified",
                    EvidenceCount = 2,
                    GapCount = 1,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        VerificationStatus = "Verified",
                        PassedStepIds = ["step-1", "step-2"],
                        VerificationGaps = [new GapSemanticSnapshot("COVERAGE_DEFICIT", "Missing frontend tests")],
                        Summary = new AgentProof.Mcp.CompactVerificationSummary(3, 2, 2, 2, 0, 0, 1)
                    }
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("Evidence count regressed", StringComparison.OrdinalIgnoreCase) ||
                                                 f.Contains("new gap", StringComparison.OrdinalIgnoreCase) ||
                                                 f.Contains("COVERAGE_DEFICIT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonHandlesBackwardCompatibilityWithScalarOnlyArtifactsGracefully()
    {
        // Older scalar-only artifacts have NO SemanticSnapshot and NO ToolResultText
        var baseline = new BenchmarkRun
        {
            RepositoryName = "SampleRepo",
            AgentProofVersion = "0.2.0",
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    WorkspaceCount = 2,
                    EvidenceCount = 3,
                    VerificationStatus = "Verified"
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "SampleRepo",
            AgentProofVersion = "0.3.0-preview.3",
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 800,
                    Success = true,
                    WorkspaceCount = 2,
                    EvidenceCount = 3,
                    VerificationStatus = "Verified"
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.NotEqual(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Empty(comparison.Failures);
        Assert.Contains(comparison.Warnings, w => w.Contains("scalar", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsVerifiedToPartiallyVerifiedAsFail()
    {
        var baseline = CreateSampleRun("Verified", payloadBytes: 1000, gaps: 0, evidence: 3);
        var candidate = CreateSampleRun("PartiallyVerified", payloadBytes: 800, gaps: 1, evidence: 2);

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("Verification status regressed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsStepCountReductionAsFail()
    {
        var baseline = CreateSampleRun("Verified", payloadBytes: 1000, gaps: 0, evidence: 3);
        var candidate = CreateSampleRun("Verified", payloadBytes: 800, gaps: 0, evidence: 3, stepCountOverride: 2);

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("Step count decreased", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsDifferentGapCodesAsFail()
    {
        var baseline = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.2",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    VerificationStatus = "PartiallyVerified",
                    GapCount = 1,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        VerificationStatus = "PartiallyVerified",
                        VerificationGaps = [new GapSemanticSnapshot("MISSING_EVIDENCE_TESTS", "No tests found")]
                    }
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.3",
            TotalPayloadBytesUtf8 = 800,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 800,
                    Success = true,
                    VerificationStatus = "PartiallyVerified",
                    GapCount = 1,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        VerificationStatus = "PartiallyVerified",
                        VerificationGaps = [new GapSemanticSnapshot("UNSUPPORTED_TECHNOLOGY", "Python not supported")]
                    }
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("new verification gap code(s)", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(comparison.Failures, f => f.Contains("UNSUPPORTED_TECHNOLOGY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsDifferentUnsupportedVerifiersAsFail()
    {
        var baseline = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.2",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "analyze_repository",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    WorkspaceCount = 1,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        Workspaces =
                        [
                            new WorkspaceSemanticSnapshot("ai-service", "src/ai", ["Python"], ["FastAPI"], null, null, "Python verification is not currently supported.")
                        ]
                    }
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.3",
            TotalPayloadBytesUtf8 = 800,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "analyze_repository",
                    PayloadBytesUtf8 = 800,
                    Success = true,
                    WorkspaceCount = 1,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        Workspaces =
                        [
                            new WorkspaceSemanticSnapshot("ai-service", "src/ai", ["Python"], ["FastAPI"], null, null, "Flutter verification is not currently supported.")
                        ]
                    }
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("unsupported verifier reason mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsSkillIdChangeWithIdenticalCountAsFail()
    {
        var baseline = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.2",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "recommend_skills",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        SkillRecommendations =
                        [
                            new SkillRecommendationSemanticSnapshot("dotnet-build", "Recommended", "DOTNET_SDK", "Project uses .NET"),
                            new SkillRecommendationSemanticSnapshot("dotnet-test", "Recommended", "TESTS", "Project has test project")
                        ]
                    }
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.3",
            TotalPayloadBytesUtf8 = 800,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "recommend_skills",
                    PayloadBytesUtf8 = 800,
                    Success = true,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        SkillRecommendations =
                        [
                            new SkillRecommendationSemanticSnapshot("dotnet-build", "Recommended", "DOTNET_SDK", "Project uses .NET"),
                            new SkillRecommendationSemanticSnapshot("npm-test", "Recommended", "TESTS", "Project has test project")
                        ]
                    }
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("Recommended skill IDs mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsSkillRecommendationLevelChangeAsFail()
    {
        var baseline = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.2",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "recommend_skills",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        SkillRecommendations =
                        [
                            new SkillRecommendationSemanticSnapshot("dotnet-test", "Recommended", "TESTS", "Project has test project")
                        ]
                    }
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.3",
            TotalPayloadBytesUtf8 = 800,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "recommend_skills",
                    PayloadBytesUtf8 = 800,
                    Success = true,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        SkillRecommendations =
                        [
                            new SkillRecommendationSemanticSnapshot("dotnet-test", "Optional", "TESTS", "Project has test project")
                        ]
                    }
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("recommendation level mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsFailedStepIdChangeWithIdenticalFailureCountAsFail()
    {
        var baseline = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.2",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    VerificationStatus = "NotVerified",
                    FailedStepCount = 1,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        VerificationStatus = "NotVerified",
                        FailedSteps =
                        [
                            new StepFailureSemanticSnapshot("step-build-fail", "backend", "build", ["dotnet", "build"], 1, "Build failed", "CS1002 ; expected")
                        ]
                    }
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.3",
            TotalPayloadBytesUtf8 = 800,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 800,
                    Success = true,
                    VerificationStatus = "NotVerified",
                    FailedStepCount = 1,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        VerificationStatus = "NotVerified",
                        FailedSteps =
                        [
                            new StepFailureSemanticSnapshot("step-test-fail", "backend", "test", ["dotnet", "test"], 1, "Test failed", "Assert.True failure")
                        ]
                    }
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("Failed step IDs mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsFailureDiagnosticsDisappearingAsFail()
    {
        var baseline = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.2",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    VerificationStatus = "NotVerified",
                    FailedStepCount = 1,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        VerificationStatus = "NotVerified",
                        FailedSteps =
                        [
                            new StepFailureSemanticSnapshot("step-1", "backend", "build", ["dotnet", "build"], 1, "Build failed", "CS1002 ; expected at line 42")
                        ]
                    }
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.3",
            TotalPayloadBytesUtf8 = 800,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 800,
                    Success = true,
                    VerificationStatus = "NotVerified",
                    FailedStepCount = 1,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        VerificationStatus = "NotVerified",
                        FailedSteps =
                        [
                            new StepFailureSemanticSnapshot("step-1", "backend", "build", ["dotnet", "build"], 1, "Build failed", "")
                        ]
                    }
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("failure diagnostics disappeared", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsUnsupportedWorkspaceDisappearingAsFail()
    {
        var baseline = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.2",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        UnsupportedWorkspaces =
                        [
                            new UnsupportedWorkspaceSemanticSnapshot("legacy-ws", ["PHP"], "PHP is not supported.")
                        ]
                    }
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.3",
            TotalPayloadBytesUtf8 = 800,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 800,
                    Success = true,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        UnsupportedWorkspaces = []
                    }
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("Unsupported workspace IDs mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsUnsupportedWorkspaceReasonChangeAsFail()
    {
        var baseline = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.2",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        UnsupportedWorkspaces =
                        [
                            new UnsupportedWorkspaceSemanticSnapshot("legacy-ws", ["PHP"], "PHP is not supported.")
                        ]
                    }
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.3",
            TotalPayloadBytesUtf8 = 800,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 800,
                    Success = true,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        UnsupportedWorkspaces =
                        [
                            new UnsupportedWorkspaceSemanticSnapshot("legacy-ws", ["PHP"], "Ruby is not supported.")
                        ]
                    }
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("reason mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonDetectsCapabilityGapDisappearingUnexpectedlyAsFail()
    {
        var baseline = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.2",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        VerificationGaps =
                        [
                            new GapSemanticSnapshot("MISSING_EVIDENCE_TESTS", "No tests found")
                        ]
                    }
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.3",
            TotalPayloadBytesUtf8 = 800,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "verify",
                    PayloadBytesUtf8 = 800,
                    Success = true,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        VerificationGaps = []
                    }
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Fail, comparison.Verdict);
        Assert.Contains(comparison.Failures, f => f.Contains("disappeared unexpectedly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComparisonPreservesPassWhenCorrectPayloadAndEquivalentSemanticsRemainUnchanged()
    {
        var baseline = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.2",
            TotalPayloadBytesUtf8 = 1000,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "recommend_skills",
                    PayloadBytesUtf8 = 1000,
                    Success = true,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        SkillRecommendations =
                        [
                            new SkillRecommendationSemanticSnapshot("dotnet-build", "Recommended", "DOTNET", "Reason A"),
                            new SkillRecommendationSemanticSnapshot("dotnet-test", "Recommended", "TESTS", "Reason B")
                        ]
                    }
                }
            ]
        };

        var candidate = new BenchmarkRun
        {
            RepositoryName = "TestRepo",
            AgentProofVersion = "0.3.0-preview.3",
            TotalPayloadBytesUtf8 = 600,
            Operations =
            [
                new BenchmarkOperationSummary
                {
                    Operation = "recommend_skills",
                    PayloadBytesUtf8 = 600,
                    Success = true,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        SkillRecommendations =
                        [
                            new SkillRecommendationSemanticSnapshot("dotnet-test", "Recommended", "TESTS", "Reason B"),
                            new SkillRecommendationSemanticSnapshot("dotnet-build", "Recommended", "DOTNET", "Reason A")
                        ]
                    }
                }
            ]
        };

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Pass, comparison.Verdict);
        Assert.Empty(comparison.Failures);
    }

    private static BenchmarkRun CreateSampleRun(
        string verificationStatus,
        long payloadBytes,
        int gaps,
        int evidence,
        int workspaces = 1,
        int? stepCountOverride = null)
    {
        var passedCount = verificationStatus == "Verified" ? evidence : 0;
        var failedCount = verificationStatus == "Verified" ? 0 : 1;
        var stepCount = stepCountOverride ?? Math.Max(evidence, passedCount + failedCount);
        var passedIds = verificationStatus == "Verified"
            ? Enumerable.Range(1, passedCount).Select(i => $"step-{i}").ToList()
            : [];

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
                    StepCount = stepCount,
                    EvidenceCount = evidence,
                    GapCount = gaps,
                    VerificationStatus = verificationStatus,
                    PlannedStepCount = stepCount,
                    ExecutedStepCount = stepCount,
                    PassedStepCount = passedCount,
                    FailedStepCount = failedCount,
                    TimedOutStepCount = 0,
                    NotRunStepCount = 0,
                    SemanticSnapshot = new OperationSemanticSnapshot
                    {
                        VerificationStatus = verificationStatus,
                        PassedStepIds = passedIds,
                        Summary = new AgentProof.Mcp.CompactVerificationSummary(
                            stepCount,
                            evidence,
                            stepCount,
                            passedCount,
                            failedCount,
                            0,
                            0)
                    }
                }
            ]
        };
    }
}
