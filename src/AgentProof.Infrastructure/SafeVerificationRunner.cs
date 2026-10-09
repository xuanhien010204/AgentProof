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
        try
        {
            var workingDirectory = ValidateWorkingDirectory(root, step.WorkingDirectory);
            ValidateCommand(root, workingDirectory, step.Command);
            var startInfo = CreateStartInfo(step.Command, workingDirectory);

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

                var output = Summarize(await stdout, await stderr);
                var passed = process.ExitCode == 0;
                return new VerificationEvidence(step,
                    passed ? VerificationStepStatus.Passed : VerificationStepStatus.Failed,
                    process.ExitCode, stopwatch.Elapsed, output,
                    passed ? null : $"Process exited with code {process.ExitCode}.");
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
        return SecretPattern().Replace(combined.Trim(), "$1=[REDACTED]");
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    [GeneratedRegex("(?i)(password|api[_-]?key|token|secret)\\s*[:=]\\s*\\S+")]
    private static partial Regex SecretPattern();
}
