using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace Aim;

internal sealed class SkillScriptRunner
{
    public const int MaxArguments = 16;
    public const int MaxArgumentChars = 400;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    public static readonly string[] AllowedExtensions = [".py", ".sh", ".js"];

    private readonly TextWriter _progress;
    private readonly int _maxChars;

    public SkillScriptRunner(TextWriter? progress, int maxChars)
    {
        _progress = progress ?? TextWriter.Null;
        _maxChars = maxChars < 1 ? ReviewAgentLimits.DefaultToolChars : maxChars;
    }

    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    public async Task<object?> RunAsync(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken)
    {
        _ = serviceProvider;
        if (!TryPrepare(skill, script, arguments, out var interpreter, out var scriptPath, out var args, out var error))
        {
            _progress.WriteLine("▶ run_skill_script (rejected)");
            return error;
        }

        _progress.WriteLine($"▶ run_skill_script {skill.Frontmatter.Name} {script.Name}");
        return await ExecuteAsync(interpreter, scriptPath, skill.Path, args, cancellationToken).ConfigureAwait(false);
    }

    private bool TryPrepare(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        out string interpreter,
        out string scriptPath,
        out List<string> args,
        out string error)
    {
        interpreter = "";
        scriptPath = "";
        args = [];
        if (string.IsNullOrWhiteSpace(skill.Path) || string.IsNullOrWhiteSpace(script.FullPath) || string.IsNullOrWhiteSpace(script.Name))
        {
            error = "Script is missing a path.";
            return false;
        }

        if (!RepoPathGuard.TryResolve(skill.Path, script.Name, out _, out var resolved, out var pathError))
        {
            error = pathError;
            return false;
        }

        scriptPath = Path.GetFullPath(script.FullPath);
        if (!string.Equals(resolved, scriptPath, PathComparison()))
        {
            error = "Script path does not match the file inside the skill directory.";
            return false;
        }

        var extension = Path.GetExtension(scriptPath);
        interpreter = Interpreter(extension);
        if (interpreter.Length == 0 || !AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            error = $"Scripts of type '{extension}' are not run. Allowed types: {string.Join(", ", AllowedExtensions)}.";
            return false;
        }

        if (!File.Exists(scriptPath))
        {
            error = $"Script file not found: {script.Name}";
            return false;
        }

        return TryReadArguments(arguments, out args, out error);
    }

    private static bool TryReadArguments(JsonElement? arguments, out List<string> args, out string error)
    {
        args = [];
        if (arguments is null || arguments.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            error = "";
            return true;
        }

        if (arguments.Value.ValueKind != JsonValueKind.Array)
        {
            error = "Script arguments must be a JSON array of strings.";
            return false;
        }

        foreach (var item in arguments.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = "Script arguments must be a JSON array of strings.";
                return false;
            }

            var value = item.GetString() ?? "";
            if (value.Contains('\0') || value.Any(char.IsControl))
            {
                error = "Script arguments cannot contain control characters.";
                return false;
            }

            if (value.Length > MaxArgumentChars)
            {
                error = $"Each script argument must be at most {MaxArgumentChars} characters.";
                return false;
            }

            args.Add(value);
            if (args.Count > MaxArguments)
            {
                error = $"A script accepts at most {MaxArguments} arguments.";
                return false;
            }
        }

        error = "";
        return true;
    }

    private async Task<string> ExecuteAsync(
        string interpreter,
        string scriptPath,
        string workingDirectory,
        List<string> args,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = interpreter,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.StartInfo.ArgumentList.Add(scriptPath);
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        try
        {
            if (!process.Start())
                return "Failed to start the script.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"Failed to start '{interpreter}': {ex.Message}";
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            var timedOut = Clip(await CombineAsync(stdoutTask, stderrTask).ConfigureAwait(false));
            var prefix = $"Script timed out after {Timeout.TotalSeconds:0} seconds.";
            return string.IsNullOrWhiteSpace(timedOut) ? prefix : prefix + "\n" + timedOut;
        }

        var combined = await CombineAsync(stdoutTask, stderrTask).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(combined))
            return process.ExitCode == 0
                ? "Script finished with no output."
                : $"Script exited {process.ExitCode} with no output.";

        if (process.ExitCode != 0)
            combined = $"Script exited {process.ExitCode}.\n{combined}";

        return Clip(combined);
    }

    private string Clip(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalized.Length <= _maxChars)
            return normalized;

        return normalized[.._maxChars].TrimEnd() + "\n[truncated to maxtoolresultchars]";
    }

    private static async Task<string> CombineAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        string stdout;
        string stderr;
        try
        {
            stdout = await stdoutTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            stdout = "";
        }

        try
        {
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            stderr = "";
        }

        if (string.IsNullOrEmpty(stderr))
            return stdout;
        if (string.IsNullOrEmpty(stdout))
            return stderr;
        return stdout.TrimEnd() + "\n" + stderr;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string Interpreter(string extension) => extension.ToLowerInvariant() switch
    {
        ".py" => OperatingSystem.IsWindows() ? "python" : "python3",
        ".sh" => OperatingSystem.IsWindows() ? "sh" : "/bin/sh",
        ".js" => "node",
        _ => ""
    };

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
