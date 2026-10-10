using AgentProof.Domain;

namespace AgentProof.Application;

public sealed class DeterministicEvidenceEvaluator : IEvidenceEvaluator
{
    public VerificationResult Evaluate(
        TaskContract contract,
        VerificationPlan plan,
        IReadOnlyList<VerificationEvidence> executedEvidence,
        IReadOnlyList<VerificationGap> executionGaps)
    {
        var allGaps = new List<VerificationGap>(executionGaps);
        foreach (var gap in plan.Gaps)
            if (!allGaps.Any(g => g.Code == gap.Code && g.Reason == gap.Reason)) allGaps.Add(gap);

        var knownWorkspaceIds = plan.WorkspaceIds
            .Concat(plan.Steps.Select(step => step.WorkspaceId).OfType<string>())
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var criteriaResults = new List<CriterionResult>();
        var idCounts = contract.AcceptanceCriteria
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var criterion in contract.AcceptanceCriteria)
        {
            if (string.IsNullOrWhiteSpace(criterion.Id))
            {
                AddGap(allGaps, "INVALID_CRITERION_ID", "Acceptance criterion has an empty or invalid identifier.");
                criteriaResults.Add(new CriterionResult { Id = string.Empty, Status = CriterionStatus.Gap, EvidenceStepIds = [], Reason = "Acceptance criterion has an empty or invalid identifier." });
                continue;
            }

            if (idCounts.TryGetValue(criterion.Id, out var count) && count > 1)
            {
                var duplicateReason = $"Duplicate acceptance criterion identifier '{criterion.Id}'.";
                AddGap(allGaps, "DUPLICATE_CRITERION_ID", duplicateReason);
                criteriaResults.Add(new CriterionResult { Id = criterion.Id, Status = CriterionStatus.Gap, EvidenceStepIds = [], Reason = duplicateReason });
                continue;
            }

            var requirements = criterion.GetEffectiveEvidenceRequirements();
            if (requirements.Count == 0)
            {
                var emptyRequirementReason = "Acceptance criterion has no required verification evidence.";
                AddGap(allGaps, "UNMAPPED_ACCEPTANCE_CRITERION", $"Acceptance criterion '{criterion.Id}' has no required verification evidence.");
                criteriaResults.Add(new CriterionResult { Id = criterion.Id, Status = CriterionStatus.Gap, EvidenceStepIds = [], Reason = emptyRequirementReason });
                continue;
            }

            var requirementResults = requirements
                .Select(requirement => EvaluateRequirement(requirement, plan, executedEvidence, knownWorkspaceIds, allGaps))
                .ToArray();
            var combinedStepIds = requirementResults.SelectMany(x => x.StepIds).Distinct(StringComparer.Ordinal).ToArray();

            var criterionStatus = requirementResults.Any(x => x.Status == CriterionStatus.Failed)
                ? CriterionStatus.Failed
                : requirementResults.Any(x => x.Status == CriterionStatus.Gap)
                    ? CriterionStatus.Gap
                    : requirementResults.Any(x => x.Status == CriterionStatus.NotEvaluated)
                        ? CriterionStatus.NotEvaluated
                        : CriterionStatus.Passed;
            var reason = requirementResults.FirstOrDefault(x => x.Status == criterionStatus)?.Reason;
            criteriaResults.Add(new CriterionResult
            {
                Id = criterion.Id,
                Status = criterionStatus,
                EvidenceStepIds = combinedStepIds,
                Reason = reason
            });
        }

        var hasGlobalRequirementGaps = false;
        foreach (var requirement in contract.GetEffectiveEvidenceRequirements())
        {
            var result = EvaluateRequirement(requirement, plan, executedEvidence, knownWorkspaceIds, allGaps);
            if (result.Status != CriterionStatus.Passed) hasGlobalRequirementGaps = true;
        }

