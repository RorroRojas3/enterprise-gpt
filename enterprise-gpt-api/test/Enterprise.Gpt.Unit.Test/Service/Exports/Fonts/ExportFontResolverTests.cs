using Enterprise.Gpt.Service.Export.Fonts;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Export;

/// <summary>
/// The resolver decides whether this deployment can render a PDF at all, so the case that matters
/// most is the one where it cannot.
/// </summary>
public sealed class ExportFontResolverTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"export-fonts-{Guid.NewGuid():N}");

    public ExportFontResolverTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone; nothing to clean up.
        }
    }

    /// <summary>
    /// A minimal file with a TrueType signature. The resolver never parses a font — it matches by
    /// file name and hands the bytes to PDFsharp — so a real typeface is not needed to assert which
    /// file it picked.
    /// </summary>
    private string WriteFace(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, [0x00, 0x01, 0x00, 0x00, .. "stub"u8]);

        return path;
    }

    /// <summary>
    /// The five role-named faces are committed and copied beside the assembly, so a deployment that
    /// provisioned nothing still renders a PDF — see <c>Export/Fonts/README.md</c>.
    /// </summary>
    /// <remarks>
    /// The directory is named rather than defaulted, and the files are asserted directly: an
    /// unqualified resolver falls back to the platform's own fonts, so it reports five roles on any
    /// developer machine whether or not the csproj glob copied anything.
    /// </remarks>
    [Fact]
    public void ShippedDirectory_ResolvesEveryRole()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fonts");

        foreach (var role in new[]
        {
            "export-sans-regular", "export-sans-bold", "export-sans-italic",
            "export-sans-bold-italic", "export-mono-regular"
        })
        {
            Assert.True(
                File.Exists(Path.Combine(directory, role + ".ttf")),
                $"{role}.ttf did not reach the output directory.");
        }

        var resolver = new ExportFontResolver(directory);

        Assert.True(resolver.IsUsable);
        Assert.Equal(5, resolver.ResolvedFaces.Count);
    }

    [Fact]
    public void IsUsable_ConfiguredDirectoryWithARegularFace_IsTrue()
    {
        WriteFace("export-sans-regular.ttf");

        Assert.True(new ExportFontResolver(_directory).IsUsable);
    }

    /// <summary>
    /// The whole point of the flag: a deployment in this state registers no PDF renderer, and the
    /// route answers <c>503</c> with the export-renderer-not-configured type instead of throwing on
    /// the first glyph, halfway into a response body.
    /// </summary>
    [Fact]
    public void IsUsable_NothingAnywhere_IsFalse()
    {
        var resolver = new ExportFontResolver(Path.Combine(_directory, "does-not-exist"));

        Assert.SkipUnless(
            !ProbePlatformFonts(),
            "This machine has platform fonts the resolver legitimately falls back to.");

        Assert.False(resolver.IsUsable);
        Assert.Empty(resolver.ResolvedFaces);
    }

    [Fact]
    public void ResolveTypeface_BoldRequestWithABoldFace_ReturnsIt()
    {
        WriteFace("export-sans-regular.ttf");
        WriteFace("export-sans-bold.ttf");

        var info = new ExportFontResolver(_directory)
            .ResolveTypeface(ExportFontFamilies.Sans, bold: true, italic: false);

        Assert.NotNull(info);
        Assert.Equal("export-sans-bold", info!.FaceName);
        Assert.False(info.MustSimulateBold);
    }

    /// <summary>
    /// PDFsharp documents its bold-simulation flag as unimplemented and requires it to be false, so a
    /// deployment with no bold face renders bold text upright rather than failing.
    /// </summary>
    [Fact]
    public void ResolveTypeface_BoldRequestWithNoBoldFace_FallsBackWithoutSimulatingBold()
    {
        WriteFace("export-sans-regular.ttf");

        var info = new ExportFontResolver(_directory)
            .ResolveTypeface(ExportFontFamilies.Sans, bold: true, italic: false);

        Assert.NotNull(info);
        Assert.Equal("export-sans-regular", info!.FaceName);
        Assert.False(info.MustSimulateBold);
    }

    /// <summary>
    /// Italic is the one substitution PDFsharp can actually perform, so it is asked to.
    /// </summary>
    [Fact]
    public void ResolveTypeface_ItalicRequestWithNoItalicFace_AsksForSimulation()
    {
        WriteFace("export-sans-regular.ttf");

        var info = new ExportFontResolver(_directory)
            .ResolveTypeface(ExportFontFamilies.Sans, bold: false, italic: true);

        Assert.NotNull(info);
        Assert.Equal("export-sans-regular", info!.FaceName);
        Assert.True(info.MustSimulateItalic);
    }

    /// <summary>
    /// Losing the monospace distinction is better than losing the export.
    /// </summary>
    [Fact]
    public void ResolveTypeface_MonoRequestWithNoMonoFace_FallsBackToTheBodyFace()
    {
        WriteFace("export-sans-regular.ttf");

        var info = new ExportFontResolver(_directory)
            .ResolveTypeface(ExportFontFamilies.Mono, bold: false, italic: false);

        Assert.NotNull(info);
        Assert.Equal("export-sans-regular", info!.FaceName);
    }

    [Fact]
    public void GetFont_ResolvedFace_ReturnsTheFileBytes()
    {
        var path = WriteFace("export-sans-regular.ttf");

        var bytes = new ExportFontResolver(_directory).GetFont("export-sans-regular");

        Assert.Equal(File.ReadAllBytes(path), bytes);
    }

    [Fact]
    public void GetFont_UnknownFace_ReturnsNull()
    {
        WriteFace("export-sans-regular.ttf");

        Assert.Null(new ExportFontResolver(_directory).GetFont("export-sans-nonexistent"));
    }

    /// <summary>
    /// A machine-wide font directory that happens to hold a file with the configured role name is not
    /// something to honour: the role names are a contract with the operator, not a discovery pattern.
    /// </summary>
    [Fact]
    public void Constructor_ConfiguredDirectoryMissing_DoesNotThrow()
    {
        var resolver = new ExportFontResolver(Path.Combine(_directory, "missing", "deeper"));

        Assert.NotNull(resolver);
    }

    private static bool ProbePlatformFonts() =>
        new ExportFontResolver(Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}")).IsUsable;
}
