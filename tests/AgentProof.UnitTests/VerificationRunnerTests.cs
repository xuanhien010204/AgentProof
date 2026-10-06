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
        Assert.Equal(VerificationStatus.Verified, result.Status);
        Assert.Equal(0, evidence.ExitCode);
        Assert.True(evidence.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task FailedProcessProducesNotVerifiedEvidence()
    {
        using var repo = MinimalProject(valid: false);
        var result = await RunBuildAsync(repo.Root, TimeSpan.FromMinutes(1));
        Assert.Equal(VerificationStatus.NotVerified, result.Status);
        Assert.Equal(VerificationStepStatus.Failed, Assert.Single(result.Evidence).Status);
    }

    [Fact]
    public async Task TimeoutIsEnforced()
    {
        using var repo = MinimalProject(valid: true);
        var result = await RunBuildAsync(repo.Root, TimeSpan.Zero);
        Assert.Equal(VerificationStatus.NotVerified, result.Status);
        Assert.Equal(VerificationStepStatus.TimedOut, Assert.Single(result.Evidence).Status);
    }

    [Fact]
    public async Task ArbitraryShellCommandIsRejected()
    {
        using var repo = MinimalProject(valid: true);
        var step = new VerificationStep("bad", "Bad", "Shell",
            new VerificationCommand("powershell", ["-Command", "Get-ChildItem"]), repo.Root, TimeSpan.FromSeconds(1), true);
        var result = await new SafeVerificationRunner().RunAsync(repo.Root, new VerificationPlan([step], []));
        Assert.Equal(VerificationStatus.NotVerified, result.Status);
        Assert.Contains("allowlist", Assert.Single(result.Evidence).FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    private static TemporaryRepository MinimalProject(bool valid)
    {
        var repo = new TemporaryRepository();
        repo.Write("Test.csproj", valid
            ? "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
            : "<Project Sdk=\"Microsoft.NET.Sdk\"><Broken></Project>");
        return repo;
    }

    private static Task<VerificationResult> RunBuildAsync(string root, TimeSpan timeout)
    {
        var step = new VerificationStep("build", "Build", ".NET",
            new VerificationCommand("dotnet", ["build", "Test.csproj"]), root, timeout, true);
        return new SafeVerificationRunner().RunAsync(root, new VerificationPlan([step], []));
    }
}
