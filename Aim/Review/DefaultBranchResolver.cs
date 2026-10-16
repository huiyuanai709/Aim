namespace Aim;

internal static class DefaultBranchResolver
{
    internal const string NotARepositoryError = "Error: Not a git repository.";
    internal const string NoHeadError = "Error: Could not resolve HEAD.";
    internal const string NoDefaultBranchError =
        "Error: Could not find a default branch. Tried origin/HEAD, main, master, origin/main, and origin/master.";

    private static readonly string[] FallbackCandidates = ["main", "master", "origin/main", "origin/master"];

    public static bool TryPrepare(
        ReviewTarget target,
        string? workingDirectory,
        out ReviewTarget prepared,
        out string? note,
        out string error)
    {
        prepared = target;
        note = null;
        error = "";

        switch (target.Kind)
        {
            case ReviewInputKind.Branch:
            {
                if (!TryResolveBranchRange(workingDirectory, out var from, out var to, out error))
                    return false;

                prepared = target with { Kind = ReviewInputKind.Range, From = from, To = to };
                return true;
            }
            case ReviewInputKind.FirstCommit:
            {
                if (!TryResolveFirstCommit(workingDirectory, out var from, out var commit, out error))
                    return false;

                prepared = target with { Kind = ReviewInputKind.Commit, Commit = commit };
                note = $"Note: Default branch is {from}. First unique commit is {commit}.";
                return true;
            }
            default:
                return true;
        }
    }

    private static bool TryResolveBranchRange(
        string? workingDirectory,
        out string from,
        out string to,
        out string error)
    {
        from = "";
        to = "";

        if (!EnsureRepository(workingDirectory, out error))
            return false;

        if (!CommitExists("HEAD", workingDirectory))
        {
            error = NoHeadError;
            return false;
        }

        if (!TryResolveDefault(workingDirectory, out from, out error))
            return false;

        to = "HEAD";
        error = "";
        return true;
    }

    private static bool TryResolveFirstCommit(
        string? workingDirectory,
        out string from,
        out string commit,
        out string error)
    {
        commit = "";
        if (!TryResolveBranchRange(workingDirectory, out from, out _, out error))
            return false;

        var (mbOk, mbOut, _) = GitExecutor.RunGit(["merge-base", from, "HEAD"], workingDirectory);
        var mergeBase = FirstLine(mbOut);
        if (!mbOk || !IsSafeRef(mergeBase))
        {
            error = $"Error: No merge base between '{from}' and HEAD.";
            return false;
        }

        // --max-count applies before --reverse, so it would return the newest commit.
        var (listOk, listOut, listErr) = GitExecutor.RunGit(
            ["rev-list", "--reverse", $"{mergeBase}..HEAD"],
            workingDirectory);
        if (!listOk)
        {
            error = string.IsNullOrWhiteSpace(listErr)
                ? "Error: Could not list commits between the default branch and HEAD."
                : "Error: " + listErr.Trim();
            return false;
        }

        commit = FirstLine(listOut);
        if (!IsSafeRef(commit))
        {
            error = $"Error: HEAD has no commit that is not already on '{from}'.";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryResolveDefault(string? workingDirectory, out string reference, out string error)
    {
        reference = "";
        if (TryReadOriginHead(workingDirectory, out reference))
        {
            error = "";
            return true;
        }

        foreach (var candidate in FallbackCandidates)
        {
            if (CommitExists(candidate, workingDirectory))
            {
                reference = candidate;
                error = "";
                return true;
            }
        }

        error = NoDefaultBranchError;
        return false;
    }

    private static bool TryReadOriginHead(string? workingDirectory, out string reference)
    {
        reference = "";
        var (ok, output, _) = GitExecutor.RunGit(
            ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"],
            workingDirectory);
        if (!ok)
            return false;

        var name = FirstLine(output);
        if (!IsSafeRef(name) || !CommitExists(name, workingDirectory))
            return false;

        reference = name;
        return true;
    }

    private static bool EnsureRepository(string? workingDirectory, out string error)
    {
        var (ok, _, err) = GitExecutor.RunGit(["rev-parse", "--git-dir"], workingDirectory);
        if (ok)
        {
            error = "";
            return true;
        }

        var detail = err.Trim();
        if (detail.StartsWith("Failed to start git", StringComparison.Ordinal))
        {
            error = "Error: " + detail;
            return false;
        }

        error = NotARepositoryError;
        return false;
    }

    private static bool CommitExists(string rev, string? workingDirectory)
    {
        if (!IsSafeRef(rev))
            return false;

        var (ok, _, _) = GitExecutor.RunGit(
            ["rev-parse", "--verify", "--quiet", rev + "^{commit}"],
            workingDirectory);
        return ok;
    }

    private static bool IsSafeRef(string rev)
    {
        if (rev.Length == 0 || rev[0] == '-')
            return false;

        foreach (var c in rev)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
                return false;

            if (c is '^' or '~' or ':' or '\\' or '?' or '*' or '[' or ']' or '!' or '{' or '}')
                return false;
        }

        return !rev.Contains("..", StringComparison.Ordinal);
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var line = text.AsSpan().TrimStart();
        var end = line.IndexOfAny('\r', '\n');
        if (end >= 0)
            line = line[..end];

        return line.Trim().ToString();
    }
}
