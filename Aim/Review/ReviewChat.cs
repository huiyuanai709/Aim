using System.ClientModel;
using System.Text;
using OpenAI;
using OpenAI.Chat;

namespace Aim;

internal static class ReviewChat
{
    public static TimeSpan NetworkTimeoutFor(TimeSpan groupTimeout) =>
        groupTimeout + TimeSpan.FromSeconds(ReviewParallelLimits.NetworkTimeoutBufferSeconds);

    public static OpenAIClient CreateClient(string apiKey, string endpoint, TimeSpan networkTimeout)
    {
        return new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint),
                NetworkTimeout = networkTimeout
            });
    }

    public static async Task<string> CompleteAsync(
        OpenAIClient client,
        string model,
        string prompt,
        CancellationToken cancellationToken)
    {
        var chatClient = client.GetChatClient(model);
        var response = await chatClient.CompleteChatAsync(
            [new UserChatMessage(prompt)],
            null,
            cancellationToken);
        return ReadContent(response.Value);
    }

    private static string ReadContent(ChatCompletion completion)
    {
        var sb = new StringBuilder();
        foreach (var part in completion.Content)
        {
            if (!string.IsNullOrEmpty(part.Text))
                sb.Append(part.Text);
        }

        return sb.ToString();
    }
}
