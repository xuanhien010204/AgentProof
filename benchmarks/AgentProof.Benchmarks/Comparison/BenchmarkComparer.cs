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
                failures.Add(msg);
                opIssues.Add(msg);
                opVerdict = ComparisonVerdict.Fail;
            }

            if (candOp.PayloadBytesUtf8 > baseOp.PayloadBytesUtf8)
            {
                var diff = candOp.PayloadBytesUtf8 - baseOp.PayloadBytesUtf8;
                var msg = $"Operation '{op}' payload increased by {diff:N0} bytes (-{Math.Abs(payloadReduction):F2}%).";
                failures.Add(msg);
                opIssues.Add(msg);
                opVerdict = ComparisonVerdict.Fail;
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
                        failures.Add(msg);
                        opIssues.Add(msg);
                        opVerdict = ComparisonVerdict.Fail;
                    }
                    else if (!string.Equals(baseOp.VerificationStatus, candOp.VerificationStatus, StringComparison.OrdinalIgnoreCase))
                    {
                        var msg = $"Verification status changed for '{op}' from '{baseOp.VerificationStatus}' to '{candOp.VerificationStatus}'.";
                        failures.Add(msg);
                        opIssues.Add(msg);
                        opVerdict = ComparisonVerdict.Fail;
                    }
                }
            }

            // Evidence count regressions
            if (baseOp.EvidenceCount.HasValue && candOp.EvidenceCount.HasValue)
            {
                if (candOp.EvidenceCount.Value < baseOp.EvidenceCount.Value)
                {
                    var msg = $"Evidence count regressed for '{op}' from {baseOp.EvidenceCount.Value} to {candOp.EvidenceCount.Value}.";
                    failures.Add(msg);
                    opIssues.Add(msg);
                    opVerdict = ComparisonVerdict.Fail;
                }
            }

            // Gap regressions
            if (baseOp.GapCount.HasValue && candOp.GapCount.HasValue)
            {
                if (candOp.GapCount.Value > baseOp.GapCount.Value)
                {
                    var diff = candOp.GapCount.Value - baseOp.GapCount.Value;
                    var msg = $"Candidate introduced {diff} new gap(s) in '{op}' (from {baseOp.GapCount.Value} to {candOp.GapCount.Value}).";
                    failures.Add(msg);
                    opIssues.Add(msg);
                    opVerdict = ComparisonVerdict.Fail;
                }
            }

            // Step count regressions
            if (baseOp.StepCount.HasValue && candOp.StepCount.HasValue)
            {
                if (candOp.StepCount.Value < baseOp.StepCount.Value)
                {
                    var msg = $"Step count decreased in '{op}' from {baseOp.StepCount.Value} to {candOp.StepCount.Value}.";
                    failures.Add(msg);
                    opIssues.Add(msg);
                    opVerdict = ComparisonVerdict.Fail;
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
}
