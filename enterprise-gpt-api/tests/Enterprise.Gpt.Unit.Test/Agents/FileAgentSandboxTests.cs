using Enterprise.Gpt.Api.Agents;
using Microsoft.Extensions.AI;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Agents;

/// <summary>
/// What a completed run's messages are read for. Every fact here was established against a live
/// deployment rather than from documentation, which is why it is pinned.
/// </summary>
#pragma warning disable MEAI001
public sealed class FileAgentSandboxTests
{
    private const string Container = "cntr_6a8f9412dc9c81908479fbce55a65c67";
    private const string FileId = "cfile_6a8f948d17e08190a7f336b685bec866";

    [Fact]
    public void Harvest_AnArtifactCitedByAnnotation_IsFoundWithItsContainer()
    {
        // The channel this deployment actually answered on, and the one the SDK's own documentation
        // does not name — a walk that trusted the documented channel alone would find nothing.
        var message = new ChatMessage(ChatRole.Assistant, "Done.");
        message.Contents[0].Annotations =
        [
            new CitationAnnotation
            {
                FileId = FileId,
                Title = "regional-summary.xlsx",
                AdditionalProperties = new AdditionalPropertiesDictionary { ["container_id"] = Container }
            }
        ];

        var artifact = Assert.Single(FileAgentSandbox.Harvest([message]));

        Assert.Equal(FileId, artifact.FileId);
        Assert.Equal("regional-summary.xlsx", artifact.Name);
        Assert.Equal(Container, artifact.ContainerId);
    }

    [Fact]
    public void Harvest_AnArtifactOnTheCodeInterpretersOwnResult_IsFound()
    {
        var result = new CodeInterpreterToolResultContent("call-1")
        {
            Outputs = [new HostedFileContent(FileId) { Name = "notes.md", Scope = Container }]
        };

        var artifact = Assert.Single(FileAgentSandbox.Harvest([new ChatMessage(ChatRole.Assistant, [result])]));

        Assert.Equal(FileId, artifact.FileId);
        Assert.Equal(Container, artifact.ContainerId);
    }

    [Fact]
    public void Harvest_OneFileOnTwoChannels_IsCountedOnce()
    {
        // A produced file legitimately appears on more than one channel. Counting it twice would
        // store it twice and put two chips under one answer.
        var result = new CodeInterpreterToolResultContent("call-1")
        {
            Outputs = [new HostedFileContent(FileId) { Name = "notes.md", Scope = Container }]
        };

        var message = new ChatMessage(ChatRole.Assistant, [result]);
        message.Contents[0].Annotations = [new CitationAnnotation { FileId = FileId, Title = "notes.md" }];

        Assert.Single(FileAgentSandbox.Harvest([message]));
    }

    [Fact]
    public void Harvest_AnArtifactSeenBeforeTheContainerIs_StillCarriesIt()
    {
        // The container is stamped on after the walk. Without that, an artifact found first would
        // carry no scope — and a scopeless download resolves against the standard Files API, which
        // cannot see a file the sandbox wrote.
        var cited = new ChatMessage(ChatRole.Assistant, "Done.");
        cited.Contents[0].Annotations = [new CitationAnnotation { FileId = FileId, Title = "notes.md" }];

        var later = new ChatMessage(ChatRole.Assistant, [new HostedFileContent("other") { Scope = Container }]);

        var artifact = FileAgentSandbox.Harvest([cited, later]).First(x => x.FileId == FileId);

        Assert.Equal(Container, artifact.ContainerId);
    }

    [Fact]
    public void Harvest_AResponseWithNoFile_FindsNothing()
    {
        Assert.Empty(FileAgentSandbox.Harvest([new ChatMessage(ChatRole.Assistant, "I could not make that.")]));
    }
}
#pragma warning restore MEAI001
