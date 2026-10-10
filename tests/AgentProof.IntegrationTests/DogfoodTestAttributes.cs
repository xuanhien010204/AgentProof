using AgentProof.Infrastructure;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AgentProof.IntegrationTests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DogfoodFactAttribute : FactAttribute
{
    public DogfoodFactAttribute(string environmentVariableName)
    {
        var resolvedPath = Environment.GetEnvironmentVariable(environmentVariableName);
        var isRequired = string.Equals(Environment.GetEnvironmentVariable("AGENTPROOF_REQUIRE_DOGFOOD"), "true", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(Environment.GetEnvironmentVariable("AGENTPROOF_REQUIRE_DOGFOOD"), "1", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(resolvedPath) || !Directory.Exists(resolvedPath))
        {
            if (isRequired)
            {
                // In dedicated dogfood CI workflow, missing required repositories must fail the job!
                // Leave Skip null so test body runs and asserts failure.
            }
            else
            {
                Skip = $"External dogfood repository was not found. Set {environmentVariableName} to run dogfood validation.";
            }
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class NodeInstalledFactAttribute : FactAttribute
{
    public NodeInstalledFactAttribute()
    {
        var isNodeAvailable = OperatingSystem.IsWindows()
            ? SafeVerificationRunner.ResolveWindowsNodeLauncher("npm") is not null
            : IsCommandAvailableOnPath("npm");

        if (!isNodeAvailable)
        {
            Skip = "npm is not installed on PATH for node verification.";
        }
    }

    private static bool IsCommandAvailableOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return false;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, command))) return true;
        }
        return false;
    }
}
