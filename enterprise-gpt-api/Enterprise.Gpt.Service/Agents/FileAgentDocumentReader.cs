using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>A file the File Agent may read, and its bytes.</summary>
/// <param name="Name">The file name as the user knows it, which is also the name in the sandbox.</param>
public sealed record FileAgentSourceFile(Guid Id, string Name, string MimeType, byte[] Content);

/// <summary>
/// Reads the files a File Agent run is allowed to work from.
/// </summary>
/// <remarks>
/// Separate from <see cref="Tool.IDocumentRetrievalService"/>'s scope, and deliberately wider than it:
/// retrieval must never see a generated document, while the agent must, or it could not edit or
/// convert the file it made an hour ago. Both apply the same ownership rule, read off the parent
/// conversation exactly as the download route does.
/// </remarks>
public interface IFileAgentDocumentReader
{
    /// <summary>Lists the files available to a run, newest last.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="userId">The caller.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The file names, or empty when the conversation holds none or is not the caller's.</returns>
    Task<IReadOnlyList<string>> ListNamesAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the files whose names appear verbatim in an instruction.
    /// </summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="userId">The caller.</param>
    /// <param name="instruction">The agent's instruction, matched case-insensitively.</param>
    /// <param name="maxFiles">How many matches to read at most.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The matched files with their bytes, or empty when nothing matched.</returns>
    /// <remarks>
    /// The reverse of <c>DocumentRetrievalService.MatchByName</c>, which answers "which document did
    /// the model name". Here nothing names a document: the tool takes one natural-language string, so
    /// the question is which of the available names occur in it.
    /// </remarks>
    Task<IReadOnlyList<FileAgentSourceFile>> ReadMentionedAsync(
        Guid conversationId, Guid userId, string instruction, int maxFiles, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class FileAgentDocumentReader(
    ILogger<FileAgentDocumentReader> logger,
    IBlobStorageService blobStorageService,
    IConfiguration configuration,
    EnterpriseGptDbContext ctx) : IFileAgentDocumentReader
{
    private readonly ILogger<FileAgentDocumentReader> _logger = logger;
    private readonly IBlobStorageService _blobStorageService = blobStorageService;
    private readonly IConfiguration _configuration = configuration;
    private readonly EnterpriseGptDbContext _ctx = ctx;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListNamesAsync(
        Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        var files = await QueryAsync(conversationId, userId, cancellationToken).ConfigureAwait(false);

        return [.. files.Select(file => file.Name)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileAgentSourceFile>> ReadMentionedAsync(
        Guid conversationId, Guid userId, string instruction, int maxFiles, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFiles);

        if (string.IsNullOrWhiteSpace(instruction))
        {
            return [];
        }

        var candidates = await QueryAsync(conversationId, userId, cancellationToken).ConfigureAwait(false);

        // Longest first: "report.docx" occurring inside "final report.docx" would otherwise match the
        // shorter name and mount the wrong file.
        var mentioned = candidates
            .Where(file => instruction.Contains(file.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.Name.Length)
            .Take(maxFiles)
            .ToList();

        List<FileAgentSourceFile> read = [];

        foreach (var file in mentioned)
        {
            var container = DocumentStorage.RequireContainer(
                _configuration,
                _logger,
                file.IsGenerated ? DocumentStorage.GeneratedContainerKey : DocumentStorage.DocumentsContainerKey);

            var bytes = await _blobStorageService
                .DownloadAsync(container, file.Path, cancellationToken)
                .ConfigureAwait(false);

            read.Add(new FileAgentSourceFile(file.Id, file.Name, file.MimeType, bytes));
        }

        return read;
    }

    /// <summary>
    /// Reads the conversation's live files, authorized off the parent conversation.
    /// </summary>
    /// <remarks>
    /// Ownership on the conversation rather than the document row, matching the download route: a
    /// generated file's own <c>UserId</c> is whoever's turn produced it, and the rule that decides
    /// what is readable has to be the one that decided it was visible.
    /// </remarks>
    private async Task<List<SourceRef>> QueryAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        await _ctx.ConversationDocuments
            .AsNoTracking()
            .Where(x => x.ConversationId == conversationId
                && !x.DateDeactivated.HasValue
                && x.Conversation.UserId == userId
                && !x.Conversation.DateDeactivated.HasValue)
            .OrderBy(x => x.DateCreated)
            .Select(x => new SourceRef(
                x.Id, x.Name, x.MimeType, x.Path, x.Type == ConversationDocumentTypes.Generated))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    private sealed record SourceRef(Guid Id, string Name, string MimeType, string Path, bool IsGenerated);
}
