using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Aim;

internal sealed class ReviewTools
{
    public const int MaxReadLines = 200;
    public const int MaxSearchLines = 80;

    private readonly string _workingDirectory;
    private readonly string? _revision;
    private readonly int _maxChars;
    private readonly TextWriter _progress;
    private string? _repoRoot;

    public ReviewTools(string workingDirectory, string? revision, int maxChars, TextWriter? progress = null)
    {
        _workingDirectory = workingDirectory;
        _revision = string.IsNullOrWhiteSpace(revision) ? null : revision;
        _maxChars = maxChars < 1 ? ReviewAgentLimits.DefaultToolChars : maxChars;
        _progress = progress ?? Console.Error;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "file_read and code_search arguments are string, int, and int?, registered on ReviewToolJsonContext.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "file_read and code_search arguments are string, int, and int?, registered on ReviewToolJsonContext.")]
    public IReadOnlyList<AIFunction> CreateFunctions()
    {
        var serializer = new JsonSerializerOptions(ReviewToolJsonContext.Default.Options)
        {
            TypeInfoResolver = ReviewToolJsonContext.Default
        };
        AIFunction fileRead = AIFunctionFactory.Create(
            FileRead,
            new AIFunctionFactoryOptions
            {
                Name = "file_read",
                Description = "Read a repository file. For a reviewed commit or range this is git show of that revision; otherwise it is the working tree. Optional 1-based start_line and end_line. At most 200 lines are returned.",
                SerializerOptions = serializer
            });
        AIFunction codeSearch = AIFunctionFactory.Create(
            CodeSearch,
            new AIFunctionFactoryOptions
            {
                Name = "code_search",
                Description = "Search tracked repository files for a fixed string with git grep. Not a regular expression. Optional repo-relative path limits the search.",
                SerializerOptions = serializer
            });
        return [fileRead, codeSearch];
    }

    internal string FileRead(
        [Description("Repo-relative path using forward slashes.")] string path,
        [Description("Optional 1-based first line.")] int? start_line = null,
        [Description("Optional 1-based last line, inclusive.")] int? end_line = null)
    {
        if (!TryPreparePath(path, out var relative, out var fullPath, out var root, out var error))
        {
            _progress.WriteLine("▶ file_read (rejected)");
            return error;
        }

        var window = FormatWindow(start_line, end_line);
        _progress.WriteLine(window.Length == 0 ? $"▶ file_read {relative}" : $"▶ file_read {relative}:{window}");

        string text;
        if (_revision is not null)
        {
            var (ok, output, err) = GitExecutor.RunGit(
                ["show", "--no-color", $"{_revision}:{relative}"],
                root);
            if (!ok)
            {
                var detail = string.IsNullOrWhiteSpace(err) ? "git show failed." : err.Trim();
                return $"Could not read {relative} at {_revision}: {detail}";
            }

            text = output;
        }
        else
        {
            if (Directory.Exists(fullPath))
                return $"{relative} is a directory.";
            if (!File.Exists(fullPath))
                return $"File not found: {relative}";

            text = File.ReadAllText(fullPath);
        }

        if (LooksBinary(text))
            return $"Binary file {relative} was not shown.";

        return Window(relative, text, start_line, end_line);
    }

    internal string CodeSearch(
        [Description("Fixed string to find. Not a regular expression.")] string query,
        [Description("Optional repo-relative directory or file.")] string? path = null)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Contains('\0') || query.Length > 200)
        {
            _progress.WriteLine("▶ code_search (rejected)");
            return "Query must be a non-empty fixed string of at most 200 characters.";
        }

