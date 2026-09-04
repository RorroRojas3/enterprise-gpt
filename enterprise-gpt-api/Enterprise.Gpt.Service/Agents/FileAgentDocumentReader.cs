using System.Text.RegularExpressions;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>A file the File Agent may read, and its bytes.</summary>
/// <param name="Name">The file name as the user knows it, which is also the name in the sandbox.</param>
public sealed record FileAgentSourceFile(Guid Id, string Name, string MimeType, byte[] Content);

/// <summary>One document an instruction named, before its bytes are read.</summary>
/// <param name="Extension">The lower-cased extension including its dot, which the matrix is keyed on.</param>
public sealed record FileAgentSourceMatch(Guid Id, string Name, string Extension);

/// <summary>
/// Which of a conversation's files an instruction named, and which of its names cannot be acted on.
/// </summary>
/// <param name="Matched">The documents named, longest name first, each named exactly once.</param>
/// <param name="Ambiguous">
/// Names the instruction used that belong to more than one live document. Acting on one of them would
/// be a guess, so the run asks instead.
/// </param>
/// <param name="Unresolved">File names the instruction used that match nothing in the conversation.</param>
/// <param name="Available">Every file the run could work from, for the sentence a refusal ends on.</param>
public sealed record FileAgentSourceResolution(
    IReadOnlyList<FileAgentSourceMatch> Matched,
    IReadOnlyList<string> Ambiguous,
    IReadOnlyList<string> Unresolved,
    IReadOnlyList<string> Available);

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
    /// Works out which files an instruction named, without reading any of them.
    /// </summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="userId">The caller.</param>
    /// <param name="instruction">The agent's instruction, matched case-insensitively.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>What the instruction named, and what about it cannot be acted on.</returns>
    /// <remarks>
    /// The reverse of <c>DocumentRetrievalService.MatchByName</c>, which answers "which document did
    /// the model name". Here nothing names a document: the tool takes one natural-language string, so
    /// the question is which of the available names occur in it.
    /// </remarks>
    Task<FileAgentSourceResolution> ResolveAsync(
        Guid conversationId, Guid userId, string instruction, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the bytes of documents already resolved for this conversation.
    /// </summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="userId">The caller.</param>
    /// <param name="documentIds">The documents to read, in the order they should be mounted.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The files with their bytes, in the requested order, skipping any that is no longer live.</returns>
    /// <remarks>
    /// Re-applies the ownership rule rather than trusting the identifiers, so the read is authorized on
    /// its own terms whatever resolved them.
    /// </remarks>
    Task<IReadOnlyList<FileAgentSourceFile>> ReadAsync(
        Guid conversationId, Guid userId, IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class FileAgentDocumentReader(
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

        // Two live documents can carry the same name — nothing enforces uniqueness — so a name the
        // instruction uses that belongs to both is a genuine ambiguity rather than a fuzzy near-miss.
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

        // Only tokens that already look like a file name of a format this platform handles. Anything
        // looser would read an ordinary word as a missing file and refuse a request nothing was wrong
        // with.
        //
        // Matched against a whole segment of an available name, not any substring of one: the token is
        // only ever the last whitespace-delimited part ("final report.docx" yields "report.docx"), so
        // equality would call every name with a space missing — while bare containment would let
        // "report.docx" be swallowed by an unrelated "quarterly-report.docx" and leave the run with
        // nothing matched and nothing refused.
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
                x.Id, x.Name, x.Extension, x.MimeType, x.Path, x.Type == ConversationDocumentTypes.Generated))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Whether a token is a whole whitespace-delimited tail of a name.</summary>
    /// <remarks>
    /// The whitespace boundary is what keeps "report.docx" from being satisfied by an unrelated
    /// "quarterly-report.docx", which would leave the run with nothing mounted and nothing refused.
    /// Reaching the end matters too: without it "notes.txt" is satisfied by "notes.txt.docx".
    /// </remarks>
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

    // Anchored on whitespace or an opening bracket rather than \b so a name with a dot or a dash in
    // it arrives whole; the seven producible formats only, because those are the pairs the matrix can
    // answer for. A stem is required, because a bare format mention — "(.docx)", "**.docx**" — names
    // the format an answer should be in, not a file that has to already exist.
    [GeneratedRegex(
        @"(?<=^|[\s(\[<*""'`])[\p{L}\p{N}][^\s""'`*()\[\]{}<>]*\.(?:docx|xlsx|pptx|pdf|csv|md|txt)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex FileNameToken();

    private sealed record SourceRef(Guid Id, string Name, string Extension, string MimeType, string Path, bool IsGenerated);
}
