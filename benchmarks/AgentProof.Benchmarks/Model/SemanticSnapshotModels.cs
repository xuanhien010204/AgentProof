using System.Text.Json.Serialization;
using AgentProof.Mcp;

namespace AgentProof.Benchmarks.Model;

public sealed record OperationSemanticSnapshot
{
    // Repository semantics (analyze_repository)
    public IReadOnlyList<WorkspaceSemanticSnapshot>? Workspaces { get; init; }
    public IReadOnlyList<string>? Technologies { get; init; }
    public IReadOnlyList<string>? Frameworks { get; init; }
    public IReadOnlyList<string>? TestFrameworks { get; init; }

    // Planning semantics (create_verification_plan)
    public IReadOnlyList<string>? WorkspaceIds { get; init; }
    public IReadOnlyDictionary<string, string>? WorkspacePaths { get; init; }
    public IReadOnlyList<PlanStepSemanticSnapshot>? PlannedSteps { get; init; }
    public IReadOnlyList<GapSemanticSnapshot>? PlanGaps { get; init; }
    public IReadOnlyList<UnsupportedWorkspaceSemanticSnapshot>? PlanUnsupportedWorkspaces { get; init; }

    // Verification & Evaluation semantics (verify)
    public string? VerificationStatus { get; init; }
    public IReadOnlyList<string>? PassedStepIds { get; init; }
    public IReadOnlyList<StepFailureSemanticSnapshot>? FailedSteps { get; init; }
    public IReadOnlyList<StepFailureSemanticSnapshot>? TimedOutSteps { get; init; }
    public IReadOnlyList<NotRunGroupSemanticSnapshot>? NotRunSteps { get; init; }
    public IReadOnlyList<GapSemanticSnapshot>? VerificationGaps { get; init; }
    public IReadOnlyList<CriterionSemanticSnapshot>? Criteria { get; init; }
    public IReadOnlyList<UnsupportedWorkspaceSemanticSnapshot>? UnsupportedWorkspaces { get; init; }
    public IReadOnlyList<EvidenceSemanticSnapshot>? Evidence { get; init; }
    public CompactVerificationSummary? Summary { get; init; }
}

public sealed record WorkspaceSemanticSnapshot(
    string Id,
    string? RelativePath,
    IReadOnlyList<string> Technologies,
    IReadOnlyList<string> Frameworks,
    string? PackageManager,
    string? DotNetEntryPoint,
    string? UnsupportedVerifierReason);

public sealed record PlanStepSemanticSnapshot(
    string Id,
    string WorkspaceId,
    string Type,
    IReadOnlyList<string> Command,
    bool Required,
    IReadOnlyList<string> ProvidedEvidence);

public sealed record StepFailureSemanticSnapshot(
    string StepId,
    string WorkspaceId,
    string Type,
    IReadOnlyList<string> Command,
    int? ExitCode,
    string? FailureReason);

public sealed record NotRunGroupSemanticSnapshot(
    string Reason,
    IReadOnlyList<string> StepIds);

public sealed record GapSemanticSnapshot(
    string Code,
    string Reason);

public sealed record CriterionSemanticSnapshot(
    string Id,
    string Status,
    IReadOnlyList<string> EvidenceStepIds,
    string? Reason);

public sealed record UnsupportedWorkspaceSemanticSnapshot(
    string WorkspaceId,
    IReadOnlyList<string> Technologies,
    string Reason);

public sealed record EvidenceSemanticSnapshot(
    string StepId,
    string WorkspaceId,
    string Status,
    int? ExitCode,
    string? FailureReason);
