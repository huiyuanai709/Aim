using System.CommandLine;

namespace Aim;

public class ConfigCommand : Command
{
    public ConfigCommand() : base("config", "Manage configuration settings")
    {
        Setup();
    }

    private void Setup()
    {
        Option<bool> showOption = new("--show")
        {
            Description = "Show current configuration"
        };
        Option<string[]> setOption = new("--set")
        {
            Description = "Set configuration values (format: key=value, repeatable)",
            AllowMultipleArgumentsPerToken = true
        };
        Option<bool> resetOption = new("--reset")
        {
            Description = "Reset to default configuration"
        };
        
        Options.Add(showOption);
        Options.Add(setOption);
        Options.Add(resetOption);
        
        SetAction(parseResult =>
        {
            var sets = parseResult.GetValue(setOption);
            var reset = parseResult.GetValue(resetOption);

            if (reset)
            {
                var defaultConfig = new ConfigManager.Config();
                ConfigManager.SaveConfig(defaultConfig);
                Console.WriteLine("✓ Configuration reset to defaults");
                return 0;
            }

            if (sets is { Length: > 0 })
            {
                var config = ConfigManager.LoadConfig();

                foreach (var set in sets)
                {
                    var parts = set.Split('=', 2);
                    if (parts.Length != 2)
                    {
                        Console.Error.WriteLine($"Error: Invalid format '{set}'. Use: key=value");
                        return 1;
                    }

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    if (!TryApplyConfigValue(config, key, value, out var error))
                    {
                        Console.Error.WriteLine(error);
                        return 1;
                    }

                    Console.WriteLine($"✓ Set {key} = {value}");
                }

                ConfigManager.SaveConfig(config);
                return 0;
            }

            ConfigManager.ShowConfig();
            return 0;
        });
    }

    private static bool TryApplyConfigValue(ConfigManager.Config config, string key, string value, out string error)
    {
        switch (key.ToLower())
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
            default:
                error = $"Error: Unknown configuration key: {key}";
                return false;
        }

        error = string.Empty;
        return true;
    }
}