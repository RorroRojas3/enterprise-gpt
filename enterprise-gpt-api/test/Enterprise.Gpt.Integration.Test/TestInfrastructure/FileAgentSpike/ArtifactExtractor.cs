using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Integration.Test.FileAgentSpike;

/// <summary>
/// Walks every content item on a response looking for produced files, on every channel that could
/// conceivably carry one.
/// </summary>
/// <remarks>
/// The SDK documents artifacts as arriving on <see cref="CodeInterpreterToolResultContent"/>; Azure's
/// Responses documentation says the model cites them in the next message's annotations. Both channels
/// are walked because which one a deployment uses is not settled by reading either document. Container
/// ids are hunted as broadly: without that scope a download resolves against the standard Files API
/// and 404s.
/// </remarks>
// Class-scope MEAI001 suppression, as in SandboxSession: these two files are the harness's whole
// contact with the evaluation-only surface.
#pragma warning disable MEAI001
internal static class ArtifactExtractor
{
    /// <summary>The channel recorded for a hosted file on a code interpreter result.</summary>
    public const string CodeInterpreterResultFileChannel = "CodeInterpreterToolResultContent.Outputs/HostedFileContent";

    /// <summary>The channel recorded for inline bytes on a code interpreter result.</summary>
    public const string CodeInterpreterResultDataChannel = "CodeInterpreterToolResultContent.Outputs/DataContent";

    /// <summary>The channel recorded for a hosted file sitting directly on a message.</summary>
    public const string MessageFileChannel = "ChatMessage.Contents/HostedFileContent";

    /// <summary>The channel recorded for a file cited by an annotation.</summary>
    public const string AnnotationChannel = "AIContent.Annotations/CitationAnnotation";

    private const string ContainerIdPropertyName = "container_id";

    private const string ContainerIdMemberName = "ContainerId";

    /// <summary>Extracts every artifact and every observed content shape from a completed response.</summary>
    public static SandboxRun Extract(ChatResponse response)
    {
        List<SandboxArtifact> artifacts = [];
        List<string> logs = [];
        SortedSet<string> kinds = [];
        string? containerId = null;

        foreach (var content in response.Messages.SelectMany(message => message.Contents))
        {
            kinds.Add(Describe(content));
            containerId ??= ContainerIdOf(content);
            containerId ??= content.Annotations?.Select(ContainerIdOf).FirstOrDefault(id => id is not null);

            switch (content)
            {
                case CodeInterpreterToolResultContent result:
                    foreach (var output in result.Outputs ?? [])
                    {
                        kinds.Add($"{nameof(CodeInterpreterToolResultContent)}.Outputs/{Describe(output)}");
                        containerId ??= ContainerIdOf(output);

                        switch (output)
                        {
                            case HostedFileContent file:
                                artifacts.Add(FromHostedFile(file, CodeInterpreterResultFileChannel));
                                break;

                            case TextContent log:
                                logs.Add(log.Text);
                                break;

                            case DataContent data:
                                artifacts.Add(new SandboxArtifact(
                                    CodeInterpreterResultDataChannel,
                                    FileId: null,
                                    data.Name,
                                    data.MediaType,
                                    containerId,
                                    data.Data.ToArray()));
                                break;
                        }
                    }

                    break;

                case HostedFileContent file:
                    artifacts.Add(FromHostedFile(file, MessageFileChannel));
                    break;
            }

            foreach (var annotation in content.Annotations ?? [])
            {
                kinds.Add($"{nameof(AIContent)}.Annotations/{Describe(annotation)}");

                if (annotation is CitationAnnotation { FileId.Length: > 0 } citation)
                {
                    artifacts.Add(new SandboxArtifact(
                        AnnotationChannel,
                        citation.FileId,
                        citation.Title,
                        MediaType: null,
                        ContainerIdOf(annotation) ?? containerId,
                        InlineData: null));
                }
            }
        }

        // One file legitimately appears on several channels; which channels saw it is the finding.
        // Name is in the key because inline artifacts have no file id, and keying on the id alone
        // would collapse the six a conversion run writes into one.
        //
        // The container is stamped on after the walk: an artifact found before the id appeared would
        // otherwise carry a null scope, which is what sends a download to the wrong API.
        var distinct = artifacts
            .GroupBy(artifact => (artifact.FileId, artifact.Channel, artifact.Name))
            .Select(group => group.First())
            .Select(artifact => artifact.ContainerId is null ? artifact with { ContainerId = containerId } : artifact)
            .ToList();

        return new SandboxRun(
            response.Text, distinct, containerId, response.ConversationId, [.. kinds], logs, response);
    }

    private static SandboxArtifact FromHostedFile(HostedFileContent file, string channel) =>
        new(channel, file.FileId, file.Name, file.MediaType, file.Scope, InlineData: null);

    private static string? ContainerIdOf(AIContent content) =>
        (content as HostedFileContent)?.Scope is { Length: > 0 } scope
            ? scope
            : ContainerIdOf(content.AdditionalProperties);

    private static string? ContainerIdOf(AIAnnotation annotation) =>
        ContainerIdOf(annotation.AdditionalProperties) ?? ContainerIdOf(annotation.RawRepresentation);

    private static string? ContainerIdOf(AdditionalPropertiesDictionary? properties) =>
        properties is not null && properties.TryGetValue(ContainerIdPropertyName, out var value)
            ? value?.ToString()
            : null;

    // Reflection because the SDK model behind the annotation is not part of OpenAI 2.12.0's public
    // surface, and Microsoft.Extensions.AI's CitationAnnotation has no member for a container. The
    // property is public on a non-public type, which reflection reads and a cast cannot.
    private static string? ContainerIdOf(object? rawRepresentation) =>
        rawRepresentation?.GetType()
            .GetProperty(ContainerIdMemberName)?
            .GetValue(rawRepresentation) as string;

    // The raw representation CLR type is part of the evidence: it names the SDK model the bridge
    // mapped from, which is what a future reader needs to look up when a shape here surprises them.
    private static string Describe(AIContent content) =>
        content.RawRepresentation is { } raw
            ? $"{content.GetType().Name}({raw.GetType().Name})"
            : content.GetType().Name;

    private static string Describe(AIAnnotation annotation) =>
        annotation.RawRepresentation is { } raw
            ? $"{annotation.GetType().Name}({raw.GetType().Name})"
            : annotation.GetType().Name;
}
#pragma warning restore MEAI001
