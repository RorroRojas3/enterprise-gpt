using Enterprise.Gpt.Service.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Rendering;

/// <summary>
/// The rendered HTML is stored and re-served, and its input is attacker-influenceable: model output
/// is shaped by uploaded documents and MCP tool results, and user prompts go through the same path.
/// These are security tests, not formatting tests.
/// </summary>
public sealed class MarkdownRendererTests
{
    private static readonly MarkdownRenderer Renderer = new(NullLogger<MarkdownRenderer>.Instance);

    [Fact]
    public void Render_ScriptElement_IsEscapedRatherThanEmitted()
    {
        var html = Renderer.Render("<script>alert(1)</script>");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ImageWithEventHandler_EmitsNoElementAndNoHandler()
    {
        var html = Renderer.Render("<img src=x onerror=alert(1)>");

        // The payload survives as text and that is the point: no element is created, so the handler
        // is a string in a paragraph rather than an attribute a browser would ever run.
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("<p>&lt;img src=x onerror=alert(1)&gt;</p>\n", html);
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JavaScript:alert(1))")]
    [InlineData("[click](  javascript:alert(1))")]
    [InlineData("[click](vbscript:msgbox(1))")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)")]
    [InlineData("[click](file:///etc/passwd)")]
    public void Render_DisallowedLinkScheme_IsStripped(string markdown)
    {
        var html = Renderer.Render(markdown);

        Assert.DoesNotContain("javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_ImageWithDisallowedScheme_IsStripped()
    {
        var html = Renderer.Render("![alt](javascript:alert(1))");

        Assert.DoesNotContain("javascript", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[ok](https://example.com/page)", "https://example.com/page")]
    [InlineData("[ok](http://example.com)", "http://example.com")]
    [InlineData("[ok](mailto:someone@example.com)", "mailto:someone@example.com")]
    [InlineData("[ok](/relative/path)", "/relative/path")]
    [InlineData("[ok](#anchor)", "#anchor")]
    [InlineData("[ok](relative/path:with-colon)", "relative/path:with-colon")]
    public void Render_AllowedOrRelativeTarget_Survives(string markdown, string expectedHref)
    {
        var html = Renderer.Render(markdown);

        Assert.Contains($"href=\"{expectedHref}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_FencedCodeBlock_EmitsPreCodeWithTheLanguageAndEscapedContents()
    {
        var html = Renderer.Render("```csharp\nvar x = a < b && c > d;\n```");

        Assert.Contains("<pre>", html, StringComparison.Ordinal);
        Assert.Contains("<code class=\"language-csharp\">", html, StringComparison.Ordinal);
        Assert.Contains("&lt;", html, StringComparison.Ordinal);
        Assert.Contains("&amp;&amp;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_HtmlInsideAFencedCodeBlock_StaysEscapedText()
    {
        var html = Renderer.Render("```html\n<script>alert(1)</script>\n```");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_OrdinaryMarkdown_RendersNormally()
    {
        var html = Renderer.Render("# Title\n\nSome **bold** text.\n\n- one\n- two");

        Assert.Contains("<h1", html, StringComparison.Ordinal);
        Assert.Contains("<strong>bold</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<li>one</li>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PipeTable_IsSupported()
    {
        var html = Renderer.Render("| a | b |\n| - | - |\n| 1 | 2 |");

        Assert.Contains("<table>", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Render_EmptyInput_ReturnsEmpty(string? markdown)
    {
        Assert.Equal(string.Empty, Renderer.Render(markdown));
    }

    [Fact]
    public void Render_BareUrlInText_BecomesALinkAndKeepsItsScheme()
    {
        var html = Renderer.Render("See https://example.com for details.");

        Assert.Contains("href=\"https://example.com\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EmailFence_IsProseRatherThanACodeBlock()
    {
        var html = Renderer.Render("```email\nTo: alice@contoso.com\nSubject: Q3\n```");

        Assert.DoesNotContain("<pre>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("language-email", html, StringComparison.Ordinal);
        Assert.Contains("<p>To: alice@contoso.com</p>", html, StringComparison.Ordinal);
        Assert.Contains("<p>Subject: Q3</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EmailFence_EscapesItsContent()
    {
        // Inside a fence the pipeline's DisableHtml never runs, so the renderer that replaces the
        // code block is the only thing standing between model output and live markup.
        var html = Renderer.Render("```email\n<script>alert(1)</script>\n```");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The renderer that carves out the email fence is registered for code blocks generally, so
    /// every other shape it owns has to reach the base renderer untouched.
    /// </summary>
    [Theory]
    [InlineData("```ts\nconst a = 1;\n```", "language-ts")]
    [InlineData("```email draft\nHi.\n```", "language-email")]
    [InlineData("```emails\nHi.\n```", "language-emails")]
    public void Render_CodeFenceThatIsNotAnEmail_StaysACodeBlock(string markdown, string expected)
    {
        var html = Renderer.Render(markdown);

        Assert.Contains("<pre>", html, StringComparison.Ordinal);
        Assert.Contains(expected, html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_IndentedCodeBlock_StaysACodeBlock()
    {
        var html = Renderer.Render("    const a = 1;");

        Assert.Contains("<pre><code>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"email\"", html, StringComparison.Ordinal);
    }
}
