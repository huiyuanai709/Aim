using System.Globalization;
using Microsoft.Extensions.AI;

namespace Aim;

/// <summary>
/// Turns DeepSeek DSML tool markup in the assistant text into function calls
/// before the agent framework decides the turn is finished.
/// </summary>
internal sealed class LeakedToolCallChatClient : DelegatingChatClient
{
    private int _nextCallId;

    public LeakedToolCallChatClient(IChatClient innerClient)
        : base(innerClient)
    {
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        Recover(response);
        return response;
    }

    private void Recover(ChatResponse response)
    {
        if (response.Messages is not { Count: > 0 } messages)
            return;

        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Assistant || HasFunctionCall(message))
                continue;

            if (!LeakedToolCallParser.TryParse(message.Text, out var calls, out var remainder))
                continue;

            var contents = new List<AIContent>(message.Contents.Count + calls.Count);
            var keptRemainder = false;
            foreach (var content in message.Contents)
            {
                if (content is TextContent)
                {
                    if (!keptRemainder && remainder.Length > 0)
                    {
                        contents.Add(new TextContent(remainder));
                        keptRemainder = true;
                    }

                    continue;
                }

                contents.Add(content);
            }

            if (!keptRemainder && remainder.Length > 0)
                contents.Add(new TextContent(remainder));

            foreach (var call in calls)
            {
                var id = "call_dsml_" + Interlocked.Increment(ref _nextCallId).ToString(CultureInfo.InvariantCulture);
                IDictionary<string, object?>? arguments = call.Arguments.Count == 0
                    ? null
                    : new Dictionary<string, object?>(call.Arguments);
                contents.Add(new FunctionCallContent(id, call.Name, arguments));
            }

            message.Contents = contents;
            // OpenAI's chat adapter sends RawRepresentation as-is and ignores Contents.
            // Drop a leaked assistant message so the synthesized tool calls are what
            // the next round sends.
            if (message.RawRepresentation is OpenAI.Chat.ChatMessage)
                message.RawRepresentation = null;
        }
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
}
