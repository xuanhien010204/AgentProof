using AgentProof.Domain;

namespace AgentProof.Application;

public sealed class AgentProofService(
    IRepositoryAnalyzer analyzer,
    ISkillRecommender recommender,
    IVerificationPlanner planner,
    IVerificationRunner runner)
{
    public Task<RepositoryProfile> AnalyzeRepositoryAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        analyzer.AnalyzeAsync(repositoryPath, cancellationToken);

    public async Task<IReadOnlyList<SkillRecommendation>> RecommendSkillsAsync(
        string repositoryPath, TaskContext task, CancellationToken cancellationToken = default) =>
        recommender.Recommend(await analyzer.AnalyzeAsync(repositoryPath, cancellationToken), task);

    public async Task<VerificationPlan> CreateVerificationPlanAsync(
        string repositoryPath, TaskContext task, CancellationToken cancellationToken = default) =>
        await planner.CreateAsync(await analyzer.AnalyzeAsync(repositoryPath, cancellationToken), task, cancellationToken);

    public async Task<VerificationResult> VerifyRepositoryAsync(
        string repositoryPath, TaskContext task, CancellationToken cancellationToken = default)
    {
        var profile = await analyzer.AnalyzeAsync(repositoryPath, cancellationToken);
        var plan = await planner.CreateAsync(profile, task, cancellationToken);
        return await runner.RunAsync(profile.RootPath, plan, cancellationToken);
    }
}
