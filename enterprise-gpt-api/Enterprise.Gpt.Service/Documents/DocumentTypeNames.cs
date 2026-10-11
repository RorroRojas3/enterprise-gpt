using Enterprise.Gpt.Common.Enums;
using FluentValidation;
using FluentValidation.Results;

namespace Enterprise.Gpt.Service.Documents;

/// <summary>
/// The <c>?type=</c> values the caller-scoped document listing accepts, and the mapping to
/// <see cref="ConversationDocumentTypes"/>.
/// </summary>
/// <remarks>
/// Case-insensitive so a client round-tripping a listing row's <c>Generated</c> still resolves.
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

    #region Public static methods

    /// <summary>
    /// Resolves a <c>?type=</c> value into the origin a listing should narrow to; blank means every document.
    /// </summary>
    /// <exception cref="ValidationException">The value names no supported origin.</exception>
    /// <remarks>
    /// Rejected rather than ignored, so a misspelling never yields a correct-looking page.
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

    #endregion
}
