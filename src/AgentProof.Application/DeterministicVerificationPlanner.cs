using AgentProof.Domain;

namespace AgentProof.Application;

public sealed class DeterministicVerificationPlanner(IRepositoryConfigurationReader configurationReader) : IVerificationPlanner
{
    private static readonly string[] KnownNodeScripts = ["lint", "typecheck", "test", "build"];

    public async Task<VerificationPlan> CreateAsync(RepositoryProfile repository, TaskContext task, CancellationToken cancellationToken = default)
    {
        var steps = new List<VerificationStep>();
        var gaps = new List<VerificationGap>();
        var contract = task.GetEffectiveContract();
        var requiredEvidence = new HashSet<EvidenceType>(contract.RequiredEvidence);
        foreach (var criterion in contract.AcceptanceCriteria) foreach (var evidence in criterion.RequiredEvidence) requiredEvidence.Add(evidence);
        if (task.HasBrowserVisibleChanges) requiredEvidence.Add(EvidenceType.Browser);
        if (task.HasDatabaseChanges) requiredEvidence.Add(EvidenceType.Database);

        var workspaces = repository.Workspaces.Count == 0
            ? await LegacyWorkspaceAsync(repository, cancellationToken)
            : repository.Workspaces;
        foreach (var workspace in workspaces)
        {
            var workspaceRoot = Path.Combine(repository.RootPath, workspace.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var workspaceKey = WorkspaceKey(workspace.Id);
            if (workspace.Technologies.Contains(".NET"))
            {
                var suffix = workspace.DotNetEntryPoint is null ? Array.Empty<string>() : new[] { workspace.DotNetEntryPoint };
                steps.Add(DotNetStep(StepId(workspaceKey, "dotnet-restore"), "Restore .NET dependencies", "restore", suffix, workspaceRoot, []));
                steps.Add(DotNetStep(StepId(workspaceKey, "dotnet-build"), "Build .NET solution", "build", suffix, workspaceRoot, [EvidenceType.Build]));
                steps.Add(DotNetStep(StepId(workspaceKey, "dotnet-test"), "Run .NET tests", "test", suffix, workspaceRoot, [EvidenceType.Tests]));
            }

            if (workspace.Technologies.Contains("Node.js"))
            {
                var scripts = workspace.PackageScripts;
                if (scripts.Count == 0) scripts = await configurationReader.ReadPackageScriptsAsync(workspaceRoot, cancellationToken);
                var manager = workspace.PackageManager ?? "npm";
                foreach (var script in KnownNodeScripts.Where(scripts.ContainsKey))
                    steps.Add(new VerificationStep(StepId(workspaceKey, $"node-{script}"), $"Run Node.js {script}", "Node", new VerificationCommand(manager, ["run", script]), workspaceRoot, TimeSpan.FromMinutes(5), true, NodeScriptEvidence(script)));
            }
        }

        if (requiredEvidence.Contains(EvidenceType.Browser))
        {
            var playwrightWorkspace = workspaces.FirstOrDefault(x => x.Technologies.Contains("Node.js") && x.TestFrameworks.Contains("Playwright"));
            if (playwrightWorkspace is not null)
            {
                var workspaceRoot = Path.Combine(repository.RootPath, playwrightWorkspace.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                steps.Add(new VerificationStep(StepId(WorkspaceKey(playwrightWorkspace.Id), "playwright"), "Run Playwright browser tests", "Browser",
                    new VerificationCommand("npx", ["--no-install", "playwright", "test"]), workspaceRoot, TimeSpan.FromMinutes(10), true, [EvidenceType.Browser]));
            }
            else gaps.Add(new VerificationGap("BROWSER_VERIFICATION_UNAVAILABLE", "Browser verification is required, but no workspace has Playwright installed. AgentProof will not install it automatically."));
        }

        var providedEvidence = new HashSet<EvidenceType>(steps.SelectMany(x => x.ProvidedEvidence));
        AddMissingEvidenceGaps(gaps, requiredEvidence, providedEvidence);
        foreach (var criterion in contract.AcceptanceCriteria)
            if (criterion.RequiredEvidence.Count == 0 && contract.RequiredEvidence.Count == 0)
                gaps.Add(new VerificationGap("UNVERIFIED_ACCEPTANCE_CRITERION", string.IsNullOrWhiteSpace(criterion.Id)
                    ? "Acceptance criterion is not connected to any required verification evidence."
                    : $"Acceptance criterion '{criterion.Id}' is not connected to any required verification evidence."));
        return new VerificationPlan(steps, gaps);
    }

    private async Task<IReadOnlyList<RepositoryWorkspace>> LegacyWorkspaceAsync(RepositoryProfile repository, CancellationToken cancellationToken)
    {
        var scripts = await configurationReader.ReadPackageScriptsAsync(repository.RootPath, cancellationToken);
        var entryPoint = configurationReader.FindDotNetEntryPoint(repository.RootPath);
        return [new RepositoryWorkspace(".", ".", repository.Technologies, repository.Frameworks, repository.TestFrameworks,
            SelectPackageManager(repository.PackageManagers), entryPoint, scripts)];
    }

    private static void AddMissingEvidenceGaps(List<VerificationGap> gaps, HashSet<EvidenceType> required, HashSet<EvidenceType> provided)
    {
        foreach (var (type, code, reason) in new[]
        {
            (EvidenceType.Database, "MISSING_EVIDENCE_DATABASE", "Database verification is required, but no database verification capability is available."),
            (EvidenceType.Runtime, "MISSING_EVIDENCE_RUNTIME", "Runtime verification is required, but no runtime verification capability is available."),
            (EvidenceType.Performance, "MISSING_EVIDENCE_PERFORMANCE", "Performance verification is required, but no performance verification capability is available."),
            (EvidenceType.Build, "MISSING_EVIDENCE_BUILD", "Build verification is required, but no build verification capability is available."),
            (EvidenceType.Tests, "MISSING_EVIDENCE_TESTS", "Test verification is required, but no test verification capability is available.")
        }) if (required.Contains(type) && !provided.Contains(type)) gaps.Add(new VerificationGap(code, reason));
    }

    private static VerificationStep DotNetStep(string id, string name, string verb, IReadOnlyList<string> suffix, string root, IReadOnlyList<EvidenceType> provided) =>
        new(id, name, ".NET", new VerificationCommand("dotnet", [verb, .. suffix]), root, TimeSpan.FromMinutes(5), true, provided);

    private static IReadOnlyList<EvidenceType> NodeScriptEvidence(string script) => script switch
    {
        "build" => [EvidenceType.Build], "test" => [EvidenceType.Tests], _ => []
    };

    private static string SelectPackageManager(IReadOnlyList<string> packageManagers) =>
        packageManagers.Contains("pnpm") ? "pnpm" : packageManagers.Contains("Yarn") ? "yarn" : "npm";

    private static string WorkspaceKey(string id)
    {
        if (id == ".") return string.Empty;
        var key = new string(id.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return string.IsNullOrEmpty(key) ? "workspace" : key;
    }

    private static string StepId(string workspaceKey, string step) => string.IsNullOrEmpty(workspaceKey) ? step : $"{workspaceKey}:{step}";
}
