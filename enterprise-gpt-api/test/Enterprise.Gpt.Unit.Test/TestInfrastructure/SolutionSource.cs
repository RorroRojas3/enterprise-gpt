using System.Runtime.CompilerServices;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Reaches the solution's own C# source, for the guards that are about what the code does
/// <em>not</em> contain.
/// </summary>
/// <remarks>
/// Located via <see cref="CallerFilePathAttribute"/> because a runner need not set the working directory;
/// meaningless off the machine that compiled the assembly, hence the existence check.
/// </remarks>
internal static class SolutionSource
{
    /// <summary>
    /// Absolute paths of the shipped C# files. Build output and tests are excluded: a guard naming what it
    /// forbids would otherwise match itself.
    /// </summary>
    /// <exception cref="InvalidOperationException">The source tree this assembly was built from is absent.</exception>
    public static IEnumerable<string> ProductionFiles()
    {
        var root = Root();

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Excluded(root, path, "obj") && !Excluded(root, path, "bin") && !Excluded(root, path, "test"));
    }

    /// <summary>Solution-relative paths of the shipped C# files containing <paramref name="text"/>.</summary>
    public static IReadOnlyList<string> FilesContaining(string text)
    {
        var root = Root();

        return
        [
            .. ProductionFiles()
                .Where(path => File.ReadAllText(path).Contains(text, StringComparison.Ordinal))
                .Select(path => Path.GetRelativePath(root, path))
                .Order(StringComparer.Ordinal)
        ];
    }

    // Relative to the root, so a checkout that itself sits under a "test" or "bin" folder is not excluded whole.
    private static bool Excluded(string root, string path, string segment) =>
        Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Contains(segment, StringComparer.Ordinal);

    private static string Root([CallerFilePath] string thisFile = "")
    {
        // ...\test\Enterprise.Gpt.Unit.Test\TestInfrastructure -> the solution directory
        var directory = new FileInfo(thisFile).Directory?.Parent?.Parent?.Parent;

        return directory is { Exists: true }
            ? directory.FullName
            : throw new InvalidOperationException(
                $"The solution source this assembly was compiled from is not present at '{thisFile}'. "
                + "These guards read the source tree, so they only run from a checkout of the repository.");
    }
}
