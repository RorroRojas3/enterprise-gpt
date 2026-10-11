using System.Runtime.InteropServices;
using Enterprise.Gpt.Service.Exports.Fonts.Constants;
using PdfSharp.Fonts;

namespace Enterprise.Gpt.Service.Exports.Fonts;

/// <summary>
/// Supplies PDFsharp with the font files a conversation export is drawn with.
/// </summary>
/// <remarks>
/// PDFsharp ships no fonts, so when <see cref="IsUsable"/> is false the PDF renderer is never registered and
/// the route answers 503. Faces are found by fixed file name in the configured directory, then the OS font
/// directories, and bold is never simulated, as <see cref="FontResolverInfo"/> requires.
/// </remarks>
public sealed class ExportFontResolver : IFontResolver
{
    /// <summary>
    /// Role-named files in the configured directory (see <c>Service/Exports/Fonts/README.md</c>), so swapping
    /// the brand face is a file copy rather than a code change.
    /// </summary>
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
    /// Not PDFsharp's <c>UseWindowsFontsUnderWindows</c>, which is Windows-only and would be a second
    /// resolution path to keep in step.
    /// </remarks>
    private static readonly Dictionary<string, string[]> PlatformNames = new(StringComparer.Ordinal)
    {
        [FaceNames.SansRegular] = ["Inter-Regular", "segoeui", "DejaVuSans", "LiberationSans-Regular", "NotoSans-Regular", "arial", "Arial", "Helvetica"],
        [FaceNames.SansBold] = ["Inter-Bold", "segoeuib", "DejaVuSans-Bold", "LiberationSans-Bold", "NotoSans-Bold", "arialbd", "Arial Bold"],
        [FaceNames.SansItalic] = ["Inter-Italic", "segoeuii", "DejaVuSans-Oblique", "LiberationSans-Italic", "NotoSans-Italic", "ariali", "Arial Italic"],
        [FaceNames.SansBoldItalic] = ["Inter-BoldItalic", "segoeuiz", "DejaVuSans-BoldOblique", "LiberationSans-BoldItalic", "NotoSans-BoldItalic", "arialbi"],
        [FaceNames.MonoRegular] = ["JetBrainsMono-Regular", "consola", "DejaVuSansMono", "LiberationMono-Regular", "NotoSansMono-Regular", "cour", "Courier New"]
    };

    private static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc"];

    private readonly Dictionary<string, byte[]> _faces;

    public ExportFontResolver(string? fontDirectory = null)
    {
        var configuredDirectory = fontDirectory ?? Path.Combine(AppContext.BaseDirectory, "Fonts");

        // All or nothing per source, not per role: a platform bold beside a configured body face would set
        // one document in two typefaces, and unemboldened text is the better failure.
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

    /// <summary>
    /// Whether the regular sans face, which every other role degrades to, was found.
    /// </summary>
    public bool IsUsable => _faces.ContainsKey(FaceNames.SansRegular);

    /// <summary>The resolved face roles, for the startup log.</summary>
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

        // Fall back along the requested axis to regular. Italic is simulated on an upright substitute
        // because PDFsharp implements slanting, but not emboldening.
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

    #region Private methods

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
                // An unreadable face is a missing one; IsUsable reports the consequence.
            }
        }

        return faces;
    }

    private static IEnumerable<string> FallbackChain(string preferred, bool mono)
    {
        yield return preferred;

        if (mono)
        {
            // With no monospace face, code renders in the body face rather than losing the export.
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
                // macOS developer machines.
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
                    // An unreadable directory is one candidate that did not answer, not a failure.
                    continue;
                }

                if (matches.Length > 0)
                {
                    // Sorted so the same machine resolves the same file on every start.
                    Array.Sort(matches, StringComparer.OrdinalIgnoreCase);

                    return matches[0];
                }
            }
        }

        return null;
    }

    #endregion
}
