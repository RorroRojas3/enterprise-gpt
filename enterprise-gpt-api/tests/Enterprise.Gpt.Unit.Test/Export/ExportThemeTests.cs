using System.Text.RegularExpressions;
using Enterprise.Gpt.Service.Export;
using Enterprise.Gpt.Service.Export.Renderers;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Export;

/// <summary>
/// The theme is the export feature's single source of appearance, and it is only that for as long as
/// the template states nothing of its own.
/// </summary>
public sealed partial class ExportThemeTests
{
    private static readonly string Template = File.ReadAllText(HtmlExportRenderer.TemplatePath);

    [GeneratedRegex(@"var\(\s*(--[a-z0-9-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VariableReference();

    [GeneratedRegex(@"#[0-9a-f]{3,8}\b", RegexOptions.IgnoreCase)]
    private static partial Regex HexLiteral();

    /// <summary>
    /// A literal here would be a colour Word and PDF could not see, and the one that drifts.
    /// </summary>
    [Fact]
    public void Template_StatesNoColourOfItsOwn()
    {
        var literals = HexLiteral().Matches(Template).Select(match => match.Value).Distinct().ToList();

        Assert.True(literals.Count == 0, $"The template names its own colours: {string.Join(", ", literals)}.");
    }

    [Fact]
    public void Template_ReferencesOnlyVariablesTheThemeSupplies()
    {
        var supplied = ExportTheme.CssVariables.Select(variable => variable.Key).ToHashSet(StringComparer.Ordinal);

        var referenced = VariableReference().Matches(Template)
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(referenced);
        Assert.DoesNotContain(referenced, name => !supplied.Contains(name));
    }

    [Theory]
    [InlineData("{{THEME}}")]
    [InlineData("{{TITLE}}")]
    [InlineData("{{DATE}}")]
    [InlineData("{{MESSAGES}}")]
    public void Template_CarriesEveryPlaceholderTheRendererFills(string placeholder)
    {
        Assert.Contains(placeholder, Template, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two that splice a block, exactly once each. A stray second occurrence — a comment naming
    /// one, say — is substituted too, and a theme block landing inside a CSS comment closes it only
    /// by luck. The name and the date are small escaped values and legitimately appear twice.
    /// </summary>
    [Theory]
    [InlineData("{{THEME}}")]
    [InlineData("{{MESSAGES}}")]
    public void Template_CarriesEachSplicedBlockPlaceholderExactlyOnce(string placeholder)
    {
        var occurrences = Template.Split(placeholder, StringSplitOptions.None).Length - 1;

        Assert.Equal(1, occurrences);
    }

    /// <summary>
    /// A family name with a space in it has to be quoted, and a generic keyword must not be — an
    /// unquoted <c>Segoe UI</c> or a quoted <c>sans-serif</c> silently resolves to nothing.
    /// </summary>
    [Theory]
    [InlineData("--font-sans", "Inter, Calibri, 'Segoe UI', system-ui, sans-serif")]
    [InlineData("--font-mono", "'JetBrains Mono', Consolas, ui-monospace, monospace")]
    public void CssVariables_FontStack_QuotesOnlyTheNamesThatNeedIt(string name, string expected)
    {
        Assert.Equal(expected, Value(name));
    }

    [Theory]
    [InlineData("--size-body", "10.5pt")]
    [InlineData("--size-code", "9pt")]
    [InlineData("--size-h1", "16.5pt")]
    public void CssVariables_Size_IsWrittenInPoints(string name, string expected)
    {
        Assert.Equal(expected, Value(name));
    }

    [Fact]
    public void CssVariables_Colour_CarriesTheHash()
    {
        Assert.Equal($"#{ExportTheme.Colors.Brand}", Value("--brand"));
    }

    [Fact]
    public void CssVariables_HasNoDuplicateName()
    {
        var names = ExportTheme.CssVariables.Select(variable => variable.Key).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(0, 16.5)]
    [InlineData(1, 16.5)]
    [InlineData(6, 11)]
    [InlineData(9, 11)]
    public void Heading_LevelOutsideTheMarkdownRange_IsClamped(int level, double expected)
    {
        Assert.Equal(expected, ExportTheme.Sizes.Heading(level));
    }

    private static string Value(string name) =>
        ExportTheme.CssVariables.Single(variable => variable.Key == name).Value;
}
