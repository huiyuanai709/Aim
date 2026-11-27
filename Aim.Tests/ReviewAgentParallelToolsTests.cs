using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Aim;

namespace Aim.Tests;

public class ReviewAgentParallelToolsTests
{
    [Fact]
    public async Task OneTurnRunsFileReadsTogetherBeforeTheNextModelCall()
    {
        using var repo = new TempRepo();
        var left = Path.Combine(repo.Path, "left.fifo");
        var right = Path.Combine(repo.Path, "right.fifo");
        MakeFifo(left);
        MakeFifo(right);
        repo.Write(".gitignore", "*.fifo\n");
        repo.Write("notes.txt", "unique-needle\n");
        repo.Git("add", "notes.txt");
        repo.Git("commit", "-m", "init");

        var leftOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rightOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leftSawPeer = false;
        var rightSawPeer = false;
        var leftWriter = Task.Run(() => WriteFifo(left, "left-payload\n", leftOpen, rightOpen, seen => leftSawPeer = seen));
        var rightWriter = Task.Run(() => WriteFifo(right, "right-payload\n", rightOpen, leftOpen, seen => rightSawPeer = seen));

        var json = """
            [{"path":"notes.txt","start_line":1,"end_line":1,"severity":"low","category":"style","content":"The note is only a marker."}]
            """;
        var calls = new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("call_left", "file_read", new Dictionary<string, object?> { ["path"] = "left.fifo" }),
            new FunctionCallContent("call_right", "file_read", new Dictionary<string, object?> { ["path"] = "right.fifo" }),
            new FunctionCallContent("call_search", "code_search", new Dictionary<string, object?> { ["query"] = "unique-needle" })
        ]);
        var script = new ScriptedChatClient(calls, new ChatMessage(ChatRole.Assistant, json));
        var progress = new StringWriter();

        string raw;
        try
        {
            var review = ReviewAgent.CompleteAsync(Request(progress, repo.Path), "Review the diff.", CancellationToken.None, script);
            var finished = await Task.WhenAny(review, Task.Delay(TimeSpan.FromSeconds(20)));
            if (finished != review)
                throw new TimeoutException("The review did not finish.");
            raw = await review;
        }
        finally
        {
            if (!leftWriter.IsCompleted || !rightWriter.IsCompleted)
            {
                using var unblockLeft = OpenRead(left);
                using var unblockRight = OpenRead(right);
                await Task.WhenAll(leftWriter, rightWriter);
            }
        }

        await Task.WhenAll(leftWriter, rightWriter);

        Assert.True(leftSawPeer, "left file_read did not overlap the right read");
        Assert.True(rightSawPeer, "right file_read did not overlap the left read");
        Assert.Equal(json, raw);
        Assert.Equal(2, script.Requests.Count);
        Assert.Empty(script.Results[0]);
        var results = script.Results[1];
        Assert.Equal(3, results.Length);
        Assert.Contains(results, result => result.Contains("left-payload", StringComparison.Ordinal));
        Assert.Contains(results, result => result.Contains("right-payload", StringComparison.Ordinal));
        Assert.Contains(results, result => result.Contains("unique-needle", StringComparison.Ordinal));
        Assert.Contains(results, result => result.Contains("notes.txt:", StringComparison.Ordinal));
        AssertLinesIntact(progress.ToString());
    }

    [Fact]
    public async Task OneTurnRunsSkillScriptsAndAResourceReadBeforeTheNextModelCall()
    {
        var root = Path.Combine(Path.GetTempPath(), "aim-parallel-skill-" + Guid.NewGuid().ToString("n"));
        var skillDir = Path.Combine(root, "explicit-errors");
        var marks = Path.Combine(root, "marks");
        Directory.CreateDirectory(Path.Combine(skillDir, "scripts"));
        Directory.CreateDirectory(Path.Combine(skillDir, "references"));
        Directory.CreateDirectory(marks);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: explicit-errors
            description: Flag swallowed exceptions in the diff.
            ---
            Prefer explicit errors.
            """);
        File.WriteAllText(Path.Combine(skillDir, "references", "notes.txt"), "skill-note-token\n");
        File.WriteAllText(Path.Combine(skillDir, "scripts", "hold.sh"), """
            #!/bin/sh
            mark=$1
            peer=$2
            : > "$mark"
            i=0
            while [ "$i" -lt 40 ]; do
              if [ -f "$peer" ]; then
                printf 'overlapped\n'
                exit 0
              fi
              i=$((i + 1))
              sleep 0.05
            done
            printf 'serial\n'
            """);

        using var argsA = JsonDocument.Parse(JsonSerializer.Serialize(new[]
        {
            Path.Combine(marks, "a"),
            Path.Combine(marks, "b")
        }));
        using var argsB = JsonDocument.Parse(JsonSerializer.Serialize(new[]
        {
            Path.Combine(marks, "b"),
            Path.Combine(marks, "a")
        }));
        var json = """
            [{"path":"notes.txt","start_line":1,"end_line":1,"severity":"low","category":"style","content":"The note is only a marker."}]
            """;
        var calls = new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("call_a", "run_skill_script", new Dictionary<string, object?>
            {
                ["skillName"] = "explicit-errors",
                ["scriptName"] = "scripts/hold.sh",
                ["arguments"] = argsA.RootElement.Clone()
            }),
            new FunctionCallContent("call_b", "run_skill_script", new Dictionary<string, object?>
            {
                ["skillName"] = "explicit-errors",
                ["scriptName"] = "scripts/hold.sh",
                ["arguments"] = argsB.RootElement.Clone()
            }),
            new FunctionCallContent("call_note", "read_skill_resource", new Dictionary<string, object?>
            {
                ["skillName"] = "explicit-errors",
                ["resourceName"] = "references/notes.txt"
            })
        ]);
        var script = new ScriptedChatClient(calls, new ChatMessage(ChatRole.Assistant, json));
        var progress = new StringWriter();

        try
        {
            var raw = await ReviewAgent.CompleteAsync(
                Request(progress, root, skillDir),
                "Review the diff.",
                CancellationToken.None,
                script);

            Assert.Equal(json, raw);
            Assert.Equal(2, script.Requests.Count);
            Assert.Empty(script.Results[0]);
            var results = script.Results[1];
            Assert.Equal(3, results.Length);
            Assert.Equal(2, results.Count(result => result.Contains("overlapped", StringComparison.Ordinal)));
            Assert.DoesNotContain(results, result => result.Contains("serial", StringComparison.Ordinal));
            Assert.Contains(results, result => result.Contains("skill-note-token", StringComparison.Ordinal));
            AssertLinesIntact(progress.ToString());
            Assert.Equal(2, progress.ToString().Split("▶ run_skill_script explicit-errors scripts/hold.sh", StringSplitOptions.None).Length - 1);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static async Task WriteFifo(
        string path,
        string text,
        TaskCompletionSource mine,
        TaskCompletionSource peer,
        Action<bool> sawPeer)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        mine.TrySetResult();
        var both = Task.WhenAll(mine.Task, peer.Task);
        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(2)));
        sawPeer(finished == both);
        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static void MakeFifo(string path)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "mkfifo",
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add(path);
        Assert.True(process.Start());
        process.WaitForExit();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, error);
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    private static void AssertLinesIntact(string progress)
    {
        var lines = progress.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            Assert.StartsWith("▶ ", line, StringComparison.Ordinal);
            Assert.Equal(1, line.Split("▶ ", StringSplitOptions.None).Length - 1);
        }
    }

    private static ReviewAgentRequest Request(TextWriter progress, string directory, string? skillDirectory = null) => new()
    {
        ApiKey = "sk-test",
        Endpoint = "https://example.invalid/v1",
        Model = "gpt-4o",
        WorkingDirectory = directory,
        MaxRounds = 4,
        MaxToolChars = 12_000,
        NameOnly = false,
        Progress = progress,
        SkillDirectories = skillDirectory is null ? [] : [skillDirectory]
    };

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<ChatMessage> _responses;

        public ScriptedChatClient(params ChatMessage[] responses)
        {
            _responses = new Queue<ChatMessage>(responses);
        }

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public List<string[]> Results { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var copy = messages.ToList();
            Requests.Add(copy);
            Results.Add(copy
                .SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .Select(result => result.Result?.ToString() ?? "")
                .ToArray());
            if (_responses.Count == 0)
                throw new InvalidOperationException("The scripted chat client ran out of responses.");

            return Task.FromResult(new ChatResponse(_responses.Dequeue()));
        }

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

    private sealed class TempRepo : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "aim-parallel-" + Guid.NewGuid().ToString("n"));

        public TempRepo()
        {
            Directory.CreateDirectory(Path);
            Git("init", "-b", "main");
            Git("config", "user.email", "aim-tests@example.com");
            Git("config", "user.name", "Aim Tests");
            Git("config", "commit.gpgsign", "false");
        }

        public void Write(string name, string contents)
        {
            var full = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, contents);
        }

        public void Git(params string[] args)
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
