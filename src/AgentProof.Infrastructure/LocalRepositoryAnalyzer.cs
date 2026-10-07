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
            }
            else if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                technologies.Add(".NET");
                var facts = ReadProjectFacts(file, info.Length <= MaxInspectableFileSize);
                AddFacts(frameworks, testFrameworks, facts.Frameworks, facts.Tests);
            }
            else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase) && info.Length <= MaxInspectableFileSize)
            {
                technologies.Add("Node.js");
                var package = ReadPackage(file);
                AddFacts(frameworks, testFrameworks, package.Frameworks, package.Tests);
                technologies.UnionWith(package.Technologies);
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

        var solutionRoots = builders.Values.Where(x => x.DotNetEntryPoint is not null).Select(x => x.AbsolutePath).ToArray();
        foreach (var file in files.Where(x => x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var projectDirectory = Path.GetDirectoryName(file)!;
            var solutionRoot = solutionRoots.Where(x => IsWithin(x, projectDirectory)).OrderByDescending(x => x.Length).FirstOrDefault();
            var builder = GetBuilder(builders, solutionRoot ?? projectDirectory);
            builder.Technologies.Add(".NET");
            var facts = ReadProjectFacts(file, new FileInfo(file).Length <= MaxInspectableFileSize);
            AddFacts(builder.Frameworks, builder.TestFrameworks, facts.Frameworks, facts.Tests);
        }

        foreach (var file in files.Where(x => Path.GetFileName(x).Equals("package.json", StringComparison.OrdinalIgnoreCase)))
        {
            var directory = Path.GetDirectoryName(file)!;
            var builder = GetBuilder(builders, directory);
            builder.Technologies.Add("Node.js");
            if (new FileInfo(file).Length <= MaxInspectableFileSize)
            {
                var package = ReadPackage(file);
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
            foreach (var child in directories) if (!IgnoredDirectories.Contains(Path.GetFileName(child))) pending.Push(child);
        }
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

    private static (HashSet<string> Frameworks, HashSet<string> Tests) ReadProjectFacts(string path, bool inspect)
    {
        var frameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var tests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!inspect) return (frameworks, tests);
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
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { }
        return (frameworks, tests);
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
            if (property.Name.Equals("vite", StringComparison.OrdinalIgnoreCase)) frameworks.Add("Vite");
        }
    }

    private static void AddFacts(HashSet<string> frameworks, HashSet<string> tests, IEnumerable<string> newFrameworks, IEnumerable<string> newTests) { frameworks.UnionWith(newFrameworks); tests.UnionWith(newTests); }
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
        public string? PackageManager { get; set; }
        public string? DotNetEntryPoint { get; set; }
        public RepositoryWorkspace ToWorkspace(string root)
        {
            var relative = Path.GetRelativePath(root, AbsolutePath).Replace(Path.DirectorySeparatorChar, '/');
            return new RepositoryWorkspace(relative, relative, Sort(Technologies), Sort(Frameworks), Sort(TestFrameworks), PackageManager, DotNetEntryPoint,
                new Dictionary<string, string>(PackageScripts, StringComparer.OrdinalIgnoreCase));
        }
    }
    private sealed record PackageFacts(HashSet<string> Frameworks, HashSet<string> Tests, HashSet<string> Technologies, Dictionary<string, string> Scripts);
}