        string? relative = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!TryPreparePath(path, out relative, out _, out _, out var pathError))
            {
                _progress.WriteLine("▶ code_search (rejected)");
                return pathError;
            }
        }

        if (!TryRoot(out var root, out var rootError))
        {
            _progress.WriteLine("▶ code_search (rejected)");
            return rootError;
        }

        var preview = query.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (preview.Length > 80)
            preview = preview[..80] + "...";
        _progress.WriteLine(relative is null ? $"▶ code_search {preview}" : $"▶ code_search {preview} {relative}");

        var args = new List<string> { "grep", "-n", "-I", "-F", "--full-name", "-e", query };
        if (_revision is not null)
            args.Add(_revision);
        args.Add("--");
        args.Add(relative ?? ".");

        var (ok, output, err) = GitExecutor.RunGit(args, root);
        if (!ok)
        {
            if (string.IsNullOrWhiteSpace(output))
                return "No matches.";

            var detail = string.IsNullOrWhiteSpace(err) ? "git grep failed." : err.Trim();
            return detail;
        }

        return ClipLines(output, MaxSearchLines, "search");
    }

    private bool TryPreparePath(string? requested, out string relative, out string fullPath, out string root, out string error)
    {
        fullPath = "";
        root = "";
        if (!RepoPathGuard.TryNormalize(requested, out relative, out error))
            return false;
        if (!TryRoot(out root, out error))
            return false;
        return RepoPathGuard.TryResolve(root, relative, out relative, out fullPath, out error);
    }

    private bool TryRoot(out string root, out string error)
    {
        if (_repoRoot is not null)
        {
            root = _repoRoot;
            error = "";
            return true;
        }

        var (ok, output, err) = GitExecutor.RunGit(["rev-parse", "--show-toplevel"], _workingDirectory);
        if (!ok || string.IsNullOrWhiteSpace(output))
        {
            root = "";
            error = string.IsNullOrWhiteSpace(err) ? "Not a git repository." : err.Trim();
            return false;
        }

        _repoRoot = output.Trim();
        root = _repoRoot;
        error = "";
        return true;
    }

    private string Window(string relative, string text, int? start, int? end)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0 && normalized.EndsWith('\n'))
            lines = lines[..^1];

        var total = lines.Length;
        if (total == 0)
            return $"{relative}: empty file.";

        var from = start ?? 1;
        if (from < 1)
            from = 1;
        if (from > total)
            return $"{relative} has {total} lines; line {from} is past the end.";

        var to = end ?? total;
        if (to < from)
            to = from;
        if (to > total)
            to = total;
        var clamped = false;
        if (to - from + 1 > MaxReadLines)
        {
            to = from + MaxReadLines - 1;
            clamped = true;
        }

        var sb = new StringBuilder();
        sb.Append(relative).Append(':').Append(from).Append('-').Append(to).Append(" of ").Append(total);
        if (clamped)
            sb.Append(" (window capped at ").Append(MaxReadLines).Append(" lines)");
        sb.AppendLine();

        for (var lineNumber = from; lineNumber <= to; lineNumber++)
        {
            var rendered = $"{lineNumber}: {lines[lineNumber - 1]}";
            if (sb.Length + rendered.Length + 1 > _maxChars)
            {
                sb.AppendLine("[truncated to maxtoolresultchars]");
                break;
            }

            sb.AppendLine(rendered);
        }

        return sb.ToString().TrimEnd();
    }

    private string ClipLines(string output, int maxLines, string label)
    {
        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        if (normalized.Length == 0)
            return "No matches.";

        var lines = normalized.Split('\n');
        var sb = new StringBuilder();
        var shown = Math.Min(lines.Length, maxLines);
        for (var i = 0; i < shown; i++)
        {
            if (sb.Length + lines[i].Length + 1 > _maxChars)
            {
                sb.AppendLine($"[truncated to maxtoolresultchars; {label} has more]");
                break;
            }

            sb.AppendLine(lines[i]);
        }

        if (shown < lines.Length && !sb.ToString().Contains("[truncated", StringComparison.Ordinal))
            sb.Append('[').Append(lines.Length - shown).Append(" more lines omitted]");

        return sb.ToString().TrimEnd();
    }

    private static string FormatWindow(int? start, int? end)
    {
        if (start is null && end is null)
            return "";
        if (start is not null && end is not null)
            return $"{start}-{end}";
        if (start is not null)
            return $"{start}-";
        return $"-{end}";
    }

    private static bool LooksBinary(string text)
    {
        var sample = text.Length > 8000 ? text[..8000] : text;
        return sample.Contains('\0');
    }
}
