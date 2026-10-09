using System.Text.Json.Serialization;
using AgentProof.Domain;

namespace AgentProof.Mcp;

public sealed record CompactRepositoryProfile(
    string Name,
    IReadOnlyList<string> Technologies,
    IReadOnlyList<string> Frameworks,
    IReadOnlyList<string> TestFrameworks,
    IReadOnlyList<string> PackageManagers,
    bool HasDocker,
    bool HasGit,
    long EstimatedSize,
    int WorkspaceCount,
    IReadOnlyList<CompactRepositoryWorkspace> Workspaces)
{
    [JsonPropertyOrder(-1)]
    public int SchemaVersion { get; init; } = 1;
}

public sealed record CompactRepositoryWorkspace(
    string Id,
    IReadOnlyList<string> Technologies,
    IReadOnlyList<string> Frameworks,
    IReadOnlyList<string> TestFrameworks)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RelativePath { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageManager { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DotNetEntryPoint { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? PackageScripts { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnsupportedVerifierReason { get; init; }
}

public sealed record CompactSkillRecommendation(
    string SkillId,
    RecommendationLevel Level,
    string ReasonCode,
    string Reason)
{
    [JsonPropertyOrder(-1)]
    public int SchemaVersion { get; init; } = 1;
}

public sealed record CompactVerificationPlan(
    int PlannedStepCount,
    IReadOnlyList<string> WorkspaceIds,
    IReadOnlyList<CompactVerificationStep> Steps,
    IReadOnlyList<VerificationGap> Gaps,
    IReadOnlyList<CompactUnsupportedWorkspace> UnsupportedWorkspaces)
{
    [JsonPropertyOrder(-1)]
    public int SchemaVersion { get; init; } = 1;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? WorkspacePaths { get; init; }
}

public sealed record CompactVerificationStep(
    string Id,
    string WorkspaceId,
    string Type,
    IReadOnlyList<string> Command,
    int TimeoutSeconds,
    bool Required,
    IReadOnlyList<EvidenceType> ProvidedEvidence);

public sealed record CompactUnsupportedWorkspace(
    string WorkspaceId,
    IReadOnlyList<string> Technologies,
    string Reason);

public sealed record CompactVerificationResult(
    VerificationStatus Status,
    CompactVerificationSummary Summary,
    IReadOnlyList<string> PassedStepIds,
    IReadOnlyList<CompactVerificationFailure> FailedSteps,
    IReadOnlyList<CompactVerificationFailure> TimedOutSteps,
    IReadOnlyList<CompactNotRunGroup> NotRun,
    IReadOnlyList<VerificationGap> Gaps,
    IReadOnlyList<CompactCriterionResult> Criteria,
    IReadOnlyList<CompactUnsupportedWorkspace> UnsupportedWorkspaces)
{
    [JsonPropertyOrder(-1)]
    public int SchemaVersion { get; init; } = 1;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DetailedVerificationEvidence>? Evidence { get; init; }
}

public sealed record DetailedVerificationEvidence(
    string StepId,
    string WorkspaceId,
    string Type,
    IReadOnlyList<string> Command,
    VerificationStepStatus Status,
    int? ExitCode,
    double DurationMs,
    string OutputSummary,
    string? FailureReason)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkingDirectory { get; init; }
}

public sealed record CompactVerificationSummary(
    int PlannedStepCount,
    int EvidenceCount,
    int ExecutedStepCount,
    int PassedStepCount,
    int FailedStepCount,
    int TimedOutStepCount,
    int NotRunStepCount);

public sealed record CompactVerificationFailure(
    string StepId,
    string WorkspaceId,
    string Type,
    IReadOnlyList<string> Command,
    int? ExitCode,
    string? FailureReason,
    string OutputSummary);

public sealed record CompactNotRunGroup(string Reason, IReadOnlyList<string> StepIds);

public sealed record CompactCriterionResult(
    string Id,
    CriterionStatus Status,
    IReadOnlyList<string> EvidenceStepIds)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}
