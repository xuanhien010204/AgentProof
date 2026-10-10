using System.Diagnostics;
using AgentProof.Domain;
using AgentProof.Infrastructure;

namespace AgentProof.UnitTests;

public sealed class VerificationRunnerTests
{
    [Fact]
    public async Task SuccessfulProcessRecordsExitCodeAndDuration()
    {
        using var repo = MinimalProject(valid: true);
        var result = await RunBuildAsync(repo.Root, TimeSpan.FromMinutes(1));
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal(VerificationStepStatus.Passed, evidence.Status);
        Assert.Equal(0, evidence.ExitCode);
        Assert.True(evidence.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task FailedProcessProducesNotVerifiedEvidence()
    {
        using var repo = MinimalProject(valid: false);
        var result = await RunBuildAsync(repo.Root, TimeSpan.FromMinutes(1));
        Assert.Equal(VerificationStepStatus.Failed, Assert.Single(result.Evidence).Status);
    }

    [Fact]
    public async Task TimeoutIsEnforced()
    {
        using var repo = MinimalProject(valid: true);
        var result = await RunBuildAsync(repo.Root, TimeSpan.Zero);
        Assert.Equal(VerificationStepStatus.TimedOut, Assert.Single(result.Evidence).Status);
    }

    [Fact]
    public async Task TimeoutKillsVerificationProcess()
    {
        using var repo = MinimalProject(valid: true);
        int capturedPid = 0;
        var runner = new SafeVerificationRunner
        {
            ProcessStartedForTesting = p => capturedPid = p.Id
        };
        var step = new VerificationStep("build", "Build", ".NET",
            new VerificationCommand("dotnet", ["build", "Test.csproj"]), repo.Root, TimeSpan.Zero, true);

        var result = await runner.RunAsync(repo.Root, new VerificationPlan([step], []));

        Assert.Equal(VerificationStepStatus.TimedOut, Assert.Single(result.Evidence).Status);
        Assert.True(capturedPid > 0);
        Assert.True(IsProcessTerminated(capturedPid));
    }

    [Fact]
    public async Task CancellationKillsVerificationProcess()
    {
        using var repo = MinimalProject(valid: true);
        int capturedPid = 0;
        using var cts = new CancellationTokenSource();
        var runner = new SafeVerificationRunner
        {
            ProcessStartedForTesting = p =>
            {
                capturedPid = p.Id;
                cts.Cancel();
            }
        };
        var step = new VerificationStep("build", "Build", ".NET",
            new VerificationCommand("dotnet", ["build", "Test.csproj"]), repo.Root, TimeSpan.FromMinutes(1), true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(repo.Root, new VerificationPlan([step], []), cts.Token));

        Assert.True(capturedPid > 0);
        Assert.True(IsProcessTerminated(capturedPid));
    }

    [Fact]
    public async Task AvailableNpmCanExecuteNodeVerificationCommand()
    {
        if (OperatingSystem.IsWindows() && SafeVerificationRunner.ResolveWindowsNodeLauncher("npm") is null) return;
        if (!OperatingSystem.IsWindows() && SafeVerificationRunner.ResolveWindowsNodeLauncher("npm") is not null) return;

        using var repo = new TemporaryRepository();
        repo.Write("package.json", """{"scripts":{"test":"node -e \"const { test } = require('node:test'); const assert = require('node:assert'); test('t', () => assert.strictEqual(1, 1));\""}}""");
        var step = new VerificationStep("node-test", "Run Node.js test", "Node",
            new VerificationCommand("npm", ["run", "test"]), repo.Root, TimeSpan.FromMinutes(1), true, [EvidenceType.Tests], ".");

        var result = await new SafeVerificationRunner().RunAsync(repo.Root, new VerificationPlan([step], []));

        var evidence = Assert.Single(result.Evidence);
        Assert.True(evidence.Status == VerificationStepStatus.Passed,
            $"{evidence.FailureReason}\n{evidence.OutputSummary}");
    }

    [Fact]
    public async Task AvailableNpxCanExecutePlaywrightVerificationCommand()
    {
        if (!OperatingSystem.IsWindows() || SafeVerificationRunner.ResolveWindowsNodeLauncher("npx") is null) return;

        using var repo = new TemporaryRepository();
        repo.Write("package.json", "{\"name\":\"agentproof-runner-test\",\"version\":\"1.0.0\"}");
        repo.Write("node_modules/playwright/package.json", "{\"name\":\"playwright\",\"version\":\"1.0.0\",\"bin\":{\"playwright\":\"cli.js\"}}");
        repo.Write("node_modules/playwright/cli.js", "process.exit(0);\r\n");
        repo.Write("node_modules/.bin/playwright.cmd", "@echo off\r\nexit /b 0\r\n");
        var step = new VerificationStep("playwright", "Run Playwright", "Browser",
            new VerificationCommand("npx", ["--no-install", "playwright", "test"]), repo.Root, TimeSpan.FromMinutes(1), true, [EvidenceType.Browser], ".");

        var result = await new SafeVerificationRunner().RunAsync(repo.Root, new VerificationPlan([step], []));

        var evidence = Assert.Single(result.Evidence);
        Assert.True(evidence.Status == VerificationStepStatus.Passed,
            $"{evidence.FailureReason}\n{evidence.OutputSummary}");
    }

    private static bool IsProcessTerminated(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    [Fact]
    public async Task ArbitraryShellCommandIsRejected()
    {
        using var repo = MinimalProject(valid: true);
        var step = new VerificationStep("bad", "Bad", "Shell",
            new VerificationCommand("powershell", ["-Command", "Get-ChildItem"]), repo.Root, TimeSpan.FromSeconds(1), true);
        var result = await new SafeVerificationRunner().RunAsync(repo.Root, new VerificationPlan([step], []));
        Assert.Contains("allowlist", Assert.Single(result.Evidence).FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("cmd.exe", "/c", "dir")]
    [InlineData("bash", "-c", "ls")]
    [InlineData("sh", "-c", "ls")]
    [InlineData("dotnet", "run", "malicious")]
    [InlineData("npm", "run", "arbitrary_script")]
    [InlineData("npx", "malicious_package", "test")]
    public async Task ArbitraryCommandsRemainRejected(string executable, params string[] args)
    {
        using var repo = MinimalProject(valid: true);
        var step = new VerificationStep("bad", "Bad", "Test",
            new VerificationCommand(executable, args), repo.Root, TimeSpan.FromSeconds(1), true);
        var result = await new SafeVerificationRunner().RunAsync(repo.Root, new VerificationPlan([step], []));
        Assert.Contains("allowlist", Assert.Single(result.Evidence).FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerificationTargetCannotEscapeRepositoryRoot()
    {
        using var repo = MinimalProject(valid: true);
        var stepRelative = new VerificationStep("escape-rel", "Escape", ".NET",
            new VerificationCommand("dotnet", ["build", "../Escape.csproj"]), repo.Root, TimeSpan.FromSeconds(5), true);
        var resultRelative = await new SafeVerificationRunner().RunAsync(repo.Root, new VerificationPlan([stepRelative], []));
        Assert.Contains("escapes the repository root", Assert.Single(resultRelative.Evidence).FailureReason, StringComparison.OrdinalIgnoreCase);

        var stepRooted = new VerificationStep("escape-root", "Escape", ".NET",
            new VerificationCommand("dotnet", ["build", Path.Combine(Path.GetTempPath(), "Escape.csproj")]), repo.Root, TimeSpan.FromSeconds(5), true);
        var resultRooted = await new SafeVerificationRunner().RunAsync(repo.Root, new VerificationPlan([stepRooted], []));
        Assert.Contains("approved solution or project path", Assert.Single(resultRooted.Evidence).FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkingDirectoryCannotEscapeRepositoryRoot()
    {
        using var repo = MinimalProject(valid: true);
        var outsideDirectory = Path.GetTempPath();
        var step = new VerificationStep("outside-cwd", "Outside", ".NET",
            new VerificationCommand("dotnet", ["build", "Test.csproj"]), outsideDirectory, TimeSpan.FromSeconds(5), true);
        var result = await new SafeVerificationRunner().RunAsync(repo.Root, new VerificationPlan([step], []));
        Assert.Contains("must be inside the repository root", Assert.Single(result.Evidence).FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("password: super_secret_123", "password=[REDACTED]")]
    [InlineData("password = my_password!", "password=[REDACTED]")]
    [InlineData("api_key: ak_test_48192849", "api_key=[REDACTED]")]
    [InlineData("api-key=secret_token_val", "api-key=[REDACTED]")]
    [InlineData("TOKEN : eyJhbGciOiJIUzI1NiIsInR5cCI6", "TOKEN=[REDACTED]")]
    [InlineData("secret: my_classified_info", "secret=[REDACTED]")]
    public void SecretLikeOutputIsRedactedInSummarize(string raw, string expected)
    {
        var summarized = SafeVerificationRunner.Summarize(raw, string.Empty);
        Assert.Equal(expected, summarized);
        Assert.DoesNotContain("secret_", summarized);
        Assert.DoesNotContain("eyJhbGci", summarized);
    }

    [Fact]
    public async Task SecretLikeOutputIsRedactedFromBuildOutput()
    {
        using var repo = new TemporaryRepository();
        repo.Write("Test.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Target Name="PrintSecret" BeforeTargets="Build">
                <Message Importance="High" Text="api_key: live_secret_12345" />
              </Target>
            </Project>
            """);
        var result = await RunBuildAsync(repo.Root, TimeSpan.FromMinutes(1));
        var evidence = Assert.Single(result.Evidence);
        Assert.DoesNotContain("live_secret_12345", evidence.OutputSummary);
        Assert.Contains("api_key=[REDACTED]", evidence.OutputSummary);
    }

    [Fact]
    public async Task RunAsyncWhenRequiredStepFailsRemainingStepsAreReportedAsNotRun()
    {
        using var repo = MinimalProject(valid: false);
        var step1 = new VerificationStep("step-1-fail", "Step 1", ".NET",
            new VerificationCommand("dotnet", ["build", "Test.csproj"]), repo.Root, TimeSpan.FromMinutes(1), true);
        var step2 = new VerificationStep("step-2-skip", "Step 2", ".NET",
            new VerificationCommand("dotnet", ["test", "Test.csproj"]), repo.Root, TimeSpan.FromMinutes(1), true);
        var step3 = new VerificationStep("step-3-skip", "Step 3", ".NET",
            new VerificationCommand("dotnet", ["test", "Test.csproj"]), repo.Root, TimeSpan.FromMinutes(1), false);

        var result = await new SafeVerificationRunner().RunAsync(repo.Root, new VerificationPlan([step1, step2, step3], []));

        Assert.Equal(3, result.Evidence.Count);
        Assert.Equal(VerificationStepStatus.Failed, result.Evidence[0].Status);
        Assert.Equal(VerificationStepStatus.NotRun, result.Evidence[1].Status);
        Assert.Equal("Not run because a prior required verification step did not pass.", result.Evidence[1].FailureReason);
        Assert.Equal(VerificationStepStatus.NotRun, result.Evidence[2].Status);
        Assert.Equal("Not run because a prior required verification step did not pass.", result.Evidence[2].FailureReason);
    }

    [Fact]
    public async Task RunAsyncWhenRequiredStepTimesOutRemainingStepsAreReportedAsNotRun()
    {
        using var repo = MinimalProject(valid: true);
        var step1 = new VerificationStep("step-1-timeout", "Step 1", ".NET",
            new VerificationCommand("dotnet", ["build", "Test.csproj"]), repo.Root, TimeSpan.Zero, true);
        var step2 = new VerificationStep("step-2-skip", "Step 2", ".NET",
            new VerificationCommand("dotnet", ["test", "Test.csproj"]), repo.Root, TimeSpan.FromMinutes(1), true);

        var result = await new SafeVerificationRunner().RunAsync(repo.Root, new VerificationPlan([step1, step2], []));

        Assert.Equal(2, result.Evidence.Count);
        Assert.Equal(VerificationStepStatus.TimedOut, result.Evidence[0].Status);
        Assert.Equal(VerificationStepStatus.NotRun, result.Evidence[1].Status);
        Assert.Equal("Not run because a prior required verification step did not pass.", result.Evidence[1].FailureReason);
    }

    [Fact]
    public async Task ExecuteAsyncWhenProcessTimesOutTerminatesEntireProcessTree()
    {
        using var repo = MinimalProject(valid: true);
        int capturedPid = 0;
        var runner = new SafeVerificationRunner
        {
            ProcessStartedForTesting = p => capturedPid = p.Id
        };
        var step = new VerificationStep("build", "Build", ".NET",
            new VerificationCommand("dotnet", ["build", "Test.csproj"]), repo.Root, TimeSpan.Zero, true);

        var result = await runner.RunAsync(repo.Root, new VerificationPlan([step], []));

        Assert.Equal(VerificationStepStatus.TimedOut, Assert.Single(result.Evidence).Status);
        Assert.True(capturedPid > 0);
        Assert.True(IsProcessTerminated(capturedPid));
    }

    [Fact]
    public async Task EvidenceAccountingPlannedStepsEqualsPassedPlusFailedPlusTimedOutPlusNotRun()
    {
        using var repo = MinimalProject(valid: false);
        var step1 = new VerificationStep("step-1-fail", "Step 1", ".NET",
            new VerificationCommand("dotnet", ["build", "Test.csproj"]), repo.Root, TimeSpan.FromMinutes(1), true);
        var step2 = new VerificationStep("step-2-skip", "Step 2", ".NET",
            new VerificationCommand("dotnet", ["test", "Test.csproj"]), repo.Root, TimeSpan.FromMinutes(1), true);
        var step3 = new VerificationStep("step-3-skip", "Step 3", ".NET",
            new VerificationCommand("dotnet", ["test", "Test.csproj"]), repo.Root, TimeSpan.FromMinutes(1), true);

        var plan = new VerificationPlan([step1, step2, step3], []);
        var result = await new SafeVerificationRunner().RunAsync(repo.Root, plan);

        var plannedCount = plan.Steps.Count;
        var passedCount = result.Evidence.Count(e => e.Status == VerificationStepStatus.Passed);
        var failedCount = result.Evidence.Count(e => e.Status == VerificationStepStatus.Failed);
        var timedOutCount = result.Evidence.Count(e => e.Status == VerificationStepStatus.TimedOut);
        var notRunCount = result.Evidence.Count(e => e.Status == VerificationStepStatus.NotRun);

        Assert.Equal(3, plannedCount);
        Assert.Equal(0, passedCount);
        Assert.Equal(1, failedCount);
        Assert.Equal(0, timedOutCount);
        Assert.Equal(2, notRunCount);
        Assert.Equal(plannedCount, passedCount + failedCount + timedOutCount + notRunCount);
        Assert.Equal(plannedCount, result.Evidence.Count);
    }

    [Fact]
    public void DotNetTestsPassingOutputReturnsTrue()
    {
        var step = new VerificationStep("test", "Test", ".NET",
            new VerificationCommand("dotnet", ["test", "Tests.csproj"]), "C:\\repo", TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var stdout = "Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 923 ms - Education.Tests.dll (net10.0)";
        
        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, stdout, string.Empty, out var reason);

        Assert.True(result);
        Assert.Null(reason);
    }

    [Fact]
    public void DotNetTestsZeroTestsExecutedReturnsFalse()
    {
        var step = new VerificationStep("test", "Test", ".NET",
            new VerificationCommand("dotnet", ["test", "Tests.csproj"]), "C:\\repo", TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var stdout = "No test matches the given testcase filter `FullyQualifiedName=Foo` in Tests.dll";

        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, stdout, string.Empty, out var reason);

        Assert.False(result);
        Assert.Equal("No tests were executed (zero tests discovered or executed).", reason);
    }

    [Fact]
    public void DotNetTestsNonTestProjectOutputReturnsFalse()
    {
        var step = new VerificationStep("test", "Test", ".NET",
            new VerificationCommand("dotnet", ["test", "App.csproj"]), "C:\\repo", TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var stdout = "Determining projects to restore...\nAll projects are up-to-date for restore.";

        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, stdout, string.Empty, out var reason);

        Assert.False(result);
        Assert.Equal("No tests were executed (zero tests discovered or executed).", reason);
    }

    [Fact]
    public void DotNetTestsSummaryTotalZeroReturnsFalse()
    {
        var step = new VerificationStep("test", "Test", ".NET",
            new VerificationCommand("dotnet", ["test", "Tests.csproj"]), "C:\\repo", TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var stdout = "Passed!  - Failed:     0, Passed:     0, Skipped:     0, Total:     0";

        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, stdout, string.Empty, out var reason);

        Assert.False(result);
        Assert.Equal("No tests were executed (zero tests discovered or executed).", reason);
    }

    [Fact]
    public void NodeNativeTestTapPassingReturnsTrue()
    {
        var step = new VerificationStep("test", "Test", "Node",
            new VerificationCommand("npm", ["run", "test"]), "C:\\repo", TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var stdout = "TAP version 13\n# tests 3\n# pass 3\n# fail 0";

        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, stdout, string.Empty, out var reason);

        Assert.True(result);
        Assert.Null(reason);
    }

    [Fact]
    public void NodeNativeTestZeroTestsReturnsFalse()
    {
        var step = new VerificationStep("test", "Test", "Node",
            new VerificationCommand("npm", ["run", "test"]), "C:\\repo", TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var stdout = "TAP version 13\n# tests 0\n# pass 0";

        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, stdout, string.Empty, out var reason);

        Assert.False(result);
        Assert.Equal("No tests were executed (zero tests discovered or executed).", reason);
    }

    [Fact]
    public void NodeVitestOrJestPassingReturnsTrue()
    {
        var step = new VerificationStep("test", "Test", "Node",
            new VerificationCommand("npm", ["run", "test"]), "C:\\repo", TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var stdout = "Tests  12 passed (12)";

        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, stdout, string.Empty, out var reason);

        Assert.True(result);
        Assert.Null(reason);
    }

    [Fact]
    public void NodeEmptyOutputReturnsFalse()
    {
        var step = new VerificationStep("test", "Test", "Node",
            new VerificationCommand("npm", ["run", "test"]), "C:\\repo", TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);

        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, string.Empty, string.Empty, out var reason);

        Assert.False(result);
        Assert.Equal("No tests were executed (zero tests discovered or executed).", reason);
    }

    [Fact]
    public void NodeFakeConsoleLog1PassedReturnsFalse()
    {
        var step = new VerificationStep("test", "Test", "Node",
            new VerificationCommand("node", ["-e", "console.log('1 passed')"]), "C:\\repo", TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var stdout = "1 passed\n";

        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, stdout, string.Empty, out var reason);

        Assert.False(result);
        Assert.Equal("No tests were executed (zero tests discovered or executed).", reason);
    }

    [Fact]
    public void NodeJestPassingReturnsTrue()
    {
        var step = new VerificationStep("test", "Test", "Node",
            new VerificationCommand("npm", ["test"]), "C:\\repo", TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var stdout = "Test Suites: 2 passed, 2 total\nTests:       10 passed, 10 total\nSnapshots:   0 total\nTime:        2.345 s";

        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, stdout, string.Empty, out var reason);

        Assert.True(result);
        Assert.Null(reason);
    }

    [Fact]
    public void NodeSpecPassingReturnsTrue()
    {
        var step = new VerificationStep("test", "Test", "Node",
            new VerificationCommand("node", ["--test"]), "C:\\repo", TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var stdout = "ℹ tests 5\nℹ suites 1\nℹ pass 5\nℹ fail 0\nℹ cancelled 0\nℹ skipped 0\nℹ todo 0\nℹ duration_ms 42.12";

        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, stdout, string.Empty, out var reason);

        Assert.True(result);
        Assert.Null(reason);
    }

    [Fact]
    public void NodeMochaPassingReturnsTrue()
    {
        var step = new VerificationStep("test", "Test", "Node",
            new VerificationCommand("npx", ["mocha"]), "C:\\repo", TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var stdout = "  ✔ should do something useful\n  ✔ should do another thing\n\n  2 passing (45ms)";

        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, stdout, string.Empty, out var reason);

        Assert.True(result);
        Assert.Null(reason);
    }

    [Fact]
    public void DotNetMtpPassingReturnsTrue()
    {
        var step = new VerificationStep("test", "Test", ".NET",
            new VerificationCommand("dotnet", ["test", "Tests.csproj"]), "C:\\repo", TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var stdout = "Test run summary: Passed!\n  total: 5\n  failed: 0\n  succeeded: 5\n  skipped: 0\nduration: 1.2s";

        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, stdout, string.Empty, out var reason);

        Assert.True(result);
        Assert.Null(reason);
    }

    [Fact]
    public void TruncatedOutputReturnsFalse()
    {
        var step = new VerificationStep("test", "Test", "Node",
            new VerificationCommand("npm", ["test"]), "C:\\repo", TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var stdout = "Test Suites: 2 passed, 2 total\n[output truncated]";

        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, stdout, string.Empty, out var reason);

        Assert.False(result);
        Assert.Equal("Test output was truncated and no valid test report artifact was found.", reason);
    }

    [Fact]
    public void TrxArtifactValidPassingReturnsTrue()
    {
        using var repo = new TemporaryRepository();
        var trxPath = Path.Combine(repo.Root, "TestResults", "results.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(trxPath)!);
        File.WriteAllText(trxPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="1" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Completed">
                <Counters total="4" executed="4" passed="4" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
              </ResultSummary>
            </TestRun>
            """);

        var step = new VerificationStep("test", "Test", ".NET",
            new VerificationCommand("dotnet", ["test"]), repo.Root, TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, "Custom runner finished", string.Empty, out var reason, repo.Root, DateTime.UtcNow.AddMinutes(-1));

        Assert.True(result);
        Assert.Null(reason);
    }

    [Fact]
    public void TrxArtifactWithFailuresReturnsFalse()
    {
        using var repo = new TemporaryRepository();
        var trxPath = Path.Combine(repo.Root, "TestResults", "results.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(trxPath)!);
        File.WriteAllText(trxPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="1" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Failed">
                <Counters total="4" executed="4" passed="3" failed="1" />
              </ResultSummary>
            </TestRun>
            """);

        var step = new VerificationStep("test", "Test", ".NET",
            new VerificationCommand("dotnet", ["test"]), repo.Root, TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, "Custom runner finished", string.Empty, out var reason, repo.Root, DateTime.UtcNow.AddMinutes(-1));

        Assert.False(result);
        Assert.Equal("Test run contained 1 failing test(s).", reason);
    }

    [Fact]
    public void StaleTrxArtifactIsIgnored()
    {
        using var repo = new TemporaryRepository();
        var trxPath = Path.Combine(repo.Root, "TestResults", "results.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(trxPath)!);
        File.WriteAllText(trxPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="1" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Completed">
                <Counters total="4" executed="4" passed="4" failed="0" />
              </ResultSummary>
            </TestRun>
            """);
        File.SetLastWriteTimeUtc(trxPath, DateTime.UtcNow.AddHours(-1));

        var step = new VerificationStep("test", "Test", ".NET",
            new VerificationCommand("dotnet", ["test"]), repo.Root, TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, "Unrecognized stdout", string.Empty, out var reason, repo.Root, DateTime.UtcNow);

        Assert.False(result);
        Assert.Equal("No tests were executed (zero tests discovered or executed).", reason);
    }

    [Fact]
    public void MalformedTrxArtifactFailsClosed()
    {
        using var repo = new TemporaryRepository();
        var trxPath = Path.Combine(repo.Root, "TestResults", "results.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(trxPath)!);
        File.WriteAllText(trxPath, "<broken xml>>>");

        var step = new VerificationStep("test", "Test", ".NET",
            new VerificationCommand("dotnet", ["test"]), repo.Root, TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, "output", string.Empty, out var reason, repo.Root, DateTime.UtcNow.AddMinutes(-1));

        Assert.False(result);
        Assert.Equal("Test report artifact is malformed or could not be parsed.", reason);
    }

    [Fact]
    public void JsonReportArtifactPassingReturnsTrue()
    {
        using var repo = new TemporaryRepository();
        var jsonPath = repo.Write("test-report.json", """
            {
              "numTotalTests": 15,
              "numPassedTests": 15,
              "numFailedTests": 0
            }
            """);

        var step = new VerificationStep("test", "Test", "Node",
            new VerificationCommand("npm", ["test"]), repo.Root, TimeSpan.FromMinutes(1), true, [EvidenceType.Tests]);
        var result = SafeVerificationRunner.HasTestExecutionAssurance(step, "Custom runner finished", string.Empty, out var reason, repo.Root, DateTime.UtcNow.AddMinutes(-1));

        Assert.True(result);
        Assert.Null(reason);
    }

    [Fact]
    public async Task RunAsyncWhenNodeScriptPrintsFake1PassedFailsWithTestsEvidence()
    {
        using var repo = new TemporaryRepository();
        repo.Write("package.json", """
            {
              "name": "fake-test-suite",
              "scripts": {
                "test": "node -e \"console.log('1 passed')\""
              }
            }
            """);
        var step = new VerificationStep("fake-test", "Fake Test", "Node",
            new VerificationCommand("npm", ["run", "test"]), repo.Root, TimeSpan.FromSeconds(30), true, [EvidenceType.Tests]);

        var result = await new SafeVerificationRunner().RunAsync(repo.Root, new VerificationPlan([step], []));

        var ev = Assert.Single(result.Evidence);
        Assert.Equal(VerificationStepStatus.Failed, ev.Status);
        Assert.Equal("No tests were executed (zero tests discovered or executed).", ev.FailureReason);
    }

    private static TemporaryRepository MinimalProject(bool valid)
    {
        var repo = new TemporaryRepository();
        repo.Write("Test.csproj", valid
            ? "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
            : "<Project Sdk=\"Microsoft.NET.Sdk\"><Broken></Project>");
        return repo;
    }

    private static Task<VerificationExecutionResult> RunBuildAsync(string root, TimeSpan timeout)
    {
        var step = new VerificationStep("build", "Build", ".NET",
            new VerificationCommand("dotnet", ["build", "Test.csproj"]), root, timeout, true);
        return new SafeVerificationRunner().RunAsync(root, new VerificationPlan([step], []));
    }
}
