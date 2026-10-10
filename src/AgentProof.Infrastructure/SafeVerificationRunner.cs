using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentProof.Application;
using AgentProof.Domain;

namespace AgentProof.Infrastructure;

public sealed partial class SafeVerificationRunner : IVerificationRunner
{
    private const int OutputLimit = 65_536;
    private static readonly HashSet<string> DotNetVerbs = new(StringComparer.Ordinal) { "restore", "build", "test" };
    private static readonly HashSet<string> NodeScripts = new(StringComparer.Ordinal) { "lint", "typecheck", "test", "build" };
    private static readonly HashSet<string> NodeExecutables = new(StringComparer.OrdinalIgnoreCase) { "npm", "npx", "pnpm", "yarn" };
    private static readonly string[] PlaywrightArguments = ["--no-install", "playwright", "test"];

    public async Task<VerificationExecutionResult> RunAsync(
        string repositoryPath, VerificationPlan plan, CancellationToken cancellationToken = default)
    {
        var root = LocalRepositoryAnalyzer.NormalizeExistingDirectory(repositoryPath);
        var evidence = new List<VerificationEvidence>();
        var gaps = plan.Gaps.ToList();
        if (plan.Steps.Count == 0)
            gaps.Add(new VerificationGap("NO_VERIFICATION_STEPS", "No safe verification steps could be generated for this repository."));

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            cancellationToken.ThrowIfCancellationRequested();
            var stepEvidence = await ExecuteAsync(root, step, cancellationToken);
            evidence.Add(stepEvidence);
            if (stepEvidence.Status != VerificationStepStatus.Passed && step.Required)
            {
                for (var j = i + 1; j < plan.Steps.Count; j++)
                {
                    evidence.Add(new VerificationEvidence(
                        plan.Steps[j],
                        VerificationStepStatus.NotRun,
                        null,
                        TimeSpan.Zero,
                        string.Empty,
                        "Not run because a prior required verification step did not pass."));
                }
                break;
            }
        }

