using Enterprise.Gpt.Api.Agents;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Agents;

public sealed class FileAgentSkillsTests
{
    private static readonly string[] _expected =
    [
        "artifact-verification",
        "csv-tabular",
        "document-comparison",
        "document-conversion",
        "docx-authoring",
        "markdown-text",
        "pdf-authoring",
        "pptx-authoring",
        "xlsx-authoring"
    ];

    [Fact]
    public void Discover_TheBuildOutput_FindsEverySkillTheAgentExpects()
    {
        // The csproj ships these with a glob rather than one entry per file, so this is what stands in
        // for the per-file copy rules the prompt templates use: a skill that stops shipping fails here
        // rather than on the first request that needed it.
        Assert.Equal(_expected, FileAgentSkills.Discover(FileAgentSkills.DeployedRoot));
    }

    [Fact]
    public void Discover_ADirectoryThatDoesNotExist_SaysSoRatherThanReturningNothing()
    {
        var missing = Path.Combine(AppContext.BaseDirectory, "no-skills-here");

        Assert.Throws<DirectoryNotFoundException>(() => FileAgentSkills.Discover(missing));
    }

    [Theory]
    [InlineData("Make me a spreadsheet of these numbers", "xlsx-authoring")]
    [InlineData("Turn my notes into a deck", "pptx-authoring")]
    [InlineData("Give me this as a PDF", "pdf-authoring")]
    [InlineData("What changed between these two?", "document-comparison")]
    public void Select_AnInstructionNamingOneFormat_AdvertisesThatSkill(string instruction, string expected)
    {
        var selected = FileAgentSkills.Select(instruction);

        Assert.NotNull(selected);
        Assert.Contains(expected, (IReadOnlySet<string>)selected);
    }

    [Fact]
    public void Select_AnInstructionNamingOneFormat_LeavesTheOtherFormatsUnadvertised()
    {
        // The point of filtering: the roughly-100-token advertisement of every other format skill is
        // not spent on a run that cannot use it.
        var selected = FileAgentSkills.Select("Make me a spreadsheet of these numbers");

        Assert.NotNull(selected);
        Assert.DoesNotContain("pptx-authoring", (IReadOnlySet<string>)selected);
        Assert.DoesNotContain("docx-authoring", (IReadOnlySet<string>)selected);
    }

    [Fact]
    public void Select_AnyInstructionThatMatched_KeepsVerificationAdvertised()
    {
        var selected = FileAgentSkills.Select("Give me this as a PDF");

        Assert.NotNull(selected);
        Assert.Contains("artifact-verification", (IReadOnlySet<string>)selected);
    }

    [Theory]
    [InlineData("Run this command for me")]
    [InlineData("Given the context, what do you think?")]
    public void Select_AWordThatMerelyContainsATopic_DoesNotAdvertiseOnIt(string instruction)
    {
        // "md" inside "command" and "text" inside "context" would otherwise advertise the markdown
        // skill on almost every turn, which is the cost this filter exists to avoid.
        Assert.Null(FileAgentSkills.Select(instruction));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Do the thing we discussed")]
    public void Select_AnInstructionNamingNoFormat_AdvertisesEverything(string instruction)
    {
        // Null means "no filter". Advertising nothing would be the worse failure: a skill the model
        // was never told about is one it cannot ask for.
        Assert.Null(FileAgentSkills.Select(instruction));
    }

    [Fact]
    public void CreateProvider_TwoRuns_GetDistinctProviders()
    {
        // Per turn, never cached: the filter closes over one run's instruction, and a shared provider
        // would advertise one conversation's formats to another's.
        using var first = FileAgentSkills.CreateProvider(
            FileAgentSkills.DeployedRoot, () => "make a spreadsheet", NullLoggerFactory.Instance);
        using var second = FileAgentSkills.CreateProvider(
            FileAgentSkills.DeployedRoot, () => "make a deck", NullLoggerFactory.Instance);

        Assert.NotSame(first, second);
    }

    [Theory]
    [MemberData(nameof(SkillDirectories))]
    public void SkillFrontmatter_NamesTheSkillAfterItsDirectory(string directory)
    {
        // The filter matches on the frontmatter name and the probe lists directories. A skill whose
        // two names disagree is one that ships, passes discovery, and is never advertised.
        var body = File.ReadAllLines(Path.Combine(FileAgentSkills.DeployedRoot, directory, "SKILL.md"));
        var name = body
            .FirstOrDefault(line => line.StartsWith("name:", StringComparison.Ordinal))?
            .Split(':', 2)[1]
            .Trim();

        Assert.Equal(directory, name);
    }

    public static TheoryData<string> SkillDirectories() => [.. _expected];

    [Fact]
    public void Solution_RegistersExactlyOneSkillScriptRunner_AndItRefuses()
    {
        // The package will not build a file-skill provider with no runner at all, so "none is
        // registered" is unreachable and this is the guard that replaces it: one call site, and the
        // runner it passes cannot execute anything.
        Assert.Equal(
            [Path.Combine("Enterprise.Gpt.Api", "Agents", "FileAgentSkills.cs")],
            SolutionSource.FilesContaining("RefuseScriptAsync)"));
    }

    [Fact]
    public async Task RefuseScriptAsync_Invoked_RefusesRatherThanRunningAnything()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => FileAgentSkills.RefuseScriptAsync(
            skill: null!, script: null!, arguments: null, services: null, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Azure.AI.Projects")]
    [InlineData("AIProjectClient")]
    public void Solution_ReachesTheSandboxThroughTheResponsesRouteAlone(string forbidden)
    {
        // The design decision that keeps container isolation per request: a toolbox-hosted code
        // interpreter shares one container across every user in a project.
        Assert.Empty(SolutionSource.FilesContaining(forbidden));
    }
}
