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
        Option<string?> setOption = new("--set")
        {
            Description = "Set a configuration value (format: key=value)"
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
            var set = parseResult.GetValue(setOption);
            var reset = parseResult.GetValue(resetOption);

            if (reset)
            {
                var defaultConfig = new ConfigManager.Config();
                ConfigManager.SaveConfig(defaultConfig);
                Console.WriteLine("✓ Configuration reset to defaults");
                return 0;
            }

            if (set != null)
            {
                var parts = set.Split('=', 2);
                if (parts.Length != 2)
                {
                    Console.Error.WriteLine("Error: Invalid format. Use: --set key=value");
                    return 1;
                }

                var config = ConfigManager.LoadConfig();
                var key = parts[0].Trim();
                var value = parts[1].Trim();

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
                    default:
                        Console.Error.WriteLine($"Error: Unknown configuration key: {key}");
                        return 1;
                }

                ConfigManager.SaveConfig(config);
                Console.WriteLine($"✓ Set {key} = {value}");
                return 0;
            }

            ConfigManager.ShowConfig();
            return 0;
        });
    }
}