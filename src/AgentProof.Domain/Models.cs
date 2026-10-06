using System.Text.Json.Serialization;

namespace AgentProof.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskType { BugFix, Feature, Refactor, Performance, UiChange, Deployment, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskComplexity { Low, Medium, High }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AffectedArea { Backend, Frontend, Database, Ui, Architecture, Infrastructure, Testing, Deployment }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecommendationLevel { Required, Recommended, Optional, NotNeeded }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerificationStatus { Verified, NotVerified, PartiallyVerified }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerificationStepStatus { Passed, Failed, TimedOut, NotRun }

public sealed record RepositoryProfile(
    string Name,
    string RootPath,
    IReadOnlyList<string> Technologies,
    IReadOnlyList<string> Frameworks,
    IReadOnlyList<string> TestFrameworks,
    IReadOnlyList<string> PackageManagers,
    bool HasDocker,
    bool HasGit,
    long EstimatedSize);

public sealed record TaskContext
{
    public string Description { get; init; } = string.Empty;
    public TaskType TaskType { get; init; } = TaskType.Unknown;
    public TaskComplexity Complexity { get; init; } = TaskComplexity.Low;
    public IReadOnlyList<AffectedArea> AffectedAreas { get; init; } = [];
    public bool HasUiChanges { get; init; }
    public bool HasDatabaseChanges { get; init; }
    public bool HasArchitectureChanges { get; init; }
    public bool HasBrowserVisibleChanges { get; init; }
    public bool HasDeploymentChanges { get; init; }
    public string? DeploymentTarget { get; init; }
}

public sealed record SkillDefinition(string Id, string Name, string Purpose);

public sealed record SkillRecommendation(
    SkillDefinition Skill,
    RecommendationLevel Level,
    string ReasonCode,
    string Reason);

public sealed record VerificationCommand(string Executable, IReadOnlyList<string> Arguments)
{
    public override string ToString() => string.Join(' ', new[] { Executable }.Concat(Arguments));
}

public sealed record VerificationStep(
    string Id,
    string Name,
    string Type,
    VerificationCommand Command,
    string WorkingDirectory,
    TimeSpan Timeout,
    bool Required);

public sealed record VerificationGap(string Code, string Reason);

public sealed record VerificationPlan(
    IReadOnlyList<VerificationStep> Steps,
    IReadOnlyList<VerificationGap> Gaps);

public sealed record VerificationEvidence(
    VerificationStep Step,
    VerificationStepStatus Status,
    int? ExitCode,
    TimeSpan Duration,
    string OutputSummary,
    string? FailureReason);

public sealed record VerificationResult(
    VerificationStatus Status,
    IReadOnlyList<VerificationEvidence> Evidence,
    IReadOnlyList<VerificationGap> Gaps);
