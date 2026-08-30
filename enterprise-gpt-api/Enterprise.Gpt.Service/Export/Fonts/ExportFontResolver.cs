using System.Runtime.InteropServices;
using PdfSharp.Fonts;

namespace Enterprise.Gpt.Service.Export.Fonts;

/// <summary>
/// The two logical font families a PDF export draws with.
/// </summary>
public static class ExportFontFamilies
{
    /// <summary>The body and heading face.</summary>
    public const string Sans = "Export Sans";

    /// <summary>The face code blocks and inline code are set in.</summary>
    public const string Mono = "Export Mono";
}

/// <summary>
/// Supplies PDFsharp with the font files a conversation export is drawn with.
/// </summary>
/// <remarks>
/// <para>
/// PDFsharp's cross-platform build ships no fonts and reads none from the operating system unless it
/// is told to; with no resolver it throws on the first glyph. A PDF export therefore genuinely depends
/// on this deployment having faces available, which is what makes the
/// <c>export-renderer-not-configured</c> problem a real state rather than dead code: when
/// <see cref="IsUsable"/> is false the PDF renderer is never registered and the route answers 503
/// rather than throwing at render time, halfway into a response.
/// </para>
/// <para>
/// Faces are found by <em>file name</em>, never by parsing a font's internal name table. That is a
/// deliberate trade: the configured directory is read with fixed, documented role names, so a
/// container gets a deterministic, brand-matching result by dropping five files in; the operating
/// system's own font directories are the fallback, so a developer machine renders with nothing
/// provisioned. What it cannot do is discover an arbitrary family by its real name, which nothing
/// here needs.
/// </para>
/// <para>
/// The two sources are <strong>all or nothing</strong>, not merged per role. Filling a missing bold
/// face from the platform while the body face came from the configured directory would set one
/// document in two unrelated typefaces; an operator who supplied a face expects the document to be in
/// it, and unemboldened text is the better of the two failures.
/// </para>
/// <para>
/// Bold is <em>not</em> simulated. <see cref="FontResolverInfo"/> documents its bold-simulation flag
/// as unimplemented and requires it to be false, so a deployment with no bold face renders bold text
/// in the regular one rather than failing.
/// </para>
/// </remarks>
public sealed class ExportFontResolver : IFontResolver
{
    /// <summary>
    /// The file names looked for in the configured directory, per role.
    /// </summary>
    /// <remarks>
    /// Documented in <c>Export/Fonts/README.md</c>. Chosen over the upstream file names (<c>Inter-Regular.ttf</c>
    /// and friends) so the contract is a role rather than a particular typeface, and swapping the
    /// brand face is a file copy rather than a code change.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> ConfiguredNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [FaceNames.SansRegular] = "export-sans-regular",
        [FaceNames.SansBold] = "export-sans-bold",
        [FaceNames.SansItalic] = "export-sans-italic",
        [FaceNames.SansBoldItalic] = "export-sans-bold-italic",
        [FaceNames.MonoRegular] = "export-mono-regular"
    };

    /// <summary>
    /// Well-known platform faces, used only when the configured directory supplies no body face.
    /// </summary>
    /// <remarks>
    /// Read from the machine they run on rather than redistributed, which is the same thing
    /// PDFsharp's own <c>UseWindowsFontsUnderWindows</c> does — and the reason that switch is not used
    /// instead: it resolves only what it knows about, under one operating system, and leaves this
    /// class with two resolution paths to keep in step.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string[]> PlatformNames = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [FaceNames.SansRegular] = ["Inter-Regular", "segoeui", "DejaVuSans", "LiberationSans-Regular", "NotoSans-Regular", "arial", "Arial", "Helvetica"],
        [FaceNames.SansBold] = ["Inter-Bold", "segoeuib", "DejaVuSans-Bold", "LiberationSans-Bold", "NotoSans-Bold", "arialbd", "Arial Bold"],
        [FaceNames.SansItalic] = ["Inter-Italic", "segoeuii", "DejaVuSans-Oblique", "LiberationSans-Italic", "NotoSans-Italic", "ariali", "Arial Italic"],
        [FaceNames.SansBoldItalic] = ["Inter-BoldItalic", "segoeuiz", "DejaVuSans-BoldOblique", "LiberationSans-BoldItalic", "NotoSans-BoldItalic", "arialbi"],
        [FaceNames.MonoRegular] = ["JetBrainsMono-Regular", "consola", "DejaVuSansMono", "LiberationMono-Regular", "NotoSansMono-Regular", "cour", "Courier New"]
    };

    private static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc"];

    private readonly Dictionary<string, byte[]> _faces;

    /// <summary>
    /// Loads the available faces.
    /// </summary>
    /// <param name="fontDirectory">
    /// Directory to search first, or <see langword="null"/> for <c>Fonts</c> beside the assembly.
    /// </param>
    public ExportFontResolver(string? fontDirectory = null)
    {
        var configuredDirectory = fontDirectory ?? Path.Combine(AppContext.BaseDirectory, "Fonts");

        // The two sources are all-or-nothing rather than per-role, and deliberately. Filling a
        // missing bold face from the platform while the body face came from the configured directory
        // sets one document in two unrelated typefaces — an operator who supplied a face expects the
        // document to be in it, and would rather see unemboldened text than a second family.
        var faces = Load(
            [configuredDirectory],
            face => [ConfiguredNames[face]],
            recurse: false);

        if (!faces.ContainsKey(FaceNames.SansRegular))
        {
            faces = Load(PlatformDirectories(), face => PlatformNames[face], recurse: true);
        }

        _faces = faces;
    }

    private static Dictionary<string, byte[]> Load(
        List<string> directories, Func<string, IReadOnlyList<string>> stemsFor, bool recurse)
    {
        Dictionary<string, byte[]> faces = new(StringComparer.Ordinal);

        foreach (var face in ConfiguredNames.Keys)
        {
            var path = FindFace(directories, stemsFor(face), recurse);

            if (path is null)
            {
                continue;
            }

            try
            {
                // Read once, at construction: the resolver is a process-lifetime singleton and a PDF
                // asks for the same face on every glyph run.
                faces[face] = File.ReadAllBytes(path);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // A face this process cannot read is a face it does not have. Anything else — and
                // the regular face in particular — decides IsUsable, which is the honest answer.
            }
        }

        return faces;
    }

    /// <summary>
    /// Gets whether enough faces were found to render a PDF at all.
    /// </summary>
    /// <remarks>
    /// The regular sans face alone, because every other role degrades to it. Without that one there
    /// is no PDF: PDFsharp throws on the first glyph rather than substituting.
    /// </remarks>
    public bool IsUsable => _faces.ContainsKey(FaceNames.SansRegular);

    /// <summary>
    /// Gets the face roles that were resolved, for the startup log.
    /// </summary>
    public IReadOnlyCollection<string> ResolvedFaces => _faces.Keys;

    /// <inheritdoc />
    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var mono = familyName.Equals(ExportFontFamilies.Mono, StringComparison.OrdinalIgnoreCase);

        var preferred = mono
            ? FaceNames.MonoRegular
            : (bold, italic) switch
            {
                (true, true) => FaceNames.SansBoldItalic,
                (true, false) => FaceNames.SansBold,
                (false, true) => FaceNames.SansItalic,
                _ => FaceNames.SansRegular
            };

        // Fall back along the same axis the request came in on, ending at the regular face. The
        // italic flag is passed to PDFsharp when the substitute is upright, because slanting a
        // regular face is implemented; emboldening one is not.
        foreach (var candidate in FallbackChain(preferred, mono))
        {
            if (_faces.ContainsKey(candidate))
            {
                var simulateItalic = italic && candidate is FaceNames.SansRegular or FaceNames.SansBold or FaceNames.MonoRegular;

                return new FontResolverInfo(candidate, mustSimulateBold: false, mustSimulateItalic: simulateItalic);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public byte[]? GetFont(string faceName) =>
        _faces.TryGetValue(faceName, out var bytes) ? bytes : null;

    private static IEnumerable<string> FallbackChain(string preferred, bool mono)
    {
        yield return preferred;

        if (mono)
        {
            // A deployment with no monospace face still renders code, in the body face. Losing the
            // distinction is better than losing the export.
            yield return FaceNames.SansRegular;

            yield break;
        }

        switch (preferred)
        {
            case FaceNames.SansBoldItalic:
                yield return FaceNames.SansBold;
                yield return FaceNames.SansItalic;
                break;

            case FaceNames.SansBold:
            case FaceNames.SansItalic:
                break;

            default:
                yield break;
        }

        yield return FaceNames.SansRegular;
    }

    private static List<string> PlatformDirectories()
    {
        List<string> directories = [];

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            directories.Add(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
        }
        else
        {
            directories.AddRange(
            [
                "/usr/share/fonts",
                "/usr/local/share/fonts",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts"),
                // macOS, so a developer on one is not the odd case out.
                "/Library/Fonts",
                "/System/Library/Fonts"
            ]);
        }

        return directories;
    }

    private static string? FindFace(List<string> directories, IReadOnlyList<string> stems, bool recurse)
    {
        foreach (var directory in directories)
        {
            if (directory.Length == 0 || !Directory.Exists(directory))
            {
                continue;
            }

            var found = FindInDirectory(directory, stems, recurse);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string? FindInDirectory(string directory, IReadOnlyList<string> stems, bool recurse)
    {
        foreach (var stem in stems)
        {
            foreach (var extension in FontExtensions)
            {
                var direct = Path.Combine(directory, stem + extension);

                if (File.Exists(direct))
                {
                    return direct;
                }
            }

            if (!recurse)
            {
                continue;
            }

            foreach (var extension in FontExtensions)
            {
                string[] matches;

                try
                {
                    matches = Directory.GetFiles(directory, stem + extension, SearchOption.AllDirectories);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    // A font directory this process cannot read is not a failure to resolve fonts; it
                    // is one candidate location that did not answer.
                    continue;
                }

                if (matches.Length > 0)
                {
                    // Ordered so the same machine resolves the same file on every start, rather than
                    // whatever the file system happened to enumerate first.
                    Array.Sort(matches, StringComparer.OrdinalIgnoreCase);

                    return matches[0];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The face keys this resolver hands to PDFsharp and reads back in <see cref="GetFont"/>.
    /// </summary>
    internal static class FaceNames
    {
        public const string SansRegular = "export-sans-regular";
        public const string SansBold = "export-sans-bold";
        public const string SansItalic = "export-sans-italic";
        public const string SansBoldItalic = "export-sans-bold-italic";
        public const string MonoRegular = "export-mono-regular";
    }
}
