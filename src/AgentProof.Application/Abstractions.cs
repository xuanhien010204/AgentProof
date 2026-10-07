using AgentProof.Domain;

namespace AgentProof.Application;

public interface IRepositoryAnalyzer
{
    Task<RepositoryProfile> AnalyzeAsync(string repositoryPath, CancellationToken cancellationToken = default);
}

public interface ISkillRecommender
{
    IReadOnlyList<SkillRecommendation> Recommend(RepositoryProfile repository, TaskContext task);
}

public interface IRepositoryConfigurationReader
{
    string? FindDotNetEntryPoint(string repositoryPath);
    Task<IReadOnlyDictionary<string, string>> ReadPackageScriptsAsync(string repositoryPath, CancellationToken cancellationToken = default);
}

public interface IVerificationPlanner
{
    Task<VerificationPlan> CreateAsync(RepositoryProfile repository, TaskContext task, CancellationToken cancellationToken = default);
}

public interface IVerificationRunner
{
    Task<VerificationExecutionResult> RunAsync(string repositoryPath, VerificationPlan plan, CancellationToken cancellationToken = default);
}

public interface IEvidenceEvaluator
{
    VerificationResult Evaluate(
        TaskContract contract,
        VerificationPlan plan,
        IReadOnlyList<VerificationEvidence> executedEvidence,
        IReadOnlyList<VerificationGap> executionGaps);
}

