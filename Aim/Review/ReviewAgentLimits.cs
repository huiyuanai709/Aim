namespace Aim;

internal static class ReviewAgentLimits
{
    public const int DefaultRounds = 10;
    public const int MaxRounds = 40;
    public const int DefaultToolChars = 12_000;
    public const int MaxToolChars = 200_000;

    // A tool-free assistant message ends the function loop. Review models sometimes
    // spend that message on a plan. Each recovery is another turn on the same session.
    public const int MaxJsonRecoveries = 3;

    // A turn with no assistant text and no tool call, or a model call that returned
    // no message at all. Separate from the prose budget so a blank reply does not
    // use up the planning recoveries. Each recovery is another turn on the same session.
    public const int MaxEmptyRecoveries = 3;

    public static bool TryResolveRounds(int? fromCli, int configured, bool agentEnabled, out int rounds, out string? note, out string error)
    {
        note = null;
        if (fromCli is int cli)
        {
            if (cli < 1 || cli > MaxRounds)
            {
                rounds = DefaultRounds;
                error = $"Error: --max-rounds must be an integer from 1 to {MaxRounds}.";
                return false;
            }

            rounds = cli;
            if (!agentEnabled)
                note = "Note: --max-rounds applies only when the review agent is on. It is ignored with --no-agent.";

            error = "";
            return true;
        }

        if (configured is >= 1 and <= MaxRounds)
            rounds = configured;
        else
        {
            rounds = DefaultRounds;
            note = $"Warning: maxreviewrounds must be from 1 to {MaxRounds}. Using {DefaultRounds}.";
        }

        error = "";
        return true;
    }

    public static int ResolveToolChars(int configured)
    {
        if (configured is >= 1 and <= MaxToolChars)
            return configured;

        return DefaultToolChars;
    }

    public static string? ToolRevision(ReviewTarget target) => target.Kind switch
    {
        ReviewInputKind.Commit => target.Commit,
        ReviewInputKind.Range => target.To,
        _ => null
    };
}
