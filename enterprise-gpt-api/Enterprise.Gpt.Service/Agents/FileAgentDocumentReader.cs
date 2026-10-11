using System.Text.RegularExpressions;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Repository.Blob;
using Enterprise.Gpt.Repository.Blob.Options;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Agents.Models;
using Enterprise.Gpt.Service.Documents;
using Enterprise.Gpt.Service.Documents.Retrieval;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>Reads the files a File Agent run is allowed to work from.</summary>
/// <remarks>
/// Wider than <see cref="IDocumentRetrievalService"/>: the agent must see generated documents to edit them.
/// Ownership is read off the parent conversation, as the download route does.
/// </remarks>
public interface IFileAgentDocumentReader
{
    /// <summary>Lists the files available to a run, newest last.</summary>
    /// <returns>The file names, or empty when the conversation holds none or is not the caller's.</returns>
    Task<IReadOnlyList<string>> ListNamesAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds which available file names occur in an instruction, case-insensitively, without reading them.
    /// </summary>
    Task<FileAgentSourceResolution> ResolveAsync(
        Guid conversationId, Guid userId, string instruction, CancellationToken cancellationToken);

    /// <summary>Reads resolved documents, re-applying the ownership rule instead of trusting ids.</summary>
    /// <returns>The files in the requested (mount) order, skipping any that is no longer live.</returns>
    Task<IReadOnlyList<FileAgentSourceFile>> ReadAsync(
        Guid conversationId, Guid userId, IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class FileAgentDocumentReader(
    ILogger<FileAgentDocumentReader> logger,
    IDocumentStore documentStore,
    IOptionsMonitor<BlobStorageOptions> storageOptions,
    EnterpriseGptDbContext ctx) : IFileAgentDocumentReader
{
    private readonly ILogger<FileAgentDocumentReader> _logger = logger;
    private readonly IDocumentStore _documentStore = documentStore;
    private readonly IOptionsMonitor<BlobStorageOptions> _storageOptions = storageOptions;
    private readonly EnterpriseGptDbContext _ctx = ctx;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListNamesAsync(
        Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        var files = await QueryAsync(conversationId, userId, cancellationToken).ConfigureAwait(false);

        return [.. files.Select(file => file.Name)];
    }

    /// <inheritdoc />
    public async Task<FileAgentSourceResolution> ResolveAsync(
        Guid conversationId, Guid userId, string instruction, CancellationToken cancellationToken)
    {
        var candidates = await QueryAsync(conversationId, userId, cancellationToken).ConfigureAwait(false);
        List<string> available = [.. candidates.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase)];

        if (string.IsNullOrWhiteSpace(instruction))
        {
            return new FileAgentSourceResolution([], [], [], available);
        }

        // Longest first: "report.docx" occurring inside "final report.docx" would otherwise match the
        // shorter name and mount the wrong file.
        var mentioned = candidates
            .Where(file => instruction.Contains(file.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.Name.Length)
            .ToList();

        // Nothing enforces unique names, so a name two live documents share is ambiguous.
        List<string> ambiguous =
        [
            .. mentioned
                .GroupBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
        ];

        List<FileAgentSourceMatch> matched =
        [
            .. mentioned
                .Where(file => !ambiguous.Contains(file.Name, StringComparer.OrdinalIgnoreCase))
                .Select(file => new FileAgentSourceMatch(file.Id, file.Name, NormalizeExtension(file)))
        ];

        // Only file-name-shaped tokens of a handled format, so an ordinary word never reads as a missing
        // file. A token is only a name's last whitespace-delimited part, hence EndsSegment over equality.
        List<string> unresolved =
        [
            .. FileNameToken().Matches(instruction)
                .Select(match => match.Value)
                .Where(token => !available.Any(name => EndsSegment(name, token)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];

        return new FileAgentSourceResolution(matched, ambiguous, unresolved, available);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileAgentSourceFile>> ReadAsync(
        Guid conversationId, Guid userId, IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documentIds);

        if (documentIds.Count == 0)
        {
            return [];
        }

        var candidates = await QueryAsync(conversationId, userId, cancellationToken).ConfigureAwait(false);
        List<FileAgentSourceFile> read = [];

        foreach (var documentId in documentIds)
        {
            if (candidates.Find(candidate => candidate.Id == documentId) is not { } file)
            {
                continue;
            }

            var container = file.IsGenerated
                ? DocumentStorage.RequireGeneratedContainer(_storageOptions.CurrentValue, _logger)
                : DocumentStorage.RequireDocumentsContainer(_storageOptions.CurrentValue, _logger);

            var bytes = await _documentStore
                .DownloadAsync(container, file.Path, cancellationToken)
                .ConfigureAwait(false);

            read.Add(new FileAgentSourceFile(file.Id, file.Name, file.MimeType, bytes));
        }

        return read;
    }

    private sealed record SourceRef(Guid Id, string Name, string Extension, string MimeType, string Path, bool IsGenerated);

    #region Private methods

    // Authorized off the conversation, as the download route is: a generated file's own UserId is whoever's
    // turn produced it.
    private async Task<List<SourceRef>> QueryAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        await _ctx.ConversationDocuments
            .AsNoTracking()
            .Where(x => x.ConversationId == conversationId
                && x.Conversation.UserId == userId)
            .OrderBy(x => x.DateCreated)
            .Select(x => new SourceRef(
                x.Id, x.Name, x.Extension, x.MimeType, x.Path, x.Type == ConversationDocumentTypes.Generated))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    // A whole whitespace-delimited tail, so "report.docx" is not satisfied by "quarterly-report.docx".
    private static bool EndsSegment(string name, string token) =>
        name.EndsWith(token, StringComparison.OrdinalIgnoreCase)
        && (name.Length == token.Length || char.IsWhiteSpace(name[name.Length - token.Length - 1]));

    /// <summary>Prefers the stored extension, falling back to the name for a row that carries none.</summary>
    private static string NormalizeExtension(SourceRef file)
    {
        var extension = string.IsNullOrWhiteSpace(file.Extension)
            ? Path.GetExtension(file.Name)
            : file.Extension;

        return extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension}".ToLowerInvariant();
    }

    // Anchored on whitespace or a bracket, not \b, so dotted or dashed names arrive whole; producible formats
    // only. A stem is required: a bare "(.docx)" names an output format, not an existing file.
    [GeneratedRegex(
        @"(?<=^|[\s(\[<*""'`])[\p{L}\p{N}][^\s""'`*()\[\]{}<>]*\.(?:docx|xlsx|pptx|pdf|csv|md|txt)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex FileNameToken();

    #endregion
}
