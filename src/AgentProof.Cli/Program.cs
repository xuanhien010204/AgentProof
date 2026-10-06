using System.Text.Json;
using System.Text.Json.Serialization;
using AgentProof.Application;
using AgentProof.Domain;
using AgentProof.Infrastructure;

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() }
};

if (args.Length < 2 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

var command = args[0].ToLowerInvariant();
var repositoryPath = args[1];
var analyzer = new LocalRepositoryAnalyzer();
var configuration = new RepositoryConfigurationReader();
var planner = new DeterministicVerificationPlanner(configuration);
var service = new AgentProofService(analyzer, new DeterministicSkillRecommender(), planner, new SafeVerificationRunner());

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
    Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    return result is VerificationResult { Status: VerificationStatus.NotVerified } ? 2 : 0;
}
catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or IOException or JsonException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

async Task<TaskContext> ReadTaskContextAsync(string[] commandArgs)
{
    var optionIndex = Array.IndexOf(commandArgs, "--task-context");
    if (optionIndex < 0 || optionIndex + 1 >= commandArgs.Length)
        throw new ArgumentException("This command requires --task-context <task.json>.");
    await using var stream = File.OpenRead(commandArgs[optionIndex + 1]);
    return await JsonSerializer.DeserializeAsync<TaskContext>(stream, jsonOptions)
        ?? throw new JsonException("Task context is empty or invalid.");
}

static void PrintUsage()
{
    Console.WriteLine("""
        AgentProof CLI
          agentproof analyze <repository>
          agentproof recommend <repository> --task-context <task.json>
          agentproof plan <repository> --task-context <task.json>
          agentproof verify <repository> --task-context <task.json>
        """);
}
