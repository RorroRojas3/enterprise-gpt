using System.Runtime.CompilerServices;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Reaches the solution's own C# source, for the guards that are about what the code does
/// <em>not</em> contain.
/// </summary>
/// <remarks>
/// Located through <see cref="CallerFilePathAttribute"/> rather than the working directory, which a
/// runner does not have to set — the same technique the File Agent spike uses to find its evidence
/// files. Meaningless off the machine that compiled the assembly, hence the existence check.
/// </remarks>
internal static class SolutionSource
{
    /// <summary>
    /// Enumerates the shipped C# files: build output excluded, and the test projects with it.
    /// </summary>
    /// <returns>Absolute paths.</returns>
    /// <remarks>
    /// Tests are excluded because a guard that names the thing it forbids would otherwise match
    /// itself, and because a string in a test is not a call site in the product.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The source tree this assembly was built from is absent.</exception>
    public static IEnumerable<string> ProductionFiles() =>
        Directory.EnumerateFiles(Root(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !Excluded(path, "obj") && !Excluded(path, "bin") && !Excluded(path, "tests"));

    /// <summary>Finds every shipped C# file mentioning a string.</summary>
    /// <param name="text">The text to search for.</param>
    /// <returns>The solution-relative paths of the files that contain it.</returns>
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

    private static bool Excluded(string path, string segment) =>
        path.Contains($"{Path.DirectorySeparatorChar}{segment}{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string Root([CallerFilePath] string thisFile = "")
    {
        // ...\tests\Enterprise.Gpt.Unit.Test\TestInfrastructure -> the solution directory
        var directory = new FileInfo(thisFile).Directory?.Parent?.Parent?.Parent;

        return directory is { Exists: true }
            ? directory.FullName
            : throw new InvalidOperationException(
                $"The solution source this assembly was compiled from is not present at '{thisFile}'. "
                + "These guards read the source tree, so they only run from a checkout of the repository.");
    }
}
