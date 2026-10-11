namespace Enterprise.Gpt.Common.Constants.McpServers;

/// <summary>
/// Column widths of an MCP server, shared by its configuration and the server validators.
/// </summary>
public static class McpServerLimits
{
    public const int NameMaxLength = 256;

    public const int DescriptionMaxLength = 1024;

    public const int UrlMaxLength = 2048;

    public const int ScopeMaxLength = 512;

    public const int IconKeyMaxLength = 64;
}
