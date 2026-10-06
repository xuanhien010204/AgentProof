using System.Reflection;
using AgentProof.Domain;
using AgentProof.Mcp;
using ModelContextProtocol.Server;

namespace AgentProof.IntegrationTests;

public sealed class ArchitectureTests
{
    private static readonly string[] ExpectedTools =
        ["analyze_repository", "recommend_skills", "create_verification_plan", "verify"];

    [Fact]
    public void DomainDoesNotReferenceInfrastructureOrMcp()
    {
        var references = typeof(RepositoryProfile).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToArray();
        Assert.DoesNotContain("AgentProof.Infrastructure", references);
        Assert.DoesNotContain("AgentProof.Mcp", references);
    }

    [Fact]
    public void McpSurfaceIsSmallAndHasNoArbitraryExecutionTool()
    {
        var names = typeof(AgentProofTools).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(x => x.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(x => x is not null)
            .ToArray();
        Assert.Equal(ExpectedTools, names);
        Assert.DoesNotContain(names, x => x!.Contains("shell", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("command", StringComparison.OrdinalIgnoreCase) || x.Contains("script", StringComparison.OrdinalIgnoreCase));
    }
}
