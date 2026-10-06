using AgentProof.Domain;

namespace AgentProof.Application;

public sealed class DeterministicVerificationPlanner(IRepositoryConfigurationReader configurationReader) : IVerificationPlanner
{
    private static readonly string[] KnownNodeScripts = ["lint", "typecheck", "test", "build"];

    public async Task<VerificationPlan> CreateAsync(
        RepositoryProfile repository,
        TaskContext task,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<VerificationStep>();
        var gaps = new List<VerificationGap>();

        if (repository.Technologies.Contains(".NET"))
        {
            var entryPoint = configurationReader.FindDotNetEntryPoint(repository.RootPath);
            var suffix = entryPoint is null ? Array.Empty<string>() : new[] { entryPoint };
            steps.Add(DotNetStep("dotnet-restore", "Restore .NET dependencies", "restore", suffix, repository.RootPath, true));
            steps.Add(DotNetStep("dotnet-build", "Build .NET solution", "build", suffix, repository.RootPath, true));
            steps.Add(DotNetStep("dotnet-test", "Run .NET tests", "test", suffix, repository.RootPath, true));
        }

        if (repository.Technologies.Contains("Node.js"))
        {
            var scripts = await configurationReader.ReadPackageScriptsAsync(repository.RootPath, cancellationToken);
            var manager = SelectPackageManager(repository.PackageManagers);
            foreach (var script in KnownNodeScripts.Where(scripts.ContainsKey))
            {
                steps.Add(new VerificationStep(
                    $"node-{script}", $"Run Node.js {script}", "Node",
                    new VerificationCommand(manager, ["run", script]),
                    repository.RootPath, TimeSpan.FromMinutes(5), true));
            }
        }

        if (task.HasBrowserVisibleChanges)
        {
            if (repository.TestFrameworks.Contains("Playwright"))
            {
                steps.Add(new VerificationStep(
                    "playwright", "Run Playwright browser tests", "Browser",
                    new VerificationCommand("npx", ["--no-install", "playwright", "test"]),
                    repository.RootPath, TimeSpan.FromMinutes(10), true));
            }
            else
            {
                gaps.Add(new VerificationGap(
                    "BROWSER_VERIFICATION_UNAVAILABLE",
                    "Browser verification is required, but Playwright is not installed. AgentProof will not install it automatically."));
            }
        }

        return new VerificationPlan(steps, gaps);
    }

    private static VerificationStep DotNetStep(
        string id, string name, string verb, IReadOnlyList<string> suffix, string root, bool required) =>
        new(id, name, ".NET", new VerificationCommand("dotnet", [verb, .. suffix]), root, TimeSpan.FromMinutes(5), required);

    private static string SelectPackageManager(IReadOnlyList<string> packageManagers) =>
        packageManagers.Contains("pnpm") ? "pnpm" : packageManagers.Contains("Yarn") ? "yarn" : "npm";
}
