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
        var agent = chat.AsAIAgent(
            instructions: ReviewPrompt.AgentInstructions(request.NameOnly),
            name: "aim-review",
            description: "Reviews a git diff and can read files or search the repository before answering.",
            tools: functions);

        if (agent.ChatClient.GetService<FunctionInvokingChatClient>() is { } invoking)
        {
            invoking.MaximumIterationsPerRequest = request.MaxRounds;
            invoking.AllowConcurrentInvocation = false;
            invoking.IncludeDetailedErrors = false;
        }

        return agent;
    }

    public static async Task<string> CompleteAsync(ReviewAgentRequest request, string userPrompt, CancellationToken cancellationToken)
    {
        var agent = Create(request);
        var response = await agent.RunAsync(userPrompt, cancellationToken: cancellationToken);
        return response.Text ?? "";
    }
}
