using AgentProof.Application;
using AgentProof.Domain;

namespace AgentProof.Mcp;

public static class CompactMcpResponseFactory
{
    public static CompactRepositoryProfile Create(RepositoryProfile profile) => new(
        profile.Name,
        profile.Technologies,
        profile.Frameworks,
        profile.TestFrameworks,
        profile.PackageManagers,
        profile.HasDocker,
        profile.HasGit,
        profile.EstimatedSize,
        profile.Workspaces.Count,
        profile.Workspaces.Select(CreateWorkspace).ToArray());

    public static IReadOnlyList<CompactSkillRecommendation> Create(IReadOnlyList<SkillRecommendation> recommendations) =>
        recommendations.Select(recommendation => new CompactSkillRecommendation(
            recommendation.Skill.Id,
            recommendation.Level,
            recommendation.ReasonCode,
            recommendation.Reason)).ToArray();

    public static CompactVerificationPlan Create(RepositoryPlanningContext context)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var steps = context.Plan.Steps.Select(step =>
        {
            var workspaceId = step.WorkspaceId ?? ".";
            var relativePath = ToRelativePath(context.Profile.RootPath, step.WorkingDirectory);
            if (!string.Equals(workspaceId, relativePath, StringComparison.Ordinal)) paths[workspaceId] = relativePath;
            return new CompactVerificationStep(
                step.Id,
                workspaceId,
                step.Type,
                [step.Command.Executable, .. step.Command.Arguments],
                (int)step.Timeout.TotalSeconds,
                step.Required,
                step.ProvidedEvidence);
        }).ToArray();

        return new CompactVerificationPlan(
            context.Plan.Steps.Count,
            context.Plan.WorkspaceIds,
            steps,
            context.Plan.Gaps,
            GetUnsupportedWorkspaces(context.Profile))
        {
            WorkspacePaths = paths.Count == 0 ? null : paths
        };
    }

    public static CompactVerificationResult Create(
        RepositoryVerificationContext context,
        string detailLevel = "compact")
    {
        var validatedDetailLevel = AgentProofTools.ValidateDetailLevel(detailLevel);
        var isFull = string.Equals(validatedDetailLevel, "full", StringComparison.Ordinal);

        var evidence = context.Result.Evidence;
        var failed = evidence.Where(item => item.Status == VerificationStepStatus.Failed).Select(CreateFailure).ToArray();
        var timedOut = evidence.Where(item => item.Status == VerificationStepStatus.TimedOut).Select(CreateFailure).ToArray();
        var notRun = evidence.Where(item => item.Status == VerificationStepStatus.NotRun)
            .GroupBy(item => item.FailureReason ?? "Not run.", StringComparer.Ordinal)
            .Select(group => new CompactNotRunGroup(group.Key, group.Select(item => item.Step.Id).ToArray()))
            .ToArray();
        var passed = evidence.Where(item => item.Status == VerificationStepStatus.Passed).Select(item => item.Step.Id).ToArray();
        var executed = evidence.Count(item => item.Status != VerificationStepStatus.NotRun);

        var detailedEvidence = isFull
            ? evidence.Select(item => new DetailedVerificationEvidence(
                item.Step.Id,
                item.Step.WorkspaceId ?? ".",
                item.Step.Type,
                [item.Step.Command.Executable, .. item.Step.Command.Arguments],
                item.Status,
                item.ExitCode,
                Math.Round(item.Duration.TotalMilliseconds, 2),
                item.OutputSummary,
                item.FailureReason)
            {
                WorkingDirectory = ToRelativePath(context.Profile.RootPath, item.Step.WorkingDirectory)
            }).ToArray()
            : null;

        return new CompactVerificationResult(
            context.Result.Status,
            new CompactVerificationSummary(
                context.Plan.Steps.Count,
                evidence.Count,
                executed,
                passed.Length,
                failed.Length,
                timedOut.Length,
                evidence.Count(item => item.Status == VerificationStepStatus.NotRun)),
            passed,
            failed,
            timedOut,
            notRun,
            context.Result.Gaps,
            context.Result.Criteria.Select(criterion => new CompactCriterionResult(
                criterion.Id,
                criterion.Status,
                criterion.EvidenceStepIds)
            {
                Reason = criterion.Reason
            }).ToArray(),
            GetUnsupportedWorkspaces(context.Profile))
        {
            Evidence = detailedEvidence
        };
    }

    private static CompactRepositoryWorkspace CreateWorkspace(RepositoryWorkspace workspace) => new(
        workspace.Id,
        workspace.Technologies,
        workspace.Frameworks,
        workspace.TestFrameworks)
    {
        RelativePath = string.Equals(workspace.Id, workspace.RelativePath, StringComparison.Ordinal) ? null : workspace.RelativePath,
        PackageManager = workspace.PackageManager,
        DotNetEntryPoint = workspace.DotNetEntryPoint,
        PackageScripts = workspace.PackageScripts.Count == 0 ? null : workspace.PackageScripts,
        UnsupportedVerifierReason = GetUnsupportedVerifierReason(workspace)
    };

    private static CompactVerificationFailure CreateFailure(VerificationEvidence evidence) => new(
        evidence.Step.Id,
        evidence.Step.WorkspaceId ?? ".",
        evidence.Step.Type,
        [evidence.Step.Command.Executable, .. evidence.Step.Command.Arguments],
        evidence.ExitCode,
        evidence.FailureReason,
        evidence.OutputSummary);

    private static CompactUnsupportedWorkspace[] GetUnsupportedWorkspaces(RepositoryProfile profile) =>
        profile.Workspaces
            .Where(workspace => GetUnsupportedVerifierReason(workspace) is not null)
            .Select(workspace => new CompactUnsupportedWorkspace(
                workspace.Id,
                workspace.Technologies,
                GetUnsupportedVerifierReason(workspace)!))
            .ToArray();

    private static string? GetUnsupportedVerifierReason(RepositoryWorkspace workspace) =>
        workspace.Technologies.Contains(".NET") || workspace.Technologies.Contains("Node.js")
            ? null
            : $"No active verifier is available for: {string.Join(", ", workspace.Technologies)}.";

    private static string ToRelativePath(string root, string path) =>
        Path.IsPathRooted(path)
            ? Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')
            : path.Replace('\\', '/');
}
