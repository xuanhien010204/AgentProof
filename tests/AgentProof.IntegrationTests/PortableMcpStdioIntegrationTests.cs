using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentProof.Benchmarks.Execution;
using AgentProof.Domain;
using AgentProof.Infrastructure;
using AgentProof.Mcp;

namespace AgentProof.IntegrationTests;

public sealed class PortableMcpStdioIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task SimpleDotNetRepositoryRealMcpStdioLifecycleVerifiesSuccessfully()
    {
        using var repo = CreateSimpleDotNetRepository();
        var task = new TaskContext
        {
            Description = "Verify portable .NET repository build and test suite",
            Contract = new TaskContract
            {
                Goal = "Build and test verification",
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests]
            }
        };

        await using var client = await McpStdioClient.StartAsync();

        // 1 & 2. initialize & tools/list
        var listResult = await client.BenchmarkInitializeAndListToolsAsync();
        Assert.True(listResult.Success);

        // 3. analyze_repository
        var analyzeResult = await client.CallToolAsync("analyze_repository", new { repositoryPath = repo.Root });
        Assert.True(analyzeResult.Success, analyzeResult.Error);
        var profile = JsonSerializer.Deserialize<CompactRepositoryProfile>(analyzeResult.ToolResultText, JsonOptions)!;
        Assert.Equal(1, profile.SchemaVersion);
        Assert.Equal(1, profile.WorkspaceCount);
        Assert.Contains(".NET", profile.Technologies);

        // 4. recommend_skills
        var recsResult = await client.CallToolAsync("recommend_skills", new { repositoryPath = repo.Root, taskContext = task });
        Assert.True(recsResult.Success, recsResult.Error);
        var recs = JsonSerializer.Deserialize<List<CompactSkillRecommendation>>(recsResult.ToolResultText, JsonOptions)!;
        Assert.NotEmpty(recs);
        Assert.All(recs, r => Assert.Equal(1, r.SchemaVersion));

        // 5. create_verification_plan
        var planResult = await client.CallToolAsync("create_verification_plan", new { repositoryPath = repo.Root, taskContext = task });
        Assert.True(planResult.Success, planResult.Error);
        var plan = JsonSerializer.Deserialize<CompactVerificationPlan>(planResult.ToolResultText, JsonOptions)!;
        Assert.Equal(1, plan.SchemaVersion);
        Assert.True(plan.PlannedStepCount >= 2);
        Assert.Single(plan.WorkspaceIds);
        Assert.Empty(plan.Gaps);

        // 6. verify (compact default mode)
        var verifyCompactResult = await client.CallToolAsync("verify", new { repositoryPath = repo.Root, taskContext = task });
        Assert.True(verifyCompactResult.Success, verifyCompactResult.Error);
        var vCompact = JsonSerializer.Deserialize<CompactVerificationResult>(verifyCompactResult.ToolResultText, JsonOptions)!;
        Assert.Equal(1, vCompact.SchemaVersion);
        Assert.Equal(VerificationStatus.Verified, vCompact.Status);
        Assert.Equal(0, vCompact.Summary.FailedStepCount);
        Assert.Equal(0, vCompact.Summary.TimedOutStepCount);
        Assert.Equal(0, vCompact.Summary.NotRunStepCount);
        Assert.Equal(vCompact.Summary.PlannedStepCount, vCompact.Summary.PassedStepCount);
        Assert.Null(vCompact.Evidence);

        // Ensure "evidence" property is not in compact JSON output
        using (var doc = JsonDocument.Parse(verifyCompactResult.ToolResultText))
        {
            Assert.False(doc.RootElement.TryGetProperty("evidence", out _));
        }

        // 7. verify (full mode detail retrieval)
        var verifyFullResult = await client.CallToolAsync("verify", new { repositoryPath = repo.Root, taskContext = task, detailLevel = "full" });
        Assert.True(verifyFullResult.Success, verifyFullResult.Error);
        var vFull = JsonSerializer.Deserialize<CompactVerificationResult>(verifyFullResult.ToolResultText, JsonOptions)!;
        Assert.Equal(1, vFull.SchemaVersion);
        Assert.Equal(VerificationStatus.Verified, vFull.Status);
        Assert.Equal(vCompact.Summary, vFull.Summary);
        Assert.NotNull(vFull.Evidence);
        Assert.Equal(vFull.Summary.EvidenceCount, vFull.Evidence.Count);
        Assert.All(vFull.Evidence, e =>
        {
            Assert.Equal(VerificationStepStatus.Passed, e.Status);
            Assert.Equal(0, e.ExitCode);
            Assert.True(e.DurationMs >= 0);
        });
    }

    [Fact]
    public async Task MultiProjectDotNetSolutionRealMcpStdioVerifiesAllProjects()
    {
        using var repo = CreateMultiProjectDotNetRepository();
        var task = new TaskContext
        {
            Description = "Verify multi-project .NET solution",
            Contract = new TaskContract
            {
                Goal = "Verify solution build and tests",
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests]
            }
        };

        await using var client = await McpStdioClient.StartAsync();

        var analyzeResult = await client.CallToolAsync("analyze_repository", new { repositoryPath = repo.Root });
        Assert.True(analyzeResult.Success);
        var profile = JsonSerializer.Deserialize<CompactRepositoryProfile>(analyzeResult.ToolResultText, JsonOptions)!;
        Assert.Equal(1, profile.SchemaVersion);
        Assert.Contains(".NET", profile.Technologies);

        var verifyResult = await client.CallToolAsync("verify", new { repositoryPath = repo.Root, taskContext = task });
        Assert.True(verifyResult.Success);
        var vResult = JsonSerializer.Deserialize<CompactVerificationResult>(verifyResult.ToolResultText, JsonOptions)!;
        Assert.Equal(VerificationStatus.Verified, vResult.Status);
        Assert.True(vResult.Summary.PassedStepCount >= 2);
        Assert.Equal(0, vResult.Summary.FailedStepCount);
    }

    [Fact]
    public async Task IntentionallyFailingRepositoryPreservesFailFastNotRunAccounting()
    {
        using var repo = CreateFailingDotNetRepository();
        var task = new TaskContext
        {
            Description = "Verify intentionally failing repository",
            Contract = new TaskContract
            {
                Goal = "Verify build and tests",
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests]
            }
        };

        await using var client = await McpStdioClient.StartAsync();

        var verifyResult = await client.CallToolAsync("verify", new { repositoryPath = repo.Root, taskContext = task });
        Assert.True(verifyResult.Success);
        var vResult = JsonSerializer.Deserialize<CompactVerificationResult>(verifyResult.ToolResultText, JsonOptions)!;

        // Status must be NotVerified
        Assert.Equal(VerificationStatus.NotVerified, vResult.Status);
        Assert.True(vResult.Summary.PlannedStepCount >= 2);
        Assert.True(vResult.Summary.FailedStepCount >= 1);
        Assert.True(vResult.Summary.NotRunStepCount >= 1);

        // Accounting invariant holds
        Assert.Equal(
            vResult.Summary.PlannedStepCount,
            vResult.Summary.PassedStepCount + vResult.Summary.FailedStepCount + vResult.Summary.TimedOutStepCount + vResult.Summary.NotRunStepCount);

        // Failed step identity and diagnostics are explicitly preserved
        var failed = Assert.Single(vResult.FailedSteps);
        Assert.Contains("build", failed.StepId);
        Assert.True(failed.ExitCode is not null and not 0);
        Assert.False(string.IsNullOrWhiteSpace(failed.FailureReason));

        // Skipped steps are grouped in NotRun
        var notRun = Assert.Single(vResult.NotRun);
        Assert.NotEmpty(notRun.StepIds);
        Assert.Contains("prior required verification step did not pass", notRun.Reason);

        // Full mode also reports same failures and detailed diagnostics
        var verifyFullResult = await client.CallToolAsync("verify", new { repositoryPath = repo.Root, taskContext = task, detailLevel = "full" });
        Assert.True(verifyFullResult.Success);
        var vFull = JsonSerializer.Deserialize<CompactVerificationResult>(verifyFullResult.ToolResultText, JsonOptions)!;
        Assert.Equal(VerificationStatus.NotVerified, vFull.Status);
        Assert.Equal(vResult.Summary, vFull.Summary);
        Assert.NotNull(vFull.Evidence);
        var failedEvidence = vFull.Evidence.Single(e => e.Status == VerificationStepStatus.Failed);
        Assert.Equal(failed.StepId, failedEvidence.StepId);
    }

    [NodeInstalledFact]
    public async Task NodeWorkspaceRealMcpStdioVerifiesSuccessfullyWhenInstalled()
    {
        using var repo = CreateNodeRepository();
        var task = new TaskContext
        {
            Description = "Verify portable Node.js workspace",
            Contract = new TaskContract
            {
                Goal = "Verify Node scripts",
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests]
            }
        };

        await using var client = await McpStdioClient.StartAsync();

        var analyzeResult = await client.CallToolAsync("analyze_repository", new { repositoryPath = repo.Root });
        Assert.True(analyzeResult.Success);
        var profile = JsonSerializer.Deserialize<CompactRepositoryProfile>(analyzeResult.ToolResultText, JsonOptions)!;
        Assert.Contains("Node.js", profile.Technologies);

        var verifyResult = await client.CallToolAsync("verify", new { repositoryPath = repo.Root, taskContext = task });
        Assert.True(verifyResult.Success);
        var vResult = JsonSerializer.Deserialize<CompactVerificationResult>(verifyResult.ToolResultText, JsonOptions)!;

        Assert.Equal(VerificationStatus.Verified, vResult.Status);
        Assert.True(vResult.Summary.PassedStepCount >= 2);
        Assert.Equal(0, vResult.Summary.FailedStepCount);
    }

    [Fact]
    public async Task FixtureBNoTestsExecutedFailsTestEvidenceAndReportsNotVerified()
    {
        using var repo = CreateNoTestsDotNetRepository();
        var task = new TaskContext
        {
            Description = "Verify repository with zero tests executed",
            Contract = new TaskContract
            {
                Goal = "Verify tests",
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests]
            }
        };

        await using var client = await McpStdioClient.StartAsync();

        var verifyResult = await client.CallToolAsync("verify", new { repositoryPath = repo.Root, taskContext = task });
        Assert.True(verifyResult.Success);
        var vResult = JsonSerializer.Deserialize<CompactVerificationResult>(verifyResult.ToolResultText, JsonOptions)!;

        Assert.Equal(VerificationStatus.NotVerified, vResult.Status);
        Assert.True(vResult.Summary.FailedStepCount >= 1);
        var failedTestStep = Assert.Single(vResult.FailedSteps, s => s.StepId.EndsWith("dotnet-test", StringComparison.Ordinal));
        Assert.Equal("No tests were executed (zero tests discovered or executed).", failedTestStep.FailureReason);
    }

    [Fact]
    public async Task FixtureCFailingTestsFailsStepAndReportsNotVerified()
    {
        using var repo = CreateFailingTestsDotNetRepository();
        var task = new TaskContext
        {
            Description = "Verify repository with failing tests",
            Contract = new TaskContract
            {
                Goal = "Verify tests",
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests]
            }
        };

        await using var client = await McpStdioClient.StartAsync();

        var verifyResult = await client.CallToolAsync("verify", new { repositoryPath = repo.Root, taskContext = task });
        Assert.True(verifyResult.Success);
        var vResult = JsonSerializer.Deserialize<CompactVerificationResult>(verifyResult.ToolResultText, JsonOptions)!;

        Assert.Equal(VerificationStatus.NotVerified, vResult.Status);
        Assert.True(vResult.Summary.FailedStepCount >= 1);
        var failedTestStep = Assert.Single(vResult.FailedSteps, s => s.StepId.EndsWith("dotnet-test", StringComparison.Ordinal));
        Assert.Equal(1, failedTestStep.ExitCode);
    }

    [Fact]
    public async Task FixtureDMissingTestProjectProducesGapAndPartiallyVerified()
    {
        using var repo = CreateStandaloneNoTestProjectDotNetRepository();
        var task = new TaskContext
        {
            Description = "Verify standalone repository with missing test project",
            Contract = new TaskContract
            {
                Goal = "Verify build and tests",
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests]
            }
        };

        await using var client = await McpStdioClient.StartAsync();

        var planResult = await client.CallToolAsync("create_verification_plan", new { repositoryPath = repo.Root, taskContext = task });
        Assert.True(planResult.Success);
        var plan = JsonSerializer.Deserialize<CompactVerificationPlan>(planResult.ToolResultText, JsonOptions)!;
        Assert.Contains(plan.Gaps, g => g.Code == "MISSING_EVIDENCE_TESTS");

        var verifyResult = await client.CallToolAsync("verify", new { repositoryPath = repo.Root, taskContext = task });
        Assert.True(verifyResult.Success);
        var vResult = JsonSerializer.Deserialize<CompactVerificationResult>(verifyResult.ToolResultText, JsonOptions)!;

        Assert.Equal(VerificationStatus.PartiallyVerified, vResult.Status);
        Assert.Equal(0, vResult.Summary.FailedStepCount);
        Assert.True(vResult.Summary.PassedStepCount >= 1);
        Assert.Contains(vResult.Gaps, g => g.Code == "MISSING_EVIDENCE_TESTS");
    }

    private static PortableRepo CreateSimpleDotNetRepository()
    {
        var repo = new PortableRepo();
        repo.Write("SampleApp/SampleApp.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """);
        repo.Write("SampleApp/Program.cs", "System.Console.WriteLine(\"Hello Portable\");");
        repo.Write("SampleApp/Calculator.cs", "public class Calculator { public int Add(int a, int b) => a + b; }");

        repo.Write("SampleApp.Tests/SampleApp.Tests.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="..\SampleApp\SampleApp.csproj" />
              </ItemGroup>
            </Project>
            """);
        repo.Write("SampleApp.Tests/CalculatorTests.cs",
            """
            using Xunit;
            public class CalculatorTests
            {
                [Fact]
                public void AddReturnsSum() => Assert.Equal(4, new Calculator().Add(2, 2));
            }
            """);

        repo.Write("SampleSolution.slnx",
            """
            <Solution>
              <Project Path="SampleApp/SampleApp.csproj" />
              <Project Path="SampleApp.Tests/SampleApp.Tests.csproj" />
            </Solution>
            """);

        return repo;
    }

    private static PortableRepo CreateMultiProjectDotNetRepository()
    {
        var repo = new PortableRepo();
        repo.Write("src/App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        repo.Write("src/App/App.cs", "public class App { }");

        repo.Write("tests/AppTests/AppTests.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="..\..\src\App\App.csproj" />
              </ItemGroup>
            </Project>
            """);
        repo.Write("tests/AppTests/Tests.cs",
            """
            using Xunit;
            public class Tests
            {
                [Fact]
                public void AppTestsPass() => Assert.NotNull(new App());
            }
            """);

        repo.Write("Multi.slnx",
            """
            <Solution>
              <Project Path="src/App/App.csproj" />
              <Project Path="tests/AppTests/AppTests.csproj" />
            </Solution>
            """);

        return repo;
    }

    private static PortableRepo CreateNoTestsDotNetRepository()
    {
        var repo = new PortableRepo();
        repo.Write("src/App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """);
        repo.Write("src/App/Program.cs", "System.Console.WriteLine(\"App without tests\");");
        repo.Write("Multi.slnx",
            """
            <Solution>
              <Project Path="src/App/App.csproj" />
            </Solution>
            """);
        return repo;
    }

    private static PortableRepo CreateFailingTestsDotNetRepository()
    {
        var repo = new PortableRepo();
        repo.Write("SampleApp/SampleApp.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        repo.Write("SampleApp.Tests/SampleApp.Tests.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
            </Project>
            """);
        repo.Write("SampleApp.Tests/FailingTests.cs",
            """
            using Xunit;
            public class FailingTests
            {
                [Fact]
                public void ThisTestFails() => Assert.Equal(4, 5);
            }
            """);
        repo.Write("Sample.slnx",
            """
            <Solution>
              <Project Path="SampleApp/SampleApp.csproj" />
              <Project Path="SampleApp.Tests/SampleApp.Tests.csproj" />
            </Solution>
            """);
        return repo;
    }

    private static PortableRepo CreateStandaloneNoTestProjectDotNetRepository()
    {
        var repo = new PortableRepo();
        repo.Write("SampleApp.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """);
        repo.Write("Program.cs", "System.Console.WriteLine(\"No tests here\");");
        return repo;
    }

    private static PortableRepo CreateFailingDotNetRepository()
    {
        var repo = new PortableRepo();
        repo.Write("Broken/Broken.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        repo.Write("Broken/Broken.cs", "public class Broken { syntax error here intentional }");
        repo.Write("Broken.Tests/Broken.Tests.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
            </Project>
            """);
        repo.Write("Broken.Tests/Tests.cs", "using Xunit; public class Tests { [Fact] public void T() => Assert.True(true); }");
        repo.Write("Broken.slnx",
            """
            <Solution>
              <Project Path="Broken/Broken.csproj" />
              <Project Path="Broken.Tests/Broken.Tests.csproj" />
            </Solution>
            """);
        return repo;
    }

    private static PortableRepo CreateNodeRepository()
    {
        var repo = new PortableRepo();
        repo.Write("package.json",
            """
            {
              "name": "portable-node-fixture",
              "version": "1.0.0",
              "scripts": {
                "lint": "node -e \"process.exit(0)\"",
                "test": "node -e \"const { test } = require('node:test'); const assert = require('node:assert'); test('portable test', () => assert.strictEqual(1, 1));\"",
                "build": "node -e \"process.exit(0)\""
              }
            }
            """);
        return repo;
    }

    private static bool IsCommandAvailableOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return false;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, command);
            if (File.Exists(full)) return true;
        }
        return false;
    }

    private sealed class PortableRepo : IDisposable
    {
        public PortableRepo() => Root = Path.Combine(Path.GetTempPath(), "AgentProof.PortableMcp", Guid.NewGuid().ToString("N"));
        public string Root { get; }

        public string Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(Root, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }
}