        return new VerificationExecutionResult(evidence, gaps);
    }

    internal Action<Process>? ProcessStartedForTesting { get; init; }

    private async Task<VerificationEvidence> ExecuteAsync(
        string root, VerificationStep step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        string? isolatedArtifactDirectory = null;
        try
        {
            var workingDirectory = ValidateWorkingDirectory(root, step.WorkingDirectory);
            ValidateCommand(root, workingDirectory, step.Command);
            var startInfo = CreateStartInfo(step.Command, workingDirectory);

            if (step.ProvidedEvidence.Contains(EvidenceType.Tests))
            {
                var runId = $"AP-{Guid.NewGuid():N}";
                isolatedArtifactDirectory = Path.Combine(Path.GetTempPath(), "AgentProof", "TestRuns", runId);
                Directory.CreateDirectory(isolatedArtifactDirectory);
                startInfo.EnvironmentVariables["AGENTPROOF_RUN_ID"] = runId;
                startInfo.EnvironmentVariables["AGENTPROOF_RESULTS_DIR"] = isolatedArtifactDirectory;
                startInfo.EnvironmentVariables["CI"] = "true";

                var executable = Path.GetFileNameWithoutExtension(step.Command.Executable).ToLowerInvariant();
                if (executable == "dotnet" && step.Command.Arguments.Contains("test"))
                {
                    if (!step.Command.Arguments.Contains("--results-directory"))
                    {
                        startInfo.ArgumentList.Add("--results-directory");
                        startInfo.ArgumentList.Add(isolatedArtifactDirectory);
                    }
                    if (!step.Command.Arguments.Contains("--logger") && !step.Command.Arguments.Any(a => a.StartsWith("-l", StringComparison.Ordinal)))
                    {
                        startInfo.ArgumentList.Add("--logger");
                        startInfo.ArgumentList.Add("trx");
                    }
                }
            }

            var startTimeUtc = DateTime.UtcNow;
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) throw new InvalidOperationException("The verification process could not be started.");
            try { process.StandardInput.Close(); } catch { }

            try
            {
                ProcessStartedForTesting?.Invoke(process);
                using var timeout = new CancellationTokenSource(step.Timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                var stdout = CaptureAsync(process.StandardOutput, linked.Token);
                var stderr = CaptureAsync(process.StandardError, linked.Token);
                try
                {
                    await process.WaitForExitAsync(linked.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    await process.WaitForExitAsync(CancellationToken.None);
                    throw;
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    TryKill(process);
                    await process.WaitForExitAsync(CancellationToken.None);
                    var timedOutOutput = Summarize(await stdout, await stderr);
                    return new VerificationEvidence(step, VerificationStepStatus.TimedOut, null, stopwatch.Elapsed,
                        timedOutOutput, $"Command exceeded timeout of {step.Timeout}.");
                }

                try
                {
                    await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                }
                catch (TimeoutException) { }

                var stdoutText = stdout.IsCompleted ? await stdout : string.Empty;
                var stderrText = stderr.IsCompleted ? await stderr : string.Empty;
                var output = Summarize(stdoutText, stderrText);
                var passed = process.ExitCode == 0;
                string? failureReason = passed ? null : $"Process exited with code {process.ExitCode}.";

                if (passed && step.ProvidedEvidence.Contains(EvidenceType.Tests))
                {
                    if (!HasTestExecutionAssurance(step, stdoutText, stderrText, out var testFailureReason, workingDirectory, startTimeUtc, isolatedArtifactDirectory))
                    {
                        passed = false;
                        failureReason = testFailureReason;
                    }
                }

                return new VerificationEvidence(step,
                    passed ? VerificationStepStatus.Passed : VerificationStepStatus.Failed,
                    process.ExitCode, stopwatch.Elapsed, output, failureReason);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new VerificationEvidence(step, VerificationStepStatus.Failed, null, stopwatch.Elapsed,
                string.Empty, ex.Message);
        }
        finally
        {
            if (isolatedArtifactDirectory != null)
            {
                try { if (Directory.Exists(isolatedArtifactDirectory)) Directory.Delete(isolatedArtifactDirectory, true); } catch { }
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(VerificationCommand command, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.EnvironmentVariables["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        startInfo.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.EnvironmentVariables["DOTNET_CLI_DO_NOT_USE_GLOBAL_MUTEX"] = "1";

        if (OperatingSystem.IsWindows() && NodeExecutables.Contains(command.Executable))
        {
            var launcher = ResolveWindowsNodeLauncher(command.Executable);
            if (launcher is not null)
            {
                startInfo.FileName = launcher;
                foreach (var argument in command.Arguments) startInfo.ArgumentList.Add(argument);
                return startInfo;
            }
        }

        foreach (var argument in command.Arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    internal static string? ResolveWindowsNodeLauncher(string executable)
    {
        if (!OperatingSystem.IsWindows() || !NodeExecutables.Contains(executable)) return null;
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in new[] { ".cmd", ".exe" })
            {
                var candidate = Path.Combine(directory, executable + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Contains('"')) throw new InvalidOperationException("Node verification arguments cannot contain quotes.");
        return $"\"{value}\"";
    }

    private static string ValidateWorkingDirectory(string root, string workingDirectory)
    {
        var full = Path.GetFullPath(workingDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!full.Equals(root, comparison) && !full.StartsWith(rootPrefix, comparison))
            throw new InvalidOperationException("Verification working directory must be inside the repository root.");
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        return full;
    }

    private static void ValidateCommand(string root, string workingDirectory, VerificationCommand command)
    {
        var executable = Path.GetFileNameWithoutExtension(command.Executable).ToLowerInvariant();
        var args = command.Arguments;
        if (executable == "dotnet" && args.Count is >= 1 and <= 2 && DotNetVerbs.Contains(args[0]))
        {
            if (args.Count == 1) return;
            var target = args[1];
            if (Path.IsPathRooted(target) || !(target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                target.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The .NET verification target is not an approved solution or project path.");
            var resolved = Path.GetFullPath(Path.Combine(workingDirectory, target));
            ValidatePathInsideRoot(root, resolved);
            if (!File.Exists(resolved)) throw new InvalidOperationException("The .NET verification target does not exist.");
            return;
        }

        if (NodeExecutables.Contains(executable))
        {
            if (!string.Equals(command.Executable, executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Node verification executables must use an approved logical command name.");
            if (args.Count == 2 && args[0] == "run" && NodeScripts.Contains(args[1])) return;
        }
        if (executable == "npx" && args.SequenceEqual(PlaywrightArguments)) return;
        throw new InvalidOperationException("Command is not in the AgentProof verification allowlist.");
    }

    private static void ValidatePathInsideRoot(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, comparison)) throw new InvalidOperationException("Verification target escapes the repository root.");
    }

    private static async Task<string> CaptureAsync(StreamReader reader, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder(Math.Min(OutputLimit, 4096));
        var buffer = new char[2048];
        int read;
        try
        {
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                var remaining = OutputLimit - builder.Length;
                if (remaining > 0) builder.Append(buffer, 0, Math.Min(read, remaining));
            }
        }
        catch (OperationCanceledException) { }
        if (builder.Length == OutputLimit) builder.AppendLine().Append("[output truncated]");
        return builder.ToString();
    }

    internal static string Summarize(string stdout, string stderr)
    {
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
        var clean = AnsiPattern().Replace(combined, "");
        return SecretPattern().Replace(clean.Trim(), "$1=[REDACTED]");
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    [GeneratedRegex("(?i)(password|api[_-]?key|token|secret)\\s*[:=]\\s*\\S+")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiPattern();

    internal static bool HasTestExecutionAssurance(
        VerificationStep step, string stdout, string stderr, out string? failureReason) =>
        HasTestExecutionAssurance(step, stdout, stderr, out failureReason, workingDirectory: null, executionStartTimeUtc: null, isolatedArtifactDirectory: null);

    internal static bool HasTestExecutionAssurance(
        VerificationStep step,
        string stdout,
        string stderr,
        out string? failureReason,
        string? workingDirectory,
        DateTime? executionStartTimeUtc) =>
        HasTestExecutionAssurance(step, stdout, stderr, out failureReason, workingDirectory, executionStartTimeUtc, isolatedArtifactDirectory: null);

    internal static bool HasTestExecutionAssurance(
        VerificationStep step,
        string stdout,
        string stderr,
        out string? failureReason,
        string? workingDirectory,
        DateTime? executionStartTimeUtc,
        string? isolatedArtifactDirectory)
    {
        var executable = Path.GetFileNameWithoutExtension(step.Command.Executable).ToLowerInvariant();
        var isDotNet = executable == "dotnet" && step.Command.Arguments.Contains("test");
        var isNode = NodeExecutables.Contains(executable) || executable == "node";

        if (!string.IsNullOrWhiteSpace(isolatedArtifactDirectory) && Directory.Exists(isolatedArtifactDirectory))
        {
            if (TryEvaluateArtifactsInDirectory(isDotNet, isNode, isolatedArtifactDirectory, executionStartTimeUtc, out var isolatedPassed, out var isolatedReason))
            {
                if (isolatedPassed)
                {
                    failureReason = null;
                    return true;
                }

                failureReason = isolatedReason;
                return false;
            }
        }
        else if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            if (TryEvaluateDesignatedReportArtifacts(isDotNet, isNode, workingDirectory, executionStartTimeUtc, out var designatedPassed, out var designatedReason))
            {
                if (designatedPassed)
                {
                    failureReason = null;
                    return true;
                }

                failureReason = designatedReason;
                return false;
            }
        }

        var raw = $"{stdout}\n{stderr}";
        var combined = AnsiPattern().Replace(raw, "");

        if (combined.Contains("[output truncated]"))
        {
            failureReason = "Test output was truncated and no valid test report artifact was found.";
            return false;
        }

        if (isDotNet)
        {
            if (DotNetZeroTestsPattern().IsMatch(combined))
            {
                failureReason = "No tests were executed (zero tests discovered or executed).";
                return false;
            }

            if (DotNetTestsPassedPattern().IsMatch(combined) || MtpTestsPassedPattern().IsMatch(combined))
            {
                failureReason = null;
                return true;
            }

            failureReason = "No tests were executed (zero tests discovered or executed).";
            return false;
        }

        if (isNode)
        {
            if (NodeZeroTestsPattern().IsMatch(combined))
            {
                failureReason = "No tests were executed (zero tests discovered or executed).";
                return false;
            }

            if (NodeTapTestsPassedPattern().IsMatch(combined) ||
                NodeSpecTestsPassedPattern().IsMatch(combined) ||
                JestTestsPassedPattern().IsMatch(combined) ||
                VitestTestsPassedPattern().IsMatch(combined) ||
                MochaTestsPassedPattern().IsMatch(combined))
            {
                failureReason = null;
                return true;
            }

            failureReason = "No tests were executed (zero tests discovered or executed).";
            return false;
        }

        failureReason = "No tests were executed (zero tests discovered or executed).";
        return false;
    }

    private static bool TryEvaluateDesignatedReportArtifacts(
        bool isDotNet, bool isNode, string workingDirectory, DateTime? executionStartTimeUtc, out bool isPassed, out string? failureReason)
    {
        isPassed = false;
        failureReason = null;

        if (isDotNet)
        {
            var candidateDirs = new List<string>();
            var testResultsDir = Path.Combine(workingDirectory, "TestResults");
            if (Directory.Exists(testResultsDir)) candidateDirs.Add(testResultsDir);
            candidateDirs.Add(workingDirectory);

            foreach (var dir in candidateDirs)
            {
                if (TryEvaluateTrxReportsInDirectory(dir, executionStartTimeUtc, out isPassed, out failureReason))
                {
                    return true;
                }
            }
        }
        else if (isNode)
        {
            if (TryEvaluateJsonReportsInDirectory(workingDirectory, executionStartTimeUtc, out isPassed, out failureReason))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryEvaluateArtifactsInDirectory(
        bool isDotNet, bool isNode, string directory, DateTime? executionStartTimeUtc, out bool isPassed, out string? failureReason)
    {
        isPassed = false;
        failureReason = null;

        if (isDotNet)
        {
            return TryEvaluateTrxReportsInDirectory(directory, executionStartTimeUtc, out isPassed, out failureReason);
        }

        if (isNode)
        {
            return TryEvaluateJsonReportsInDirectory(directory, executionStartTimeUtc, out isPassed, out failureReason);
        }

        return false;
    }

    private static bool TryEvaluateTrxReportsInDirectory(
        string directory, DateTime? executionStartTimeUtc, out bool isPassed, out string? failureReason)
    {
        isPassed = false;
        failureReason = null;

        try
        {
            var files = Directory.GetFiles(directory, "*.trx", SearchOption.TopDirectoryOnly);
            if (files.Length == 0) return false;

            int aggregateTotal = 0;
            int aggregatePassed = 0;
            int aggregateFailed = 0;
            bool foundValidReport = false;

            foreach (var file in files)
            {
                if (executionStartTimeUtc.HasValue)
                {
                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if (lastWrite < executionStartTimeUtc.Value.AddSeconds(-1))
                    {
                        continue;
                    }
                }

                try
                {
                    var doc = XDocument.Load(file);
                    if (doc.Root?.Name.LocalName != "TestRun")
                    {
                        failureReason = "Test report artifact is malformed or could not be parsed.";
                        return true;
                    }

                    var counters = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Counters");
                    if (counters == null || counters.Attribute("total") == null || counters.Attribute("passed") == null)
                    {
                        failureReason = "Test report artifact is malformed or could not be parsed.";
                        return true;
                    }

                    var total = (int?)counters.Attribute("total") ?? 0;
                    var passed = (int?)counters.Attribute("passed") ?? 0;
                    var failed = (int?)counters.Attribute("failed") ?? 0;

                    aggregateTotal += total;
                    aggregatePassed += passed;
                    aggregateFailed += failed;
                    foundValidReport = true;
                }
                catch (Exception)
                {
                    failureReason = "Test report artifact is malformed or could not be parsed.";
                    return true;
                }
            }

            if (!foundValidReport) return false;

            if (aggregateTotal == 0)
            {
                failureReason = "No tests were executed (zero tests discovered or executed).";
                return true;
            }

            if (aggregateFailed > 0)
            {
                failureReason = $"Test run contained {aggregateFailed} failing test(s).";
                return true;
            }

            if (aggregatePassed > 0)
            {
                isPassed = true;
                failureReason = null;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return false;
    }

    private static bool TryEvaluateJsonReportsInDirectory(
        string directory, DateTime? executionStartTimeUtc, out bool isPassed, out string? failureReason)
    {
        isPassed = false;
        failureReason = null;

        try
        {
            var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    var name = Path.GetFileName(f);
                    return name.Equals("test-report.json", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("test-results.json", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("report.json", StringComparison.OrdinalIgnoreCase);
                })
                .ToArray();

            if (files.Length == 0) return false;

            int aggregateTotal = 0;
            int aggregatePassed = 0;
            int aggregateFailed = 0;
            bool foundValidReport = false;

            foreach (var file in files)
            {
                if (executionStartTimeUtc.HasValue)
                {
                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if (lastWrite < executionStartTimeUtc.Value.AddSeconds(-1))
                    {
                        continue;
                    }
                }

                try
                {
                    using var stream = File.OpenRead(file);
                    using var doc = JsonDocument.Parse(stream);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("numTotalTests", out var numTotal) &&
                        root.TryGetProperty("numPassedTests", out var numPassed) &&
                        root.TryGetProperty("numFailedTests", out var numFailed) &&
                        numTotal.TryGetInt32(out var total) &&
                        numPassed.TryGetInt32(out var passed) &&
                        numFailed.TryGetInt32(out var failed))
                    {
                        aggregateTotal += total;
                        aggregatePassed += passed;
                        aggregateFailed += failed;
                        foundValidReport = true;
                    }
                    else if (root.TryGetProperty("stats", out var stats) &&
                             stats.TryGetProperty("tests", out var mTests) &&
                             stats.TryGetProperty("passes", out var mPasses) &&
                             stats.TryGetProperty("failures", out var mFailures) &&
                             mTests.TryGetInt32(out var mochaTotal) &&
                             mPasses.TryGetInt32(out var mochaPassed) &&
                             mFailures.TryGetInt32(out var mochaFailed))
                    {
                        aggregateTotal += mochaTotal;
                        aggregatePassed += mochaPassed;
                        aggregateFailed += mochaFailed;
                        foundValidReport = true;
                    }
                    else
                    {
                        failureReason = "Test report artifact is malformed or could not be parsed.";
                        return true;
                    }
                }
                catch (JsonException)
                {
                    failureReason = "Test report artifact is malformed or could not be parsed.";
                    return true;
                }
            }

            if (!foundValidReport) return false;

            if (aggregateTotal == 0)
            {
                failureReason = "No tests were executed (zero tests discovered or executed).";
                return true;
            }

            if (aggregateFailed > 0)
            {
                failureReason = $"Test run contained {aggregateFailed} failing test(s).";
                return true;
            }

            if (aggregatePassed > 0)
            {
                isPassed = true;
                failureReason = null;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return false;
    }

    [GeneratedRegex(@"(?i)(Passed!\s*-\s*Failed:\s*0,\s*Passed:\s*[1-9]\d*|Total tests:\s*[1-9]\d*[\s\S]*?Passed:\s*[1-9]\d*[\s\S]*?Failed:\s*0)")]
    private static partial Regex DotNetTestsPassedPattern();

    [GeneratedRegex(@"(?i)Test run summary:\s*Passed!(?:[\s\S]*?\btotal:\s*[1-9]\d*)?(?:[\s\S]*?\b(?:succeeded|passed):\s*[1-9]\d*)?[\s\S]*?\bfailed:\s*0\b")]
    private static partial Regex MtpTestsPassedPattern();

    [GeneratedRegex(@"(?i)(No test matches the given|A total of 0 test files matched|No tests found to run|Total tests:\s*0\b|Passed!\s*-\s*Failed:\s*0,\s*Passed:\s*0\b|Total:\s*0\b|succeeded:\s*0\b)")]
    private static partial Regex DotNetZeroTestsPattern();

    [GeneratedRegex(@"(?m)(?:TAP version 13|^ok\s+\d+\s+-)[\s\S]*?#\s*tests\s*[1-9]\d*[\s\S]*?#\s*pass\s*[1-9]\d*[\s\S]*?#\s*fail\s*0\b")]
    private static partial Regex NodeTapTestsPassedPattern();

    [GeneratedRegex(@"(?i)ℹ\s*tests\s*[1-9]\d*[\s\S]*?ℹ\s*pass\s*[1-9]\d*[\s\S]*?ℹ\s*fail\s*0\b")]
    private static partial Regex NodeSpecTestsPassedPattern();

    [GeneratedRegex(@"(?i)Test Suites:\s*.*?\b[1-9]\d*\s*passed,\s*[1-9]\d*\s*total[\s\S]*?Tests:\s*.*?\b[1-9]\d*\s*passed,\s*[1-9]\d*\s*total")]
    private static partial Regex JestTestsPassedPattern();

    [GeneratedRegex(@"(?i)\bTests\s+[1-9]\d*\s*passed\s*\([1-9]\d*\)")]
    private static partial Regex VitestTestsPassedPattern();

    [GeneratedRegex(@"(?m)^\s*[1-9]\d*\s+passing\s+\(\d+(?:\.\d+)?(?:ms|s|m)\)")]
    private static partial Regex MochaTestsPassedPattern();

    [GeneratedRegex(@"(?i)(#\s*tests\s*0\b|#\s*pass\s*0\b|\b0\s*passing\b|\b0\s*passed\b|No tests found|Tests:\s*0 total|ℹ\s*tests\s*0\b)")]
    private static partial Regex NodeZeroTestsPattern();
}
