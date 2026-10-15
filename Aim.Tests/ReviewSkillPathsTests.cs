using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Aim;

namespace Aim.Tests;

public class ReviewSkillPathsTests
{
    [Fact]
    public void DefaultsIncludeExistingAimAndRootSkillDirectories()
    {
        using var repo = new SkillRepo();
        repo.Write(".aim/skills/explicit-errors/SKILL.md", Skill("explicit-errors", "From aim."));
        repo.Write("skills/api-contracts/SKILL.md", Skill("api-contracts", "From root."));

        var found = ReviewSkillPaths.Resolve(repo.Path, iniSkillsPaths: null);

        Assert.Equal([".aim/skills", "skills"], found.Labels);
        Assert.Empty(found.Notes);
        Assert.Equal(repo.Path, found.Root);
    }

    [Fact]
    public void MissingDefaultsAreQuietAndASubdirectoryStillUsesTheRepoRoot()
    {
        using var repo = new SkillRepo();
        repo.Write(".aim/skills/explicit-errors/SKILL.md", Skill("explicit-errors", "From aim."));
        var nested = Path.Combine(repo.Path, "src");
        Directory.CreateDirectory(nested);

        var found = ReviewSkillPaths.Resolve(nested, iniSkillsPaths: null);

        Assert.Equal([".aim/skills"], found.Labels);
        Assert.Empty(found.Notes);
    }

    [Fact]
    public void ProjectJsonAndIniPathsComeBeforeDefaultsAndMissingPathsAreNoted()
    {
        using var repo = new SkillRepo();
        var shared = Path.Combine(Path.GetTempPath(), "aim-shared-skills-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(shared);
        try
        {
            repo.Write(".aim/skills/local/SKILL.md", Skill("local-skill", "Local."));
            repo.Write(".aim/config.json", $$"""
                {
                  "skills": ["{{shared.Replace("\\", "\\\\")}}", "missing-skills"],
                  "skillspaths": "also-missing"
                }
                """);

            var found = ReviewSkillPaths.Resolve(repo.Path, iniSkillsPaths: "ini-missing;.aim/skills");

            Assert.Equal(shared, found.Directories[0]);
            Assert.Equal(".aim/skills", found.Labels[^1]);
            Assert.Contains(found.Notes, note => note.Contains("missing-skills") && note.Contains("not found"));
            Assert.Contains(found.Notes, note => note.Contains("also-missing") && note.Contains("not found"));
            Assert.Contains(found.Notes, note => note.Contains("ini-missing") && note.Contains("not found"));
            Assert.Equal(2, found.Directories.Count);
        }
        finally
        {
            Directory.Delete(shared, true);
        }
    }

    [Fact]
    public void InvalidProjectJsonIsAWarningAndDoesNotHideDefaults()
    {
        using var repo = new SkillRepo();
        repo.Write("skills/api-contracts/SKILL.md", Skill("api-contracts", "From root."));
        repo.Write(".aim/config.json", "{");

        var found = ReviewSkillPaths.Resolve(repo.Path, iniSkillsPaths: null);

        Assert.Equal(["skills"], found.Labels);
        Assert.Contains(found.Notes, note => note.Contains(".aim/config.json"));
    }

    [Fact]
    public async Task FileSourceFindsSkillsFromTheResolvedDirectoriesAndKeepsTheFirstName()
    {
        using var repo = new SkillRepo();
        repo.Write(".aim/skills/explicit-errors/SKILL.md", Skill("explicit-errors", "First copy."));
        repo.Write(".aim/skills/explicit-errors/references/notes.txt", "detail");
        Directory.CreateDirectory(Path.Combine(repo.Path, ".aim", "skills", "explicit-errors", "scripts"));
        repo.Write(".aim/skills/explicit-errors/scripts/run.py", "print(1)\n");
        repo.Write("skills/explicit-errors/SKILL.md", Skill("explicit-errors", "Second copy."));
        repo.Write("skills/api-contracts/SKILL.md", Skill("api-contracts", "Contracts."));

        var found = ReviewSkillPaths.Resolve(repo.Path, iniSkillsPaths: null);
        var agent = ReviewAgent.Create(new ReviewAgentRequest
        {
            ApiKey = "sk-test",
            Endpoint = "https://example.invalid/v1",
            Model = "gpt-4o",
            WorkingDirectory = repo.Path,
            MaxRounds = 2,
            MaxToolChars = 1000,
            NameOnly = false,
            Progress = TextWriter.Null,
            SkillDirectories = found.Directories
        });
        try
        {
            var provider = Assert.IsType<AgentSkillsProvider>(Assert.Single(agent.AIContextProviders!));
            var session = await agent.CreateSessionAsync();
#pragma warning disable MAAI001
            var context = await provider.InvokingAsync(new AIContextProvider.InvokingContext(agent, session, new AIContext()));
#pragma warning restore MAAI001

            Assert.Contains("explicit-errors", context.Instructions);
            Assert.Contains("First copy.", context.Instructions);
            Assert.DoesNotContain("Second copy.", context.Instructions);
            Assert.Contains("api-contracts", context.Instructions);
            var toolNames = context.Tools!.Select(tool => tool.Name).ToList();
            Assert.Contains(AgentSkillsProvider.LoadSkillToolName, toolNames);
            Assert.Contains(AgentSkillsProvider.ReadSkillResourceToolName, toolNames);
            Assert.Contains(AgentSkillsProvider.RunSkillScriptToolName, toolNames);
            Assert.DoesNotContain(
                context.Tools!,
                tool => tool.Name == AgentSkillsProvider.LoadSkillToolName && tool is ApprovalRequiredAIFunction);
            Assert.Contains(
                context.Tools!,
                tool => tool.Name == AgentSkillsProvider.RunSkillScriptToolName && tool is ApprovalRequiredAIFunction);

            using var source = new AgentFileSkillsSource(
                found.Directories,
                scriptRunner: null,
                new AgentFileSkillsSourceOptions { ScriptFilter = static _ => false });
            var skills = await source.GetSkillsAsync(new AgentSkillsSourceContext(agent, session));
            var loaded = skills.Single(skill => skill.Frontmatter.Description.Contains("First copy.", StringComparison.Ordinal));
            Assert.Null(await loaded.GetScriptAsync("scripts/run.py"));
            Assert.NotNull(await loaded.GetResourceAsync("references/notes.txt"));
        }
        finally
        {
            ReviewAgent.DisposeProviders(agent);
        }
    }

    private static string Skill(string name, string description) =>
        $"""
        ---
        name: {name}
        description: {description}
        ---
        Review with this skill.
        """;

    private sealed class SkillRepo : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "aim-skills-" + Guid.NewGuid().ToString("n"));

        public SkillRepo()
        {
            Directory.CreateDirectory(Path);
            Git("init", "-b", "main");
        }

        public void Write(string name, string contents)
        {
            var full = System.IO.Path.Combine(Path, name.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, contents);
        }

        private void Git(params string[] args)
        {
            var (ok, stdout, stderr) = GitExecutor.RunGit(args, Path);
            Assert.True(ok, stderr + stdout);
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
