using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Aim;

/// <summary>
/// Turns a model call that produced no message into a <see cref="ChatResponse"/> the
/// review loop can retry. An empty <c>choices</c> array makes the OpenAI SDK throw
/// when it reads the first choice. An empty HTTP body fails JSON parsing. An assistant
/// message that simply has no text is a normal response and is left alone.
/// </summary>
internal sealed class EmptyPayloadChatClient : DelegatingChatClient
{
    public const string Key = "aim.empty_payload";

    public EmptyPayloadChatClient(IChatClient innerClient)
        : base(innerClient)
    {
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsEmptyPayload(ex))
        {
            return Mark(new ChatResponse());
        }

        if (response.Messages.Count == 0)
            Mark(response);

        return response;
    }

    internal static bool IsMarked(AdditionalPropertiesDictionary? properties) =>
        properties is not null &&
        properties.TryGetValue(Key, out var value) &&
        value is true;

    private static ChatResponse Mark(ChatResponse response)
    {
        (response.AdditionalProperties ??= [])[Key] = true;
        return response;
    }

    private static bool IsEmptyPayload(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            // ChatCompletion.Content, FinishReason, and ToolCalls all index Choices[0].
            if (current is ArgumentOutOfRangeException { ParamName: "index" })
                return true;

            if (current is JsonException json &&
                json.Message.Contains("does not contain any JSON tokens", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
