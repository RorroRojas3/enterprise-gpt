using System.Collections.Frozen;
using Enterprise.Gpt.Service.Agents;
using Microsoft.Agents.AI;

namespace Enterprise.Gpt.Api.Agents;

/// <summary>
/// Builds the File Agent's skill provider out of the skill directories that shipped with the build.
/// </summary>
/// <remarks>
/// Two levels of trimming, and both matter. <see cref="CreateProvider"/> filters what is
/// <em>advertised</em> to the formats a run actually names, before a token is spent on describing the
/// rest; progressive disclosure then trims what is <em>loaded</em>, because a skill's body is only
/// read when the model calls for it. Either alone is half the mechanism.
/// </remarks>
public static class FileAgentSkills
{
    /// <summary>The directory, relative to the build output, the skills are deployed under.</summary>
    public static readonly string DeployedRoot =
        Path.Combine(AppContext.BaseDirectory, "Agents", "Documents", "Skills");

    /// <summary>The skill every run advertises, whatever it was asked for.</summary>
    private const string VerificationSkill = "artifact-verification";

    /// <summary>
    /// What each skill is about, lower-cased. A skill is advertised when the run's instruction
    /// mentions any of its topics — the formats it authors, or the verb it performs.
    /// </summary>
    /// <remarks>
    /// Matched against the instruction's own words rather than against a parsed request, because the
    /// agent's tool takes one natural-language string and there is nothing else to match on. A run
    /// naming no topic at all advertises everything, which is the safe direction to fail: an
    /// unadvertised skill is one the model cannot know to ask for.
    /// </remarks>
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

    /// <summary>
    /// Lists the skills present under a root, for a startup probe.
    /// </summary>
    /// <param name="root">The directory the skills were deployed to.</param>
    /// <returns>The skill directory names, sorted.</returns>
    /// <exception cref="DirectoryNotFoundException">
    /// The root is absent, or holds no <c>SKILL.md</c> — which is what a missing copy rule looks like.
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

    /// <summary>
    /// Builds a provider for one run, advertising only the skills its instruction implies.
    /// </summary>
    /// <param name="root">The directory the skills were deployed to.</param>
    /// <param name="instruction">
    /// Reads the run's own instruction. A function rather than a string because the provider is built
    /// when the turn's tools are assembled, and the instruction does not exist until the model writes
    /// one — the filter runs later, once per run, and reads it then.
    /// </param>
    /// <param name="loggerFactory">The logger factory the provider reports through.</param>
    /// <returns>A provider the caller owns and must dispose with the run.</returns>
    /// <remarks>
    /// Built fresh per turn rather than cached: the filter closes over this turn's instruction, and a
    /// provider shared across conversations would advertise one turn's formats to another's. Skill
    /// caching is off for the same reason — a cached advertisement would be the first call's answer,
    /// whatever the second call asked for.
    /// <para>
    /// Three things together make host-side execution impossible. <c>AllowedScriptExtensions</c> is
    /// empty, so no file is ever discovered as a script; <c>run_skill_script</c> keeps its approval,
    /// which nothing in a headless turn can answer; and the runner itself is
    /// <see cref="RefuseScriptAsync"/>, which throws. A runner is not optional in practice —
    /// <c>Build()</c> fails with "File-based skill sources require a script runner" without one,
    /// whatever the parameter's documentation says — so refusing is the strongest available
    /// guarantee, and a stricter one than absence because it fails loudly rather than by omission.
    /// </para>
    /// </remarks>
    public static AgentSkillsProvider CreateProvider(
        string root, Func<string> instruction, ILoggerFactory loggerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        // Memoized on the instruction rather than recomputed per skill: the filter is called once for
        // each of the nine, and they all see the same run.
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
                // Without these, every load_skill and read_skill_resource call returns an approval
                // request instead of executing, and a server turn with nobody to answer it stalls
                // until the run's own deadline. run_skill_script keeps its approval, which — with a
                // runner that refuses — is the second half of the guarantee that nothing this agent
                // loads can run outside the sandbox.
                options.DisableLoadSkillApproval = true;
                options.DisableReadSkillResourceApproval = true;
            })
            .UseLoggerFactory(loggerFactory)
            .Build();
    }

    /// <summary>
    /// The script runner, which refuses. Unreachable in practice and loud if it ever is reached.
    /// </summary>
    /// <remarks>
    /// Every line of Python this feature runs, runs in the hosted sandbox. A skill script executing on
    /// the API host would run outside every bound this design has — no per-request container, no
    /// network isolation, no artifact ceiling — so the runner the package insists on is one that
    /// cannot execute anything.
    /// </remarks>
    internal static Task<object?> RefuseScriptAsync(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        System.Text.Json.JsonElement? arguments,
        IServiceProvider? services,
        CancellationToken cancellationToken) =>
        // On the task rather than at the call site, so the refusal reaches the caller the same way
        // whichever way the package invokes the delegate.
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

        // Whole words, not substrings: "text" occurs inside "context" and "md" inside "command",
        // and a topic that matches everything advertises everything, which is the cost this filter
        // exists to avoid. The same split the conversion pre-flight reads its targets with, so the
        // two cannot disagree about where a word ends.
        var words = FileAgentFormats.Words(instruction);

        List<string> matched = [.. _topics
            .Where(pair => pair.Value.Any(words.Contains))
            .Select(pair => pair.Key)];

        return matched.Count == 0
            ? null
            : new[] { VerificationSkill }.Concat(matched).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}
