namespace Enterprise.Gpt.Common.Constants.Usage;

/// <summary>
/// Column widths of a recorded model turn.
/// </summary>
/// <remarks>
/// Clipped to by the writer for the same reason as <see cref="ConversationUsageToolCallLimits"/>.
/// </remarks>
public static class ConversationUsageTurnLimits
{
    public const int ResponseIdMaxLength = 128;

    public const int ReportedModelIdMaxLength = 512;
}
