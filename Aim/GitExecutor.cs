using System.Diagnostics;

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