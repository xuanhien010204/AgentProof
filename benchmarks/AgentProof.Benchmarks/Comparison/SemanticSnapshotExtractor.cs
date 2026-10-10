using System.Text.Json;
using System.Text.Json.Serialization;
using AgentProof.Benchmarks.Model;
using AgentProof.Mcp;

namespace AgentProof.Benchmarks.Comparison;

public static class SemanticSnapshotExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static OperationSemanticSnapshot? Extract(string operation, string? toolResultText)
    {
        if (string.IsNullOrWhiteSpace(toolResultText))
        {
            return null;
        }

        try
        {
            return operation.ToLowerInvariant() switch
            {
                "analyze_repository" => ExtractRepository(toolResultText),
                "recommend_skills" => ExtractSkillRecommendations(toolResultText),
                "create_verification_plan" => ExtractPlan(toolResultText),
                "verify" => ExtractVerification(toolResultText),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static OperationSemanticSnapshot? ExtractRepository(string toolResultText)
    {
        var profile = JsonSerializer.Deserialize<CompactRepositoryProfile>(toolResultText, JsonOptions);
        if (profile == null) return null;

        var workspaces = profile.Workspaces?.Select(w => new WorkspaceSemanticSnapshot(
            w.Id,
            w.RelativePath,
            w.Technologies ?? [],
            w.Frameworks ?? [],
            w.PackageManager,
            w.DotNetEntryPoint,
            w.UnsupportedVerifierReason
        )).ToList() ?? [];

        return new OperationSemanticSnapshot
        {
            Workspaces = workspaces,
            WorkspaceIds = workspaces.Select(w => w.Id).ToList(),
            Technologies = profile.Technologies ?? [],
            Frameworks = profile.Frameworks ?? [],
            TestFrameworks = profile.TestFrameworks ?? []
        };
    }

    private static OperationSemanticSnapshot? ExtractSkillRecommendations(string toolResultText)
    {
        var recommendations = JsonSerializer.Deserialize<IReadOnlyList<CompactSkillRecommendation>>(toolResultText, JsonOptions);
        if (recommendations == null) return null;

        return new OperationSemanticSnapshot
        {
            SkillRecommendations = recommendations.Select(r => new SkillRecommendationSemanticSnapshot(
                r.SkillId,
                r.Level.ToString(),
                r.ReasonCode,
                r.Reason
            )).ToList()
        };
    }

    private static OperationSemanticSnapshot? ExtractPlan(string toolResultText)
    {
        var plan = JsonSerializer.Deserialize<CompactVerificationPlan>(toolResultText, JsonOptions);
        if (plan == null) return null;

        return new OperationSemanticSnapshot
        {
            WorkspaceIds = plan.WorkspaceIds ?? [],
            WorkspacePaths = plan.WorkspacePaths,
            PlannedSteps = plan.Steps?.Select(s => new PlanStepSemanticSnapshot(
                s.Id,
                s.WorkspaceId,
                s.Type,
                s.Command ?? [],
                s.Required,
                s.ProvidedEvidence?.Select(e => e.ToString()).ToList() ?? []
            )).ToList() ?? [],
            PlanGaps = plan.Gaps?.Select(g => new GapSemanticSnapshot(
                g.Code,
                g.Reason
            )).ToList() ?? [],
            PlanUnsupportedWorkspaces = plan.UnsupportedWorkspaces?.Select(u => new UnsupportedWorkspaceSemanticSnapshot(
                u.WorkspaceId,
                u.Technologies ?? [],
                u.Reason
            )).ToList() ?? []
        };
    }

    private static OperationSemanticSnapshot? ExtractVerification(string toolResultText)
    {
        var vResult = JsonSerializer.Deserialize<CompactVerificationResult>(toolResultText, JsonOptions);
        if (vResult == null) return null;

        return new OperationSemanticSnapshot
        {
            VerificationStatus = vResult.Status.ToString(),
            Summary = vResult.Summary,
            PassedStepIds = vResult.PassedStepIds ?? [],
            FailedSteps = vResult.FailedSteps?.Select(f => new StepFailureSemanticSnapshot(
                f.StepId,
                f.WorkspaceId,
                f.Type,
                f.Command ?? [],
                f.ExitCode,
                f.FailureReason,
                f.OutputSummary
            )).ToList() ?? [],
            TimedOutSteps = vResult.TimedOutSteps?.Select(f => new StepFailureSemanticSnapshot(
                f.StepId,
                f.WorkspaceId,
                f.Type,
                f.Command ?? [],
                f.ExitCode,
                f.FailureReason,
                f.OutputSummary
            )).ToList() ?? [],
            NotRunSteps = vResult.NotRun?.Select(nr => new NotRunGroupSemanticSnapshot(
                nr.Reason,
                nr.StepIds ?? []
            )).ToList() ?? [],
            VerificationGaps = vResult.Gaps?.Select(g => new GapSemanticSnapshot(
                g.Code,
                g.Reason
            )).ToList() ?? [],
            Criteria = vResult.Criteria?.Select(c => new CriterionSemanticSnapshot(
                c.Id,
                c.Status.ToString(),
                c.EvidenceStepIds ?? [],
                c.Reason
            )).ToList() ?? [],
            UnsupportedWorkspaces = vResult.UnsupportedWorkspaces?.Select(u => new UnsupportedWorkspaceSemanticSnapshot(
                u.WorkspaceId,
                u.Technologies ?? [],
                u.Reason
            )).ToList() ?? [],
            Evidence = vResult.Evidence?.Select(e => new EvidenceSemanticSnapshot(
                e.StepId,
                e.WorkspaceId,
                e.Status.ToString(),
                e.ExitCode,
                e.FailureReason,
                e.OutputSummary
            )).ToList()
        };
    }
}
