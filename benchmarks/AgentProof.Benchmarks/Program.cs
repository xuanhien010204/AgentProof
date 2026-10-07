using System.Text.Json;
using System.Text.Json.Serialization;
using AgentProof.Benchmarks.Comparison;
using AgentProof.Benchmarks.Execution;
using AgentProof.Benchmarks.Formatting;
using AgentProof.Benchmarks.Model;
using AgentProof.Domain;

namespace AgentProof.Benchmarks;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            PrintUsage();
            return 0;
        }

        try
        {
            if (IsCompareCommand(args))
            {
                return await HandleCompareCommandAsync(args);
            }

            return await HandleRunCommandAsync(args);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[AgentProof.Benchmarks Error]: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static bool IsCompareCommand(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("compare", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return args.Contains("--baseline") && args.Contains("--candidate");
    }

    private static async Task<int> HandleRunCommandAsync(string[] args)
    {
        var repositoryPath = GetOption(args, "--repository", "-r");
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            // If first arg doesn't start with '-', check if it's the repo path or subcommand 'run'
            var nonOptionArgs = args.Where(a => !a.StartsWith('-')).ToList();
            if (nonOptionArgs.Count > 0 && nonOptionArgs[0].Equals("run", StringComparison.OrdinalIgnoreCase))
            {
                nonOptionArgs.RemoveAt(0);
            }
            if (nonOptionArgs.Count > 0)
            {
                repositoryPath = nonOptionArgs[0];
            }
        }

        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            Console.Error.WriteLine("Error: Repository path is required. Use --repository <path>.");
            PrintUsage();
            return 1;
        }

        var scenarioName = GetOption(args, "--scenario", "-s") ?? "FullVerification";
        var taskContextPath = GetOption(args, "--task-context", "-t");
        var iterationsStr = GetOption(args, "--iterations", "-i");
        var outputPath = GetOption(args, "--output", "-o");
        var markdownPath = GetOption(args, "--markdown", "-m");
        var serverAssembly = GetOption(args, "--server-assembly");

        int iterations = 3;
        if (!string.IsNullOrWhiteSpace(iterationsStr) && int.TryParse(iterationsStr, out var parsedIter))
        {
            iterations = parsedIter;
        }

        TaskContext? taskContext = null;
        if (!string.IsNullOrWhiteSpace(taskContextPath))
        {
            if (!File.Exists(taskContextPath))
            {
                Console.Error.WriteLine($"Error: Task context file not found: {taskContextPath}");
                return 1;
            }
            await using var stream = File.OpenRead(taskContextPath);
            taskContext = await JsonSerializer.DeserializeAsync<TaskContext>(stream, JsonOptions);
        }

        Console.WriteLine($"Starting benchmark run on '{repositoryPath}' (Scenario: {scenarioName}, Iterations: {iterations})...");

        var result = await BenchmarkRunner.RunAsync(
            repositoryPath,
            scenarioName,
            taskContext,
            iterations,
            serverAssembly);

        Console.WriteLine();
        Console.WriteLine(BenchmarkFormatter.FormatRunToConsole(result));

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(result, JsonOptions);
            await File.WriteAllTextAsync(outputPath, json);
            Console.WriteLine($"Saved JSON snapshot to: {outputPath}");
        }

        if (!string.IsNullOrWhiteSpace(markdownPath))
        {
            var dir = Path.GetDirectoryName(markdownPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var md = BenchmarkFormatter.FormatRunToMarkdown(result);
            await File.WriteAllTextAsync(markdownPath, md);
            Console.WriteLine($"Saved Markdown summary to: {markdownPath}");
        }

        return result.Operations.All(o => o.Success) ? 0 : 1;
    }

    private static async Task<int> HandleCompareCommandAsync(string[] args)
    {
        var baselinePath = GetOption(args, "--baseline", "-b");
        var candidatePath = GetOption(args, "--candidate", "-c");
        var outputPath = GetOption(args, "--output", "-o");
        var markdownPath = GetOption(args, "--markdown", "-m");

        if (string.IsNullOrWhiteSpace(baselinePath) || !File.Exists(baselinePath))
        {
            Console.Error.WriteLine($"Error: Valid baseline file required (--baseline <file>). Given: '{baselinePath}'");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
        {
            Console.Error.WriteLine($"Error: Valid candidate file required (--candidate <file>). Given: '{candidatePath}'");
            return 1;
        }

        await using var baseStream = File.OpenRead(baselinePath);
        var baseline = await JsonSerializer.DeserializeAsync<BenchmarkRun>(baseStream, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize baseline benchmark file.");

        await using var candStream = File.OpenRead(candidatePath);
        var candidate = await JsonSerializer.DeserializeAsync<BenchmarkRun>(candStream, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize candidate benchmark file.");

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Console.WriteLine();
        Console.WriteLine(BenchmarkFormatter.FormatComparisonToConsole(comparison));

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(comparison, JsonOptions);
            await File.WriteAllTextAsync(outputPath, json);
            Console.WriteLine($"Saved Comparison JSON to: {outputPath}");
        }

        if (!string.IsNullOrWhiteSpace(markdownPath))
        {
            var dir = Path.GetDirectoryName(markdownPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var md = BenchmarkFormatter.FormatComparisonToMarkdown(comparison);
            await File.WriteAllTextAsync(markdownPath, md);
            Console.WriteLine($"Saved Comparison Markdown to: {markdownPath}");
        }

        return comparison.Verdict switch
        {
            ComparisonVerdict.Pass => 0,
            ComparisonVerdict.Warning => 0,
            ComparisonVerdict.Fail => 2,
            _ => 1
        };
    }

    private static string? GetOption(string[] args, string longName, string? shortName = null)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(longName, StringComparison.OrdinalIgnoreCase) ||
                (shortName != null && args[i].Equals(shortName, StringComparison.OrdinalIgnoreCase)))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    return args[i + 1];
                }
            }
        }
        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            AgentProof Context Efficiency Benchmark Harness

            Usage:
              dotnet run --project benchmarks/AgentProof.Benchmarks -- [options]
              dotnet run --project benchmarks/AgentProof.Benchmarks -- compare [options]

            Run Benchmark Options:
              -r, --repository <path>      Path to local repository to benchmark (Required)
              -s, --scenario <name>        Scenario: AnalyzeOnly, Planning, FullVerification (Default: FullVerification)
              -t, --task-context <path>    Optional path to structured task context JSON file
              -i, --iterations <n>         Number of iterations to run (Default: 3)
              -o, --output <path>          Output file path to save JSON snapshot (e.g., artifacts/benchmarks/run.json)
              -m, --markdown <path>        Output file path to save Markdown summary
              --server-assembly <path>     Optional path to AgentProof.Mcp.dll

            Compare Options:
              -b, --baseline <path>        Path to baseline benchmark JSON snapshot (Required)
              -c, --candidate <path>       Path to candidate benchmark JSON snapshot (Required)
              -o, --output <path>          Output file path to save comparison JSON
              -m, --markdown <path>        Output file path to save comparison Markdown

            Scenarios:
              AnalyzeOnly        Runs analyze_repository
              Planning           Runs analyze_repository, create_verification_plan
              FullVerification   Runs analyze_repository, recommend_skills, create_verification_plan, verify

            Examples:
              dotnet run --project benchmarks/AgentProof.Benchmarks -- \
                --repository "D:\project\EducationCMS" \
                --scenario FullVerification \
                --output artifacts/benchmarks/educationcms-baseline.json

              dotnet run --project benchmarks/AgentProof.Benchmarks -- compare \
                --baseline artifacts/benchmarks/asrp-baseline.json \
                --candidate artifacts/benchmarks/asrp-compact.json
            """);
    }
}
