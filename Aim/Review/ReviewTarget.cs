namespace Aim;

internal enum ReviewInputKind
{
    Staged,
    Range,
    Commit,
    Branch,
    FirstCommit
}

internal sealed record ReviewTarget(
    ReviewInputKind Kind,
    string? From,
    string? To,
    string? Commit,
    string Format)
{
    public const string LatestCommit = "HEAD";

    public static bool TryParse(
        string? from,
        string? to,
        string? commit,
        bool branch,
        bool firstCommit,
        string? format,
        out ReviewTarget target,
        out string error,
        bool commitSpecified = false)
    {
        target = new ReviewTarget(ReviewInputKind.Staged, null, null, null, "text");
        from = BlankToNull(from);
        to = BlankToNull(to);
        commit = BlankToNull(commit);
        if (commitSpecified && commit is null)
            commit = LatestCommit;
        format = string.IsNullOrWhiteSpace(format) ? "text" : format.Trim().ToLowerInvariant();

        if (format is not ("text" or "json"))
        {
            error = "Error: --format must be 'text' or 'json'.";
            return false;
        }

        if (branch && firstCommit)
        {
            error = "Error: --branch and --first-commit cannot be used together.";
            return false;
        }

        if (branch || firstCommit)
        {
            if (from is not null || to is not null || commit is not null)
            {
                var flag = branch ? "--branch" : "--first-commit";
                error = $"Error: {flag} cannot be combined with --from/--to or --commit.";
                return false;
            }

            target = new ReviewTarget(
                branch ? ReviewInputKind.Branch : ReviewInputKind.FirstCommit,
                null,
                null,
                null,
                format);
            error = "";
            return true;
        }

        if ((from is null) != (to is null))
        {
            error = "Error: --from and --to must be used together.";
            return false;
        }

        if (commit is not null && from is not null)
        {
            error = "Error: --commit cannot be combined with --from/--to.";
            return false;
        }

        if (from is not null && !IsRev(from))
        {
            error = $"Error: --from is not a valid git ref '{from}'.";
            return false;
        }

        if (to is not null && !IsRev(to))
        {
            error = $"Error: --to is not a valid git ref '{to}'.";
            return false;
        }

        if (commit is not null && !IsRev(commit))
        {
            error = $"Error: --commit is not a valid git ref '{commit}'.";
            return false;
        }

        if (commit is not null)
            target = new ReviewTarget(ReviewInputKind.Commit, null, null, commit, format);
        else if (from is not null && to is not null)
            target = new ReviewTarget(ReviewInputKind.Range, from, to, null, format);
        else
            target = new ReviewTarget(ReviewInputKind.Staged, null, null, null, format);

        error = "";
        return true;
    }

    private static string? BlankToNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    private static bool IsRev(string rev)
    {
        if (rev.StartsWith('-'))
            return false;

        foreach (var c in rev)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
                return false;
        }

        return true;
    }
}
