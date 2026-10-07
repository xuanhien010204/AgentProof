using System.Globalization;
using System.Text;
using AgentProof.Benchmarks.Model;

namespace AgentProof.Benchmarks.Formatting;

public static class BenchmarkFormatter
{
    public static string FormatRunToMarkdown(BenchmarkRun run)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine("# AgentProof Context Benchmark");
        sb.AppendLine();
        sb.AppendLine(inv, $"- **Repository**: `{run.RepositoryName}` (`{run.RepositoryPath}`)");
        sb.AppendLine(inv, $"- **Scenario**: `{run.ScenarioName}`");
        var shaSuffix = string.IsNullOrEmpty(run.GitCommitSha) ? "" : $" ({run.GitCommitSha})";
        sb.AppendLine(inv, $"- **Version**: `{run.AgentProofVersion}`{shaSuffix}");
        sb.AppendLine(inv, $"- **Iterations**: {run.Iterations}");
        sb.AppendLine(inv, $"- **Timestamp**: {run.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine("| Operation | Payload bytes | Duration ms | Workspaces | Steps | Evidence | Gaps | Status |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");

        foreach (var op in run.Operations)
        {
            var ws = op.WorkspaceCount.HasValue ? op.WorkspaceCount.Value.ToString(inv) : "-";
            var steps = op.StepCount.HasValue ? op.StepCount.Value.ToString(inv) : "-";
            var ev = op.EvidenceCount.HasValue ? op.EvidenceCount.Value.ToString(inv) : "-";
            var gaps = op.GapCount.HasValue ? op.GapCount.Value.ToString(inv) : "-";
            var status = op.VerificationStatus ?? (op.Success ? "OK" : "ERROR");

            sb.AppendLine(inv, $"| {op.Operation} | {op.PayloadBytesUtf8:N0} | {op.MedianDurationMs:N0} | {ws} | {steps} | {ev} | {gaps} | {status} |");
        }

        sb.AppendLine();
        sb.AppendLine(inv, $"**Total Payload**: {run.TotalPayloadBytesUtf8:N0} UTF-8 bytes | **Total Median Duration**: {run.TotalDurationMs:N0} ms");
        sb.AppendLine();
        sb.AppendLine("> Payload reduction is a context-efficiency proxy, not a measurement of total LLM token usage.");

        return sb.ToString();
    }

    public static string FormatRunToConsole(BenchmarkRun run)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine("================================================================================");
        sb.AppendLine(inv, $"AGENTPROOF BENCHMARK REPORT: {run.RepositoryName} [{run.ScenarioName}]");
        sb.AppendLine(inv, $"Version: {run.AgentProofVersion} | Iterations: {run.Iterations} | SHA: {run.GitCommitSha ?? "N/A"}");
        sb.AppendLine("================================================================================");
        sb.AppendLine(string.Format(inv, "{0,-26} {1,14} {2,12} {3,6} {4,6} {5,6} {6,6}",
            "Operation", "Payload (B)", "Median (ms)", "WS", "Steps", "Evid", "Gaps"));
        sb.AppendLine(new string('-', 80));

        foreach (var op in run.Operations)
        {
            var ws = op.WorkspaceCount.HasValue ? op.WorkspaceCount.Value.ToString(inv) : "-";
            var steps = op.StepCount.HasValue ? op.StepCount.Value.ToString(inv) : "-";
            var ev = op.EvidenceCount.HasValue ? op.EvidenceCount.Value.ToString(inv) : "-";
            var gaps = op.GapCount.HasValue ? op.GapCount.Value.ToString(inv) : "-";

            sb.AppendLine(string.Format(inv, "{0,-26} {1,14:N0} {2,12:N1} {3,6} {4,6} {5,6} {6,6}",
                op.Operation, op.PayloadBytesUtf8, op.MedianDurationMs, ws, steps, ev, gaps));

            if (!op.IsDeterministicPayload)
            {
                sb.AppendLine(inv, $"  [!] Warning: Payload varied across runs: {string.Join(", ", op.PayloadBytesHistory)} bytes");
            }
            if (!op.Success)
            {
                sb.AppendLine(inv, $"  [!] Error: {op.Error}");
            }
        }

