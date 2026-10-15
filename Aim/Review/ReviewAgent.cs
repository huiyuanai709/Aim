using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Aim;

internal sealed class ReviewAgentRequest
{
    public required string ApiKey { get; init; }
    public required string Endpoint { get; init; }
    public required string Model { get; init; }
    public required string WorkingDirectory { get; init; }
    public required int MaxRounds { get; init; }
    public required int MaxToolChars { get; init; }
    public required bool NameOnly { get; init; }
    public string? Revision { get; init; }
    public TextWriter? Progress { get; init; }
    public IReadOnlyList<string> SkillDirectories { get; init; } = [];
}

internal static class ReviewAgent
{
    public static ChatClientAgent Create(ReviewAgentRequest request)
    {
        var tools = new ReviewTools(request.WorkingDirectory, request.Revision, request.MaxToolChars, request.Progress);
        var openAI = new OpenAIClient(
            new ApiKeyCredential(request.ApiKey),
            new OpenAIClientOptions { Endpoint = new Uri(request.Endpoint) });
        var chat = openAI.GetChatClient(request.Model);
        IList<AITool> functions = [.. tools.CreateFunctions()];
        AgentSkillsProvider? skills = null;
        try
        {
            if (request.SkillDirectories.Count > 0)
                skills = CreateSkills(request);

            var agent = chat.AsAIAgent(new ChatClientAgentOptions
            {
                Name = "aim-review",
                Description = "Reviews a git diff and can read files, search the repository, and load review skills before answering.",
                ChatOptions = new ChatOptions
                {
                    Instructions = ReviewPrompt.AgentInstructions(request.NameOnly, skills: skills is not null),
                    Tools = functions
                },
                AllowConcurrentInvocation = false,
                AIContextProviders = skills is null ? null : [skills]
            });

            if (agent.ChatClient.GetService<FunctionInvokingChatClient>() is { } invoking)
            {
                invoking.MaximumIterationsPerRequest = request.MaxRounds;
                invoking.AllowConcurrentInvocation = false;
                invoking.IncludeDetailedErrors = false;
            }

            return agent;
        }
        catch
        {
            skills?.Dispose();
            throw;
        }
    }

    internal static void DisposeProviders(ChatClientAgent agent)
    {
        if (agent.AIContextProviders is null)
            return;

        foreach (var provider in agent.AIContextProviders)
        {
            if (provider is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private const string SkillsPrompt =
        """
        Review skills are available. When one matches the diff, call load_skill, then read_skill_resource for a listed file. You may call run_skill_script for a listed .py, .sh, .js, .cs, or .csx script. It runs on this machine, in the skill directory, with the arguments you pass.
        <available_skills>
        {skills}
        </available_skills>
        """;

    private static AgentSkillsProvider CreateSkills(ReviewAgentRequest request)
    {
        var runner = new SkillScriptRunner(request.Progress, request.MaxToolChars);
        return new AgentSkillsProvider(
            request.SkillDirectories,
            runner.RunAsync,
            new AgentFileSkillsSourceOptions
            {
                AllowedScriptExtensions = SkillScriptRunner.AllowedExtensions
            },
            new AgentSkillsProviderOptions
            {
                DisableLoadSkillApproval = true,
                DisableReadSkillResourceApproval = true,
                DisableRunSkillScriptApproval = true,
                SkillsInstructionPrompt = SkillsPrompt
            });
    }

    public static async Task<string> CompleteAsync(ReviewAgentRequest request, string userPrompt, CancellationToken cancellationToken)
    {
        var agent = Create(request);
        try
        {
            var response = await agent.RunAsync(userPrompt, cancellationToken: cancellationToken);
            return response.Text ?? "";
        }
        finally
        {
            DisposeProviders(agent);
        }
    }
}
