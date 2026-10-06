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
        var contract = task.GetEffectiveContract();

        var requiredEvidence = new HashSet<EvidenceType>(contract.RequiredEvidence);
        foreach (var criterion in contract.AcceptanceCriteria)
        {
            foreach (var evidence in criterion.RequiredEvidence)
            {
                requiredEvidence.Add(evidence);
            }
        }

        if (task.HasBrowserVisibleChanges) requiredEvidence.Add(EvidenceType.Browser);
        if (task.HasDatabaseChanges) requiredEvidence.Add(EvidenceType.Database);

        if (repository.Technologies.Contains(".NET"))
        {
            var entryPoint = configurationReader.FindDotNetEntryPoint(repository.RootPath);
            var suffix = entryPoint is null ? Array.Empty<string>() : new[] { entryPoint };
            steps.Add(DotNetStep("dotnet-restore", "Restore .NET dependencies", "restore", suffix, repository.RootPath, true, []));
            steps.Add(DotNetStep("dotnet-build", "Build .NET solution", "build", suffix, repository.RootPath, true, [EvidenceType.Build]));
            steps.Add(DotNetStep("dotnet-test", "Run .NET tests", "test", suffix, repository.RootPath, true, [EvidenceType.Tests]));
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
                    repository.RootPath, TimeSpan.FromMinutes(5), true, NodeScriptEvidence(script)));
            }
        }

        if (requiredEvidence.Contains(EvidenceType.Browser))
        {
            if (repository.TestFrameworks.Contains("Playwright"))
            {
                steps.Add(new VerificationStep(
                    "playwright", "Run Playwright browser tests", "Browser",
                    new VerificationCommand("npx", ["--no-install", "playwright", "test"]),
                    repository.RootPath, TimeSpan.FromMinutes(10), true, [EvidenceType.Browser]));
            }
            else
            {
                gaps.Add(new VerificationGap(
                    "BROWSER_VERIFICATION_UNAVAILABLE",
                    "Browser verification is required, but Playwright is not installed. AgentProof will not install it automatically."));
            }
        }

        var providedEvidence = new HashSet<EvidenceType>(steps.SelectMany(x => x.ProvidedEvidence));

        if (requiredEvidence.Contains(EvidenceType.Database) && !providedEvidence.Contains(EvidenceType.Database))
        {
            gaps.Add(new VerificationGap(
                "MISSING_EVIDENCE_DATABASE",
                "Database verification is required, but no database verification capability is available."));
        }

        if (requiredEvidence.Contains(EvidenceType.Runtime) && !providedEvidence.Contains(EvidenceType.Runtime))
        {
            gaps.Add(new VerificationGap(
                "MISSING_EVIDENCE_RUNTIME",
                "Runtime verification is required, but no runtime verification capability is available."));
        }

        if (requiredEvidence.Contains(EvidenceType.Performance) && !providedEvidence.Contains(EvidenceType.Performance))
        {
            gaps.Add(new VerificationGap(
                "MISSING_EVIDENCE_PERFORMANCE",
                "Performance verification is required, but no performance verification capability is available."));
        }

        if (requiredEvidence.Contains(EvidenceType.Build) && !providedEvidence.Contains(EvidenceType.Build))
        {
            gaps.Add(new VerificationGap(
                "MISSING_EVIDENCE_BUILD",
                "Build verification is required, but no build verification capability is available."));
        }

        if (requiredEvidence.Contains(EvidenceType.Tests) && !providedEvidence.Contains(EvidenceType.Tests))
        {
            gaps.Add(new VerificationGap(
                "MISSING_EVIDENCE_TESTS",
                "Test verification is required, but no test verification capability is available."));
        }

        foreach (var criterion in contract.AcceptanceCriteria)
        {
            if (criterion.RequiredEvidence.Count == 0 && contract.RequiredEvidence.Count == 0)
            {
                gaps.Add(new VerificationGap(
                    "UNVERIFIED_ACCEPTANCE_CRITERION",
                    string.IsNullOrWhiteSpace(criterion.Id)
                        ? "Acceptance criterion is not connected to any required verification evidence."
                        : $"Acceptance criterion '{criterion.Id}' is not connected to any required verification evidence."));
            }
        }

        return new VerificationPlan(steps, gaps);
    }

    private static VerificationStep DotNetStep(
        string id, string name, string verb, IReadOnlyList<string> suffix, string root, bool required, IReadOnlyList<EvidenceType> provided) =>
        new(id, name, ".NET", new VerificationCommand("dotnet", [verb, .. suffix]), root, TimeSpan.FromMinutes(5), required, provided);

    private static IReadOnlyList<EvidenceType> NodeScriptEvidence(string script) =>
        script switch
        {
            "build" => [EvidenceType.Build],
            "test" => [EvidenceType.Tests],
            _ => []
        };

    private static string SelectPackageManager(IReadOnlyList<string> packageManagers) =>
        packageManagers.Contains("pnpm") ? "pnpm" : packageManagers.Contains("Yarn") ? "yarn" : "npm";
}
