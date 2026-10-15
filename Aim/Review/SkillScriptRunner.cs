using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace Aim;

internal sealed class SkillScriptRunner
{
    public const int MaxArguments = 16;
    public const int MaxArgumentChars = 400;
    public const int MaxShebangChars = 256;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    public static readonly string[] AllowedExtensions = [".py", ".sh", ".js", ".cs", ".csx"];

    private readonly record struct Launch(string FileName, IReadOnlyList<string> Prefix, bool EndOfOptions);

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
        if (!TryPrepare(skill, script, arguments, out var launch, out var scriptPath, out var args, out var error))
        {
            _progress.WriteLine("▶ run_skill_script (rejected)");
            return error;
        }

        _progress.WriteLine($"▶ run_skill_script {skill.Frontmatter.Name} {script.Name}");
        return await ExecuteAsync(launch, scriptPath, skill.Path, args, cancellationToken).ConfigureAwait(false);
    }

    private bool TryPrepare(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        out Launch launch,
        out string scriptPath,
        out List<string> args,
        out string error)
    {
        launch = default;
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
        if (!AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            error = $"Scripts of type '{extension}' are not run. Allowed types: {string.Join(", ", AllowedExtensions)}.";
            return false;
        }

        if (!File.Exists(scriptPath))
        {
            error = $"Script file not found: {script.Name}";
            return false;
        }

        if (!TryReadShebang(scriptPath, out var shebangProgram, out var shebangArgument, out error))
            return false;

        if (shebangProgram is not null)
        {
            if (!TryValidateInterpreter(shebangProgram, out error))
                return false;
            if (shebangArgument is not null && !IsSafeArgument(shebangArgument, out error))
                return false;

            launch = new Launch(shebangProgram, shebangArgument is null ? [] : [shebangArgument], false);
        }
        else if (!TryExtensionLaunch(extension, out launch, out error))
        {
            return false;
        }

        return TryReadArguments(arguments, out args, out error);
    }

    private static bool TryReadShebang(string path, out string? program, out string? argument, out string error)
    {
        program = null;
        argument = null;
        error = "";
        string text;
        try
        {
            var buffer = new byte[MaxShebangChars];
            using var stream = File.OpenRead(path);
            var read = stream.Read(buffer, 0, buffer.Length);
            text = Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex)
        {
            error = $"Could not read the script: {ex.Message}";
            return false;
        }

        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];
        if (!text.StartsWith("#!", StringComparison.Ordinal))
            return true;

        var newline = text.IndexOf('\n');
        if (newline < 0 && text.Length >= MaxShebangChars)
        {
            error = "Shebang line is too long.";
            return false;
        }

        var line = (newline < 0 ? text : text[..newline]).TrimEnd('\r');
        var rest = line[2..].TrimStart(' ', '\t');
        if (rest.Length == 0)
        {
            error = "Shebang is missing an interpreter.";
            return false;
        }

        var split = rest.IndexOfAny([' ', '\t']);
        program = split < 0 ? rest : rest[..split];
        if (split >= 0)
        {
            var tail = rest[(split + 1)..].Trim();
            if (tail.Length > 0)
                argument = tail;
        }

        return true;
    }

    private static bool TryValidateInterpreter(string program, out string error)
    {
        if (program.Contains('\0') || program.Any(char.IsControl) || !Path.IsPathRooted(program))
        {
            error = "Shebang interpreter must be an absolute path.";
            return false;
        }

        var segments = program.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment => segment == ".."))
        {
            error = "Shebang interpreter path must not contain '..'.";
            return false;
        }

        if (!File.Exists(program))
        {
            error = $"Shebang interpreter was not found: {program}";
            return false;
        }

        error = "";
        return true;
    }

    private static bool IsSafeArgument(string value, out string error)
    {
        if (value.Contains('\0') || value.Any(char.IsControl))
        {
            error = "Shebang argument cannot contain control characters.";
            return false;
        }

        if (value.Length > MaxArgumentChars)
        {
            error = $"Shebang argument must be at most {MaxArgumentChars} characters.";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryExtensionLaunch(string extension, out Launch launch, out string error)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".py":
                launch = new Launch(OperatingSystem.IsWindows() ? "python" : "python3", [], false);
                break;
            case ".sh":
                launch = new Launch(OperatingSystem.IsWindows() ? "sh" : "/bin/sh", [], false);
                break;
            case ".js":
                launch = new Launch("node", [], false);
                break;
            case ".cs":
            case ".csx":
                launch = new Launch("dotnet", ["run", "--file"], true);
                break;
            default:
                launch = default;
                error = $"Scripts of type '{extension}' are not run. Allowed types: {string.Join(", ", AllowedExtensions)}.";
                return false;
        }

        error = "";
        return true;
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
        Launch launch,
        string scriptPath,
        string workingDirectory,
        List<string> args,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = launch.FileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var part in launch.Prefix)
            process.StartInfo.ArgumentList.Add(part);
        process.StartInfo.ArgumentList.Add(scriptPath);
        if (launch.EndOfOptions)
            process.StartInfo.ArgumentList.Add("--");
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        try
        {
            if (!process.Start())
                return "Failed to start the script.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"Failed to start '{launch.FileName}': {ex.Message}";
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

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
