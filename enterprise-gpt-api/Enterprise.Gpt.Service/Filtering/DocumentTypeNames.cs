using Enterprise.Gpt.Dto.Enums;
using FluentValidation;
using FluentValidation.Results;

namespace Enterprise.Gpt.Service.Filtering;

/// <summary>
/// The <c>?type=</c> values the caller-scoped document listing accepts, and the mapping to
/// <see cref="ConversationDocumentTypes"/>.
/// </summary>
/// <remarks>
/// One table rather than a parse method and a separate display string, for the reason
/// <c>ConversationExportFormatNames</c> records. The lookup is case-insensitive because a listing row
/// carries <c>Generated</c> while the advertised token is <c>generated</c>, so a client that
/// round-trips a row's own value still resolves.
/// </remarks>
public static class DocumentTypeNames
{
    /// <summary>
    /// The <c>errors</c> key a rejected value is reported under.
    /// </summary>
    public const string TypeKey = "type";

    /// <summary>
    /// Gets the accepted values, in the order a rejection message lists them.
    /// </summary>
    public static IReadOnlyList<string> Supported { get; } = ["uploaded", "generated"];

    private static readonly Dictionary<string, ConversationDocumentTypes> ByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["uploaded"] = ConversationDocumentTypes.Uploaded,
            ["generated"] = ConversationDocumentTypes.Generated
        };

    /// <summary>
    /// Gets the accepted values rendered for a validation message: <c>'uploaded', 'generated'</c>.
    /// </summary>
    public static string SupportedList { get; } = string.Join(", ", Supported.Select(name => $"'{name}'"));

    /// <summary>
    /// Resolves a <c>?type=</c> value into the origin a listing should narrow to.
    /// </summary>
    /// <param name="type">The requested value, or <see langword="null"/> when the caller sent none.</param>
    /// <returns>The origin to filter on, or <see langword="null"/> for every document.</returns>
    /// <exception cref="ValidationException">The value names no supported origin.</exception>
    /// <remarks>
    /// Rejected rather than ignored: a caller who misspells the value would otherwise get a
    /// correct-looking page holding documents they asked not to see. Blank is not a misspelling.
    /// </remarks>
    public static ConversationDocumentTypes? Resolve(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        if (ByName.TryGetValue(type.Trim(), out var resolved))
        {
            return resolved;
        }

        throw new ValidationException(
        [
            new ValidationFailure(
                TypeKey,
                $"'{type}' is not a supported document type. Supported types are {SupportedList}.")
        ]);
    }
}
