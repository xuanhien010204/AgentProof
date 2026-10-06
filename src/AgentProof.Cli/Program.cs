using System.Text.Json;
using System.Text.Json.Serialization;
using AgentProof.Application;
using AgentProof.Domain;
using AgentProof.Infrastructure;

namespace AgentProof.Cli;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        return await RunAsync(args, Console.Out, Console.Error);
    }

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, AgentProofService? service = null)
    {
        if (args.Length == 1 && args[0] is "-h" or "--help")
        {
            PrintUsage(output);
            return 0;
        }

        if (args.Length < 2)
        {
            PrintUsage(error);
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        var repositoryPath = args[1];

        service ??= new AgentProofService(
            new LocalRepositoryAnalyzer(),
            new DeterministicSkillRecommender(),
            new DeterministicVerificationPlanner(new RepositoryConfigurationReader()),
            new SafeVerificationRunner());

        try
        {
            object result = command switch
            {
                "analyze" => await service.AnalyzeRepositoryAsync(repositoryPath),
                "recommend" => await service.RecommendSkillsAsync(repositoryPath, await ReadTaskContextAsync(args)),
                "plan" => await service.CreateVerificationPlanAsync(repositoryPath, await ReadTaskContextAsync(args)),
                "verify" => await service.VerifyRepositoryAsync(repositoryPath, await ReadTaskContextAsync(args)),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'.")
            };

            await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));

            if (result is VerificationResult vr)
            {
                return vr.Status switch
                {
                    VerificationStatus.Verified => 0,
                    VerificationStatus.NotVerified => 2,
                    VerificationStatus.PartiallyVerified => 3,
                    _ => 1
                };
            }

            return 0;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    private static async Task<TaskContext> ReadTaskContextAsync(string[] commandArgs)
    {
        var optionIndex = Array.IndexOf(commandArgs, "--task-context");
        if (optionIndex < 0 || optionIndex + 1 >= commandArgs.Length)
            throw new ArgumentException("This command requires --task-context <task.json>.");
        await using var stream = File.OpenRead(commandArgs[optionIndex + 1]);
        return await JsonSerializer.DeserializeAsync<TaskContext>(stream, JsonOptions)
            ?? throw new JsonException("Task context is empty or invalid.");
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("""
            AgentProof CLI
              agentproof analyze <repository>
              agentproof recommend <repository> --task-context <task.json>
              agentproof plan <repository> --task-context <task.json>
              agentproof verify <repository> --task-context <task.json>
            """);
    }
}
