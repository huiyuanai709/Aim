namespace Aim;

internal static class ReviewDiffLoader
{
    public static (bool success, string diff, string error) Load(
        ReviewTarget target,
        bool nameOnly,
        string? workingDirectory = null)
    {
        if (target.Kind is ReviewInputKind.Branch or ReviewInputKind.FirstCommit)
            return (false, "", "Error: Branch review was not resolved.");

        if (!EnsureRepository(target.Kind, workingDirectory, out var repoError))
            return (false, "", repoError);

        switch (target.Kind)
        {
            case ReviewInputKind.Staged:
                return GitExecutor.DiffCached(nameOnly, workingDirectory);
            case ReviewInputKind.Range:
            {
                if (!TryResolve(target.From!, workingDirectory, out var fromError))
                    return (false, "", fromError);
                if (!TryResolve(target.To!, workingDirectory, out var toError))
                    return (false, "", toError);
                return GitExecutor.DiffRange(target.From!, target.To!, nameOnly, workingDirectory);
            }
            case ReviewInputKind.Commit:
            {
                if (!TryResolve(target.Commit!, workingDirectory, out var commitError))
                    return (false, "", commitError);
                return GitExecutor.DiffCommit(target.Commit!, nameOnly, workingDirectory);
            }
            default:
                return (false, "", "Unknown review input.");
        }
    }

    public static string EmptyMessage(ReviewTarget target) => target.Kind switch
    {
        ReviewInputKind.Range =>
            $"No changes introduced by '{target.To}' since it diverged from '{target.From}'.",
        ReviewInputKind.Commit => $"Commit '{target.Commit}' has no diff to review.",
        ReviewInputKind.Branch => "No changes on HEAD since it diverged from the default branch.",
        ReviewInputKind.FirstCommit => "The first unique commit has no diff to review.",
        _ => "No staged changes to review. Run 'git add' first."
    };

    public static string Describe(ReviewTarget target) => target.Kind switch
    {
        ReviewInputKind.Range => $"{target.From}...{target.To}",
        ReviewInputKind.Commit => $"commit {target.Commit}",
        ReviewInputKind.Branch => "default branch...HEAD",
        ReviewInputKind.FirstCommit => "first unique commit on HEAD",
        _ => "staged changes"
    };

    public static string DiffLabel(ReviewTarget target, bool nameOnly)
    {
        var label = target.Kind switch
        {
            ReviewInputKind.Range => $"range {target.From}...{target.To} (triple-dot)",
            ReviewInputKind.Commit => $"commit {target.Commit}",
            ReviewInputKind.Branch => "default branch...HEAD (triple-dot)",
            ReviewInputKind.FirstCommit => "first unique commit on HEAD",
            _ => "staged changes (git diff --cached)"
        };

        if (nameOnly)
            label += ", file names only";

        return label;
    }

    private static bool EnsureRepository(ReviewInputKind kind, string? workingDirectory, out string error)
    {
        if (kind == ReviewInputKind.Staged)
        {
            var (ok, output, err) = GitExecutor.RunGit(["rev-parse", "--is-inside-work-tree"], workingDirectory);
            if (ok && output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                error = "";
                return true;
            }

            error = string.IsNullOrWhiteSpace(err) ? "Not a git repository." : err.Trim();
            return false;
        }

        var (dirOk, _, dirErr) = GitExecutor.RunGit(["rev-parse", "--git-dir"], workingDirectory);
        if (dirOk)
        {
            error = "";
            return true;
        }

        error = string.IsNullOrWhiteSpace(dirErr) ? "Not a git repository." : dirErr.Trim();
        return false;
    }

    private static bool TryResolve(string rev, string? workingDirectory, out string error)
    {
        var (ok, _, err) = GitExecutor.RunGit(
            ["rev-parse", "--verify", "--quiet", rev + "^{commit}"],
            workingDirectory);

        if (ok)
        {
            error = "";
            return true;
        }

        error = string.IsNullOrWhiteSpace(err) ? $"Unknown git ref '{rev}'." : err.Trim();
        return false;
    }
}
