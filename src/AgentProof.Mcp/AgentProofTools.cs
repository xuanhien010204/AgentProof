using System.ComponentModel;
using AgentProof.Application;
using AgentProof.Domain;
using ModelContextProtocol.Server;

namespace AgentProof.Mcp;

[McpServerToolType]
public sealed class AgentProofTools(AgentProofService service)
{
    [McpServerTool(Name = "analyze_repository", ReadOnly = true, Idempotent = true),
     Description("Analyze a local repository using deterministic, read-only technology detection.")]
    public Task<RepositoryProfile> AnalyzeRepositoryAsync(
        [Description("Absolute or host-resolvable path to the local repository.")] string repositoryPath,
        CancellationToken cancellationToken) =>
        service.AnalyzeRepositoryAsync(repositoryPath, cancellationToken);

    [McpServerTool(Name = "recommend_skills", ReadOnly = true, Idempotent = true),
     Description("Recommend the minimum sufficient engineering skills for a structured task context.")]
    public Task<IReadOnlyList<SkillRecommendation>> RecommendSkillsAsync(
        [Description("Path to the local repository.")] string repositoryPath,
        [Description("Structured task information supplied by the host AI agent.")] TaskContext taskContext,
        CancellationToken cancellationToken) =>
        service.RecommendSkillsAsync(repositoryPath, taskContext, cancellationToken);

    [McpServerTool(Name = "create_verification_plan", ReadOnly = true, Idempotent = true),
     Description("Create a safe verification plan from actual repository configuration and structured task context.")]
    public Task<VerificationPlan> CreateVerificationPlanAsync(
        [Description("Path to the local repository.")] string repositoryPath,
        [Description("Structured task information supplied by the host AI agent.")] TaskContext taskContext,
        CancellationToken cancellationToken) =>
        service.CreateVerificationPlanAsync(repositoryPath, taskContext, cancellationToken);

    [McpServerTool(Name = "verify", Destructive = false, Idempotent = false),
     Description("Generate and execute only AgentProof-approved verification steps, then return evidence.")]
    public Task<VerificationResult> VerifyAsync(
        [Description("Path to the local repository.")] string repositoryPath,
        [Description("Structured task information supplied by the host AI agent.")] TaskContext taskContext,
        CancellationToken cancellationToken) =>
        service.VerifyRepositoryAsync(repositoryPath, taskContext, cancellationToken);
}
