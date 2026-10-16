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
            "\u001b[94mif\u001b[0m (count > \u001b[33m0\u001b[0m) \u001b[90m// ready\u001b[0m\n" +
            "\u001b[94mvar\u001b[0m s = \u001b[32m\"http://x\"\u001b[0m;\n" +
            "\u001b[94mreturn\u001b[0m;",
            text);
    }

    [Fact]
    public void CsharpKeepsIdentifiersThatOnlyStartWithAKeyword()
    {
        Assert.Equal("returnValue", ReviewHighlighter.Colorize("returnValue", "App.cs"));
        Assert.Equal("foo.if", ReviewHighlighter.Colorize("foo.if", "App.cs"));
        Assert.Equal("iffy", ReviewHighlighter.Colorize("iffy", "App.cs"));
    }

    [Fact]
    public void CsharpColorsVerbatimStringsNumbersAndPreprocessorLines()
    {
        Assert.Equal("\u001b[32m@\"a \"\" b\"\u001b[0m", ReviewHighlighter.Colorize("@\"a \"\" b\"", "App.cs"));
        Assert.Equal("\u001b[33m1.5f\u001b[0m \u001b[33m0x1F\u001b[0m \u001b[33m10_000\u001b[0m", ReviewHighlighter.Colorize("1.5f 0x1F 10_000", "App.cs"));
        Assert.Equal("\u001b[33m1\u001b[0m..\u001b[33m2\u001b[0m \u001b[33m.5\u001b[0m \u001b[33m10\u001b[0munits", ReviewHighlighter.Colorize("1..2 .5 10units", "App.cs"));
        Assert.Equal("..\u001b[33m5\u001b[0m", ReviewHighlighter.Colorize("..5", "App.cs"));
        Assert.Equal("\u001b[90m#if DEBUG\u001b[0m", ReviewHighlighter.Colorize("#if DEBUG", "App.cs"));
        Assert.Equal("\u001b[32m$\"hi {name}\"\u001b[0m", ReviewHighlighter.Colorize("$\"hi {name}\"", "App.cs"));
    }

    [Fact]
    public void BlockCommentsAndTripleStringsCarryAcrossLines()
    {
        var comment = ReviewHighlighter.Colorize("/* start\nstill\nend */ int x = 1;", "App.cs");
        Assert.Equal(
            "\u001b[90m/* start\u001b[0m\n" +
            "\u001b[90mstill\u001b[0m\n" +
            "\u001b[90mend */\u001b[0m \u001b[94mint\u001b[0m x = \u001b[33m1\u001b[0m;",
            comment);

        var raw = ReviewHighlighter.Colorize("x = \"\"\"\nhello // not comment\n\"\"\"", "main.py");
        Assert.Equal(
            "x = \u001b[32m\"\"\"\u001b[0m\n" +
            "\u001b[32mhello // not comment\u001b[0m\n" +
            "\u001b[32m\"\"\"\u001b[0m",
            raw);
    }

    [Fact]
    public void WebLanguagesColorTheirOwnTokens()
    {
        Assert.Equal(
            "\u001b[94mconst\u001b[0m name = \u001b[32m\"ada\"\u001b[0m; \u001b[90m// hi\u001b[0m",
            ReviewHighlighter.Colorize("const name = \"ada\"; // hi", "web/app.TS"));
        Assert.Equal(
            "\u001b[94mconst\u001b[0m s = \u001b[32m`hi ${name}`\u001b[0m;",
            ReviewHighlighter.Colorize("const s = `hi ${name}`;", "web/app.tsx"));
        Assert.Equal(
            "<\u001b[94mdiv\u001b[0m class=\u001b[32m\"x\"\u001b[0m>\u001b[90m<!-- note -->\u001b[0m</\u001b[94mdiv\u001b[0m>",
            ReviewHighlighter.Colorize("<div class=\"x\"><!-- note --></div>", "page.html"));
        Assert.Equal(
            "{ \u001b[32m\"ok\"\u001b[0m: \u001b[94mtrue\u001b[0m, \u001b[32m\"n\"\u001b[0m: \u001b[33m2\u001b[0m }",
            ReviewHighlighter.Colorize("{ \"ok\": true, \"n\": 2 }", "app.json"));
        Assert.Equal(
            "\u001b[94m@media\u001b[0m (min-width: \u001b[33m600\u001b[0mpx) \u001b[90m/* q */\u001b[0m",
            ReviewHighlighter.Colorize("@media (min-width: 600px) /* q */", "site.css"));
    }

    [Fact]
    public void SqlKeywordsAreCaseInsensitive()
    {
        Assert.Equal(
            "\u001b[94mSELECT\u001b[0m * \u001b[94mFROM\u001b[0m t \u001b[90m-- keep\u001b[0m",
            ReviewHighlighter.Colorize("SELECT * FROM t -- keep", "q.sql"));
    }

    [Fact]
    public void UnknownExtensionUsesOneMildColor()
    {
        Assert.Equal("\u001b[36mif (ready)\u001b[0m", ReviewHighlighter.Colorize("if (ready)", "notes/readme.txt"));
        Assert.Equal("\u001b[36mFROM scratch\u001b[0m", ReviewHighlighter.Colorize("FROM scratch", "Dockerfile"));
        Assert.Equal("\u001b[36mif\u001b[0m\n\n\u001b[36mreturn\u001b[0m", ReviewHighlighter.Colorize("if\n\nreturn", "Notes.md"));
    }

    [Fact]
    public void RazorColorsMarkupAndCsharp()
    {
        Assert.Equal(
            "<\u001b[94mp\u001b[0m>@\u001b[94mif\u001b[0m</\u001b[94mp\u001b[0m>",
            ReviewHighlighter.Colorize("<p>@if</p>", "Views/Index.cshtml"));
    }
}
