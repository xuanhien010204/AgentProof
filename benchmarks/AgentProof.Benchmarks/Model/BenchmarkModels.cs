using System.Text.Json.Serialization;

namespace AgentProof.Benchmarks.Model;

public sealed record BenchmarkRun
{
    public string RepositoryName { get; init; } = string.Empty;
    public string RepositoryPath { get; init; } = string.Empty;
    public string ScenarioName { get; init; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string AgentProofVersion { get; init; } = string.Empty;
    public string? GitCommitSha { get; init; }
    public int Iterations { get; init; } = 1;
    public IReadOnlyList<BenchmarkOperationSummary> Operations { get; init; } = [];
    public long TotalPayloadBytesUtf8 { get; init; }
    public double TotalDurationMs { get; init; }
}

public sealed record BenchmarkOperationResult
{
    public string Operation { get; init; } = string.Empty;
    public double DurationMs { get; init; }
    public long PayloadBytesUtf8 { get; init; }
    public int PayloadCharacters { get; init; }
    public long JsonRpcPayloadBytes { get; init; }
    public long ToolResultPayloadBytes { get; init; }
    public string ToolResultText { get; init; } = string.Empty;
    public bool Success { get; init; } = true;
    public string? Error { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OperationSemanticSnapshot? SemanticSnapshot { get; init; }

    // Operation-specific metrics
    public int? WorkspaceCount { get; init; }
    public int? TechnologyCount { get; init; }
    public int? FrameworkCount { get; init; }
    public int? TestFrameworkCount { get; init; }
    public int? RecommendationCount { get; init; }
    public int? StepCount { get; init; }
    public int? EvidenceCount { get; init; }
    public int? GapCount { get; init; }
    public string? VerificationStatus { get; init; }
    public int? PlannedStepCount { get; init; }
    public int? ExecutedStepCount { get; init; }
    public int? PassedStepCount { get; init; }
    public int? FailedStepCount { get; init; }
    public int? TimedOutStepCount { get; init; }
    public int? NotRunStepCount { get; init; }
}

public sealed record BenchmarkOperationSummary
{
    public string Operation { get; init; } = string.Empty;
    public double MinDurationMs { get; init; }
    public double MedianDurationMs { get; init; }
    public double MaxDurationMs { get; init; }
    public long PayloadBytesUtf8 { get; init; }
    public int PayloadCharacters { get; init; }
    public long JsonRpcPayloadBytes { get; init; }
    public long ToolResultPayloadBytes { get; init; }
    public bool IsDeterministicPayload { get; init; } = true;
    public IReadOnlyList<long> PayloadBytesHistory { get; init; } = [];
    public bool Success { get; init; } = true;
    public string? Error { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolResultText { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OperationSemanticSnapshot? SemanticSnapshot { get; init; }

    // Operation-specific metrics (from representative/last successful run)
    public int? WorkspaceCount { get; init; }
    public int? TechnologyCount { get; init; }
    public int? FrameworkCount { get; init; }
    public int? TestFrameworkCount { get; init; }
    public int? RecommendationCount { get; init; }
    public int? StepCount { get; init; }
    public int? EvidenceCount { get; init; }
    public int? GapCount { get; init; }
    public string? VerificationStatus { get; init; }
    public int? PlannedStepCount { get; init; }
    public int? ExecutedStepCount { get; init; }
    public int? PassedStepCount { get; init; }
    public int? FailedStepCount { get; init; }
    public int? TimedOutStepCount { get; init; }
    public int? NotRunStepCount { get; init; }

    public IReadOnlyList<BenchmarkOperationResult> IterationRuns { get; init; } = [];
}
