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
        {
            if (!allGaps.Any(g => g.Code == gap.Code && g.Reason == gap.Reason))
            {
                allGaps.Add(gap);
            }
        }

        var criteriaResults = new List<CriterionResult>();
        var idCounts = contract.AcceptanceCriteria
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var criterion in contract.AcceptanceCriteria)
        {
            if (string.IsNullOrWhiteSpace(criterion.Id))
            {
                if (!allGaps.Any(g => g.Code == "INVALID_CRITERION_ID"))
                {
                    allGaps.Add(new VerificationGap("INVALID_CRITERION_ID", "Acceptance criterion has an empty or invalid identifier."));
                }

                criteriaResults.Add(new CriterionResult
                {
                    Id = string.Empty,
                    Status = CriterionStatus.Gap,
                    EvidenceStepIds = [],
                    Reason = "Acceptance criterion has an empty or invalid identifier."
                });
                continue;
            }

            if (idCounts.TryGetValue(criterion.Id, out var count) && count > 1)
            {
                if (!allGaps.Any(g => g.Code == "DUPLICATE_CRITERION_ID" && g.Reason.Contains(criterion.Id)))
                {
                    allGaps.Add(new VerificationGap("DUPLICATE_CRITERION_ID", $"Duplicate acceptance criterion identifier '{criterion.Id}'."));
                }

                criteriaResults.Add(new CriterionResult
                {
                    Id = criterion.Id,
                    Status = CriterionStatus.Gap,
                    EvidenceStepIds = [],
                    Reason = $"Duplicate acceptance criterion identifier '{criterion.Id}'."
                });
                continue;
            }

            if (criterion.RequiredEvidence.Count == 0)
            {
                if (!allGaps.Any(g => g.Code == "UNMAPPED_ACCEPTANCE_CRITERION" && g.Reason.Contains(criterion.Id)))
                {
                    allGaps.Add(new VerificationGap("UNMAPPED_ACCEPTANCE_CRITERION", $"Acceptance criterion '{criterion.Id}' has no required verification evidence."));
                }

                criteriaResults.Add(new CriterionResult
                {
                    Id = criterion.Id,
                    Status = CriterionStatus.Gap,
                    EvidenceStepIds = [],
                    Reason = "Acceptance criterion has no required verification evidence."
                });
                continue;
            }

            var typeResults = new List<(EvidenceType Type, CriterionStatus Status, string? Reason, IReadOnlyList<string> StepIds)>();

            foreach (var requiredType in criterion.RequiredEvidence)
            {
                var matchingPlannedSteps = plan.Steps
                    .Where(s => s.ProvidedEvidence.Contains(requiredType))
                    .ToList();

                if (matchingPlannedSteps.Count == 0)
                {
                    typeResults.Add((requiredType, CriterionStatus.Gap, $"No verification capability is available for evidence type '{requiredType}'.", []));
                    continue;
                }

                var executedForType = executedEvidence
                    .Where(e => matchingPlannedSteps.Any(s => s.Id == e.Step.Id))
                    .ToList();

                if (executedForType.Count == 0)
                {
                    typeResults.Add((requiredType, CriterionStatus.NotEvaluated, $"Verification step providing '{requiredType}' was not executed.", []));
                    continue;
                }

                var failedEvidence = executedForType.FirstOrDefault(e => e.Status is VerificationStepStatus.Failed or VerificationStepStatus.TimedOut);
                if (failedEvidence is not null)
                {
                    typeResults.Add((
                        requiredType,
                        CriterionStatus.Failed,
                        failedEvidence.FailureReason ?? $"Verification step '{failedEvidence.Step.Id}' failed.",
                        executedForType.Select(e => e.Step.Id).Distinct().ToArray()));
                    continue;
                }

                if (executedForType.Count < matchingPlannedSteps.Count)
                {
                    typeResults.Add((
                        requiredType,
                        CriterionStatus.NotEvaluated,
                        $"Not all verification steps providing '{requiredType}' were executed.",
                        executedForType.Select(e => e.Step.Id).Distinct().ToArray()));
                    continue;
                }

                if (executedForType.All(e => e.Status == VerificationStepStatus.Passed))
                {
                    typeResults.Add((
                        requiredType,
                        CriterionStatus.Passed,
                        null,
                        executedForType.Select(e => e.Step.Id).Distinct().ToArray()));
                }
                else
                {
                    typeResults.Add((
                        requiredType,
                        CriterionStatus.Failed,
                        "Verification step did not pass.",
                        executedForType.Select(e => e.Step.Id).Distinct().ToArray()));
                }
            }

            var combinedStepIds = typeResults
                .SelectMany(r => r.StepIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (typeResults.Any(r => r.Status == CriterionStatus.Failed))
            {
                var failure = typeResults.First(r => r.Status == CriterionStatus.Failed);
                criteriaResults.Add(new CriterionResult
                {
                    Id = criterion.Id,
                    Status = CriterionStatus.Failed,
                    EvidenceStepIds = combinedStepIds,
                    Reason = failure.Reason
                });
            }
            else if (typeResults.Any(r => r.Status == CriterionStatus.Gap))
            {
                var gap = typeResults.First(r => r.Status == CriterionStatus.Gap);
                criteriaResults.Add(new CriterionResult
                {
                    Id = criterion.Id,
                    Status = CriterionStatus.Gap,
                    EvidenceStepIds = combinedStepIds,
                    Reason = gap.Reason
                });
            }
            else if (typeResults.Any(r => r.Status == CriterionStatus.NotEvaluated))
            {
                var notEval = typeResults.First(r => r.Status == CriterionStatus.NotEvaluated);
                criteriaResults.Add(new CriterionResult
                {
                    Id = criterion.Id,
                    Status = CriterionStatus.NotEvaluated,
                    EvidenceStepIds = combinedStepIds,
                    Reason = notEval.Reason
                });
            }
            else
            {
                criteriaResults.Add(new CriterionResult
                {
                    Id = criterion.Id,
                    Status = CriterionStatus.Passed,
                    EvidenceStepIds = combinedStepIds,
                    Reason = null
                });
            }
        }

        var hasGlobalRequirementGaps = false;
        foreach (var globalType in contract.RequiredEvidence)
        {
            var matchingPlanned = plan.Steps.Where(s => s.ProvidedEvidence.Contains(globalType)).ToList();
            if (matchingPlanned.Count == 0)
            {
                hasGlobalRequirementGaps = true;
                break;
            }

            var executed = executedEvidence.Where(e => matchingPlanned.Any(s => s.Id == e.Step.Id)).ToList();
            if (executed.Count < matchingPlanned.Count || executed.Any(e => e.Status != VerificationStepStatus.Passed))
            {
                hasGlobalRequirementGaps = true;
                break;
            }
        }

        var anyRequiredStepFailed = executedEvidence.Any(e => e.Step.Required && e.Status is VerificationStepStatus.Failed or VerificationStepStatus.TimedOut);
        var anyCriterionFailed = criteriaResults.Any(c => c.Status == CriterionStatus.Failed);

        var finalStatus = (anyRequiredStepFailed || anyCriterionFailed)
            ? VerificationStatus.NotVerified
            : (allGaps.Count > 0 ||
               criteriaResults.Any(c => c.Status is CriterionStatus.Gap or CriterionStatus.NotEvaluated) ||
               hasGlobalRequirementGaps ||
               executedEvidence.Any(e => !e.Step.Required && e.Status != VerificationStepStatus.Passed) ||
               (plan.Steps.Count == 0 && (contract.RequiredEvidence.Count > 0 || contract.AcceptanceCriteria.Count > 0)))
                ? VerificationStatus.PartiallyVerified
                : VerificationStatus.Verified;

        return new VerificationResult(finalStatus, executedEvidence, allGaps, criteriaResults);
    }
}
