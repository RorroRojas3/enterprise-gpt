using System.Collections.Frozen;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>
/// Builds the File Agent's skill provider out of the skill directories that shipped with the build.
/// </summary>
/// <remarks>
/// <see cref="CreateProvider"/> trims what is advertised to the formats a run names; progressive disclosure
/// then trims what is loaded.
/// </remarks>
public static class FileAgentSkills
{
    /// <summary>The directory, relative to the build output, the skills are deployed under.</summary>
    public static readonly string DeployedRoot =
        Path.Combine(AppContext.BaseDirectory, "Agents", "Documents", "Skills");

    /// <summary>The skill every run advertises, whatever it was asked for.</summary>
    private const string VerificationSkill = "artifact-verification";

    // A skill is advertised when the instruction mentions one of its topics. Naming none advertises
    // everything, the safe failure: the model cannot ask for an unadvertised skill.
    private static readonly FrozenDictionary<string, string[]> _topics = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["docx-authoring"] = ["docx", "word", "document", "doc"],
        ["xlsx-authoring"] = ["xlsx", "excel", "spreadsheet", "workbook", "sheet"],
        ["pptx-authoring"] = ["pptx", "powerpoint", "deck", "slide", "slides", "presentation"],
        ["pdf-authoring"] = ["pdf"],
        ["csv-tabular"] = ["csv", "table", "tabular", "rows"],
        ["markdown-text"] = ["md", "markdown", "txt", "text", "plain"],
        ["document-comparison"] = ["compare", "comparison", "diff", "difference", "changed"],
        ["document-conversion"] = ["convert", "conversion", "converted", "export"]
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    #region Public static methods

    /// <summary>Lists the skills present under a root, for a startup probe.</summary>
    /// <returns>The skill directory names, sorted.</returns>
    /// <exception cref="DirectoryNotFoundException">
    /// The root is absent or holds no <c>SKILL.md</c>, which is what a missing copy rule looks like.
    /// </exception>
    public static IReadOnlyList<string> Discover(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"The File Agent's skills were expected at '{root}' and the directory does not exist.");
        }

        List<string> names =
        [
            .. Directory.EnumerateDirectories(root)
                .Where(directory => File.Exists(Path.Combine(directory, "SKILL.md")))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
        ];

        return names.Count > 0
            ? names
            : throw new DirectoryNotFoundException(
                $"The File Agent's skills directory '{root}' holds no SKILL.md, so no skill shipped with this build.");
    }

    /// <summary>Builds a provider for one run, advertising only the skills its instruction implies.</summary>
    /// <returns>A provider the caller owns and must dispose with the run.</returns>
    /// <remarks>
    /// <paramref name="instruction"/> is a function because the model writes it after the tools are built.
    /// Per turn with caching off, so one turn's advertisement never reaches another.
    /// </remarks>
    public static AgentSkillsProvider CreateProvider(
        string root, Func<string> instruction, ILoggerFactory loggerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        // Memoized: the filter runs once per skill, all against the same instruction.
        string? seen = null;
        FrozenSet<string>? advertised = null;

        return new AgentSkillsProviderBuilder()
            .UseFileSkill(
                root,
                new AgentFileSkillsSourceOptions
                {
                    AllowedResourceExtensions = [".md"],
                    AllowedScriptExtensions = []
                },
                RefuseScriptAsync)
            .UseFilter((skill, _) =>
            {
                var current = instruction();

                if (!string.Equals(seen, current, StringComparison.Ordinal))
                {
                    seen = current;
                    advertised = Select(current);
                }

                return advertised is null || advertised.Contains(skill.Frontmatter.Name);
            })
            .DisableCaching()
            .UseOptions(options =>
            {
                // Otherwise these calls wait for an approval nobody in a server turn can give.
                // run_skill_script deliberately keeps its approval (see RefuseScriptAsync).
                options.DisableLoadSkillApproval = true;
                options.DisableReadSkillResourceApproval = true;
            })
            .UseLoggerFactory(loggerFactory)
            .Build();
    }

    /// <summary>The script runner the package insists on (<c>Build()</c> needs one); it refuses.</summary>
    /// <remarks>
    /// All Python runs in the sandbox. With no script extensions allowed, <c>run_skill_script</c> gated by
    /// approval, and this runner throwing, nothing a skill loads can execute on the API host.
    /// </remarks>
    internal static Task<object?> RefuseScriptAsync(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        System.Text.Json.JsonElement? arguments,
        IServiceProvider? services,
        CancellationToken cancellationToken) =>
        // A faulted task, so the refusal arrives the same way however the package invokes the delegate.
        Task.FromException<object?>(new NotSupportedException(
            "A File Agent skill tried to run a script on the API host. Skill scripts are never executed here; "
            + "every line of Python this feature runs, runs in the sandbox."));

    /// <summary>
    /// Chooses the skills to advertise, or <see langword="null"/> to advertise every one of them.
    /// </summary>
    internal static FrozenSet<string>? Select(string? instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return null;
        }

        // Whole words ("text" is inside "context"), split the same way as the conversion pre-flight so the
        // two agree on word boundaries.
        var words = FileAgentFormats.Words(instruction);

        List<string> matched = [.. _topics
            .Where(pair => pair.Value.Any(words.Contains))
            .Select(pair => pair.Key)];

        return matched.Count == 0
            ? null
            : new[] { VerificationSkill }.Concat(matched).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    #endregion
}
