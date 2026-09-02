using NotesApp.Core.Text;

namespace NotesApp.Tests;

// MarkdownConverter turns the AI's Markdown replies into editor HTML. It's a
// pure function, so it's the easiest part of the app to pin down with tests.
public class MarkdownConverterTests
{
    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MarkdownConverter.ToHtml(""));
        Assert.Equal(string.Empty, MarkdownConverter.ToHtml("   "));
    }

    [Fact]
    public void PlainLine_BecomesDiv()
    {
        Assert.Equal("<div>hello</div>", MarkdownConverter.ToHtml("hello"));
    }

    [Theory]
    [InlineData("# Big", "<h1>Big</h1>")]
    [InlineData("## Medium", "<h2>Medium</h2>")]
    [InlineData("### Small", "<h3>Small</h3>")]
    public void Headings_Convert(string markdown, string expected)
    {
        Assert.Equal(expected, MarkdownConverter.ToHtml(markdown));
    }

    [Fact]
    public void BulletList_BecomesUlWithItems()
    {
        Assert.Equal(
            "<ul><li>one</li><li>two</li></ul>",
            MarkdownConverter.ToHtml("- one\n- two"));
    }

    [Fact]
    public void BoldMarkers_BecomeBoldTags()
    {
        Assert.Equal("<div>a <b>bold</b> word</div>", MarkdownConverter.ToHtml("a **bold** word"));
    }

    // Regression test: AI "Make Table" output used to render as literal pipes
    // because the reply was escaped as plain text instead of converted.
    [Fact]
    public void Table_BecomesRealHtmlTable()
    {
        var md = "| Name | Age |\n|------|-----|\n| John | 28 |\n| Maria | 34 |";

        var html = MarkdownConverter.ToHtml(md);

        Assert.Equal(
            "<table><tr><th>Name</th><th>Age</th></tr>" +
            "<tr><td>John</td><td>28</td></tr>" +
            "<tr><td>Maria</td><td>34</td></tr></table>",
            html);
        Assert.DoesNotContain("|", html); // no leftover Markdown syntax
    }

    // Small models often wrap replies in ``` fences and add a preamble line.
    [Fact]
    public void CodeFencesAreStripped_AndPreambleKept()
    {
        var md = "Here it is:\n```\n| A | B |\n|---|---|\n| 1 | 2 |\n```";

        var html = MarkdownConverter.ToHtml(md);

        Assert.StartsWith("<div>Here it is:</div>", html);
        Assert.Contains("<table>", html);
        Assert.DoesNotContain("```", html);
    }

    // The converter's output goes straight into the editor, so untrusted text
    // must be escaped rather than injected as markup.
    [Fact]
    public void HtmlInSourceText_IsEscaped()
    {
        var html = MarkdownConverter.ToHtml("<script>alert('x')</script>");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void MixedDocument_KeepsBlockOrder()
    {
        var html = MarkdownConverter.ToHtml("## Title\n- a\n- b\nplain tail");

        Assert.Equal("<h2>Title</h2><ul><li>a</li><li>b</li></ul><div>plain tail</div>", html);
    }
}
