using AgentProof.Application;
using AgentProof.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<IRepositoryAnalyzer, LocalRepositoryAnalyzer>();
builder.Services.AddSingleton<ISkillRecommender, DeterministicSkillRecommender>();
builder.Services.AddSingleton<IRepositoryConfigurationReader, RepositoryConfigurationReader>();
builder.Services.AddSingleton<IVerificationPlanner, DeterministicVerificationPlanner>();
builder.Services.AddSingleton<IVerificationRunner, SafeVerificationRunner>();
builder.Services.AddSingleton<AgentProofService>();
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
