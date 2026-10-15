using Aim;

namespace Aim.Tests;

public class ConfigManagerTests
{
    [Fact]
    public void ReviewSettingsRoundTripThroughIni()
    {
        var config = new ConfigManager.Config
        {
            ApiKey = "sk-test",
            ApiEndpoint = "https://example.test/v1",
            Model = "gpt-4o",
            AutoCommit = true,
            AutoPush = false,
            MaxSubjectLength = 50,
            DiffNameOnly = true,
            MaxDiffChars = 1234,
            ReviewModel = "gpt-review",
            MaxReviewComments = 7,
            MaxReviewRounds = 6,
            MaxToolResultChars = 4000,
            SkillsPaths = "/opt/team-skills;../shared"
        };

        var path = Path.Combine(Path.GetTempPath(), "aim-config-" + Guid.NewGuid().ToString("n") + ".ini");
        File.WriteAllText(path, ConfigManager.BuildIni(config));
        try
        {
            var loaded = ConfigManager.LoadFromFile(path);
            Assert.Equal("sk-test", loaded.ApiKey);
            Assert.Equal("https://example.test/v1", loaded.ApiEndpoint);
            Assert.Equal("gpt-4o", loaded.Model);
            Assert.True(loaded.AutoCommit);
            Assert.False(loaded.AutoPush);
            Assert.Equal(50, loaded.MaxSubjectLength);
            Assert.True(loaded.DiffNameOnly);
            Assert.Equal(1234, loaded.MaxDiffChars);
            Assert.Equal("gpt-review", loaded.ReviewModel);
            Assert.Equal(7, loaded.MaxReviewComments);
            Assert.Equal(6, loaded.MaxReviewRounds);
            Assert.Equal(4000, loaded.MaxToolResultChars);
            Assert.Equal("/opt/team-skills;../shared", loaded.SkillsPaths);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingReviewSectionKeepsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "aim-config-" + Guid.NewGuid().ToString("n") + ".ini");
        File.WriteAllText(path, "[General]\nModel = gpt-4o\n");
        try
        {
            var loaded = ConfigManager.LoadFromFile(path);
            Assert.Equal("gpt-4o", loaded.Model);
            Assert.Equal("", loaded.ReviewModel);
            Assert.Equal(20, loaded.MaxReviewComments);
            Assert.Equal(10, loaded.MaxReviewRounds);
            Assert.Equal(12000, loaded.MaxToolResultChars);
            Assert.Equal("", loaded.SkillsPaths);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryApplyAcceptsReviewKeysAndRejectsBadValues()
    {
        var config = new ConfigManager.Config();

        Assert.True(ConfigManager.TryApply(config, "reviewmodel", "gpt-4.1", out var error), error);
        Assert.Equal("gpt-4.1", config.ReviewModel);

        Assert.True(ConfigManager.TryApply(config, "reviewmodel", "", out error), error);
        Assert.Equal("", config.ReviewModel);

        Assert.True(ConfigManager.TryApply(config, "maxreviewcomments", "12", out error), error);
        Assert.Equal(12, config.MaxReviewComments);

        Assert.False(ConfigManager.TryApply(config, "maxreviewcomments", "0", out error));
        Assert.Contains("positive integer", error);

        Assert.False(ConfigManager.TryApply(config, "reviewmodel", "bad\nmodel", out error));
        Assert.Contains("single line", error);

        Assert.False(ConfigManager.TryApply(config, "nope", "1", out error));
        Assert.Contains("Unknown configuration key", error);

        Assert.True(ConfigManager.TryApply(config, "maxreviewrounds", "8", out error), error);
        Assert.Equal(8, config.MaxReviewRounds);
        Assert.False(ConfigManager.TryApply(config, "maxreviewrounds", "0", out error));
        Assert.Contains("1 to 40", error);
        Assert.False(ConfigManager.TryApply(config, "maxreviewrounds", "41", out error));

        Assert.True(ConfigManager.TryApply(config, "maxtoolresultchars", "500", out error), error);
        Assert.Equal(500, config.MaxToolResultChars);
        Assert.False(ConfigManager.TryApply(config, "maxtoolresultchars", "0", out error));
        Assert.Contains("1 to 200000", error);

        Assert.True(ConfigManager.TryApply(config, "skillspaths", "/opt/skills; ../shared", out error), error);
        Assert.Equal("/opt/skills; ../shared", config.SkillsPaths);
        Assert.True(ConfigManager.TryApply(config, "skillspaths", "", out error), error);
        Assert.Equal("", config.SkillsPaths);
        Assert.False(ConfigManager.TryApply(config, "skillspaths", "a\nb", out error));
        Assert.Contains("single line", error);
    }
}
