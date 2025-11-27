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
    }

    public static Config LoadConfig()
    {
        var config = new Config();

        if (!File.Exists(ConfigPath))
            return config;

        try
        {
            var builder = new ConfigurationBuilder()
                .AddIniFile(ConfigPath, optional: true, reloadOnChange: false);

            var configuration = builder.Build();

            configuration.GetSection("General").Bind(config);
            configuration.GetSection("Behavior").Bind(config);
            configuration.GetSection("Rules").Bind(config);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to load config.ini: {ex.Message}");
        }

        return config;
    }

    public static void SaveConfig(Config config)
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

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
            sb.AppendLine();

            File.WriteAllText(ConfigPath, sb.ToString(), Encoding.UTF8);
            Console.WriteLine($"Configuration saved to: {ConfigPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to save config: {ex.Message}");
        }
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
        Console.WriteLine(new string('-', 60));
    }
}