using AgentProof.Domain;

namespace AgentProof.Application;

public sealed class AgentProofService(
    IRepositoryAnalyzer analyzer,
    ISkillRecommender recommender,
    IVerificationPlanner planner,
    IVerificationRunner runner,
    IEvidenceEvaluator? evaluator = null)
{
    private readonly IEvidenceEvaluator _evaluator = evaluator ?? new DeterministicEvidenceEvaluator();

    public Task<RepositoryProfile> AnalyzeRepositoryAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        analyzer.AnalyzeAsync(repositoryPath, cancellationToken);

    public async Task<IReadOnlyList<SkillRecommendation>> RecommendSkillsAsync(
        string repositoryPath, TaskContext task, CancellationToken cancellationToken = default) =>
        recommender.Recommend(await analyzer.AnalyzeAsync(repositoryPath, cancellationToken), task);

    public async Task<VerificationPlan> CreateVerificationPlanAsync(
        string repositoryPath, TaskContext task, CancellationToken cancellationToken = default) =>
        (await CreateVerificationPlanWithProfileAsync(repositoryPath, task, cancellationToken)).Plan;

    public async Task<RepositoryPlanningContext> CreateVerificationPlanWithProfileAsync(
        string repositoryPath, TaskContext task, CancellationToken cancellationToken = default)
    {
        var profile = await analyzer.AnalyzeAsync(repositoryPath, cancellationToken);
        var plan = await planner.CreateAsync(profile, task, cancellationToken);
        return new RepositoryPlanningContext(profile, plan);
    }

    public async Task<VerificationResult> VerifyRepositoryAsync(
        string repositoryPath, TaskContext task, CancellationToken cancellationToken = default)
    {
        return (await VerifyRepositoryWithContextAsync(repositoryPath, task, cancellationToken)).Result;
    }

    public async Task<RepositoryVerificationContext> VerifyRepositoryWithContextAsync(
        string repositoryPath, TaskContext task, CancellationToken cancellationToken = default)
    {
        var profile = await analyzer.AnalyzeAsync(repositoryPath, cancellationToken);
        var plan = await planner.CreateAsync(profile, task, cancellationToken);
        var execution = await runner.RunAsync(profile.RootPath, plan, cancellationToken);
        var result = _evaluator.Evaluate(task.GetEffectiveContract(), plan, execution.Evidence, execution.Gaps);
        return new RepositoryVerificationContext(profile, plan, result);
    }
}

public sealed record RepositoryPlanningContext(RepositoryProfile Profile, VerificationPlan Plan);

public sealed record RepositoryVerificationContext(
    RepositoryProfile Profile,
    VerificationPlan Plan,
    VerificationResult Result);
