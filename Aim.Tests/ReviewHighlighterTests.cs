using Aim;

namespace Aim.Tests;

public class ReviewHighlighterTests
{
    [Fact]
    public void CsharpColorsKeywordsStringsCommentsAndNumbers()
    {
        var text = ReviewHighlighter.Colorize(
            "if (count > 0) // ready\nvar s = \"http://x\";\nreturn;",
            "src/App.cs");

        Assert.Equal(
            "\u001b[1;95mif\u001b[0m (count > \u001b[93m0\u001b[0m) \u001b[3;90m// ready\u001b[0m\n" +
            "\u001b[1;95mvar\u001b[0m s = \u001b[92m\"http://x\"\u001b[0m;\n" +
            "\u001b[1;95mreturn\u001b[0m;",
            text);
    }

    [Fact]
    public void CsharpKeepsIdentifiersThatOnlyStartWithAKeyword()
    {
        Assert.Equal("returnValue", ReviewHighlighter.Colorize("returnValue", "App.cs"));
        Assert.Equal("iffy", ReviewHighlighter.Colorize("iffy", "App.cs"));

        var dotted = ReviewHighlighter.Colorize("foo.if", "App.cs");
        Assert.Equal("foo\u001b[96m.if\u001b[0m", dotted);
        Assert.DoesNotContain("\u001b[1;95m", dotted, StringComparison.Ordinal);
        Assert.Equal("items\u001b[96m.Count\u001b[0m", ReviewHighlighter.Colorize("items.Count", "App.cs"));
        Assert.Equal("...args", ReviewHighlighter.Colorize("...args", "web/app.ts"));
    }

    [Fact]
    public void CsharpColorsVerbatimStringsNumbersAndPreprocessorLines()
    {
        Assert.Equal("\u001b[92m@\"a \"\" b\"\u001b[0m", ReviewHighlighter.Colorize("@\"a \"\" b\"", "App.cs"));
        Assert.Equal("\u001b[93m1.5f\u001b[0m \u001b[93m0x1F\u001b[0m \u001b[93m10_000\u001b[0m", ReviewHighlighter.Colorize("1.5f 0x1F 10_000", "App.cs"));
        Assert.Equal("\u001b[93m1\u001b[0m..\u001b[93m2\u001b[0m \u001b[93m.5\u001b[0m \u001b[93m10\u001b[0munits", ReviewHighlighter.Colorize("1..2 .5 10units", "App.cs"));
        Assert.Equal("..\u001b[93m5\u001b[0m", ReviewHighlighter.Colorize("..5", "App.cs"));
        Assert.Equal("\u001b[3;90m#if DEBUG\u001b[0m", ReviewHighlighter.Colorize("#if DEBUG", "App.cs"));
        Assert.Equal("\u001b[92m$\"hi {name}\"\u001b[0m", ReviewHighlighter.Colorize("$\"hi {name}\"", "App.cs"));
    }

    [Fact]
    public void BlockCommentsAndTripleStringsCarryAcrossLines()
    {
        var comment = ReviewHighlighter.Colorize("/* start\nstill\nend */ int x = 1;", "App.cs");
        Assert.Equal(
            "\u001b[3;90m/* start\u001b[0m\n" +
            "\u001b[3;90mstill\u001b[0m\n" +
            "\u001b[3;90mend */\u001b[0m \u001b[1;95mint\u001b[0m x = \u001b[93m1\u001b[0m;",
            comment);

        var raw = ReviewHighlighter.Colorize("x = \"\"\"\nhello // not comment\n\"\"\"", "main.py");
        Assert.Equal(
            "x = \u001b[92m\"\"\"\u001b[0m\n" +
            "\u001b[92mhello // not comment\u001b[0m\n" +
            "\u001b[92m\"\"\"\u001b[0m",
            raw);
    }

    [Fact]
    public void WebLanguagesColorTheirOwnTokens()
    {
        Assert.Equal(
            "\u001b[1;95mconst\u001b[0m name = \u001b[92m\"ada\"\u001b[0m; \u001b[3;90m// hi\u001b[0m",
            ReviewHighlighter.Colorize("const name = \"ada\"; // hi", "web/app.TS"));
        Assert.Equal(
            "\u001b[1;95mconst\u001b[0m s = \u001b[92m`hi ${name}`\u001b[0m;",
            ReviewHighlighter.Colorize("const s = `hi ${name}`;", "web/app.tsx"));
        Assert.Equal(
            "<\u001b[1;95mdiv\u001b[0m class=\u001b[92m\"x\"\u001b[0m>\u001b[3;90m<!-- note -->\u001b[0m</\u001b[1;95mdiv\u001b[0m>",
            ReviewHighlighter.Colorize("<div class=\"x\"><!-- note --></div>", "page.html"));
        Assert.Equal(
            "{ \u001b[92m\"ok\"\u001b[0m: \u001b[1;95mtrue\u001b[0m, \u001b[92m\"n\"\u001b[0m: \u001b[93m2\u001b[0m }",
            ReviewHighlighter.Colorize("{ \"ok\": true, \"n\": 2 }", "app.json"));
        Assert.Equal(
            "\u001b[1;95m@media\u001b[0m (min-width: \u001b[93m600\u001b[0mpx) \u001b[3;90m/* q */\u001b[0m",
            ReviewHighlighter.Colorize("@media (min-width: 600px) /* q */", "site.css"));
    }

