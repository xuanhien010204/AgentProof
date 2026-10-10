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
        var effectiveRequirements = contract.GetEffectiveEvidenceRequirements()
            .Concat(contract.AcceptanceCriteria.SelectMany(criterion => criterion.GetEffectiveEvidenceRequirements()))
            .Distinct()
            .ToArray();
        var requiredEvidence = new HashSet<EvidenceType>(effectiveRequirements.Select(x => x.Type));
        if (task.HasBrowserVisibleChanges) requiredEvidence.Add(EvidenceType.Browser);
        if (task.HasDatabaseChanges) requiredEvidence.Add(EvidenceType.Database);

        var browserRequirements = effectiveRequirements
            .Where(requirement => requirement.Type == EvidenceType.Browser)
            .ToList();
        if (task.HasBrowserVisibleChanges) browserRequirements.Add(new EvidenceRequirement { Type = EvidenceType.Browser });
        browserRequirements = browserRequirements.Distinct().ToList();

        var workspaces = repository.Workspaces.Count == 0
            ? await LegacyWorkspaceAsync(repository, cancellationToken)
            : repository.Workspaces;
        var plannedWorkspaces = SelectPlannedWorkspaces(workspaces, effectiveRequirements, task);
        foreach (var workspace in plannedWorkspaces)
        {
            var workspaceRoot = Path.Combine(repository.RootPath, workspace.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var workspaceKey = WorkspaceKey(workspace.Id);
            if (workspace.Technologies.Contains(".NET"))
            {
                var suffix = workspace.DotNetEntryPoint is null ? Array.Empty<string>() : new[] { workspace.DotNetEntryPoint };
                steps.Add(DotNetStep(StepId(workspaceKey, "dotnet-restore"), "Restore .NET dependencies", "restore", suffix, workspaceRoot, [], workspace.Id));
                steps.Add(DotNetStep(StepId(workspaceKey, "dotnet-build"), "Build .NET solution", "build", suffix, workspaceRoot, [EvidenceType.Build], workspace.Id));
                var isStandaloneCsproj = workspace.DotNetEntryPoint is not null &&
                    workspace.DotNetEntryPoint.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
                if (workspace.TestFrameworks.Count > 0 || !isStandaloneCsproj)
                {
                    steps.Add(DotNetStep(StepId(workspaceKey, "dotnet-test"), "Run .NET tests", "test", suffix, workspaceRoot, [EvidenceType.Tests], workspace.Id));
                }
            }

            if (workspace.Technologies.Contains("Node.js"))
            {
                var scripts = workspace.PackageScripts;
                if (scripts.Count == 0) scripts = await configurationReader.ReadPackageScriptsAsync(workspaceRoot, cancellationToken);
                var manager = workspace.PackageManager ?? "npm";
                foreach (var script in KnownNodeScripts.Where(scripts.ContainsKey))
                    steps.Add(new VerificationStep(StepId(workspaceKey, $"node-{script}"), $"Run Node.js {script}", "Node", new VerificationCommand(manager, ["run", script]), workspaceRoot, TimeSpan.FromMinutes(5), true, NodeScriptEvidence(script), workspace.Id));
            }

            if (!workspace.Technologies.Contains(".NET") && !workspace.Technologies.Contains("Node.js"))
            {
                var tech = workspace.Technologies.Count == 0 ? "unknown" : string.Join(", ", workspace.Technologies);
                AddGap(gaps, "UNSUPPORTED_WORKSPACE_VERIFIER", $"Workspace '{workspace.Id}' is in scope, but no active verifier is available for: {tech}.");
            }
        }

        if (requiredEvidence.Contains(EvidenceType.Browser))
        {
            var addedBrowserWorkspaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var requirement in browserRequirements.Where(x => x.WorkspaceId is not null))
            {
                var workspace = workspaces.FirstOrDefault(x => string.Equals(x.Id, requirement.WorkspaceId, StringComparison.Ordinal));
                if (workspace is null) continue;
                if (!HasPlaywright(workspace))
                {
                    AddGap(gaps, "BROWSER_VERIFICATION_UNAVAILABLE", $"Browser verification is required for workspace '{workspace.Id}', but Playwright is not installed there. AgentProof will not install it automatically.");
                    continue;
                }

                if (addedBrowserWorkspaces.Add(workspace.Id)) AddPlaywrightStep(steps, repository.RootPath, workspace);
            }

            if (browserRequirements.Any(x => x.WorkspaceId is null))
            {
                var workspace = workspaces.FirstOrDefault(HasPlaywright);
                if (workspace is null)
                    AddGap(gaps, "BROWSER_VERIFICATION_UNAVAILABLE", "Browser verification is required, but no workspace has Playwright installed. AgentProof will not install it automatically.");
                else if (addedBrowserWorkspaces.Add(workspace.Id)) AddPlaywrightStep(steps, repository.RootPath, workspace);
            }
        }

        var providedEvidence = new HashSet<EvidenceType>(steps.SelectMany(x => x.ProvidedEvidence));
        AddMissingEvidenceGaps(gaps, requiredEvidence, providedEvidence);
        foreach (var req in effectiveRequirements.Where(r => r.WorkspaceId is not null))
        {
            var ws = workspaces.FirstOrDefault(w => string.Equals(w.Id, req.WorkspaceId, StringComparison.Ordinal));
            if (ws is null)
            {
                AddGap(gaps, "MISSING_WORKSPACE_EVIDENCE_PROVIDER", $"Evidence requirement references unknown workspace '{req.WorkspaceId}'.");
            }
            else if (req.Type != EvidenceType.Browser &&
                     !steps.Any(s => string.Equals(s.WorkspaceId, req.WorkspaceId, StringComparison.Ordinal) && s.ProvidedEvidence.Contains(req.Type)))
            {
                AddGap(gaps, "MISSING_WORKSPACE_EVIDENCE_PROVIDER", $"No verification capability is available for evidence type '{req.Type}' in workspace '{req.WorkspaceId}'.");
            }
        }
        foreach (var criterion in contract.AcceptanceCriteria)
            if (criterion.GetEffectiveEvidenceRequirements().Count == 0 && contract.GetEffectiveEvidenceRequirements().Count == 0)
                gaps.Add(new VerificationGap("UNVERIFIED_ACCEPTANCE_CRITERION", string.IsNullOrWhiteSpace(criterion.Id)
                    ? "Acceptance criterion is not connected to any required verification evidence."
                    : $"Acceptance criterion '{criterion.Id}' is not connected to any required verification evidence."));
        return new VerificationPlan(steps, gaps, plannedWorkspaces.Select(x => x.Id).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<RepositoryWorkspace> SelectPlannedWorkspaces(
        IReadOnlyList<RepositoryWorkspace> workspaces,
        IReadOnlyList<EvidenceRequirement> requirements,
        TaskContext task)
    {
        var workspaceEvidenceRequirements = requirements
            .Where(requirement => requirement.Type is EvidenceType.Build or EvidenceType.Tests or EvidenceType.Browser)
            .ToArray();

        // A fallback Browser requirement is intentionally unscoped. Preserve the existing
        // repository-wide behavior whenever it is present, as well as for legacy and mixed
        // contracts. Scoped planning is only safe when every executable requirement is scoped.
        if (task.HasBrowserVisibleChanges || workspaceEvidenceRequirements.Length == 0 ||
            requirements.Any(requirement => requirement.Type is not (EvidenceType.Build or EvidenceType.Tests or EvidenceType.Browser) ||
                requirement.WorkspaceId is null))
            return workspaces;

        var requiredWorkspaceIds = workspaceEvidenceRequirements
            .Select(requirement => requirement.WorkspaceId!)
            .ToHashSet(StringComparer.Ordinal);
        return workspaces.Where(workspace => requiredWorkspaceIds.Contains(workspace.Id)).ToArray();
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

    private static bool HasPlaywright(RepositoryWorkspace workspace) =>
        workspace.Technologies.Contains("Node.js") && workspace.TestFrameworks.Contains("Playwright");

    private static void AddPlaywrightStep(List<VerificationStep> steps, string repositoryRoot, RepositoryWorkspace workspace)
    {
        var workspaceRoot = Path.Combine(repositoryRoot, workspace.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        steps.Add(new VerificationStep(StepId(WorkspaceKey(workspace.Id), "playwright"), "Run Playwright browser tests", "Browser",
            new VerificationCommand("npx", ["--no-install", "playwright", "test"]), workspaceRoot, TimeSpan.FromMinutes(10), true, [EvidenceType.Browser], workspace.Id));
    }

    private static void AddGap(List<VerificationGap> gaps, string code, string reason)
    {
        if (!gaps.Any(gap => gap.Code == code && gap.Reason == reason)) gaps.Add(new VerificationGap(code, reason));
    }

    private static VerificationStep DotNetStep(string id, string name, string verb, IReadOnlyList<string> suffix, string root, IReadOnlyList<EvidenceType> provided, string workspaceId) =>
        new(id, name, ".NET", new VerificationCommand("dotnet", [verb, .. suffix]), root, TimeSpan.FromMinutes(5), true, provided, workspaceId);

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
