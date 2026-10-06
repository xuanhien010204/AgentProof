using System.Text.Json;
using System.Xml.Linq;
using AgentProof.Application;
using AgentProof.Domain;

namespace AgentProof.Infrastructure;

public sealed class LocalRepositoryAnalyzer : IRepositoryAnalyzer
{
    private const long MaxInspectableFileSize = 1_048_576;
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".next", "dist", "build", "coverage", "TestResults"
    };

    public Task<RepositoryProfile> AnalyzeAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var root = NormalizeExistingDirectory(repositoryPath);
        var technologies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var testFrameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packageManagers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasDocker = false;
        long estimatedSize = 0;

        foreach (var file in EnumerateRepositoryFiles(root, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info;
            try { info = new FileInfo(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            estimatedSize += info.Length;
            var name = info.Name;

            if (name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                technologies.Add(".NET");
            }
            else if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                technologies.Add(".NET");
                if (info.Length <= MaxInspectableFileSize)
                    InspectProject(file, frameworks, testFrameworks);
            }
            else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase) && info.Length <= MaxInspectableFileSize)
            {
                technologies.Add("Node.js");
                InspectPackageJson(file, technologies, frameworks, testFrameworks);
            }

            if (name.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase)) packageManagers.Add("npm");
            if (name.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase)) packageManagers.Add("pnpm");
            if (name.Equals("yarn.lock", StringComparison.OrdinalIgnoreCase)) packageManagers.Add("Yarn");
            if (name.StartsWith("tailwind.config.", StringComparison.OrdinalIgnoreCase)) frameworks.Add("Tailwind CSS");
            if (name.StartsWith("playwright.config.", StringComparison.OrdinalIgnoreCase)) testFrameworks.Add("Playwright");
            if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) hasDocker = true;
            if (name.Equals("compose.yml", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("compose.yaml", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("docker-compose.yml", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("docker-compose.yaml", StringComparison.OrdinalIgnoreCase))
            {
                hasDocker = true;
                technologies.Add("Docker Compose");
            }
        }

        if (technologies.Contains("Node.js") && packageManagers.Count == 0) packageManagers.Add("npm");
        if (hasDocker) technologies.Add("Docker");

        return Task.FromResult(new RepositoryProfile(
            new DirectoryInfo(root).Name,
            root,
            Sort(technologies),
            Sort(frameworks),
            Sort(testFrameworks),
            Sort(packageManagers),
            hasDocker,
            Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")),
            estimatedSize));
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
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(directory);
                directories = Directory.EnumerateDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var file in files) yield return file;
            foreach (var child in directories)
                if (!IgnoredDirectories.Contains(Path.GetFileName(child))) pending.Push(child);
        }
    }

    private static void InspectProject(string path, HashSet<string> frameworks, HashSet<string> tests)
    {
        try
        {
            var document = XDocument.Load(path, LoadOptions.None);
            var sdk = document.Root?.Attribute("Sdk")?.Value ?? string.Empty;
            if (sdk.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)) frameworks.Add("ASP.NET Core");

            foreach (var package in document.Descendants().Where(x => x.Name.LocalName == "PackageReference")
                         .Select(x => x.Attribute("Include")?.Value ?? x.Attribute("Update")?.Value ?? string.Empty))
            {
                if (package.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase)) frameworks.Add("ASP.NET Core");
                if (package.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)) frameworks.Add("EF Core");
                if (package.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)) tests.Add("xUnit");
                if (package.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase)) tests.Add("NUnit");
                if (package.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase)) tests.Add("MSTest");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { }
    }

    private static void InspectPackageJson(
        string path, HashSet<string> technologies, HashSet<string> frameworks, HashSet<string> tests)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddPropertyNames(document.RootElement, "dependencies", dependencies);
            AddPropertyNames(document.RootElement, "devDependencies", dependencies);

            if (dependencies.Contains("react")) frameworks.Add("React");
            if (dependencies.Contains("next")) frameworks.Add("Next.js");
            if (dependencies.Contains("typescript")) technologies.Add("TypeScript");
            if (dependencies.Contains("tailwindcss")) frameworks.Add("Tailwind CSS");
            if (dependencies.Contains("@playwright/test") || dependencies.Contains("playwright")) tests.Add("Playwright");
            if (dependencies.Contains("vite")) frameworks.Add("Vite");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static void AddPropertyNames(JsonElement root, string propertyName, HashSet<string> target)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object) return;
        foreach (var property in value.EnumerateObject()) target.Add(property.Name);
    }

    private static string[] Sort(IEnumerable<string> values) =>
        values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
}
