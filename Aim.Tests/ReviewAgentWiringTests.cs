using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Aim;

namespace Aim.Tests;

public class ReviewAgentWiringTests
{
    [Fact]
    public void CreateRegistersTheTwoToolsAndTheRoundCap()
    {
        var agent = ReviewAgent.Create(new ReviewAgentRequest
        {
            ApiKey = "sk-test",
            Endpoint = "https://example.invalid/v1",
            Model = "gpt-4o",
            WorkingDirectory = Environment.CurrentDirectory,
            MaxRounds = 6,
            MaxToolChars = 1000,
            NameOnly = false,
            Progress = TextWriter.Null
        });

        var invoking = agent.ChatClient.GetService<FunctionInvokingChatClient>();
        Assert.NotNull(invoking);
        Assert.Equal(6, invoking.MaximumIterationsPerRequest);
        Assert.False(invoking.AllowConcurrentInvocation);

        var names = RegisteredToolNames(agent);
        Assert.Contains("file_read", names);
        Assert.Contains("code_search", names);
        Assert.Equal(2, names.Count);
        Assert.Contains("file_read", agent.Instructions);
        Assert.Equal("aim-review", agent.Name);
    }

    private static List<string> RegisteredToolNames(ChatClientAgent agent)
    {
        var property = typeof(ChatClientAgent).GetProperty(
            "ChatOptions",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var options = Assert.IsAssignableFrom<ChatOptions>(property?.GetValue(agent));
        Assert.NotNull(options.Tools);
        return options.Tools.Select(tool => tool.Name).ToList();
    }
}
