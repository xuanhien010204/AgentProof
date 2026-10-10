using AgentProof.Application;
using AgentProof.Domain;
using AgentProof.Infrastructure;

namespace AgentProof.UnitTests;

public sealed class TaskContractTests
{
    [Fact]
    public async Task BackendBugWithBuildAndTestsEvidenceAvailableProducesNoGapsAndVerifies()
    {
        using var repo = MinimalDotNetRepo();
        var analyzer = new LocalRepositoryAnalyzer();
        var planner = new DeterministicVerificationPlanner(new RepositoryConfigurationReader());
        var runner = new SafeVerificationRunner();
        var service = new AgentProofService(analyzer, new DeterministicSkillRecommender(), planner, runner);

        var task = new TaskContext
        {
            Contract = new TaskContract
            {
                Goal = "Fix null reference in analyzer",
                AcceptanceCriteria =
                [
                    new AcceptanceCriterion
                    {
                        Id = "AC-1",
                        Description = "Analyzer handles null input safely",
                        RequiredEvidence = [EvidenceType.Tests]
                    }
                ],
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests]
            }
        };

        var profile = await analyzer.AnalyzeAsync(repo.Root);
        var plan = await planner.CreateAsync(profile, task);

        Assert.Empty(plan.Gaps);
        Assert.Contains(plan.Steps, s => s.ProvidedEvidence.Contains(EvidenceType.Build));
        Assert.Contains(plan.Steps, s => s.ProvidedEvidence.Contains(EvidenceType.Tests));

