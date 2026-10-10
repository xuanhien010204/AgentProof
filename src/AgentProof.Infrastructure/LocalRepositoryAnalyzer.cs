using System.Text.Json;
using System.Xml.Linq;
using AgentProof.Application;
using AgentProof.Domain;

namespace AgentProof.Infrastructure;

public sealed class LocalRepositoryAnalyzer : IRepositoryAnalyzer
{
    private const long MaxInspectableFileSize = 1_048_576;
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git", "bin", "obj", "node_modules", ".next", "dist", "build", "coverage", "TestResults" };
    private static readonly HashSet<string> VitestConfigNames = new(StringComparer.OrdinalIgnoreCase)
    { "vitest.config.ts", "vitest.config.js", "vitest.config.mts", "vitest.config.mjs" };

    public Task<RepositoryProfile> AnalyzeAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var root = NormalizeExistingDirectory(repositoryPath);
        var files = EnumerateRepositoryFiles(root, cancellationToken).ToArray();
        var projectFiles = files.Where(x => x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToArray();
        var packageFiles = files.Where(x => Path.GetFileName(x).Equals("package.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        var projectFacts = projectFiles.ToDictionary(x => x, x => ReadProjectFacts(x, IsInspectableFile(x)), PathComparer);
        var packageFacts = packageFiles.ToDictionary(x => x, x => IsInspectableFile(x) ? ReadPackage(x) : EmptyPackageFacts(), PathComparer);
        var technologies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var testFrameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packageManagers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builders = new Dictionary<string, WorkspaceBuilder>(PathComparer);
        var hasDocker = false;
        long estimatedSize = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info;
            try { info = new FileInfo(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            estimatedSize += info.Length;
            var name = info.Name;
            if (name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                technologies.Add(".NET");
                var builder = GetBuilder(builders, Path.GetDirectoryName(file)!);
                builder.Technologies.Add(".NET");
                if (builder.DotNetEntryPoint is null || string.CompareOrdinal(name, builder.DotNetEntryPoint) < 0) builder.DotNetEntryPoint = name;
                builder.SolutionProjectPaths.UnionWith(ReadSolutionProjectPaths(file));
            }
            else if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                technologies.Add(".NET");
                var facts = projectFacts[file];
                AddFacts(frameworks, testFrameworks, facts.Frameworks, facts.Tests);
            }
            else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase) && info.Length <= MaxInspectableFileSize)
            {
                technologies.Add("Node.js");
                var package = packageFacts[file];
                AddFacts(frameworks, testFrameworks, package.Frameworks, package.Tests);
                technologies.UnionWith(package.Technologies);
            }
            else if (name.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase) || name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase))
            {
                technologies.Add("Python");
                var builder = GetBuilder(builders, Path.GetDirectoryName(file)!);
                builder.Technologies.Add("Python");
                if (ContainsText(file, "fastapi", info.Length <= MaxInspectableFileSize))
                {
                    frameworks.Add("FastAPI");
                    builder.Frameworks.Add("FastAPI");
                }
            }
            else if (name.Equals("pubspec.yaml", StringComparison.OrdinalIgnoreCase))
            {
                technologies.Add("Flutter");
                var builder = GetBuilder(builders, Path.GetDirectoryName(file)!);
                builder.Technologies.Add("Flutter");
            }

