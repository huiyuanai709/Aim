using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Aim;

public class ConfigManager
{
    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "aim",
        "config.ini"
    );

    public class Config
    {
        public string? ApiKey { get; set; }
        public string? ApiEndpoint { get; set; }
        public string Model { get; set; } = "gpt-4o";
        public bool AutoCommit { get; set; } = false;
        public bool AutoPush { get; set; } = false;
        public int MaxSubjectLength { get; set; } = 72;
        public bool DiffNameOnly { get; set; } = false;
        public int MaxDiffChars { get; set; } = 80000;
        public string ReviewModel { get; set; } = "";
        public int MaxReviewComments { get; set; } = 20;
        public int MaxReviewRounds { get; set; } = 10;
        public int MaxToolResultChars { get; set; } = 12000;
        public string SkillsPaths { get; set; } = "";
        public int ReviewConcurrency { get; set; } = ReviewParallelLimits.DefaultConcurrency;
        public int ReviewTimeoutMinutes { get; set; } = ReviewParallelLimits.DefaultTimeoutMinutes;
    }

    public static Config LoadConfig() => LoadFromFile(ConfigPath);

    internal static Config LoadFromFile(string path)
    {
        var config = new Config();

        if (!File.Exists(path))
            return config;

        try
        {
            var builder = new ConfigurationBuilder()
                .AddIniFile(path, optional: true, reloadOnChange: false);

            var configuration = builder.Build();

            configuration.GetSection("General").Bind(config);
            configuration.GetSection("Behavior").Bind(config);
            configuration.GetSection("Rules").Bind(config);
            configuration.GetSection("Review").Bind(config);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to load config.ini: {ex.Message}");
        }

        return config;
    }

    internal static string BuildIni(Config config)
    {
        var sb = new StringBuilder();

        sb.AppendLine("[General]");
        sb.AppendLine($"ApiKey = {config.ApiKey}");
        sb.AppendLine($"ApiEndpoint = {config.ApiEndpoint}");
        sb.AppendLine($"Model = {config.Model}");
        sb.AppendLine();
        sb.AppendLine("[Behavior]");
        sb.AppendLine($"AutoCommit = {config.AutoCommit}".ToLower());
        sb.AppendLine($"AutoPush = {config.AutoPush}".ToLower());
        sb.AppendLine();
        sb.AppendLine("[Rules]");
        sb.AppendLine($"MaxSubjectLength = {config.MaxSubjectLength}");
        sb.AppendLine($"DiffNameOnly = {config.DiffNameOnly}");
        sb.AppendLine($"MaxDiffChars = {config.MaxDiffChars}");
        sb.AppendLine();
        sb.AppendLine("[Review]");
        sb.AppendLine($"ReviewModel = {config.ReviewModel}");
        sb.AppendLine($"MaxReviewComments = {config.MaxReviewComments}");
        sb.AppendLine($"MaxReviewRounds = {config.MaxReviewRounds}");
        sb.AppendLine($"MaxToolResultChars = {config.MaxToolResultChars}");
        sb.AppendLine($"SkillsPaths = {config.SkillsPaths}");
        sb.AppendLine($"ReviewConcurrency = {config.ReviewConcurrency}");
        sb.AppendLine($"ReviewTimeoutMinutes = {config.ReviewTimeoutMinutes}");
        sb.AppendLine();

        return sb.ToString();
    }

    public static void SaveConfig(Config config)
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(ConfigPath, BuildIni(config), Encoding.UTF8);
            Console.WriteLine($"Configuration saved to: {ConfigPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to save config: {ex.Message}");
        }
    }

    internal static bool TryApply(Config config, string key, string value, out string error)
    {
        switch (key.ToLowerInvariant())
        {
            case "apikey":
                config.ApiKey = value;
                break;
            case "apiendpoint":
                config.ApiEndpoint = value;
                break;
            case "model":
                config.Model = value;
                break;
            case "autocommit":
                config.AutoCommit = bool.Parse(value);
                break;
            case "maxsubjectlength":
                config.MaxSubjectLength = int.Parse(value);
                break;
            case "diffnameonly":
                config.DiffNameOnly = bool.Parse(value);
                break;
            case "maxdiffchars":
                config.MaxDiffChars = int.Parse(value);
                break;
            case "reviewmodel":
                if (value.Contains('\n') || value.Contains('\r'))
                {
                    error = "Error: reviewmodel must be a single line.";
                    return false;
                }

                config.ReviewModel = value;
                break;
            case "maxreviewcomments":
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxComments) ||
                    maxComments < 1)
                {
                    error = $"Error: maxreviewcomments must be a positive integer, got '{value}'.";
                    return false;
                }

                config.MaxReviewComments = maxComments;
                break;
            case "maxreviewrounds":
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxRounds) ||
                    maxRounds < 1 || maxRounds > ReviewAgentLimits.MaxRounds)
                {
                    error = $"Error: maxreviewrounds must be an integer from 1 to {ReviewAgentLimits.MaxRounds}, got '{value}'.";
                    return false;
                }

                config.MaxReviewRounds = maxRounds;
                break;
            case "maxtoolresultchars":
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxToolChars) ||
                    maxToolChars < 1 || maxToolChars > ReviewAgentLimits.MaxToolChars)
                {
                    error = $"Error: maxtoolresultchars must be an integer from 1 to {ReviewAgentLimits.MaxToolChars}, got '{value}'.";
                    return false;
                }

                config.MaxToolResultChars = maxToolChars;
                break;
            case "skillspaths":
                if (value.Contains('\n') || value.Contains('\r') || value.Contains('\0'))
                {
                    error = "Error: skillspaths must be a single line of semicolon-separated paths.";
                    return false;
                }

                config.SkillsPaths = value.Trim();
                break;
            case "reviewconcurrency":
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var concurrency) ||
                    concurrency < 1 || concurrency > ReviewParallelLimits.MaxConcurrency)
                {
                    error = $"Error: reviewconcurrency must be an integer from 1 to {ReviewParallelLimits.MaxConcurrency}, got '{value}'.";
                    return false;
                }

                config.ReviewConcurrency = concurrency;
                break;
            case "reviewtimeoutminutes":
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutMinutes) ||
                    timeoutMinutes < 1 || timeoutMinutes > ReviewParallelLimits.MaxTimeoutMinutes)
                {
                    error = $"Error: reviewtimeoutminutes must be an integer from 1 to {ReviewParallelLimits.MaxTimeoutMinutes}, got '{value}'.";
                    return false;
                }

                config.ReviewTimeoutMinutes = timeoutMinutes;
                break;
            default:
                error = $"Error: Unknown configuration key: {key}";
                return false;
        }

        error = "";
        return true;
    }

    public static void ShowConfig()
    {
        var config = LoadConfig();
        Console.WriteLine($"\nCurrent Configuration (Location: {ConfigPath}):");
        Console.WriteLine(new string('-', 60));
        Console.WriteLine(
            $"  ApiKey:            {(string.IsNullOrEmpty(config.ApiKey) ? "(not set)" : config.ApiKey)}");
        Console.WriteLine($"  ApiEndpoint:       {config.ApiEndpoint ?? "(not set)"}");
        Console.WriteLine($"  Model:             {config.Model}");
        Console.WriteLine($"  AutoCommit:        {config.AutoCommit}");
        Console.WriteLine($"  AutoPush:          {config.AutoPush}");
        Console.WriteLine($"  MaxSubjectLength:  {config.MaxSubjectLength}");
        Console.WriteLine($"  DiffNameOnly:      {config.DiffNameOnly}");
        Console.WriteLine($"  MaxDiffChars:      {(config.MaxDiffChars <= 0 ? "(unlimited, no fallback)" : config.MaxDiffChars)}");
        Console.WriteLine($"  ReviewModel:       {(string.IsNullOrWhiteSpace(config.ReviewModel) ? "(not set, uses model)" : config.ReviewModel)}");
        Console.WriteLine($"  MaxReviewComments: {config.MaxReviewComments}");
        Console.WriteLine($"  MaxReviewRounds:   {config.MaxReviewRounds}");
        Console.WriteLine($"  MaxToolResultChars: {config.MaxToolResultChars}");
        Console.WriteLine($"  SkillsPaths:       {(string.IsNullOrWhiteSpace(config.SkillsPaths) ? "(not set)" : config.SkillsPaths)}");
        Console.WriteLine($"  ReviewConcurrency: {config.ReviewConcurrency}");
        Console.WriteLine($"  ReviewTimeoutMinutes: {config.ReviewTimeoutMinutes}");
        Console.WriteLine(new string('-', 60));
    }
}