        var anyRequiredStepFailed = executedEvidence.Any(e => e.Step.Required && e.Status is VerificationStepStatus.Failed or VerificationStepStatus.TimedOut);
        var anyCriterionFailed = criteriaResults.Any(c => c.Status == CriterionStatus.Failed);
        var finalStatus = (anyRequiredStepFailed || anyCriterionFailed)
            ? VerificationStatus.NotVerified
            : (allGaps.Count > 0 ||
               criteriaResults.Any(c => c.Status is CriterionStatus.Gap or CriterionStatus.NotEvaluated) ||
               hasGlobalRequirementGaps ||
               executedEvidence.Any(e => !e.Step.Required && e.Status != VerificationStepStatus.Passed) ||
               (plan.Steps.Count == 0 && (contract.GetEffectiveEvidenceRequirements().Count > 0 || contract.AcceptanceCriteria.Count > 0)))
                ? VerificationStatus.PartiallyVerified
                : VerificationStatus.Verified;

        return new VerificationResult(finalStatus, executedEvidence, allGaps, criteriaResults);
    }

    private static RequirementEvaluation EvaluateRequirement(
        EvidenceRequirement requirement,
        VerificationPlan plan,
        IReadOnlyList<VerificationEvidence> executedEvidence,
        HashSet<string> knownWorkspaceIds,
        List<VerificationGap> allGaps)
    {
        if (requirement.WorkspaceId is not null && !knownWorkspaceIds.Contains(requirement.WorkspaceId))
        {
            var reason = $"Evidence requirement references unknown workspace '{requirement.WorkspaceId}'.";
            AddGap(allGaps, "MISSING_WORKSPACE_EVIDENCE_PROVIDER", reason);
            return new RequirementEvaluation(CriterionStatus.Gap, reason, []);
        }

        var matchingPlannedSteps = plan.Steps
            .Where(step => step.ProvidedEvidence.Contains(requirement.Type) &&
                (requirement.WorkspaceId is null || string.Equals(step.WorkspaceId, requirement.WorkspaceId, StringComparison.Ordinal)))
            .ToList();
        if (matchingPlannedSteps.Count == 0)
        {
            var scope = requirement.WorkspaceId is null ? string.Empty : $" in workspace '{requirement.WorkspaceId}'";
            var gapReason = $"No verification capability is available for evidence type '{requirement.Type}'{scope}.";
            AddGap(allGaps, "MISSING_WORKSPACE_EVIDENCE_PROVIDER", gapReason);
            return new RequirementEvaluation(CriterionStatus.Gap, gapReason, []);
        }

        var executed = executedEvidence
            .Where(evidence => matchingPlannedSteps.Any(step => step.Id == evidence.Step.Id))
            .ToList();
        if (executed.Count == 0)
            return new RequirementEvaluation(CriterionStatus.NotEvaluated, $"Verification step providing '{requirement.Type}' was not executed.", []);

        var failedEvidence = executed.FirstOrDefault(evidence => evidence.Status is VerificationStepStatus.Failed or VerificationStepStatus.TimedOut);
        var evidenceStepIds = executed.Select(evidence => evidence.Step.Id).Distinct(StringComparer.Ordinal).ToArray();
        if (failedEvidence is not null)
            return new RequirementEvaluation(CriterionStatus.Failed, failedEvidence.FailureReason ?? $"Verification step '{failedEvidence.Step.Id}' failed.", evidenceStepIds);
        if (executed.Count < matchingPlannedSteps.Count)
            return new RequirementEvaluation(CriterionStatus.NotEvaluated, $"Not all verification steps providing '{requirement.Type}' were executed.", evidenceStepIds);
        if (executed.All(evidence => evidence.Status == VerificationStepStatus.Passed))
            return new RequirementEvaluation(CriterionStatus.Passed, null, evidenceStepIds);
        return new RequirementEvaluation(CriterionStatus.Failed, "Verification step did not pass.", evidenceStepIds);
    }

    private static void AddGap(List<VerificationGap> gaps, string code, string reason)
    {
        if (!gaps.Any(gap => gap.Code == code && gap.Reason == reason)) gaps.Add(new VerificationGap(code, reason));
    }

    private sealed record RequirementEvaluation(CriterionStatus Status, string? Reason, IReadOnlyList<string> StepIds);
}
