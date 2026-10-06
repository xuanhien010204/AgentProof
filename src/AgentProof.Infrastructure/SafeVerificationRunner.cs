using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AgentProof.Application;
using AgentProof.Domain;

namespace AgentProof.Infrastructure;

public sealed partial class SafeVerificationRunner : IVerificationRunner
{
    private const int OutputLimit = 65_536;
    private static readonly HashSet<string> DotNetVerbs = new(StringComparer.Ordinal) { "restore", "build", "test" };
    private static readonly HashSet<string> NodeScripts = new(StringComparer.Ordinal) { "lint", "typecheck", "test", "build" };
    private static readonly string[] PlaywrightArguments = ["--no-install", "playwright", "test"];

    public async Task<VerificationResult> RunAsync(
        string repositoryPath, VerificationPlan plan, CancellationToken cancellationToken = default)
    {
        var root = LocalRepositoryAnalyzer.NormalizeExistingDirectory(repositoryPath);
        var evidence = new List<VerificationEvidence>();
        var gaps = plan.Gaps.ToList();
        if (plan.Steps.Count == 0)
            gaps.Add(new VerificationGap("NO_VERIFICATION_STEPS", "No safe verification steps could be generated for this repository."));

        foreach (var step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            evidence.Add(await ExecuteAsync(root, step, cancellationToken));
            if (evidence[^1].Status != VerificationStepStatus.Passed && step.Required) break;
        }

        var requiredFailure = evidence.Any(x => x.Step.Required && x.Status != VerificationStepStatus.Passed);
        var optionalFailure = evidence.Any(x => !x.Step.Required && x.Status != VerificationStepStatus.Passed);
        var status = requiredFailure
            ? VerificationStatus.NotVerified
            : gaps.Count > 0 || optionalFailure
                ? VerificationStatus.PartiallyVerified
                : VerificationStatus.Verified;
        return new VerificationResult(status, evidence, gaps);
    }

    private static async Task<VerificationEvidence> ExecuteAsync(
        string root, VerificationStep step, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var workingDirectory = ValidateWorkingDirectory(root, step.WorkingDirectory);
            ValidateCommand(root, step.Command);
            var startInfo = new ProcessStartInfo
            {
                FileName = step.Command.Executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in step.Command.Arguments) startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) throw new InvalidOperationException("The verification process could not be started.");
            var stdout = CaptureAsync(process.StandardOutput);
            var stderr = CaptureAsync(process.StandardError);
            using var timeout = new CancellationTokenSource(step.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                var timedOutOutput = Summarize(await stdout, await stderr);
                return new VerificationEvidence(step, VerificationStepStatus.TimedOut, null, stopwatch.Elapsed,
                    timedOutOutput, $"Command exceeded timeout of {step.Timeout}.");
            }

            var output = Summarize(await stdout, await stderr);
            var passed = process.ExitCode == 0;
            return new VerificationEvidence(step,
                passed ? VerificationStepStatus.Passed : VerificationStepStatus.Failed,
                process.ExitCode, stopwatch.Elapsed, output,
                passed ? null : $"Process exited with code {process.ExitCode}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new VerificationEvidence(step, VerificationStepStatus.Failed, null, stopwatch.Elapsed,
                string.Empty, ex.Message);
        }
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

    private static void ValidateCommand(string root, VerificationCommand command)
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
            var resolved = Path.GetFullPath(Path.Combine(root, target));
            ValidatePathInsideRoot(root, resolved);
            if (!File.Exists(resolved)) throw new InvalidOperationException("The .NET verification target does not exist.");
            return;
        }

        if (executable is "npm" or "pnpm" or "yarn" && args.Count == 2 && args[0] == "run" && NodeScripts.Contains(args[1])) return;
        if (executable == "npx" && args.SequenceEqual(PlaywrightArguments)) return;
        throw new InvalidOperationException("Command is not in the AgentProof verification allowlist.");
    }

    private static void ValidatePathInsideRoot(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, comparison)) throw new InvalidOperationException("Verification target escapes the repository root.");
    }

    private static async Task<string> CaptureAsync(StreamReader reader)
    {
        var builder = new StringBuilder(Math.Min(OutputLimit, 4096));
        var buffer = new char[2048];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            var remaining = OutputLimit - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(read, remaining));
        }
        if (builder.Length == OutputLimit) builder.AppendLine().Append("[output truncated]");
        return builder.ToString();
    }

    private static string Summarize(string stdout, string stderr)
    {
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
        return SecretPattern().Replace(combined.Trim(), "$1=[REDACTED]");
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    [GeneratedRegex("(?i)(password|api[_-]?key|token|secret)\\s*[:=]\\s*\\S+")]
    private static partial Regex SecretPattern();
}
