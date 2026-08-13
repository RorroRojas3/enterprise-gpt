using Enterprise.Gpt.Service.Rendering;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Rendering;

/// <summary>
/// Model output is attacker-influenceable through uploaded documents and MCP tool results, so what
/// the renderer receives is untrusted input. These pin that it stays untrusted on the way out.
/// </summary>
public class MarkdownRendererTests
{
    private readonly MarkdownRenderer _renderer = new();

    [Fact]
    public void Render_ScriptElement_EscapesItRatherThanEmittingAnElement()
    {
        var html = _renderer.Render("Before <script>alert(1)</script> after.");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The whole tag is escaped into text, so the handler survives as characters rather than as an
    /// attribute — inert, because no element was ever emitted for it to hang off.
    /// </summary>
    [Fact]
    public void Render_ImageWithAnEventHandler_EmitsNoElementForTheHandlerToHangOff()
    {
        var html = _renderer.Render("<img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;img", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_FencedCodeBlock_EmitsPreCodeWithTheLanguageAndEscapedContents()
    {
        var html = _renderer.Render("```csharp\nvar x = a < b && c > d;\n```");

        Assert.Contains("<pre>", html);
        Assert.Contains("<code class=\"language-csharp\">", html);
        Assert.Contains("&lt;", html);
        Assert.DoesNotContain("a < b", html);
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JaVaScRiPt:alert(1))")]
    [InlineData("[click](  javascript:alert(1))")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[click](vbscript:msgbox(1))")]
    public void Render_DangerousLinkScheme_StripsItFromTheHref(string markdown)
    {
        var html = _renderer.Render(markdown);

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[docs](https://example.com/a)", "https://example.com/a")]
    [InlineData("[mail](mailto:someone@example.com)", "mailto:someone@example.com")]
    [InlineData("[relative](/conversations/42)", "/conversations/42")]
    public void Render_SafeLink_KeepsItsHref(string markdown, string expectedHref)
    {
        var html = _renderer.Render(markdown);

        Assert.Contains(expectedHref, html);
    }

    /// <summary>
    /// A colon after a slash belongs to a path, not to a scheme, so such a link is relative and
    /// must survive.
    /// </summary>
    [Fact]
    public void Render_RelativeLinkContainingAColon_IsNotMistakenForAScheme()
    {
        var html = _renderer.Render("[ref](/a/b:c)");

        Assert.Contains("/a/b:c", html);
    }

    /// <summary>
    /// Markdig's generic-attribute syntax attaches arbitrary attributes to the preceding element,
    /// which disabling raw HTML does not touch — the renderer emits them itself.
    /// </summary>
    [Theory]
    [InlineData("Some **text**{onmouseover=\"alert(1)\"} here.")]
    [InlineData("Some **text**{onclick=\"alert(1)\"} here.")]
    [InlineData("# Heading {#id onload=\"alert(1)\"}")]
    [InlineData("Some **text**{.cls #id} here.")]
    public void Render_GenericAttributeSyntax_EmitsNoAttributeOnAnyElement(string markdown)
    {
        var html = _renderer.Render(markdown);

        // The syntax survives as escaped text, which is inert; what must not happen is it becoming
        // an attribute on the element in front of it.
        Assert.DoesNotMatch(@"<[a-zA-Z][^>]*\s[a-zA-Z-]+\s*=", html);
    }

    /// <summary>
    /// An autolink is not a link inline, so a scheme sweep that only walks links misses it.
    /// </summary>
    [Theory]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("<vbscript:msgbox(1)>")]
    public void Render_DangerousAutolinkScheme_StripsItFromTheHref(string markdown)
    {
        var html = _renderer.Render(markdown);

        Assert.DoesNotContain("href=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"vbscript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_PipeTable_RendersAsATable()
    {
        var html = _renderer.Render("| a | b |\n|---|---|\n| 1 | 2 |");

        Assert.Contains("<table>", html);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Render_EmptyInput_ReturnsEmpty(string? markdown)
    {
        Assert.Equal(string.Empty, _renderer.Render(markdown));
    }

    [Fact]
    public void Render_PlainMarkdown_RendersTheExpectedInlineFormatting()
    {
        var html = _renderer.Render("Some **bold** text.");

        Assert.Contains("<strong>bold</strong>", html);
    }
}