        sb.AppendLine(new string('-', 80));
        sb.AppendLine(inv, $"Total Payload: {run.TotalPayloadBytesUtf8:N0} UTF-8 bytes");
        sb.AppendLine(inv, $"Total Median Duration: {run.TotalDurationMs:N1} ms");
        sb.AppendLine("================================================================================");
        return sb.ToString();
    }

    public static string FormatComparisonToConsole(BenchmarkComparison comp)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine("================================================================================");
        sb.AppendLine(inv, $"AGENTPROOF BENCHMARK COMPARISON: {comp.Verdict.ToString().ToUpperInvariant()}");
        sb.AppendLine(inv, $"Repository: {comp.RepositoryName} | Scenario: {comp.ScenarioName}");
        sb.AppendLine(inv, $"Baseline: {comp.BaselineVersion} -> Candidate: {comp.CandidateVersion}");
        sb.AppendLine("================================================================================");
        sb.AppendLine();
        sb.AppendLine("Payload bytes:");
        sb.AppendLine(inv, $"  baseline:  {comp.TotalBaselinePayloadBytes:N0}");
        sb.AppendLine(inv, $"  candidate: {comp.TotalCandidatePayloadBytes:N0}");
        sb.AppendLine(inv, $"  reduction: {comp.TotalPayloadReductionPercent:F2}%");
        sb.AppendLine();
        sb.AppendLine("Duration:");
        sb.AppendLine(inv, $"  baseline:  {comp.TotalBaselineDurationMs:N1} ms");
        sb.AppendLine(inv, $"  candidate: {comp.TotalCandidateDurationMs:N1} ms");
        sb.AppendLine(inv, $"  change:    {(comp.TotalDurationChangePercent >= 0 ? "+" : "")}{comp.TotalDurationChangePercent:F2}%");
        sb.AppendLine();

        foreach (var op in comp.Operations)
        {
            sb.AppendLine(inv, $"Operation: {op.Operation} [{op.Verdict.ToString().ToUpperInvariant()}]");
            sb.AppendLine(inv, $"  Payload:  {op.BaselinePayloadBytes:N0} -> {op.CandidatePayloadBytes:N0} ({op.PayloadReductionPercent:F2}% reduction)");
            sb.AppendLine(inv, $"  Duration: {op.BaselineDurationMs:N1}ms -> {op.CandidateDurationMs:N1}ms ({(op.DurationChangePercent >= 0 ? "+" : "")}{op.DurationChangePercent:F2}%)");

            if (op.BaselineVerificationStatus != null || op.CandidateVerificationStatus != null)
            {
                sb.AppendLine(inv, $"  Status:   {op.BaselineVerificationStatus ?? "N/A"} -> {op.CandidateVerificationStatus ?? "N/A"}");
            }
            if (op.BaselineEvidenceCount.HasValue || op.CandidateEvidenceCount.HasValue)
            {
                sb.AppendLine(inv, $"  Evidence: {op.BaselineEvidenceCount?.ToString(inv) ?? "N/A"} -> {op.CandidateEvidenceCount?.ToString(inv) ?? "N/A"}");
            }
            if (op.BaselineGapCount.HasValue || op.CandidateGapCount.HasValue)
            {
                sb.AppendLine(inv, $"  Gaps:     {op.BaselineGapCount?.ToString(inv) ?? "N/A"} -> {op.CandidateGapCount?.ToString(inv) ?? "N/A"}");
            }
            if (op.BaselineWorkspaceCount.HasValue || op.CandidateWorkspaceCount.HasValue)
            {
                sb.AppendLine(inv, $"  Workspaces: {op.BaselineWorkspaceCount?.ToString(inv) ?? "N/A"} -> {op.CandidateWorkspaceCount?.ToString(inv) ?? "N/A"}");
            }
            foreach (var issue in op.Issues)
            {
                sb.AppendLine(inv, $"  [!] {issue}");
            }
            sb.AppendLine();
        }

        if (comp.Failures.Count > 0)
        {
            sb.AppendLine("FAILURES:");
            foreach (var f in comp.Failures)
            {
                sb.AppendLine(inv, $"  [x] {f}");
            }
            sb.AppendLine();
        }

        if (comp.Warnings.Count > 0)
        {
            sb.AppendLine("WARNINGS:");
            foreach (var w in comp.Warnings)
            {
                sb.AppendLine(inv, $"  [!] {w}");
            }
            sb.AppendLine();
        }

        sb.AppendLine(inv, $"FINAL VERDICT: {comp.Verdict.ToString().ToUpperInvariant()}");
        sb.AppendLine("================================================================================");
        return sb.ToString();
    }

    public static string FormatComparisonToMarkdown(BenchmarkComparison comp)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine(inv, $"# AgentProof Context Benchmark Comparison: {comp.Verdict.ToString().ToUpperInvariant()}");
        sb.AppendLine();
        sb.AppendLine(inv, $"- **Repository**: `{comp.RepositoryName}`");
        sb.AppendLine(inv, $"- **Scenario**: `{comp.ScenarioName}`");
        sb.AppendLine(inv, $"- **Baseline Version**: `{comp.BaselineVersion}`");
        sb.AppendLine(inv, $"- **Candidate Version**: `{comp.CandidateVersion}`");
        sb.AppendLine(inv, $"- **Total Baseline Payload**: {comp.TotalBaselinePayloadBytes:N0} bytes");
        sb.AppendLine(inv, $"- **Total Candidate Payload**: {comp.TotalCandidatePayloadBytes:N0} bytes");
        sb.AppendLine(inv, $"- **Total Payload Reduction**: **{comp.TotalPayloadReductionPercent:F2}%**");
        sb.AppendLine(inv, $"- **Total Duration Change**: {(comp.TotalDurationChangePercent >= 0 ? "+" : "")}{comp.TotalDurationChangePercent:F2}%");
        sb.AppendLine();
        sb.AppendLine("| Operation | Baseline (B) | Candidate (B) | Reduction | Base ms | Cand ms | Duration Change | Status | Verdict |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|---|");

        foreach (var op in comp.Operations)
        {
            var statusStr = $"{op.BaselineVerificationStatus ?? "-"} -> {op.CandidateVerificationStatus ?? "-"}";
            var durChangeStr = $"{(op.DurationChangePercent >= 0 ? "+" : "")}{op.DurationChangePercent:F1}%";
            sb.AppendLine(inv, $"| {op.Operation} | {op.BaselinePayloadBytes:N0} | {op.CandidatePayloadBytes:N0} | **{op.PayloadReductionPercent:F2}%** | {op.BaselineDurationMs:N0} | {op.CandidateDurationMs:N0} | {durChangeStr} | {statusStr} | {op.Verdict.ToString().ToUpperInvariant()} |");
        }

        sb.AppendLine();
        if (comp.Failures.Count > 0)
        {
            sb.AppendLine("### Regressions & Failures");
            foreach (var f in comp.Failures)
            {
                sb.AppendLine(inv, $"- :x: {f}");
            }
            sb.AppendLine();
        }

        if (comp.Warnings.Count > 0)
        {
            sb.AppendLine("### Warnings");
            foreach (var w in comp.Warnings)
            {
                sb.AppendLine(inv, $"- :warning: {w}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("> Payload reduction is a context-efficiency proxy, not a measurement of total LLM token usage.");
        return sb.ToString();
    }
}