        var result = await service.VerifyRepositoryAsync(repo.Root, task);
        Assert.Equal(VerificationStatus.Verified, result.Status);
        Assert.Empty(result.Gaps);
    }

    [Fact]
    public async Task BrowserVisibleUiTaskRequiresBrowserVerificationWhenPlaywrightAvailable()
    {
        using var repo = new TemporaryRepository();
        repo.Write("package.json", """{"scripts":{"test":"vitest run"}}""");
        repo.Write("playwright.config.ts", "export default {};");

        var analyzer = new LocalRepositoryAnalyzer();
        var planner = new DeterministicVerificationPlanner(new RepositoryConfigurationReader());

        var task = new TaskContext
        {
            Contract = new TaskContract
            {
                Goal = "Add responsive navigation header",
                AcceptanceCriteria =
                [
                    new AcceptanceCriterion
                    {
                        Id = "AC-1",
                        Description = "Header displays navigation links",
                        RequiredEvidence = [EvidenceType.Browser]
                    }
                ],
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Browser]
            }
        };

        var profile = await analyzer.AnalyzeAsync(repo.Root);
        var plan = await planner.CreateAsync(profile, task);

        Assert.Contains(plan.Steps, s => s.Id == "playwright" && s.ProvidedEvidence.Contains(EvidenceType.Browser));
        Assert.DoesNotContain(plan.Gaps, g => g.Code == "BROWSER_VERIFICATION_UNAVAILABLE");
    }

    [Fact]
    public async Task BrowserEvidenceUnavailableProducesExplicitGapAndPreventsVerified()
    {
        using var repo = MinimalDotNetRepo();
        var analyzer = new LocalRepositoryAnalyzer();
        var planner = new DeterministicVerificationPlanner(new RepositoryConfigurationReader());
        var runner = new SafeVerificationRunner();
        var service = new AgentProofService(analyzer, new DeterministicSkillRecommender(), planner, runner);

        var task = new TaskContext
        {
            Contract = new TaskContract
            {
                Goal = "Add visual modal dialog",
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests, EvidenceType.Browser]
            }
        };

        var profile = await analyzer.AnalyzeAsync(repo.Root);
        var plan = await planner.CreateAsync(profile, task);

        Assert.Contains(plan.Gaps, g => g.Code == "BROWSER_VERIFICATION_UNAVAILABLE");

        var result = await service.VerifyRepositoryAsync(repo.Root, task);
        Assert.Equal(VerificationStatus.PartiallyVerified, result.Status);
        Assert.Contains(result.Gaps, g => g.Code == "BROWSER_VERIFICATION_UNAVAILABLE");
    }

    [Fact]
    public async Task DatabaseEvidenceUnavailableProducesExplicitGapAndPreventsVerified()
    {
        using var repo = MinimalDotNetRepo();
        var analyzer = new LocalRepositoryAnalyzer();
        var planner = new DeterministicVerificationPlanner(new RepositoryConfigurationReader());
        var runner = new SafeVerificationRunner();
        var service = new AgentProofService(analyzer, new DeterministicSkillRecommender(), planner, runner);

        var task = new TaskContext
        {
            Contract = new TaskContract
            {
                Goal = "Add index to users table migration",
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests, EvidenceType.Database]
            }
        };

        var profile = await analyzer.AnalyzeAsync(repo.Root);
        var plan = await planner.CreateAsync(profile, task);

        Assert.Contains(plan.Gaps, g => g.Code == "MISSING_EVIDENCE_DATABASE");

        var result = await service.VerifyRepositoryAsync(repo.Root, task);
        Assert.Equal(VerificationStatus.PartiallyVerified, result.Status);
        Assert.Contains(result.Gaps, g => g.Code == "MISSING_EVIDENCE_DATABASE");
    }

    [Fact]
    public async Task RuntimeAndPerformanceEvidenceMissingProduceExplicitGaps()
    {
        using var repo = MinimalDotNetRepo();
        var analyzer = new LocalRepositoryAnalyzer();
        var planner = new DeterministicVerificationPlanner(new RepositoryConfigurationReader());

        var task = new TaskContext
        {
            Contract = new TaskContract
            {
                Goal = "Optimize request throughput under high load",
                RequiredEvidence = [EvidenceType.Runtime, EvidenceType.Performance]
            }
        };

        var profile = await analyzer.AnalyzeAsync(repo.Root);
        var plan = await planner.CreateAsync(profile, task);

        Assert.Contains(plan.Gaps, g => g.Code == "MISSING_EVIDENCE_RUNTIME");
        Assert.Contains(plan.Gaps, g => g.Code == "MISSING_EVIDENCE_PERFORMANCE");
    }

    [Fact]
    public async Task SimpleTaskDoesNotRequireIrrelevantEvidence()
    {
        using var repo = MinimalDotNetRepo();
        var analyzer = new LocalRepositoryAnalyzer();
        var planner = new DeterministicVerificationPlanner(new RepositoryConfigurationReader());

        var task = new TaskContext
        {
            Contract = new TaskContract
            {
                Goal = "Refactor internal string helper",
                RequiredEvidence = [EvidenceType.Build, EvidenceType.Tests]
            }
        };

        var profile = await analyzer.AnalyzeAsync(repo.Root);
        var plan = await planner.CreateAsync(profile, task);

        Assert.DoesNotContain(plan.Gaps, g => g.Code == "BROWSER_VERIFICATION_UNAVAILABLE");
        Assert.DoesNotContain(plan.Gaps, g => g.Code == "MISSING_EVIDENCE_DATABASE");
        Assert.DoesNotContain(plan.Gaps, g => g.Code == "MISSING_EVIDENCE_RUNTIME");
        Assert.DoesNotContain(plan.Gaps, g => g.Code == "MISSING_EVIDENCE_PERFORMANCE");
        Assert.Empty(plan.Gaps);
    }

    [Fact]
    public async Task CriterionSpecificEvidenceContributesToRequiredEvidence()
    {
        using var repo = MinimalDotNetRepo();
        var analyzer = new LocalRepositoryAnalyzer();
        var planner = new DeterministicVerificationPlanner(new RepositoryConfigurationReader());

        var task = new TaskContext
        {
            Contract = new TaskContract
            {
                Goal = "Implement user preferences",
                RequiredEvidence = [EvidenceType.Build],
                AcceptanceCriteria =
                [
                    new AcceptanceCriterion
                    {
                        Id = "AC-1",
                        Description = "Validation logic passes test cases",
                        RequiredEvidence = [EvidenceType.Tests]
                    },
                    new AcceptanceCriterion
                    {
                        Id = "AC-2",
                        Description = "Preferences persist across schema migration",
                        RequiredEvidence = [EvidenceType.Database]
                    }
                ]
            }
        };

        var profile = await analyzer.AnalyzeAsync(repo.Root);
        var plan = await planner.CreateAsync(profile, task);

        Assert.Contains(plan.Steps, s => s.ProvidedEvidence.Contains(EvidenceType.Build));
        Assert.Contains(plan.Steps, s => s.ProvidedEvidence.Contains(EvidenceType.Tests));
        Assert.Contains(plan.Gaps, g => g.Code == "MISSING_EVIDENCE_DATABASE");
    }

    [Fact]
    public async Task AcceptanceCriteriaWithoutAnyRequiredEvidenceProducesExplicitGap()
    {
        using var repo = MinimalDotNetRepo();
        var analyzer = new LocalRepositoryAnalyzer();
        var planner = new DeterministicVerificationPlanner(new RepositoryConfigurationReader());

        var task = new TaskContext
        {
            Contract = new TaskContract
            {
                Goal = "Review architecture documentation",
                RequiredEvidence = [],
                AcceptanceCriteria =
                [
                    new AcceptanceCriterion
                    {
                        Id = "AC-DOCS",
                        Description = "Documentation is up to date",
                        RequiredEvidence = []
                    }
                ]
            }
        };

        var profile = await analyzer.AnalyzeAsync(repo.Root);
        var plan = await planner.CreateAsync(profile, task);

        Assert.Contains(plan.Gaps, g => g.Code == "UNVERIFIED_ACCEPTANCE_CRITERION");
    }

    [Fact]
    public void LegacyTaskContextDerivesEffectiveContractDeterministically()
    {
        var legacyTask = new TaskContext
        {
            Description = "Legacy backend bugfix",
            TaskType = TaskType.BugFix,
            Complexity = TaskComplexity.Low,
            AffectedAreas = [AffectedArea.Backend],
            HasBrowserVisibleChanges = true,
            HasDatabaseChanges = true
        };

        var contract = legacyTask.GetEffectiveContract();

        Assert.Equal("Legacy backend bugfix", contract.Goal);
        Assert.Contains(EvidenceType.Build, contract.RequiredEvidence);
        Assert.Contains(EvidenceType.Tests, contract.RequiredEvidence);
        Assert.Contains(EvidenceType.Browser, contract.RequiredEvidence);
        Assert.Contains(EvidenceType.Database, contract.RequiredEvidence);
    }

    [Fact]
    public void SkillRecommenderRecommendsPlaywrightFromContractEvidence()
    {
        var recommender = new DeterministicSkillRecommender();
        var profile = new RepositoryProfile("repo", Path.GetTempPath(), [".NET"], ["React"], [], [], false, false, 0);

        var taskWithContract = new TaskContext
        {
            Contract = new TaskContract
            {
                Goal = "Update client header",
                RequiredEvidence = [EvidenceType.Browser]
            }
        };

        var recommendations = recommender.Recommend(profile, taskWithContract);
        var playwright = recommendations.Single(r => r.Skill.Name == "Playwright");
        Assert.Equal(RecommendationLevel.Required, playwright.Level);
    }

    private static TemporaryRepository MinimalDotNetRepo()
    {
        var repo = new TemporaryRepository();
        repo.Write("Test.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Library</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
            </Project>
            """);
        repo.Write("Tests.cs", """
            using Xunit;
            public class Tests
            {
                [Fact]
                public void PassingTest() => Assert.True(true);
            }
            """);
        return repo;
    }
}
