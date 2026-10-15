using System.Diagnostics;
using System.Text;

namespace Aim;

internal static class GitExecutor
{
    public static (bool success, string output, string error) RunGitCommand(string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode == 0, output, error);
    }

    public static (bool success, string output, string error) RunGit(
        IReadOnlyList<string> arguments,
        string? workingDirectory = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        if (!string.IsNullOrEmpty(workingDirectory))
            process.StartInfo.WorkingDirectory = workingDirectory;

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start())
                return (false, "", "Failed to start git.");
        }
        catch (Exception ex)
        {
            return (false, "", $"Failed to start git: {ex.Message}");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return (process.ExitCode == 0, outputTask.Result, errorTask.Result);
    }

    public static (bool success, string output, string error) DiffCached(bool nameOnly, string? workingDirectory = null)
    {
        var args = new List<string> { "diff", "--cached", "--no-color", "--no-ext-diff" };
        if (nameOnly)
            args.Add("--name-only");
        else
            args.Add("--unified=3");

        return RunGit(args, workingDirectory);
    }

    public static (bool success, string output, string error) DiffRange(
        string from,
        string to,
        bool nameOnly,
        string? workingDirectory = null)
    {
        var args = new List<string> { "diff", "--no-color", "--no-ext-diff" };
        if (nameOnly)
            args.Add("--name-only");
        else
            args.Add("--unified=3");

        // Triple-dot: changes on `to` since it diverged from `from`.
        args.Add($"{from}...{to}");
        return RunGit(args, workingDirectory);
    }

    public static (bool success, string output, string error) DiffCommit(
        string commit,
        bool nameOnly,
        string? workingDirectory = null)
    {
        var args = new List<string>
        {
            "show",
            "--no-color",
            "--no-ext-diff",
            "--format=",
            "--first-parent"
        };
        if (nameOnly)
            args.Add("--name-only");
        else
            args.Add("--unified=3");

        args.Add(commit);
        return RunGit(args, workingDirectory);
    }

    public static bool GitAdd(string files = ".")
    {
        Console.WriteLine($"→ git add {files}");
        var (success, output, error) = RunGitCommand($"add {files}");

        if (!string.IsNullOrEmpty(output)) Console.WriteLine(output);
        if (!string.IsNullOrEmpty(error)) Console.Error.WriteLine(error);

        if (success)
            Console.WriteLine("✓ Files staged successfully");

        return success;
    }

    public static bool GitCommit(string message, bool amend = false, bool addAll = true)
    {
        var args = "commit";
        if (amend) args += " --amend";
        if (addAll) args += " -a";
        args += $" -m \"{message}\"";

        Console.WriteLine($"→ git {args}");
        var (success, output, error) = RunGitCommand(args);

        if (!string.IsNullOrEmpty(output)) Console.WriteLine(output);
        if (!string.IsNullOrEmpty(error)) Console.Error.WriteLine(error);

        if (success)
            Console.WriteLine("✓ Commit successful");

        return success;
    }
}