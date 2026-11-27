using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

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
                    // Ask the provider to allow more than one tool call in a response. A provider may ignore this.
                    AllowMultipleToolCalls = true,
                    Instructions = ReviewPrompt.AgentInstructions(request.NameOnly, skills: skills is not null),
                    Tools = functions
                },
                // Copied onto the FunctionInvokingChatClient created below. That client runs every
                // function call from one model response together. This does not make RunAsync concurrent;
                // each review group already has its own agent.
                AllowConcurrentInvocation = true,
                AIContextProviders = skills is null ? null : [skills]
            };

            IChatClient client = chatClient ?? CreateModelClient(request, networkTimeout);
            // Empty-payload recovery sits under DSML recovery so a real assistant
            // message is still scanned for leaked tool markup.
            client = new LeakedToolCallChatClient(new EmptyPayloadChatClient(client));
            var agent = client.AsAIAgent(options);

            if (agent.ChatClient.GetService<FunctionInvokingChatClient>() is { } invoking)
            {
                invoking.MaximumIterationsPerRequest = request.MaxRounds;
                // This is the switch the function loop reads. Set it here as well as on the agent
                // options: Aim adjusts this client after AsAIAgent builds it.
                invoking.AllowConcurrentInvocation = true;
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
        Review skills are available. When one matches the diff, call load_skill, then read_skill_resource for a listed file. You may call run_skill_script for a listed .py, .sh, .js, .cs, or .csx script. It runs on this machine, in the skill directory, with the arguments you pass. Independent load_skill, read_skill_resource, and run_skill_script calls can share a turn with file_read and code_search when none of them needs another's result.
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
            var emptyRecoveries = 0;
            var jsonRecoveries = 0;
            while (true)
            {
                var text = response.Text ?? "";
                if (IsReviewJson(text))
                    return text;

                // A blank assistant message and a response with no message are different
                // failures. Both can be continued on this session. Planning prose is not.
                if (TryDescribeEmpty(response, out var empty))
                {
                    if (emptyRecoveries >= ReviewAgentLimits.MaxEmptyRecoveries)
                        throw new ReviewModelEmptyException(empty.Error);

                    emptyRecoveries++;
                    WriteEmptyStatus(request.Progress, empty.Status);
                    response = await agent.RunAsync(
                        ReviewPrompt.EmptyContinuation,
                        session,
                        cancellationToken: cancellationToken);
                    continue;
                }

                if (jsonRecoveries >= ReviewAgentLimits.MaxJsonRecoveries)
                    return text;

                // The function loop already stopped. Give the model the same thread so it can call tools and then return JSON.
                jsonRecoveries++;
                WritePlanningStatus(request.Progress, text);
                response = await agent.RunAsync(ReviewPrompt.JsonContinuation, session, cancellationToken: cancellationToken);
            }
        }
        finally
        {
            DisposeProviders(agent);
        }
    }

    private static bool IsReviewJson(string text) =>
        ReviewCommentParser.Parse(text, int.MaxValue).Success;

    private readonly record struct EmptyTurn(string Error, string Status);

    private static bool TryDescribeEmpty(AgentResponse response, out EmptyTurn empty)
    {
        empty = default;
        var finishReason = response.FinishReason?.ToString();
        var payload = IsEmptyPayload(response);
        if (!payload)
        {
            var last = LastAssistant(response);
            if (last is null)
                payload = true;
            else if (HasFunctionCall(last) || !string.IsNullOrWhiteSpace(last.Text))
                return false;
        }

        var error = DescribeEmpty(payload, finishReason, continuing: false);
        var status = DescribeEmpty(payload, finishReason, continuing: true);
        empty = new EmptyTurn(error, status);
        return true;
    }

    private static bool IsEmptyPayload(AgentResponse response)
    {
        if (EmptyPayloadChatClient.IsMarked(response.AdditionalProperties))
            return true;

        return response.RawRepresentation is ChatResponse raw &&
               EmptyPayloadChatClient.IsMarked(raw.AdditionalProperties);
    }

    private static ChatMessage? LastAssistant(AgentResponse response)
    {
        ChatMessage? last = null;
        foreach (var message in response.Messages)
        {
            if (message.Role == ChatRole.Assistant)
                last = message;
        }

        return last;
    }

    private static bool HasFunctionCall(ChatMessage message)
    {
        foreach (var content in message.Contents)
        {
            if (content is FunctionCallContent)
                return true;
        }

        return false;
    }

    private static string DescribeEmpty(bool payload, string? finishReason, bool continuing)
    {
        var text = "The model returned an empty response";
        string? note = null;
        if (payload && !string.IsNullOrEmpty(finishReason))
            note = "empty payload, finish_reason: " + finishReason;
        else if (payload)
            note = "empty payload";
        else if (!string.IsNullOrEmpty(finishReason))
            note = "finish_reason: " + finishReason;

        if (note is not null)
            text += " (" + note + ")";

        text += ".";
        if (continuing)
            text += " Continuing the review.";

        return text;
    }

    private static void WriteEmptyStatus(TextWriter? progress, string status)
    {
        if (progress is null)
            return;

        ProgressLog.WriteLine(progress, "▶ " + status);
    }

    private static void WritePlanningStatus(TextWriter? progress, string text)
    {
        if (progress is null || !ReviewTools.TrySanitizeProgress(text, out var excerpt))
            return;

        ProgressLog.WriteLine(progress, "▶ " + excerpt);
    }
}

/// <summary>
/// The review agent kept receiving an empty model turn after the recovery cap.
/// The message is the user-facing error, including a finish reason only when the response had one.
/// </summary>
internal sealed class ReviewModelEmptyException : Exception
{
    public ReviewModelEmptyException(string message)
        : base(message)
    {
    }
}
