namespace Enterprise.Gpt.Common.Constants.Documents;

/// <summary>
/// Column widths of a document file row, shared by both document families and the upload validator.
/// </summary>
public static class DocumentLimits
{
    public const int NameMaxLength = 256;

    public const int ExtensionMaxLength = 8;

    public const int MimeTypeMaxLength = 256;
}
