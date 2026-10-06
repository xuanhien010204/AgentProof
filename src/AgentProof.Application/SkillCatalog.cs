using AgentProof.Domain;

namespace AgentProof.Application;

public static class SkillCatalog
{
    public static readonly SkillDefinition Serena = new("serena", "Serena", "Semantic codebase understanding and navigation.");
    public static readonly SkillDefinition Context7 = new("context7", "Context7", "Current framework and library documentation.");
    public static readonly SkillDefinition SpecKit = new("speckit", "SpecKit", "Requirements and planning for large features.");
    public static readonly SkillDefinition Archify = new("archify", "Archify", "Architecture, data-flow, and sequence visualization.");
    public static readonly SkillDefinition Impeccable = new("impeccable", "Impeccable", "UI and UX review and polish.");
    public static readonly SkillDefinition VercelAgentSkills = new("vercel-agent-skills", "VercelAgentSkills", "React and Next.js engineering best practices.");
    public static readonly SkillDefinition Playwright = new("playwright", "Playwright", "Browser and runtime verification.");
    public static readonly SkillDefinition AzureSkills = new("azure-skills", "AzureSkills", "Azure deployment workflows.");
}
