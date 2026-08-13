using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Enterprise.Gpt.Service.Export;

/// <summary>
/// A conversation rendered into a file the caller downloads.
/// </summary>
/// <param name="Content">The file's bytes.</param>
/// <param name="ContentType">The media type to serve it as.</param>
/// <param name="FileName">The name to suggest to the browser.</param>
public sealed record ConversationExportFile(byte[] Content, string ContentType, string FileName);

/// <summary>
/// Takes a conversation out of the platform as a file.
/// </summary>
public interface IConversationExportService
{
    /// <summary>
    /// Exports one of the caller's conversations.
    /// </summary>
    /// <param name="conversationId">The conversation to export.</param>
    /// <param name="format">Either <c>html</c> or <c>json</c>, matched case-insensitively.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>The file to serve.</returns>
    /// <exception cref="ValidationException">The format is missing or unsupported.</exception>
    /// <exception cref="NotFoundException">The conversation is not a conversation of the caller.</exception>
    Task<ConversationExportFile> ExportAsync(Guid conversationId, string? format, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <param name="logger">Receives export failures.</param>
/// <param name="tokenService">Identifies the caller.</param>
/// <param name="cosmosService">Reads the transcript.</param>
/// <param name="ctx">Enforces ownership against the relational row.</param>
public sealed partial class ConversationExportService(
    ILogger<ConversationExportService> logger,
    ITokenService tokenService,
    IAzureCosmosService cosmosService,
    EnterpriseGptDbContext ctx) : IConversationExportService
{
    private const string HtmlFormat = "html";
    private const string JsonFormat = "json";

    /// <summary>
    /// How many message documents an export reads per round trip.
    /// </summary>
    private const int ExportPageSize = 200;

    private readonly ILogger<ConversationExportService> _logger = logger;
    private readonly ITokenService _tokenService = tokenService;
    private readonly IAzureCosmosService _cosmosService = cosmosService;
    private readonly EnterpriseGptDbContext _ctx = ctx;

    /// <inheritdoc />
    public async Task<ConversationExportFile> ExportAsync(Guid conversationId, string? format, CancellationToken cancellationToken)
    {
        var normalizedFormat = (format ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedFormat is not (HtmlFormat or JsonFormat))
        {
            throw new ValidationException(
            [
                new ValidationFailure(nameof(format), $"Supported formats are '{HtmlFormat}' and '{JsonFormat}'.")
            ]);
        }

        var userId = _tokenService.GetOid();

        var conversation = await _ctx.Conversations
            .AsNoTracking()
            .Where(x => x.Id == conversationId && x.UserId == userId && !x.DateDeactivated.HasValue)
            .Select(x => new { x.Id, x.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (conversation is null)
        {
            _logger.LogError("Conversation {Id} not found for export.", conversationId);
            throw new NotFoundException($"Conversation {conversationId} not found.");
        }

        var header = await _cosmosService.GetItemAsync<CosmosConversationHeader>(
            conversationId.ToString(), userId, conversationId, cancellationToken);

        if (header is null)
        {
            _logger.LogError("Conversation {Id} has no transcript header to export.", conversationId);
            throw new NotFoundException($"Conversation {conversationId} not found.");
        }

        var messages = await ReadMessagesAsync(conversationId, userId, cancellationToken);
        var fileName = BuildFileName(header.Name ?? conversation.Name, conversationId, normalizedFormat);

        return normalizedFormat == HtmlFormat
            ? new ConversationExportFile(Encoding.UTF8.GetBytes(BuildHtml(header, messages)), "text/html; charset=utf-8", fileName)
            : new ConversationExportFile(BuildJson(header, messages), "application/json; charset=utf-8", fileName);
    }

    /// <summary>
    /// Reads every non-system message of the conversation, oldest first.
    /// </summary>
    private async Task<List<CosmosConversationMessage>> ReadMessagesAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.type = @type AND c.conversationId = @conversationId AND c.role != @systemRole ORDER BY c.sequence")
            .WithParameter("@type", CosmosTranscriptTypes.Message)
            .WithParameter("@conversationId", conversationId)
            .WithParameter("@systemRole", MappingService.MapToRoleName(ChatRoles.System));

        var messages = new List<CosmosConversationMessage>();
        string? continuationToken = null;

        do
        {
            var page = await _cosmosService.QueryPageAsync<CosmosConversationMessage>(
                query, userId, conversationId, ExportPageSize, continuationToken, cancellationToken);

            messages.AddRange(page.Items);
            continuationToken = page.ContinuationToken;
        }
        while (continuationToken is not null);

        return messages;
    }

    /// <summary>
    /// Assembles a self-contained HTML document from the stored rendering of each message.
    /// </summary>
    /// <remarks>
    /// The stored HTML is embedded verbatim because it was produced with raw HTML passthrough
    /// disabled, so it cannot carry markup smuggled through the model. A message stored before
    /// rendering existed is escaped here instead.
    /// </remarks>
    private static string BuildHtml(CosmosConversationHeader header, List<CosmosConversationMessage> messages)
    {
        var builder = new StringBuilder();
        var title = WebUtility.HtmlEncode(header.Name ?? string.Empty);

        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine($"<title>{title}</title>");
        builder.AppendLine("<style>");
        builder.AppendLine("body{font-family:system-ui,-apple-system,Segoe UI,sans-serif;max-width:48rem;margin:2rem auto;padding:0 1rem;line-height:1.6}");
        builder.AppendLine(".message{border-top:1px solid #d0d7de;padding:1rem 0}");
        builder.AppendLine(".message-role{font-weight:600;text-transform:capitalize}");
        builder.AppendLine(".message-date{color:#57606a;font-size:.875rem;margin-left:.5rem}");
        builder.AppendLine("pre{background:#f6f8fa;padding:.75rem;overflow-x:auto}");
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine($"<h1>{title}</h1>");
        builder.AppendLine("<main>");

        foreach (var message in messages)
        {
            var role = WebUtility.HtmlEncode(message.Role ?? string.Empty);
            builder.AppendLine($"<section class=\"message message-{role}\">");
            builder.AppendLine($"<header><span class=\"message-role\">{role}</span><time class=\"message-date\" datetime=\"{message.DateCreated:O}\">{message.DateCreated:u}</time></header>");
            builder.AppendLine(string.IsNullOrEmpty(message.HtmlContent)
                ? $"<p>{WebUtility.HtmlEncode(message.Content ?? string.Empty)}</p>"
                : message.HtmlContent);
            builder.AppendLine("</section>");
        }

        builder.AppendLine("</main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");

        return builder.ToString();
    }

    private static byte[] BuildJson(CosmosConversationHeader header, List<CosmosConversationMessage> messages)
    {
        var export = new ConversationExportDto
        {
            Name = header.Name,
            DateCreated = header.DateCreated,
            DateModified = header.DateModified,
            Messages =
            [
                .. messages.Select(message => new ConversationExportMessageDto
                {
                    Sequence = message.Sequence,
                    Role = message.Role,
                    Content = message.Content,
                    HtmlContent = message.HtmlContent,
                    Tokens = message.Tokens,
                    TokenAccuracy = message.TokenAccuracy,
                    Model = message.Model,
                    DateCreated = message.DateCreated,
                    Usage = message.Usage is null
                        ? null
                        : new ConversationExportUsageDto
                        {
                            InputTokens = message.Usage.InputTokens,
                            OutputTokens = message.Usage.OutputTokens
                        }
                })
            ]
        };

        return JsonSerializer.SerializeToUtf8Bytes(export, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Builds a download file name from the conversation's name, falling back to its identifier.
    /// </summary>
    private static string BuildFileName(string name, Guid conversationId, string format)
    {
        var slug = FileNameUnsafeCharacters().Replace(name ?? string.Empty, "-").Trim('-');
        if (slug.Length > 60)
        {
            slug = slug[..60].TrimEnd('-');
        }

        return string.IsNullOrWhiteSpace(slug)
            ? $"conversation-{conversationId}.{format}"
            : $"{slug}.{format}";
    }

    [GeneratedRegex(@"[^A-Za-z0-9\-_. ]+")]
    private static partial Regex FileNameUnsafeCharacters();
}
