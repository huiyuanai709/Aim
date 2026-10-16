using System.Globalization;

namespace Aim;

internal static class ReviewParallelLimits
{
    public const int DefaultConcurrency = 8;
    public const int MaxConcurrency = 32;
    public const int DefaultTimeoutMinutes = 5;
    public const int MaxTimeoutMinutes = 60;
    public const int MaxFilesPerGroup = 10;
    public const int DefaultGroupChars = 80_000;
    public const int NetworkTimeoutBufferSeconds = 30;

    public static string FormatMinutes(int minutes)
    {
        var value = minutes.ToString(CultureInfo.InvariantCulture);
        return minutes == 1 ? "1 minute" : $"{value} minutes";
    }

    public static bool TryResolveConcurrency(int? fromCli, int configured, out int concurrency, out string? note, out string error)
    {
        note = null;
        if (fromCli is int cli)
        {
            if (cli < 1 || cli > MaxConcurrency)
            {
                concurrency = DefaultConcurrency;
                error = $"Error: --concurrency must be an integer from 1 to {MaxConcurrency}.";
                return false;
            }

            concurrency = cli;
            error = "";
            return true;
        }

        if (configured is >= 1 and <= MaxConcurrency)
            concurrency = configured;
        else
        {
            concurrency = DefaultConcurrency;
            note = $"Warning: reviewconcurrency must be from 1 to {MaxConcurrency}. Using {DefaultConcurrency}.";
        }

        error = "";
        return true;
    }

    public static bool TryResolveTimeout(int? fromCli, int configured, out int minutes, out string? note, out string error)
    {
        note = null;
        if (fromCli is int cli)
        {
            if (cli < 1 || cli > MaxTimeoutMinutes)
            {
                minutes = DefaultTimeoutMinutes;
                error = $"Error: --timeout must be an integer number of minutes from 1 to {MaxTimeoutMinutes}.";
                return false;
            }

            minutes = cli;
            error = "";
            return true;
        }

        if (configured is >= 1 and <= MaxTimeoutMinutes)
            minutes = configured;
        else
        {
            minutes = DefaultTimeoutMinutes;
            note = $"Warning: reviewtimeoutminutes must be from 1 to {MaxTimeoutMinutes}. Using {DefaultTimeoutMinutes}.";
        }

        error = "";
        return true;
    }

    public static bool TryResolveBudget(int? fromCli, out int budget, out string error)
    {
        if (fromCli is null)
        {
            budget = 0;
            error = "";
            return true;
        }

        if (fromCli < 1)
        {
            budget = 0;
            error = "Error: --max-tokens-budget must be a positive integer. Omit it for no limit.";
            return false;
        }

        budget = fromCli.Value;
        error = "";
        return true;
    }
}
