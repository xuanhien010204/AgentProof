using System.Text.Json.Serialization;

namespace AgentProof.Benchmarks.Model;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ComparisonVerdict
{
    Pass,
    Warning,
    Fail
}

public sealed record OperationComparison
{
    public string Operation { get; init; } = string.Empty;
    public long BaselinePayloadBytes { get; init; }
    public long CandidatePayloadBytes { get; init; }
    public double PayloadReductionPercent { get; init; }

    public double BaselineDurationMs { get; init; }
    public double CandidateDurationMs { get; init; }
    public double DurationChangePercent { get; init; }

    public int? BaselineWorkspaceCount { get; init; }
    public int? CandidateWorkspaceCount { get; init; }

    public int? BaselineStepCount { get; init; }
    public int? CandidateStepCount { get; init; }

    public int? BaselineEvidenceCount { get; init; }
    public int? CandidateEvidenceCount { get; init; }

    public int? BaselineGapCount { get; init; }
    public int? CandidateGapCount { get; init; }

    public string? BaselineVerificationStatus { get; init; }
    public string? CandidateVerificationStatus { get; init; }

    public ComparisonVerdict Verdict { get; init; } = ComparisonVerdict.Pass;
    public IReadOnlyList<string> Issues { get; init; } = [];
}

public sealed record BenchmarkComparison
{
    public string RepositoryName { get; init; } = string.Empty;
    public string ScenarioName { get; init; } = string.Empty;
    public DateTimeOffset ComparedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string BaselineVersion { get; init; } = string.Empty;
    public string CandidateVersion { get; init; } = string.Empty;

    public long TotalBaselinePayloadBytes { get; init; }
    public long TotalCandidatePayloadBytes { get; init; }
    public double TotalPayloadReductionPercent { get; init; }

    public double TotalBaselineDurationMs { get; init; }
    public double TotalCandidateDurationMs { get; init; }
    public double TotalDurationChangePercent { get; init; }

    public ComparisonVerdict Verdict { get; init; } = ComparisonVerdict.Pass;
    public IReadOnlyList<string> Failures { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<OperationComparison> Operations { get; init; } = [];
}
