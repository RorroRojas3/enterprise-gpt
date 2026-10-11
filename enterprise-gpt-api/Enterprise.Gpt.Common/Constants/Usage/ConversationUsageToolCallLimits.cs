namespace Enterprise.Gpt.Common.Constants.Usage;

/// <summary>
/// Column widths of a recorded tool call.
/// </summary>
/// <remarks>
/// The writer clips to these rather than failing: it runs after the answer has streamed, so an unusually long value must not lose the turn's accounting.
/// </remarks>
public static class ConversationUsageToolCallLimits
{
    public const int ToolNameMaxLength = 256;

    public const int SourceMaxLength = 256;

    public const int CallIdMaxLength = 128;

    public const int DeploymentNameMaxLength = 512;
}
