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
    Task<VerificationResult> RunAsync(string repositoryPath, VerificationPlan plan, CancellationToken cancellationToken = default);
}