            if (name.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase)) packageManagers.Add("npm");
            if (name.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase)) packageManagers.Add("pnpm");
            if (name.Equals("yarn.lock", StringComparison.OrdinalIgnoreCase)) packageManagers.Add("Yarn");
            if (name.StartsWith("tailwind.config.", StringComparison.OrdinalIgnoreCase)) frameworks.Add("Tailwind CSS");
            if (name.StartsWith("playwright.config.", StringComparison.OrdinalIgnoreCase)) testFrameworks.Add("Playwright");
            if (VitestConfigNames.Contains(name)) testFrameworks.Add("Vitest");
            if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) hasDocker = true;
            if (name.Equals("compose.yml", StringComparison.OrdinalIgnoreCase) || name.Equals("compose.yaml", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("docker-compose.yml", StringComparison.OrdinalIgnoreCase) || name.Equals("docker-compose.yaml", StringComparison.OrdinalIgnoreCase))
            { hasDocker = true; technologies.Add("Docker Compose"); }
        }

        var solutionBuilders = builders.Values.Where(x => x.DotNetEntryPoint is not null).ToArray();
        var projectsAssignedToSolution = new HashSet<string>(PathComparer);
        foreach (var file in projectFiles)
        {
            var projectDirectory = Path.GetDirectoryName(file)!;
            var normalizedProjectPath = NormalizePath(file);
            var matchingSolutions = solutionBuilders
                .Where(x => x.SolutionProjectPaths.Contains(normalizedProjectPath))
                .ToArray();

            // A malformed or project-less solution still establishes a local boundary.
            // Only use the physical containment fallback when no project membership was parsed.
            if (matchingSolutions.Length == 0)
            {
                matchingSolutions = solutionBuilders
                    .Where(x => x.SolutionProjectPaths.Count == 0 && IsWithin(x.AbsolutePath, projectDirectory))
                    .OrderByDescending(x => x.AbsolutePath.Length)
                    .Take(1)
                    .ToArray();
            }

            if (matchingSolutions.Length == 0) continue;

            projectsAssignedToSolution.Add(file);
            foreach (var builder in matchingSolutions)
                builder.AddProjectFacts(projectFacts[file]);
        }

        var standaloneProjects = projectFiles.Where(x => !projectsAssignedToSolution.Contains(x)).ToArray();
        foreach (var component in FindProjectComponents(standaloneProjects, projectFacts))
        {
            var entryPoint = SelectProjectEntryPoint(component, projectFacts);
            var workspaceRoot = Path.GetDirectoryName(entryPoint)!;
            var builder = GetBuilder(builders, workspaceRoot);
            builder.Technologies.Add(".NET");
            builder.DotNetEntryPoint ??= Path.GetRelativePath(workspaceRoot, entryPoint);
            foreach (var project in component) builder.AddProjectFacts(projectFacts[project]);
        }

        foreach (var file in packageFiles)
        {
            var directory = Path.GetDirectoryName(file)!;
            var builder = GetBuilder(builders, directory);
            builder.Technologies.Add("Node.js");
            if (IsInspectableFile(file))
            {
                var package = packageFacts[file];
                AddFacts(builder.Frameworks, builder.TestFrameworks, package.Frameworks, package.Tests);
                builder.Technologies.UnionWith(package.Technologies);
                foreach (var script in package.Scripts) builder.PackageScripts[script.Key] = script.Value;
            }
            builder.PackageManager = ResolvePackageManager(directory);
            if (File.Exists(Path.Combine(directory, "playwright.config.ts")) || File.Exists(Path.Combine(directory, "playwright.config.js"))) builder.TestFrameworks.Add("Playwright");
            if (VitestConfigNames.Any(name => File.Exists(Path.Combine(directory, name)))) builder.TestFrameworks.Add("Vitest");
        }

        if (technologies.Contains("Node.js") && packageManagers.Count == 0) packageManagers.Add("npm");
        if (hasDocker) technologies.Add("Docker");
        var workspaces = builders.Values.Where(x => x.Technologies.Count > 0).Select(x => x.ToWorkspace(root)).OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray();
        var profile = new RepositoryProfile(new DirectoryInfo(root).Name, root, Sort(technologies), Sort(frameworks), Sort(testFrameworks), Sort(packageManagers), hasDocker,
            Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")), estimatedSize) { Workspaces = workspaces };
        return Task.FromResult(profile);
    }

    public static string NormalizeExistingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Repository path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"Repository path does not exist: {fullPath}");
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static IEnumerable<string> EnumerateRepositoryFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> files; IEnumerable<string> directories;
            try { files = Directory.EnumerateFiles(directory); directories = Directory.EnumerateDirectories(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
            foreach (var child in directories)
                if (!ShouldIgnoreDirectory(root, child)) pending.Push(child);
        }
    }

    private static bool ShouldIgnoreDirectory(string root, string directory)
    {
        var name = Path.GetFileName(directory);
        return IgnoredDirectories.Contains(name) ||
            (name.Length > 0 && name[0] == '.' && !directory.Equals(root, PathComparison));
    }

    private static WorkspaceBuilder GetBuilder(Dictionary<string, WorkspaceBuilder> builders, string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!builders.TryGetValue(fullPath, out var builder)) builders[fullPath] = builder = new WorkspaceBuilder(fullPath);
        return builder;
    }

    private static string? ResolvePackageManager(string directory)
    {
        if (File.Exists(Path.Combine(directory, "package-lock.json"))) return "npm";
        if (File.Exists(Path.Combine(directory, "pnpm-lock.yaml"))) return "pnpm";
        if (File.Exists(Path.Combine(directory, "yarn.lock"))) return "Yarn";
        return null;
    }

    private static ProjectFacts ReadProjectFacts(string path, bool inspect)
    {
        var frameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var tests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectReferences = new HashSet<string>(PathComparer);
        if (!inspect) return new ProjectFacts(frameworks, tests, projectReferences);
        try
        {
            var document = XDocument.Load(path, LoadOptions.None);
            if ((document.Root?.Attribute("Sdk")?.Value ?? string.Empty).Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)) frameworks.Add("ASP.NET Core");
            foreach (var package in document.Descendants().Where(x => x.Name.LocalName == "PackageReference").Select(x => x.Attribute("Include")?.Value ?? x.Attribute("Update")?.Value ?? string.Empty))
            {
                if (package.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase)) frameworks.Add("ASP.NET Core");
                if (package.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)) frameworks.Add("EF Core");
                if (package.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)) tests.Add("xUnit");
                if (package.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase)) tests.Add("NUnit");
                if (package.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase)) tests.Add("MSTest");
                if (package.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)) tests.Add("MSTest");
            }

            if (tests.Count == 0 && document.Descendants().Any(x => x.Name.LocalName == "IsTestProject" && string.Equals(x.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)))
            {
                tests.Add("MSTest");
            }

            foreach (var reference in document.Descendants().Where(x => x.Name.LocalName == "ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value ?? reference.Attribute("Update")?.Value;
                var resolved = ResolveReferencedProjectPath(path, include);
                if (resolved is not null) projectReferences.Add(resolved);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { }
        return new ProjectFacts(frameworks, tests, projectReferences);
    }

    private static HashSet<string> ReadSolutionProjectPaths(string path)
    {
        var projects = new HashSet<string>(PathComparer);
        try
        {
            if (path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                var document = XDocument.Load(path, LoadOptions.None);
                foreach (var project in document.Descendants().Where(x => x.Name.LocalName == "Project"))
                    AddResolvedProjectPath(projects, path, project.Attribute("Path")?.Value);
            }
            else
            {
                foreach (var line in File.ReadLines(path))
                {
                    var quoted = line.Split('"');
                    if (quoted.Length > 5 && quoted[0].TrimStart().StartsWith("Project(", StringComparison.Ordinal))
                        AddResolvedProjectPath(projects, path, quoted[5]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { }
        return projects;
    }

    private static void AddResolvedProjectPath(HashSet<string> projects, string sourcePath, string? relativePath)
    {
        var resolved = ResolveReferencedProjectPath(sourcePath, relativePath);
        if (resolved is not null && resolved.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) projects.Add(resolved);
    }

    private static string? ResolveReferencedProjectPath(string sourcePath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        try
        {
            var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
            return NormalizePath(Path.Combine(directory, relativePath));
        }
        catch (ArgumentException) { return null; }
    }

    private static List<IReadOnlyList<string>> FindProjectComponents(
        IReadOnlyList<string> projects,
        IReadOnlyDictionary<string, ProjectFacts> projectFacts)
    {
        var projectSet = projects.ToHashSet(PathComparer);
        var adjacency = projects.ToDictionary(project => project, _ => new HashSet<string>(PathComparer), PathComparer);
        foreach (var project in projects)
        {
            foreach (var reference in projectFacts[project].ProjectReferences.Where(projectSet.Contains))
            {
                adjacency[project].Add(reference);
                adjacency[reference].Add(project);
            }
        }

        var components = new List<IReadOnlyList<string>>();
        var visited = new HashSet<string>(PathComparer);
        foreach (var project in projects.OrderBy(x => x, PathComparer))
        {
            if (!visited.Add(project)) continue;
            var component = new List<string>();
            var pending = new Stack<string>();
            pending.Push(project);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                component.Add(current);
                foreach (var reference in adjacency[current].OrderBy(x => x, PathComparer))
                    if (visited.Add(reference)) pending.Push(reference);
            }
            components.Add(component.OrderBy(x => x, PathComparer).ToArray());
        }
        return components;
    }

    private static string SelectProjectEntryPoint(
        IReadOnlyList<string> component,
        IReadOnlyDictionary<string, ProjectFacts> projectFacts)
    {
        var componentSet = component.ToHashSet(PathComparer);
        var referencedProjects = component
            .SelectMany(project => projectFacts[project].ProjectReferences)
            .Where(componentSet.Contains)
            .ToHashSet(PathComparer);
        return component.Where(project => !referencedProjects.Contains(project)).OrderBy(x => x, PathComparer).FirstOrDefault()
            ?? component.OrderBy(x => x, PathComparer).First();
    }

    private static bool IsInspectableFile(string path)
    {
        try { return new FileInfo(path).Length <= MaxInspectableFileSize; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool ContainsText(string path, string value, bool inspect)
    {
        if (!inspect) return false;
        try { return File.ReadAllText(path).Contains(value, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static PackageFacts ReadPackage(string path)
    {
        var frameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var tests = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var technologies = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(File.OpenRead(path));
            AddPackageDependencies(document.RootElement, "dependencies", frameworks, tests, technologies); AddPackageDependencies(document.RootElement, "devDependencies", frameworks, tests, technologies);
            if (document.RootElement.TryGetProperty("scripts", out var scriptElement) && scriptElement.ValueKind == JsonValueKind.Object)
                foreach (var property in scriptElement.EnumerateObject()) if (property.Value.ValueKind == JsonValueKind.String) scripts[property.Name] = property.Value.GetString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new PackageFacts(frameworks, tests, technologies, scripts);
    }

    private static void AddPackageDependencies(JsonElement root, string propertyName, HashSet<string> frameworks, HashSet<string> tests, HashSet<string> technologies)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object) return;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name.Equals("react", StringComparison.OrdinalIgnoreCase)) frameworks.Add("React");
            if (property.Name.Equals("next", StringComparison.OrdinalIgnoreCase)) frameworks.Add("Next.js");
            if (property.Name.Equals("typescript", StringComparison.OrdinalIgnoreCase)) technologies.Add("TypeScript");
            if (property.Name.Equals("tailwindcss", StringComparison.OrdinalIgnoreCase)) frameworks.Add("Tailwind CSS");
            if (property.Name.Equals("@playwright/test", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("playwright", StringComparison.OrdinalIgnoreCase)) tests.Add("Playwright");
            if (property.Name.Equals("vitest", StringComparison.OrdinalIgnoreCase)) tests.Add("Vitest");
            if (property.Name.Equals("jest", StringComparison.OrdinalIgnoreCase) || property.Name.StartsWith("@jest/", StringComparison.OrdinalIgnoreCase)) tests.Add("Jest");
            if (property.Name.Equals("mocha", StringComparison.OrdinalIgnoreCase)) tests.Add("Mocha");
            if (property.Name.Equals("vite", StringComparison.OrdinalIgnoreCase)) frameworks.Add("Vite");
        }
    }

    private static void AddFacts(HashSet<string> frameworks, HashSet<string> tests, IEnumerable<string> newFrameworks, IEnumerable<string> newTests) { frameworks.UnionWith(newFrameworks); tests.UnionWith(newTests); }
    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool IsWithin(string parent, string child) => child.Equals(parent, PathComparison) || child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, PathComparison);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static string[] Sort(IEnumerable<string> values) => values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    private sealed class WorkspaceBuilder(string absolutePath)
    {
        public string AbsolutePath { get; } = absolutePath;
        public HashSet<string> Technologies { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Frameworks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TestFrameworks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> PackageScripts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SolutionProjectPaths { get; } = new(PathComparer);
        public string? PackageManager { get; set; }
        public string? DotNetEntryPoint { get; set; }
        public void AddProjectFacts(ProjectFacts facts) => AddFacts(Frameworks, TestFrameworks, facts.Frameworks, facts.Tests);
        public RepositoryWorkspace ToWorkspace(string root)
        {
            var relative = Path.GetRelativePath(root, AbsolutePath).Replace(Path.DirectorySeparatorChar, '/');
            return new RepositoryWorkspace(relative, relative, Sort(Technologies), Sort(Frameworks), Sort(TestFrameworks), PackageManager, DotNetEntryPoint,
                new Dictionary<string, string>(PackageScripts, StringComparer.OrdinalIgnoreCase));
        }
    }
    private sealed record PackageFacts(HashSet<string> Frameworks, HashSet<string> Tests, HashSet<string> Technologies, Dictionary<string, string> Scripts);
    private sealed record ProjectFacts(HashSet<string> Frameworks, HashSet<string> Tests, HashSet<string> ProjectReferences);

    private static PackageFacts EmptyPackageFacts() => new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}
