using System.Diagnostics;
using System.Reflection;
using AgentProof.Benchmarks.Model;
using AgentProof.Domain;

namespace AgentProof.Benchmarks.Execution;

public sealed class BenchmarkRunner
{
    public static async Task<BenchmarkRun> RunAsync(
        string repositoryPath,
        string scenarioName = "FullVerification",
        TaskContext? taskContext = null,
        int iterations = 3,
        string? serverAssemblyPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations), "Iterations must be at least 1.");

        var fullRepoPath = Path.GetFullPath(repositoryPath);
        if (!Directory.Exists(fullRepoPath))
        {
            throw new DirectoryNotFoundException($"Repository path does not exist: {fullRepoPath}");
        }

        taskContext ??= CreateDefaultTaskContext();

        var operationNames = GetOperationsForScenario(scenarioName);
        var iterationResults = new List<List<BenchmarkOperationResult>>();

        for (int i = 0; i < iterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var client = await McpStdioClient.StartAsync(serverAssemblyPath, cancellationToken);

            var currentIterationResults = new List<BenchmarkOperationResult>();

            foreach (var op in operationNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var opResult = await ExecuteOperationAsync(client, op, fullRepoPath, taskContext, cancellationToken);
                currentIterationResults.Add(opResult);
            }

            iterationResults.Add(currentIterationResults);
        }

        var summaries = new List<BenchmarkOperationSummary>();
        for (int opIdx = 0; opIdx < operationNames.Count; opIdx++)
        {
            var opName = operationNames[opIdx];
            var opRuns = iterationResults.Select(iter => iter[opIdx]).ToList();

            var durations = opRuns.Select(r => r.DurationMs).OrderBy(d => d).ToList();
            var minDuration = durations.First();
            var maxDuration = durations.Last();
            var medianDuration = CalculateMedian(durations);

            var payloadBytesHistory = opRuns.Select(r => r.PayloadBytesUtf8).ToList();
            var isDeterministic = payloadBytesHistory.Distinct().Count() <= 1;

            var representative = opRuns.LastOrDefault(r => r.Success) ?? opRuns.Last();

            summaries.Add(new BenchmarkOperationSummary
            {
                Operation = opName,
                MinDurationMs = minDuration,
                MedianDurationMs = medianDuration,
                MaxDurationMs = maxDuration,
                PayloadBytesUtf8 = representative.PayloadBytesUtf8,
                PayloadCharacters = representative.PayloadCharacters,
                JsonRpcPayloadBytes = representative.JsonRpcPayloadBytes,
                ToolResultPayloadBytes = representative.ToolResultPayloadBytes,
                IsDeterministicPayload = isDeterministic,
                PayloadBytesHistory = payloadBytesHistory,
                Success = representative.Success,
                Error = representative.Error,
                WorkspaceCount = representative.WorkspaceCount,
                TechnologyCount = representative.TechnologyCount,
                FrameworkCount = representative.FrameworkCount,
                TestFrameworkCount = representative.TestFrameworkCount,
                RecommendationCount = representative.RecommendationCount,
                StepCount = representative.StepCount,
                EvidenceCount = representative.EvidenceCount,
                GapCount = representative.GapCount,
                VerificationStatus = representative.VerificationStatus,
                PlannedStepCount = representative.PlannedStepCount,
                ExecutedStepCount = representative.ExecutedStepCount,
                PassedStepCount = representative.PassedStepCount,
                FailedStepCount = representative.FailedStepCount,
                TimedOutStepCount = representative.TimedOutStepCount,
                NotRunStepCount = representative.NotRunStepCount,
                IterationRuns = opRuns
            });
        }

        var repoName = Path.GetFileName(fullRepoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(repoName)) repoName = fullRepoPath;

        var totalPayloadBytes = summaries.Sum(s => s.PayloadBytesUtf8);
        var totalDuration = summaries.Sum(s => s.MedianDurationMs);

        return new BenchmarkRun
        {
            RepositoryName = repoName,
            RepositoryPath = fullRepoPath,
            ScenarioName = scenarioName,
            TimestampUtc = DateTimeOffset.UtcNow,
            AgentProofVersion = GetAgentProofVersion(),
            GitCommitSha = TryGetGitCommitSha(fullRepoPath) ?? TryGetGitCommitSha(AppContext.BaseDirectory),
            Iterations = iterations,
            Operations = summaries,
            TotalPayloadBytesUtf8 = totalPayloadBytes,
            TotalDurationMs = totalDuration
        };
    }

    private static IReadOnlyList<string> GetOperationsForScenario(string scenarioName)
    {
        return scenarioName.ToLowerInvariant() switch
        {
            "analyzeonly" => ["analyze_repository"],
            "planning" => ["analyze_repository", "create_verification_plan"],
            "fullverification" => ["analyze_repository", "recommend_skills", "create_verification_plan", "verify"],
            "full" => ["analyze_repository", "recommend_skills", "create_verification_plan", "verify"],
            _ => throw new ArgumentException($"Unknown scenario: '{scenarioName}'. Supported scenarios: AnalyzeOnly, Planning, FullVerification.")
        };
    }

    private static async Task<BenchmarkOperationResult> ExecuteOperationAsync(
        McpStdioClient client,
        string operation,
        string repositoryPath,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        return operation switch
        {
            "analyze_repository" => await client.CallToolAsync(
                "analyze_repository",
                new { repositoryPath },
                cancellationToken),

            "recommend_skills" => await client.CallToolAsync(
                "recommend_skills",
                new { repositoryPath, taskContext },
                cancellationToken),

            "create_verification_plan" => await client.CallToolAsync(
                "create_verification_plan",
                new { repositoryPath, taskContext },
                cancellationToken),

            "verify" => await client.CallToolAsync(
                "verify",
                new { repositoryPath, taskContext },
                cancellationToken),

            _ => throw new ArgumentException($"Unknown operation: '{operation}'")
        };
    }

    public static double CalculateMedian(IReadOnlyList<double> sortedValues)
    {
        if (sortedValues == null || sortedValues.Count == 0) return 0;
        int count = sortedValues.Count;
        if (count % 2 == 1)
        {
            return sortedValues[count / 2];
        }
        return (sortedValues[(count / 2) - 1] + sortedValues[count / 2]) / 2.0;
    }

    public static double CalculatePercentageReduction(long baseline, long candidate)
    {
        if (baseline == 0) return 0;
        return ((double)(baseline - candidate) / baseline) * 100.0;
    }

    public static double CalculatePercentageChange(double baseline, double candidate)
    {
        if (baseline == 0) return 0;
        return ((candidate - baseline) / baseline) * 100.0;
    }

    public static TaskContext CreateDefaultTaskContext()
    {
        return new TaskContext
        {
            Description = "Deterministic context efficiency verification",
            TaskType = TaskType.Feature,
            Complexity = TaskComplexity.Medium,
            AffectedAreas = [AffectedArea.Backend, AffectedArea.Frontend],
            Contract = new TaskContract
            {
                Goal = "Deterministic context efficiency verification",
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests],
                AcceptanceCriteria =
                [
                    new AcceptanceCriterion
                    {
                        Id = "AC-1",
                        Description = "Build and test verification succeeds",
                        RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests]
                    }
                ]
            }
        };
    }

    private static string GetAgentProofVersion()
    {
        var infoVersion = typeof(AgentProof.Mcp.AgentProofTools).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            var plusIdx = infoVersion.IndexOf('+');
            return plusIdx > 0 ? infoVersion[..plusIdx] : infoVersion;
        }

        var version = typeof(AgentProof.Mcp.AgentProofTools).Assembly.GetName().Version;
        return version != null ? version.ToString() : "0.3.0-preview.2";
    }

    private static string? TryGetGitCommitSha(string workingDirectory)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --short HEAD",
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var sha = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(1000);
                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(sha))
                {
                    return sha;
                }
            }
        }
        catch
        {
            // Ignore git lookup errors
        }
        return null;
    }
}
