using Enterprise.Gpt.Service.Agents.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>Moves files in and out of the hosted code interpreter.</summary>
/// <remarks>
/// The Responses bridge silently drops raw bytes on <see cref="HostedCodeInterpreterTool.Inputs"/>, so inputs
/// go through the Files API; outputs can arrive on citations, so the harvest walks every channel. See
/// <c>docs/tools/file-agent.md</c>.
/// </remarks>
// Hosted files are experimental in Microsoft.Extensions.AI 10.9.0; suppressed per file, not project-wide.
#pragma warning disable MEAI001
internal sealed partial class FileAgentSandbox(IHostedFileClient files, ILogger logger)
{
    /// <summary>The purpose an uploaded input is registered under, which is what makes it mountable.</summary>
    private const string UploadPurpose = "assistants";

    private readonly IHostedFileClient _files = files;
    private readonly ILogger _logger = logger;
    private readonly List<string> _uploaded = [];

    /// <summary>Uploads a file and returns the reference the sandbox can be given.</summary>
    public async Task<HostedFileContent> UploadAsync(
        string fileName, string mediaType, byte[] bytes, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(bytes, writable: false);

        var uploaded = await _files.UploadAsync(
                stream, mediaType, fileName, new HostedFileClientOptions { Purpose = UploadPurpose }, cancellationToken)
            .ConfigureAwait(false);

        _uploaded.Add(uploaded.FileId);

        return uploaded;
    }

    /// <summary>Downloads an artifact the sandbox produced.</summary>
    /// <exception cref="InvalidOperationException">The artifact carries no container to read it from.</exception>
    public async Task<byte[]> DownloadAsync(SandboxArtifact artifact, CancellationToken cancellationToken)
    {
        if (artifact.ContainerId is not { Length: > 0 } scope)
        {
            // Loud rather than best-effort: without the scope this call resolves against the standard
            // Files API and 404s, and a silent empty file is worse than a named failure.
            throw new InvalidOperationException(
                "The sandbox produced a file whose container could not be determined, so it cannot be read back.");
        }

        using var stream = await _files
            .DownloadAsync(artifact.FileId, new HostedFileClientOptions { Scope = scope }, cancellationToken)
            .ConfigureAwait(false);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return buffer.ToArray();
    }

    /// <summary>Deletes every file this run uploaded. Idempotent, and never throws.</summary>
    /// <remarks>
    /// Best effort by design: a leak costs file quota and must not fail a turn that has already
    /// answered. Logged rather than swallowed, because nothing else would notice.
    /// </remarks>
    public async Task ReleaseAsync()
    {
        foreach (var fileId in _uploaded)
        {
            try
            {
                await _files.DeleteAsync(fileId).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogCouldNotDeleteSandboxInput(_logger, exception, fileId);
            }
        }

        _uploaded.Clear();
    }

    #region Private methods

    private static string? ContainerIdOf(AIContent content) =>
        content is HostedFileContent { Scope: { Length: > 0 } scope } ? scope : ContainerIdOf(content.AdditionalProperties);

    private static string? ContainerIdOf(AIAnnotation annotation) =>
        ContainerIdOf(annotation.AdditionalProperties) ?? ContainerIdOf(annotation.RawRepresentation);

    private static string? ContainerIdOf(AdditionalPropertiesDictionary? properties) =>
        properties is not null && properties.TryGetValue("container_id", out var value) ? value?.ToString() : null;

    // Reflection: the annotation's SDK model is non-public in OpenAI 2.12.0, and CitationAnnotation
    // exposes no container.
    private static string? ContainerIdOf(object? rawRepresentation) =>
        rawRepresentation?.GetType().GetProperty("ContainerId")?.GetValue(rawRepresentation) as string;

    #endregion

    #region Public static methods

    /// <summary>Finds every file a completed run produced.</summary>
    /// <returns>Each artifact once, in the order the response mentions it.</returns>
    public static IReadOnlyList<SandboxArtifact> Harvest(IEnumerable<ChatMessage> messages)
    {
        List<SandboxArtifact> artifacts = [];
        string? containerId = null;

        foreach (var content in messages.SelectMany(message => message.Contents))
        {
            containerId ??= ContainerIdOf(content);

            switch (content)
            {
                case CodeInterpreterToolResultContent result:
                    foreach (var output in result.Outputs ?? [])
                    {
                        containerId ??= ContainerIdOf(output);

                        if (output is HostedFileContent produced)
                        {
                            artifacts.Add(new SandboxArtifact(produced.FileId, produced.Name, produced.Scope));
                        }
                    }

                    break;

                case HostedFileContent file:
                    artifacts.Add(new SandboxArtifact(file.FileId, file.Name, file.Scope));
                    break;
            }

            foreach (var annotation in content.Annotations ?? [])
            {
                if (annotation is CitationAnnotation { FileId.Length: > 0 } citation)
                {
                    artifacts.Add(new SandboxArtifact(
                        citation.FileId, citation.Title, ContainerIdOf(annotation) ?? containerId));
                }
            }
        }

        // A file can appear on several channels carrying different parts (a missing name, or no scope, which
        // sends its download to the wrong API), so sightings are merged rather than first-wins.
        return
        [
            .. artifacts
                .GroupBy(artifact => artifact.FileId, StringComparer.Ordinal)
                .Select(group => group.First() with
                {
                    Name = group.Select(x => x.Name).FirstOrDefault(name => !string.IsNullOrEmpty(name)),
                    ContainerId = group.Select(x => x.ContainerId).FirstOrDefault(id => !string.IsNullOrEmpty(id)) ?? containerId
                })
        ];
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the sandbox input {FileId}; it remains against the account's quota")]
    private static partial void LogCouldNotDeleteSandboxInput(ILogger logger, Exception exception, string fileId);

    #endregion
}
