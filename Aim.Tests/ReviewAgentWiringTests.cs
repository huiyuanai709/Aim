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
        Assert.DoesNotContain("load_skill", agent.Instructions);
        Assert.Equal("aim-review", agent.Name);
        Assert.Null(agent.AIContextProviders);
    }

    [Fact]
    public void CreateAddsTheSkillsProviderWithoutReplacingReviewTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "aim-skill-agent-" + Guid.NewGuid().ToString("n"));
        var skillDir = Path.Combine(root, "explicit-errors");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: explicit-errors
            description: Flag swallowed exceptions in the diff.
            ---
            Prefer explicit errors.
            """);
        try
        {
            var agent = ReviewAgent.Create(new ReviewAgentRequest
            {
                ApiKey = "sk-test",
                Endpoint = "https://example.invalid/v1",
                Model = "gpt-4o",
                WorkingDirectory = root,
                MaxRounds = 4,
                MaxToolChars = 1000,
                NameOnly = false,
                Progress = TextWriter.Null,
                SkillDirectories = [skillDir]
            });
            try
            {
                Assert.Contains(agent.AIContextProviders!, provider => provider is AgentSkillsProvider);
                Assert.Contains("load_skill", agent.Instructions);
                var names = RegisteredToolNames(agent);
                Assert.Contains("file_read", names);
                Assert.Contains("code_search", names);
                Assert.Equal(2, names.Count);
            }
            finally
            {
                ReviewAgent.DisposeProviders(agent);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
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
