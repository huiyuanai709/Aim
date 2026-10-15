using System.Text.Json;
using Microsoft.Agents.AI;
using Aim;

namespace Aim.Tests;

public class SkillScriptRunnerTests
{
    [Fact]
    public async Task ShellScriptReceivesArgumentsWithoutAShell()
    {
        using var repo = new ScriptRepo();
        var marker = Path.Combine(Path.GetTempPath(), "aim-script-pwn-" + Guid.NewGuid().ToString("n"));
        var arg = $"hello; touch {marker}";
        repo.Write(".aim/skills/explicit-errors/scripts/check.sh", """
            #!/bin/sh
            printf '%s\n' "$1"
            printf '%s\n' "$PWD"
            """);
        repo.Write(".aim/skills/explicit-errors/notes.txt", "beside\n");

        var script = await LoadScript(repo.Path, "scripts/check.sh", maxChars: 4000, TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new[] { arg }));
        var result = Assert.IsType<string>(await script.Script.RunAsync(script.Skill, doc.RootElement, null, CancellationToken.None));

        Assert.False(File.Exists(marker));
        Assert.Contains(arg, result);
        Assert.Contains(Path.GetFullPath(script.Skill.Path), result);
    }

    [Fact]
    public async Task SymlinkThatLeavesTheSkillIsNotDiscovered()
    {
        using var repo = new ScriptRepo();
        var outside = Path.Combine(Path.GetTempPath(), "aim-script-out-" + Guid.NewGuid().ToString("n") + ".sh");
        File.WriteAllText(outside, "#!/bin/sh\nprintf leaked\\n");
        var scripts = Path.Combine(repo.Path, ".aim", "skills", "explicit-errors", "scripts");
        Directory.CreateDirectory(scripts);
        File.CreateSymbolicLink(Path.Combine(scripts, "leak.sh"), outside);
        try
        {
            var loaded = await LoadSkill(repo.Path, maxChars: 1000, TimeSpan.FromSeconds(5));
            Assert.Null(await loaded.GetScriptAsync("scripts/leak.sh"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task TimeoutAndOutputCapAreEnforced()
    {
        using var repo = new ScriptRepo();
        repo.Write(".aim/skills/explicit-errors/scripts/slow.sh", """
            #!/bin/sh
            printf 'abcdefghijklmnopqrstuvwxyz\n'
            sleep 30
            """);

        var script = await LoadScript(repo.Path, "scripts/slow.sh", maxChars: 8, TimeSpan.FromMilliseconds(400));
        var result = Assert.IsType<string>(await script.Script.RunAsync(script.Skill, null, null, CancellationToken.None));

        Assert.Contains("timed out", result);
        Assert.Contains("[truncated to maxtoolresultchars]", result);
        Assert.DoesNotContain("uvwxyz", result);
    }

    [Fact]
    public async Task NonStringArgumentsAreRejectedBeforeTheProcessStarts()
    {
        using var repo = new ScriptRepo();
        var marker = Path.Combine(Path.GetTempPath(), "aim-script-args-" + Guid.NewGuid().ToString("n"));
        repo.Write(".aim/skills/explicit-errors/scripts/check.sh", $"#!/bin/sh\ntouch {marker}\n");

        var script = await LoadScript(repo.Path, "scripts/check.sh", maxChars: 1000, TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse("""{"cmd":"echo hi"}""");
        var result = Assert.IsType<string>(await script.Script.RunAsync(script.Skill, doc.RootElement, null, CancellationToken.None));

        Assert.Contains("JSON array of strings", result);
        Assert.False(File.Exists(marker));
    }

    private static async Task<(AgentFileSkill Skill, AgentSkillScript Script)> LoadScript(
        string repo,
        string name,
        int maxChars,
        TimeSpan timeout)
    {
        var skill = await LoadSkill(repo, maxChars, timeout);
        var script = await skill.GetScriptAsync(name);
        Assert.NotNull(script);
        return (skill, script);
    }

    private static async Task<AgentFileSkill> LoadSkill(string repo, int maxChars, TimeSpan timeout)
    {
        var runner = new SkillScriptRunner(TextWriter.Null, maxChars) { Timeout = timeout };
        using var source = new AgentFileSkillsSource(
            Path.Combine(repo, ".aim", "skills"),
            runner.RunAsync,
            new AgentFileSkillsSourceOptions { AllowedScriptExtensions = SkillScriptRunner.AllowedExtensions });
        var agent = ReviewAgent.Create(new ReviewAgentRequest
        {
            ApiKey = "sk-test",
            Endpoint = "https://example.invalid/v1",
            Model = "gpt-4o",
            WorkingDirectory = repo,
            MaxRounds = 2,
            MaxToolChars = maxChars,
            NameOnly = false,
            Progress = TextWriter.Null
        });
        try
        {
            var session = await agent.CreateSessionAsync();
            var skills = await source.GetSkillsAsync(new AgentSkillsSourceContext(agent, session));
            return Assert.IsType<AgentFileSkill>(Assert.Single(skills));
        }
        finally
        {
            ReviewAgent.DisposeProviders(agent);
        }
    }

    private sealed class ScriptRepo : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "aim-script-" + Guid.NewGuid().ToString("n"));

        public ScriptRepo()
        {
            Directory.CreateDirectory(Path);
            var (ok, stdout, stderr) = GitExecutor.RunGit(["init", "-b", "main"], Path);
            Assert.True(ok, stderr + stdout);
            Write(".aim/skills/explicit-errors/SKILL.md", """
                ---
                name: explicit-errors
                description: Flag swallowed exceptions in the diff.
                ---
                Prefer explicit errors.
                """);
        }

        public void Write(string name, string contents)
        {
            var full = System.IO.Path.Combine(Path, name.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, contents);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
                return;

            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(Path, true);
        }
    }
}
