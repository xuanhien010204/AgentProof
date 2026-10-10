using System.Globalization;
using AgentProof.Benchmarks.Execution;
using AgentProof.Benchmarks.Model;

namespace AgentProof.Benchmarks.Comparison;

public static class BenchmarkComparer
{
    public static BenchmarkComparison Compare(BenchmarkRun baseline, BenchmarkRun candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var failures = new List<string>();
        var warnings = new List<string>();
        var operationComparisons = new List<OperationComparison>();

        var allOps = baseline.Operations.Select(o => o.Operation)
            .Union(candidate.Operations.Select(o => o.Operation))
            .ToList();

        foreach (var op in allOps)
        {
            var baseOp = baseline.Operations.FirstOrDefault(o => o.Operation == op);
            var candOp = candidate.Operations.FirstOrDefault(o => o.Operation == op);

            var opIssues = new List<string>();
            var opVerdict = ComparisonVerdict.Pass;

            if (baseOp == null)
            {
                warnings.Add($"Operation '{op}' exists in candidate but not in baseline.");
                continue;
            }

            if (candOp == null)
            {
                var msg = $"Operation '{op}' missing in candidate run.";
                failures.Add(msg);
                opIssues.Add(msg);
                opVerdict = ComparisonVerdict.Fail;

                operationComparisons.Add(new OperationComparison
                {
                    Operation = op,
                    BaselinePayloadBytes = baseOp.PayloadBytesUtf8,
                    CandidatePayloadBytes = 0,
                    PayloadReductionPercent = 0,
                    BaselineDurationMs = baseOp.MedianDurationMs,
                    CandidateDurationMs = 0,
                    DurationChangePercent = 0,
                    Verdict = ComparisonVerdict.Fail,
                    Issues = opIssues
                });
                continue;
            }

            // Payloads
            var payloadReduction = BenchmarkRunner.CalculatePercentageReduction(baseOp.PayloadBytesUtf8, candOp.PayloadBytesUtf8);
            var durationChange = BenchmarkRunner.CalculatePercentageChange(baseOp.MedianDurationMs, candOp.MedianDurationMs);

            if (!candOp.Success)
            {
                var msg = $"Candidate operation '{op}' failed: {candOp.Error}";
                RecordFail(failures, opIssues, ref opVerdict, msg);
            }

            if (candOp.PayloadBytesUtf8 > baseOp.PayloadBytesUtf8)
            {
                var diff = candOp.PayloadBytesUtf8 - baseOp.PayloadBytesUtf8;
                var msg = $"Operation '{op}' payload increased by {diff:N0} bytes (-{Math.Abs(payloadReduction):F2}%).";
                RecordFail(failures, opIssues, ref opVerdict, msg);
            }

            // Deep Semantic Comparison
            var baseSnap = baseOp.SemanticSnapshot ?? SemanticSnapshotExtractor.Extract(op, baseOp.ToolResultText);
            var candSnap = candOp.SemanticSnapshot ?? SemanticSnapshotExtractor.Extract(op, candOp.ToolResultText);

            if (baseSnap == null && candSnap == null)
            {
                if (op != "tools/list" && (baseOp.WorkspaceCount.HasValue || baseOp.StepCount.HasValue))
                {
                    var msg = $"Semantic snapshot unavailable for operation '{op}'; evaluated using scalar metrics only.";
                    warnings.Add(msg);
                    opIssues.Add(msg);
                    if (opVerdict == ComparisonVerdict.Pass)
                    {
                        opVerdict = ComparisonVerdict.Warning;
                    }
                }
            }
            else if (baseSnap == null)
            {
                var msg = $"Baseline semantic snapshot unavailable for operation '{op}'; falling back to scalar metric comparison.";
                warnings.Add(msg);
                opIssues.Add(msg);
                if (opVerdict == ComparisonVerdict.Pass)
                {
                    opVerdict = ComparisonVerdict.Warning;
                }
            }
            else if (candSnap == null)
            {
                var msg = $"Candidate semantic snapshot missing for operation '{op}'.";
                RecordFail(failures, opIssues, ref opVerdict, msg);
            }
            else
            {
                CompareSemanticSnapshots(op, baseSnap, candSnap, failures, warnings, opIssues, ref opVerdict);
            }

            // Verification status regressions
            if (baseOp.VerificationStatus != null || candOp.VerificationStatus != null)
            {
                if (baseOp.VerificationStatus != null && candOp.VerificationStatus != null)
                {
                    if (string.Equals(baseOp.VerificationStatus, "Verified", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(candOp.VerificationStatus, "Verified", StringComparison.OrdinalIgnoreCase))
                    {
                        var msg = $"Verification status regressed for '{op}' from '{baseOp.VerificationStatus}' to '{candOp.VerificationStatus}'.";
                        RecordFail(failures, opIssues, ref opVerdict, msg);
                    }
                    else if (!string.Equals(baseOp.VerificationStatus, candOp.VerificationStatus, StringComparison.OrdinalIgnoreCase))
                    {
                        var msg = $"Verification status changed for '{op}' from '{baseOp.VerificationStatus}' to '{candOp.VerificationStatus}'.";
                        RecordFail(failures, opIssues, ref opVerdict, msg);
                    }
                }
            }

            // Evidence count regressions
            if (baseOp.EvidenceCount.HasValue && candOp.EvidenceCount.HasValue)
            {
                if (candOp.EvidenceCount.Value < baseOp.EvidenceCount.Value)
                {
                    var msg = $"Evidence count regressed for '{op}' from {baseOp.EvidenceCount.Value} to {candOp.EvidenceCount.Value}.";
                    RecordFail(failures, opIssues, ref opVerdict, msg);
                }
            }

            // Gap regressions
            if (baseOp.GapCount.HasValue && candOp.GapCount.HasValue)
            {
                if (candOp.GapCount.Value > baseOp.GapCount.Value)
                {
                    var diff = candOp.GapCount.Value - baseOp.GapCount.Value;
                    var msg = $"Candidate introduced {diff} new gap(s) in '{op}' (from {baseOp.GapCount.Value} to {candOp.GapCount.Value}).";
                    RecordFail(failures, opIssues, ref opVerdict, msg);
                }
            }

            // Step count regressions
            if (baseOp.StepCount.HasValue && candOp.StepCount.HasValue)
            {
                if (candOp.StepCount.Value < baseOp.StepCount.Value)
                {
                    var msg = $"Step count decreased in '{op}' from {baseOp.StepCount.Value} to {candOp.StepCount.Value}.";
                    RecordFail(failures, opIssues, ref opVerdict, msg);
                }
            }

            // Workspace count check (warning if changed)
            if (baseOp.WorkspaceCount.HasValue && candOp.WorkspaceCount.HasValue)
            {
                if (candOp.WorkspaceCount.Value != baseOp.WorkspaceCount.Value)
                {
                    var msg = $"Workspace count changed unexpectedly in '{op}' from {baseOp.WorkspaceCount.Value} to {candOp.WorkspaceCount.Value}.";
                    warnings.Add(msg);
                    opIssues.Add(msg);
                    if (opVerdict == ComparisonVerdict.Pass)
                    {
                        opVerdict = ComparisonVerdict.Warning;
                    }
                }
            }

            operationComparisons.Add(new OperationComparison
            {
                Operation = op,
                BaselinePayloadBytes = baseOp.PayloadBytesUtf8,
                CandidatePayloadBytes = candOp.PayloadBytesUtf8,
                PayloadReductionPercent = payloadReduction,
                BaselineDurationMs = baseOp.MedianDurationMs,
                CandidateDurationMs = candOp.MedianDurationMs,
                DurationChangePercent = durationChange,
                BaselineWorkspaceCount = baseOp.WorkspaceCount,
                CandidateWorkspaceCount = candOp.WorkspaceCount,
                BaselineStepCount = baseOp.StepCount,
                CandidateStepCount = candOp.StepCount,
                BaselineEvidenceCount = baseOp.EvidenceCount,
                CandidateEvidenceCount = candOp.EvidenceCount,
                BaselineGapCount = baseOp.GapCount,
                CandidateGapCount = candOp.GapCount,
                BaselineVerificationStatus = baseOp.VerificationStatus,
                CandidateVerificationStatus = candOp.VerificationStatus,
                Verdict = opVerdict,
                Issues = opIssues
            });
        }

        var totalBaseBytes = baseline.TotalPayloadBytesUtf8;
        var totalCandBytes = candidate.TotalPayloadBytesUtf8;
        var totalReduction = BenchmarkRunner.CalculatePercentageReduction(totalBaseBytes, totalCandBytes);

        var totalBaseDuration = baseline.TotalDurationMs;
        var totalCandDuration = candidate.TotalDurationMs;
        var totalDurationChange = BenchmarkRunner.CalculatePercentageChange(totalBaseDuration, totalCandDuration);

        var overallVerdict = ComparisonVerdict.Pass;
        if (failures.Count > 0)
        {
            overallVerdict = ComparisonVerdict.Fail;
        }
        else if (warnings.Count > 0)
        {
            overallVerdict = ComparisonVerdict.Warning;
        }

        return new BenchmarkComparison
        {
            RepositoryName = candidate.RepositoryName,
            ScenarioName = candidate.ScenarioName,
            ComparedAtUtc = DateTimeOffset.UtcNow,
            BaselineVersion = baseline.AgentProofVersion,
            CandidateVersion = candidate.AgentProofVersion,
            TotalBaselinePayloadBytes = totalBaseBytes,
            TotalCandidatePayloadBytes = totalCandBytes,
            TotalPayloadReductionPercent = totalReduction,
            TotalBaselineDurationMs = totalBaseDuration,
            TotalCandidateDurationMs = totalCandDuration,
            TotalDurationChangePercent = totalDurationChange,
            Verdict = overallVerdict,
            Failures = failures,
            Warnings = warnings,
            Operations = operationComparisons
        };
    }

    private static void CompareSemanticSnapshots(
        string op,
        OperationSemanticSnapshot baseSnap,
        OperationSemanticSnapshot candSnap,
        List<string> failures,
        List<string> warnings,
        List<string> opIssues,
        ref ComparisonVerdict opVerdict)
    {
        // 1. Repository Semantics
        if (baseSnap.Workspaces != null || candSnap.Workspaces != null)
        {
            var baseWsList = baseSnap.Workspaces ?? [];
            var candWsList = candSnap.Workspaces ?? [];
            var baseWsIds = baseWsList.Select(w => w.Id).ToHashSet();
            var candWsIds = candWsList.Select(w => w.Id).ToHashSet();

            if (!baseWsIds.SetEquals(candWsIds))
            {
                var missing = baseWsIds.Except(candWsIds).OrderBy(x => x).ToList();
                var unexpected = candWsIds.Except(baseWsIds).OrderBy(x => x).ToList();
                RecordFail(failures, opIssues, ref opVerdict,
                    $"Workspace IDs mismatch in '{op}': Expected [{string.Join(", ", baseWsIds.OrderBy(x => x))}], Actual [{string.Join(", ", candWsIds.OrderBy(x => x))}]. Missing: [{string.Join(", ", missing)}], Unexpected: [{string.Join(", ", unexpected)}].");
            }
            else
            {
                var baseDict = baseWsList.ToDictionary(w => w.Id);
                var candDict = candWsList.ToDictionary(w => w.Id);

                foreach (var id in baseWsIds)
                {
                    var bw = baseDict[id];
                    var cw = candDict[id];

                    if (bw.RelativePath != cw.RelativePath)
                    {
                        RecordFail(failures, opIssues, ref opVerdict,
                            $"Workspace '{id}' relative path mismatch in '{op}': Expected '{bw.RelativePath ?? "(null)"}', Actual '{cw.RelativePath ?? "(null)"}'.");
                    }

                    if (!bw.Technologies.ToHashSet().SetEquals(cw.Technologies.ToHashSet()))
                    {
                        RecordFail(failures, opIssues, ref opVerdict,
                            $"Workspace '{id}' technologies mismatch in '{op}': Expected [{string.Join(", ", bw.Technologies.OrderBy(x => x))}], Actual [{string.Join(", ", cw.Technologies.OrderBy(x => x))}].");
                    }

                    if (!bw.Frameworks.ToHashSet().SetEquals(cw.Frameworks.ToHashSet()))
                    {
                        RecordFail(failures, opIssues, ref opVerdict,
                            $"Workspace '{id}' frameworks mismatch in '{op}': Expected [{string.Join(", ", bw.Frameworks.OrderBy(x => x))}], Actual [{string.Join(", ", cw.Frameworks.OrderBy(x => x))}].");
                    }

                    if (bw.PackageManager != cw.PackageManager)
                    {
                        RecordFail(failures, opIssues, ref opVerdict,
                            $"Workspace '{id}' package manager mismatch in '{op}': Expected '{bw.PackageManager ?? "(null)"}', Actual '{cw.PackageManager ?? "(null)"}'.");
                    }

                    if (bw.DotNetEntryPoint != cw.DotNetEntryPoint)
                    {
                        RecordFail(failures, opIssues, ref opVerdict,
                            $"Workspace '{id}' .NET entry point mismatch in '{op}': Expected '{bw.DotNetEntryPoint ?? "(null)"}', Actual '{cw.DotNetEntryPoint ?? "(null)"}'.");
                    }

                    if (bw.UnsupportedVerifierReason != cw.UnsupportedVerifierReason)
                    {
                        RecordFail(failures, opIssues, ref opVerdict,
                            $"Workspace '{id}' unsupported verifier reason mismatch in '{op}': Expected '{bw.UnsupportedVerifierReason ?? "(null)"}', Actual '{cw.UnsupportedVerifierReason ?? "(null)"}'.");
                    }
                }
            }

            if (baseSnap.Technologies != null && candSnap.Technologies != null &&
                !baseSnap.Technologies.ToHashSet().SetEquals(candSnap.Technologies.ToHashSet()))
            {
                RecordFail(failures, opIssues, ref opVerdict,
                    $"Repository technologies mismatch in '{op}': Expected [{string.Join(", ", baseSnap.Technologies.OrderBy(x => x))}], Actual [{string.Join(", ", candSnap.Technologies.OrderBy(x => x))}].");
            }

            if (baseSnap.Frameworks != null && candSnap.Frameworks != null &&
                !baseSnap.Frameworks.ToHashSet().SetEquals(candSnap.Frameworks.ToHashSet()))
            {
                RecordFail(failures, opIssues, ref opVerdict,
                    $"Repository frameworks mismatch in '{op}': Expected [{string.Join(", ", baseSnap.Frameworks.OrderBy(x => x))}], Actual [{string.Join(", ", candSnap.Frameworks.OrderBy(x => x))}].");
            }
        }

        // 2. Planning Semantics
        if (baseSnap.PlannedSteps != null || candSnap.PlannedSteps != null)
        {
            var baseSteps = baseSnap.PlannedSteps ?? [];
            var candSteps = candSnap.PlannedSteps ?? [];
            var baseStepIds = baseSteps.Select(s => s.Id).ToHashSet();
            var candStepIds = candSteps.Select(s => s.Id).ToHashSet();

            if (!baseStepIds.SetEquals(candStepIds))
            {
                var missing = baseStepIds.Except(candStepIds).OrderBy(x => x).ToList();
                var unexpected = candStepIds.Except(baseStepIds).OrderBy(x => x).ToList();
                RecordFail(failures, opIssues, ref opVerdict,
                    $"Planned step IDs mismatch in '{op}': Expected [{string.Join(", ", baseStepIds.OrderBy(x => x))}], Actual [{string.Join(", ", candStepIds.OrderBy(x => x))}]. Missing: [{string.Join(", ", missing)}], Unexpected: [{string.Join(", ", unexpected)}].");
            }
            else
            {
                var baseStepDict = baseSteps.ToDictionary(s => s.Id);
                var candStepDict = candSteps.ToDictionary(s => s.Id);

                foreach (var stepId in baseStepIds)
                {
                    var bs = baseStepDict[stepId];
                    var cs = candStepDict[stepId];

                    if (bs.WorkspaceId != cs.WorkspaceId)
                    {
                        RecordFail(failures, opIssues, ref opVerdict,
                            $"Step '{stepId}' workspace mismatch in '{op}': Expected '{bs.WorkspaceId}', Actual '{cs.WorkspaceId}'.");
                    }

                    if (bs.Type != cs.Type)
                    {
                        RecordFail(failures, opIssues, ref opVerdict,
                            $"Step '{stepId}' type mismatch in '{op}': Expected '{bs.Type}', Actual '{cs.Type}'.");
                    }

                    if (!bs.Command.SequenceEqual(cs.Command))
                    {
                        RecordFail(failures, opIssues, ref opVerdict,
                            $"Step '{stepId}' command changed in '{op}': Expected [{string.Join(" ", bs.Command)}], Actual [{string.Join(" ", cs.Command)}].");
                    }

                    if (bs.Required != cs.Required)
                    {
                        RecordFail(failures, opIssues, ref opVerdict,
                            $"Step '{stepId}' required flag mismatch in '{op}': Expected '{bs.Required}', Actual '{cs.Required}'.");
                    }

                    if (!bs.ProvidedEvidence.ToHashSet().SetEquals(cs.ProvidedEvidence.ToHashSet()))
                    {
                        RecordFail(failures, opIssues, ref opVerdict,
                            $"Step '{stepId}' provided evidence mismatch in '{op}': Expected [{string.Join(", ", bs.ProvidedEvidence.OrderBy(x => x))}], Actual [{string.Join(", ", cs.ProvidedEvidence.OrderBy(x => x))}].");
                    }
                }
            }

            if (baseSnap.WorkspaceIds != null && candSnap.WorkspaceIds != null &&
                !baseSnap.WorkspaceIds.ToHashSet().SetEquals(candSnap.WorkspaceIds.ToHashSet()))
            {
                RecordFail(failures, opIssues, ref opVerdict,
                    $"Planned workspace IDs mismatch in '{op}': Expected [{string.Join(", ", baseSnap.WorkspaceIds.OrderBy(x => x))}], Actual [{string.Join(", ", candSnap.WorkspaceIds.OrderBy(x => x))}].");
            }

            if (baseSnap.PlanGaps != null && candSnap.PlanGaps != null)
            {
                var baseGaps = baseSnap.PlanGaps.Select(g => g.Code).ToHashSet();
                var candGaps = candSnap.PlanGaps.Select(g => g.Code).ToHashSet();
                var newGaps = candGaps.Except(baseGaps).ToList();
                if (newGaps.Count > 0)
                {
                    RecordFail(failures, opIssues, ref opVerdict,
                        $"Candidate introduced new planning gap(s) in '{op}': [{string.Join(", ", newGaps)}].");
                }
            }
        }

        // 3. Verification & Evaluation Semantics
        if (baseSnap.VerificationStatus != null || candSnap.VerificationStatus != null ||
            baseSnap.Summary != null || candSnap.Summary != null)
        {
            // Accounting consistency on candidate
            if (candSnap.Summary != null)
            {
                var s = candSnap.Summary;
                if (s.ExecutedStepCount + s.NotRunStepCount != s.PlannedStepCount)
                {
                    RecordFail(failures, opIssues, ref opVerdict,
                        $"Accounting inconsistency in candidate '{op}': ExecutedStepCount ({s.ExecutedStepCount}) + NotRunStepCount ({s.NotRunStepCount}) != PlannedStepCount ({s.PlannedStepCount}).");
                }

                if (s.PassedStepCount + s.FailedStepCount + s.TimedOutStepCount != s.ExecutedStepCount)
                {
                    RecordFail(failures, opIssues, ref opVerdict,
                        $"Accounting inconsistency in candidate '{op}': Passed ({s.PassedStepCount}) + Failed ({s.FailedStepCount}) + TimedOut ({s.TimedOutStepCount}) != ExecutedStepCount ({s.ExecutedStepCount}).");
                }
            }

            // Passed steps comparison
            if (baseSnap.PassedStepIds != null && candSnap.PassedStepIds != null)
            {
                var basePassed = baseSnap.PassedStepIds.ToHashSet();
                var candPassed = candSnap.PassedStepIds.ToHashSet();
                var missingPassed = basePassed.Except(candPassed).OrderBy(x => x).ToList();
                if (missingPassed.Count > 0)
                {
                    RecordFail(failures, opIssues, ref opVerdict,
                        $"Passed step(s) missing in candidate '{op}': [{string.Join(", ", missingPassed)}].");
                }
            }

            // Failed steps comparison
            if (baseSnap.FailedSteps != null && candSnap.FailedSteps != null)
            {
                var baseFailed = baseSnap.FailedSteps.ToDictionary(f => f.StepId);
                var candFailed = candSnap.FailedSteps.ToDictionary(f => f.StepId);

                foreach (var stepId in baseFailed.Keys)
                {
                    if (candFailed.TryGetValue(stepId, out var cf))
                    {
                        var bf = baseFailed[stepId];
                        if (bf.ExitCode != cf.ExitCode)
                        {
                            RecordFail(failures, opIssues, ref opVerdict,
                                $"Step '{stepId}' exit code changed in '{op}': Expected {bf.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "(null)"}, Actual {cf.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "(null)"}.");
                        }

                        if (!string.Equals(bf.FailureReason, cf.FailureReason, StringComparison.Ordinal))
                        {
                            RecordFail(failures, opIssues, ref opVerdict,
                                $"Step '{stepId}' failure reason changed in '{op}': Expected '{bf.FailureReason ?? "(null)"}', Actual '{cf.FailureReason ?? "(null)"}'.");
                        }
                    }
                }
            }

            // Criteria comparison
            if (baseSnap.Criteria != null && candSnap.Criteria != null)
            {
                var baseCriteria = baseSnap.Criteria.ToDictionary(c => c.Id);
                var candCriteria = candSnap.Criteria.ToDictionary(c => c.Id);

                if (!baseCriteria.Keys.ToHashSet().SetEquals(candCriteria.Keys.ToHashSet()))
                {
                    RecordFail(failures, opIssues, ref opVerdict,
                        $"Evaluated criteria IDs mismatch in '{op}': Expected [{string.Join(", ", baseCriteria.Keys.OrderBy(x => x))}], Actual [{string.Join(", ", candCriteria.Keys.OrderBy(x => x))}].");
                }
                else
                {
                    foreach (var cId in baseCriteria.Keys)
                    {
                        var bc = baseCriteria[cId];
                        var cc = candCriteria[cId];

                        if (!string.Equals(bc.Status, cc.Status, StringComparison.OrdinalIgnoreCase))
                        {
                            RecordFail(failures, opIssues, ref opVerdict,
                                $"Criterion '{cId}' status changed in '{op}': Expected '{bc.Status}', Actual '{cc.Status}'.");
                        }

                        if (!bc.EvidenceStepIds.ToHashSet().SetEquals(cc.EvidenceStepIds.ToHashSet()))
                        {
                            RecordFail(failures, opIssues, ref opVerdict,
                                $"Criterion '{cId}' evidence step IDs mismatch in '{op}': Expected [{string.Join(", ", bc.EvidenceStepIds.OrderBy(x => x))}], Actual [{string.Join(", ", cc.EvidenceStepIds.OrderBy(x => x))}].");
                        }
                    }
                }
            }

            // Verification gaps comparison
            if (baseSnap.VerificationGaps != null && candSnap.VerificationGaps != null)
            {
                var baseCodes = baseSnap.VerificationGaps.Select(g => g.Code).ToHashSet();
                var candCodes = candSnap.VerificationGaps.Select(g => g.Code).ToHashSet();
                var newCodes = candCodes.Except(baseCodes).OrderBy(x => x).ToList();
                if (newCodes.Count > 0)
                {
                    RecordFail(failures, opIssues, ref opVerdict,
                        $"Candidate introduced new verification gap code(s) in '{op}': [{string.Join(", ", newCodes)}].");
                }
            }

            // Unsupported workspaces comparison
            if (baseSnap.UnsupportedWorkspaces != null && candSnap.UnsupportedWorkspaces != null)
            {
                var baseUnsup = baseSnap.UnsupportedWorkspaces.Select(u => u.WorkspaceId).ToHashSet();
                var candUnsup = candSnap.UnsupportedWorkspaces.Select(u => u.WorkspaceId).ToHashSet();
                if (!baseUnsup.SetEquals(candUnsup))
                {
                    warnings.Add($"Unsupported workspaces visibility changed in '{op}': Expected [{string.Join(", ", baseUnsup.OrderBy(x => x))}], Actual [{string.Join(", ", candUnsup.OrderBy(x => x))}].");
                }
            }
        }
    }

    private static void RecordFail(
        List<string> failures,
        List<string> opIssues,
        ref ComparisonVerdict opVerdict,
        string message)
    {
        failures.Add(message);
        opIssues.Add(message);
        opVerdict = ComparisonVerdict.Fail;
    }
}
