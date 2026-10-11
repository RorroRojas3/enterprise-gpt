using Enterprise.Gpt.Service.Agents.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>
/// Moves files in and out of the hosted code interpreter.
/// </summary>
/// <remarks>
/// The mechanism here was established against a live deployment rather than from documentation, and
/// two of its findings are load-bearing. Raw bytes on <see cref="HostedCodeInterpreterTool.Inputs"/>
/// are <em>silently discarded</em> by the Responses bridge, so an input has to be uploaded to the
/// Files API first and referenced by id. And a produced file was surfaced on the message's citation
/// annotations rather than on the code interpreter's own result content, so the harvest walks every
/// channel that can carry one instead of the single documented one. See
/// <c>docs/tools/file-agent.md</c>.
/// </remarks>
// The hosted-file surface is still marked experimental in Microsoft.Extensions.AI 10.9.0. Suppressed
// on this one file rather than project-wide, so a second experimental API cannot slip in behind it.
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

    // Reflection because the SDK model behind the annotation is not on OpenAI 2.12.0's public surface,
    // and CitationAnnotation has no member for a container. The property is public on a non-public
    // type, which reflection reads and a cast cannot.
    private static string? ContainerIdOf(object? rawRepresentation) =>
        rawRepresentation?.GetType().GetProperty("ContainerId")?.GetValue(rawRepresentation) as string;

    #endregion

    #region Public static methods

    /// <summary>Finds every file a completed run produced.</summary>
    /// <param name="messages">The run's own messages.</param>
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

        // One file legitimately appears on more than one channel, and the channels do not agree on
        // what they carry: a citation may name no file, and an artifact seen before the container id
        // appeared carries no scope — which is what sends its download to the wrong API. So the
        // sightings are merged rather than deduped to whichever came first.
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
