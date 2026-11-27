using System.Text.Json;
using Microsoft.Extensions.AI;
using Aim;

namespace Aim.Tests;

public class EmptyPayloadChatClientTests
{
    [Fact]
    public async Task EmptyChoicesExceptionBecomesAnEmptyPayload()
    {
        var inner = new OnceChatClient(() => throw new ArgumentOutOfRangeException("index"));
        var client = new EmptyPayloadChatClient(inner);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "review")]);

        Assert.Empty(response.Messages);
        Assert.True(EmptyPayloadChatClient.IsMarked(response.AdditionalProperties));
    }

    [Fact]
    public async Task EmptyJsonBodyBecomesAnEmptyPayload()
    {
        var inner = new OnceChatClient(() => throw new JsonException(
            "The input does not contain any JSON tokens. Expected the input to start with a valid JSON token, when isFinalBlock is true."));
        var client = new EmptyPayloadChatClient(inner);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "review")]);

        Assert.True(EmptyPayloadChatClient.IsMarked(response.AdditionalProperties));
    }

    [Fact]
    public async Task AResponseWithNoMessagesIsMarked()
    {
        var inner = new OnceChatClient(() => new ChatResponse());
        var client = new EmptyPayloadChatClient(inner);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "review")]);

        Assert.True(EmptyPayloadChatClient.IsMarked(response.AdditionalProperties));
    }

    [Fact]
    public async Task AnEmptyAssistantMessageIsNotAnEmptyPayload()
    {
        var message = new ChatMessage(ChatRole.Assistant, "");
        var inner = new OnceChatClient(() => new ChatResponse(message) { FinishReason = ChatFinishReason.Stop });
        var client = new EmptyPayloadChatClient(inner);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "review")]);

        Assert.False(EmptyPayloadChatClient.IsMarked(response.AdditionalProperties));
        Assert.Equal("", Assert.Single(response.Messages).Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
    }

    [Fact]
    public async Task OtherModelFailuresPropagate()
    {
        var inner = new OnceChatClient(() => throw new ArgumentOutOfRangeException("count"));
        var client = new EmptyPayloadChatClient(inner);

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "review")]));

        Assert.Equal("count", ex.ParamName);
    }

    private sealed class OnceChatClient : IChatClient
    {
        private readonly Func<ChatResponse> _respond;

        public OnceChatClient(Func<ChatResponse> respond)
        {
            _respond = respond;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_respond());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
