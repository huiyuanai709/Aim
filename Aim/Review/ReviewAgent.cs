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
    public TimeSpan NetworkTimeout { get; init; } =
        ReviewParallelLimits.CancellationTimeout(ReviewParallelLimits.DefaultTimeoutMinutes);
}

internal static class ReviewAgent
{
    public static ChatClientAgent Create(ReviewAgentRequest request) =>
        Create(request, chatClient: null);

    internal static ChatClientAgent Create(ReviewAgentRequest request, IChatClient? chatClient)
    {
        var tools = new ReviewTools(request.WorkingDirectory, request.Revision, request.MaxToolChars, request.Progress);
        var networkTimeout = request.NetworkTimeout > TimeSpan.Zero
            ? request.NetworkTimeout
            : Timeout.InfiniteTimeSpan;
        IList<AITool> functions = [.. tools.CreateFunctions()];
        AgentSkillsProvider? skills = null;
        try
        {
            if (request.SkillDirectories.Count > 0)
                skills = CreateSkills(request);

            var options = new ChatClientAgentOptions
            {
                Name = "aim-review",
                Description = "Reviews a git diff and can read files, search the repository, report a short status, and load review skills before answering.",
                ChatOptions = new ChatOptions
                {
                    Instructions = ReviewPrompt.AgentInstructions(request.NameOnly, skills: skills is not null),
                    Tools = functions
                },
                AllowConcurrentInvocation = false,
                AIContextProviders = skills is null ? null : [skills]
            };

            IChatClient client = chatClient ?? CreateModelClient(request, networkTimeout);
            client = new LeakedToolCallChatClient(client);
            var agent = client.AsAIAgent(options);

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

    private static IChatClient CreateModelClient(ReviewAgentRequest request, TimeSpan networkTimeout)
    {
        var openAI = new OpenAIClient(
            new ApiKeyCredential(request.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(request.Endpoint),
                NetworkTimeout = networkTimeout
            });
        return openAI.GetChatClient(request.Model).AsIChatClient();
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

    public static Task<string> CompleteAsync(ReviewAgentRequest request, string userPrompt, CancellationToken cancellationToken) =>
        CompleteAsync(request, userPrompt, cancellationToken, chatClient: null);

    internal static async Task<string> CompleteAsync(
        ReviewAgentRequest request,
        string userPrompt,
        CancellationToken cancellationToken,
        IChatClient? chatClient)
    {
        var agent = Create(request, chatClient);
        try
        {
            var session = await agent.CreateSessionAsync(cancellationToken);
            var response = await agent.RunAsync(userPrompt, session, cancellationToken: cancellationToken);
            var text = response.Text ?? "";
            for (var recovery = 0; recovery < ReviewAgentLimits.MaxJsonRecoveries && !IsReviewJson(text); recovery++)
            {
                // The function loop already stopped. Give the model the same thread so it can call tools and then return JSON.
                WritePlanningStatus(request.Progress, text);
                response = await agent.RunAsync(ReviewPrompt.JsonContinuation, session, cancellationToken: cancellationToken);
                text = response.Text ?? "";
            }

            return text;
        }
        finally
        {
            DisposeProviders(agent);
        }
    }

    private static bool IsReviewJson(string text) =>
        ReviewCommentParser.Parse(text, int.MaxValue).Success;

    private static void WritePlanningStatus(TextWriter? progress, string text)
    {
        if (progress is null || !ReviewTools.TrySanitizeProgress(text, out var excerpt))
            return;

        progress.WriteLine("▶ " + excerpt);
        progress.Flush();
    }
}
