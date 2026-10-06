using AgentProof.Domain;

namespace AgentProof.Application;

public sealed class DeterministicSkillRecommender : ISkillRecommender
{
    public IReadOnlyList<SkillRecommendation> Recommend(RepositoryProfile repository, TaskContext task)
    {
        var substantialUnderstanding = task.Complexity == TaskComplexity.High ||
            task.AffectedAreas.Count > 2 || task.HasArchitectureChanges;
        var frameworkWork = repository.Frameworks.Count > 0 && task.TaskType != TaskType.Unknown;
        var largeFeature = task.TaskType == TaskType.Feature && task.Complexity == TaskComplexity.High;
        var architecture = task.HasArchitectureChanges || task.AffectedAreas.Contains(AffectedArea.Architecture);
        var ui = task.HasUiChanges || task.AffectedAreas.Contains(AffectedArea.Ui);
        var frontend = task.AffectedAreas.Contains(AffectedArea.Frontend) || ui;
        var reactOrNext = repository.Frameworks.Any(x => x is "React" or "Next.js");
        var azure = task.HasDeploymentChanges &&
            task.DeploymentTarget?.Contains("azure", StringComparison.OrdinalIgnoreCase) == true;

        var contract = task.GetEffectiveContract();
        var browserRequired = task.HasBrowserVisibleChanges ||
            contract.RequiredEvidence.Contains(EvidenceType.Browser) ||
            contract.AcceptanceCriteria.Any(x => x.RequiredEvidence.Contains(EvidenceType.Browser));

        return
        [
            substantialUnderstanding
                ? Recommendation(SkillCatalog.Serena, RecommendationLevel.Recommended, "SUBSTANTIAL_REPOSITORY_UNDERSTANDING", "The task spans enough code or architecture to benefit from semantic repository navigation.")
                : Recommendation(SkillCatalog.Serena, RecommendationLevel.Optional, "LIMITED_REPOSITORY_SCOPE", "The task appears focused; semantic navigation may help but is not required."),
            frameworkWork
                ? Recommendation(SkillCatalog.Context7, RecommendationLevel.Recommended, "FRAMEWORK_API_RELEVANT", "The repository uses frameworks whose current APIs may matter for this task.")
                : Recommendation(SkillCatalog.Context7, RecommendationLevel.NotNeeded, "NO_FRAMEWORK_API_NEED", "No current framework API lookup is indicated."),
            largeFeature
                ? Recommendation(SkillCatalog.SpecKit, RecommendationLevel.Recommended, "HIGH_COMPLEXITY_FEATURE", "A high-complexity feature benefits from explicit requirements and implementation planning.")
                : Recommendation(SkillCatalog.SpecKit, RecommendationLevel.NotNeeded, "NO_LARGE_FEATURE", "The task is not a high-complexity feature requiring specification support."),
            architecture
                ? Recommendation(SkillCatalog.Archify, RecommendationLevel.Recommended, "ARCHITECTURE_CHANGE", "The task changes architecture or data flow and benefits from visualization.")
                : Recommendation(SkillCatalog.Archify, RecommendationLevel.NotNeeded, "NO_ARCHITECTURE_CHANGE", "No architecture or data-flow change was identified for this task."),
            ui
                ? Recommendation(SkillCatalog.Impeccable, RecommendationLevel.Recommended, "UI_CHANGE", "The task changes UI behavior and benefits from UI/UX review.")
                : Recommendation(SkillCatalog.Impeccable, RecommendationLevel.NotNeeded, "NO_UI_CHANGE", "No UI or UX change was identified."),
            reactOrNext && frontend
                ? Recommendation(SkillCatalog.VercelAgentSkills, RecommendationLevel.Recommended, "REACT_NEXT_FRONTEND_CHANGE", "The repository uses React or Next.js and the task changes frontend code.")
                : Recommendation(SkillCatalog.VercelAgentSkills, RecommendationLevel.NotNeeded, "NO_REACT_NEXT_FRONTEND_CHANGE", "The task does not combine a React or Next.js repository with frontend changes."),
            browserRequired
                ? Recommendation(SkillCatalog.Playwright, RecommendationLevel.Required, "BROWSER_VISIBLE_CHANGE", "The task changes browser-visible behavior and requires runtime browser verification.")
                : Recommendation(SkillCatalog.Playwright, RecommendationLevel.NotNeeded, "NO_BROWSER_VISIBLE_CHANGE", "No browser-visible behavior change was identified."),
            azure
                ? Recommendation(SkillCatalog.AzureSkills, RecommendationLevel.Recommended, "AZURE_DEPLOYMENT", "The task explicitly targets an Azure deployment workflow.")
                : Recommendation(SkillCatalog.AzureSkills, RecommendationLevel.NotNeeded, "NO_AZURE_DEPLOYMENT", "No Azure deployment was identified for this task.")
        ];
    }

    private static SkillRecommendation Recommendation(
        SkillDefinition skill, RecommendationLevel level, string code, string reason) =>
        new(skill, level, code, reason);
}
