using AgentProof.Application;
using AgentProof.Domain;

namespace AgentProof.UnitTests;

public sealed class SkillRecommenderTests
{
    private readonly DeterministicSkillRecommender _recommender = new();

    [Fact]
    public void BrowserVisibleUiTaskRequiresPlaywrightAndRecommendsImpeccable()
    {
        var results = _recommender.Recommend(Profile("React"), new TaskContext
        {
            TaskType = TaskType.Feature,
            Complexity = TaskComplexity.Medium,
            AffectedAreas = [AffectedArea.Frontend, AffectedArea.Ui],
            HasUiChanges = true,
            HasBrowserVisibleChanges = true
        });
        Assert.Equal(RecommendationLevel.Required, Find(results, "Playwright").Level);
        Assert.Equal(RecommendationLevel.Recommended, Find(results, "Impeccable").Level);
        Assert.Equal(RecommendationLevel.Recommended, Find(results, "VercelAgentSkills").Level);
    }

    [Fact]
    public void ArchitectureChangeRecommendsArchify()
    {
        var results = _recommender.Recommend(Profile(), new TaskContext { HasArchitectureChanges = true });
        Assert.Equal(RecommendationLevel.Recommended, Find(results, "Archify").Level);
    }

    [Fact]
    public void SimpleBackendBugExcludesUiArchitectureSpecAndAzureSkills()
    {
        var results = _recommender.Recommend(Profile(), new TaskContext
        {
            TaskType = TaskType.BugFix,
            Complexity = TaskComplexity.Low,
            AffectedAreas = [AffectedArea.Backend]
        });
        foreach (var name in new[] { "Impeccable", "Playwright", "Archify", "SpecKit", "AzureSkills" })
            Assert.Equal(RecommendationLevel.NotNeeded, Find(results, name).Level);
    }

    [Fact]
    public void DoesNotRequireEverySkill()
    {
        var results = _recommender.Recommend(Profile("Next.js"), new TaskContext
        {
            TaskType = TaskType.Feature,
            Complexity = TaskComplexity.High,
            AffectedAreas = [AffectedArea.Frontend, AffectedArea.Ui, AffectedArea.Architecture],
            HasArchitectureChanges = true,
            HasBrowserVisibleChanges = true,
            HasUiChanges = true
        });
        Assert.True(results.Count(x => x.Level == RecommendationLevel.Required) < results.Count);
        Assert.All(results, x => Assert.False(string.IsNullOrWhiteSpace(x.ReasonCode)));
    }

    private static SkillRecommendation Find(IEnumerable<SkillRecommendation> results, string name) =>
        results.Single(x => x.Skill.Name == name);

    private static RepositoryProfile Profile(params string[] frameworks) =>
        new("repo", Path.GetTempPath(), [".NET"], frameworks, [], [], false, false, 0);
}