    [Fact]
    public void SqlKeywordsAreCaseInsensitive()
    {
        Assert.Equal(
            "\u001b[1;95mSELECT\u001b[0m * \u001b[1;95mFROM\u001b[0m t \u001b[3;90m-- keep\u001b[0m",
            ReviewHighlighter.Colorize("SELECT * FROM t -- keep", "q.sql"));
    }

    [Fact]
    public void UnknownExtensionDoesNotPaintTheWholeLine()
    {
        Assert.Equal("if (ready)", ReviewHighlighter.Colorize("if (ready)", "notes/readme.txt"));
        Assert.Equal("if\n\nreturn", ReviewHighlighter.Colorize("if\n\nreturn", "Notes.md"));
        Assert.Equal("README", ReviewHighlighter.Colorize("README", "README"));
        Assert.Equal("items.Count", ReviewHighlighter.Colorize("items.Count", "Notes.md"));
        Assert.Equal(
            "see \u001b[92m\"ada\"\u001b[0m and \u001b[93m2\u001b[0m // note",
            ReviewHighlighter.Colorize("see \"ada\" and 2 // note", "notes/readme.txt"));
        Assert.DoesNotContain("\u001b[36m", ReviewHighlighter.Colorize("if (ready)", "notes/readme.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public void DockerfileIsTokenizedInsteadOfOneColor()
    {
        Assert.Equal(
            "\u001b[1;95mFROM\u001b[0m scratch \u001b[1;95mAS\u001b[0m base",
            ReviewHighlighter.Colorize("FROM scratch AS base", "Dockerfile"));
        Assert.Equal(
            "\u001b[1;95mRUN\u001b[0m echo \u001b[92m\"hi\"\u001b[0m",
            ReviewHighlighter.Colorize("RUN echo \"hi\"", "deploy/Dockerfile.prod"));
        Assert.Equal("\u001b[3;90m# pin\u001b[0m", ReviewHighlighter.Colorize("# pin", "Containerfile"));
    }

    [Fact]
    public void PathLineSuffixStillSelectsTheLanguage()
    {
        const string expected = "\u001b[1;95mreturn\u001b[0m \u001b[93m1\u001b[0m;";
        Assert.Equal(expected, ReviewHighlighter.Colorize("return 1;", "src/App.cs:12"));
        Assert.Equal(expected, ReviewHighlighter.Colorize("return 1;", "src/App.cs:12-18"));
        Assert.Equal(expected, ReviewHighlighter.Colorize("return 1;", "src/App.cs:12:3"));
        Assert.Equal(expected, ReviewHighlighter.Colorize("return 1;", "src/App.cs#L12"));
        Assert.Equal(expected, ReviewHighlighter.Colorize("return 1;", "src/App.cs(12)"));
        Assert.Equal(expected, ReviewHighlighter.Colorize("return 1;", "\"src/App.cs\""));
    }

    [Theory]
    [InlineData("src/App.cs", "if (count > 0) // ready\nvar s = \"ok\";")]
    [InlineData("web/app.ts", "const name = \"ada\"; // hi\nconst n = 1;")]
    [InlineData("main.py", "if n == 1: # go\ns = \"ok\"")]
    [InlineData("app.json", "{ \"ok\": true, \"n\": 2 }")]
    public void KnownLanguagesUseDistinctColors(string path, string snippet)
    {
        var text = ReviewHighlighter.Colorize(snippet, path);
        Assert.True(CountColorCodes(text) >= 3, text);
        Assert.Contains("\u001b[1;95m", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[92m", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[93m", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[36m", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RazorColorsMarkupAndCsharp()
    {
        Assert.Equal(
            "<\u001b[1;95mp\u001b[0m>@\u001b[1;95mif\u001b[0m</\u001b[1;95mp\u001b[0m>",
            ReviewHighlighter.Colorize("<p>@if</p>", "Views/Index.cshtml"));
    }

    private static int CountColorCodes(string text)
    {
        var seen = new HashSet<string>();
        var i = 0;
        while ((i = text.IndexOf('\u001b', i)) >= 0)
        {
            var end = text.IndexOf('m', i);
            if (end < 0)
                break;

            var code = text.Substring(i, end - i + 1);
            if (code != "\u001b[0m")
                seen.Add(code);
            i = end + 1;
        }

        return seen.Count;
    }
}
