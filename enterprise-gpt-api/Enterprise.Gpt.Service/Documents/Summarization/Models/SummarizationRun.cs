namespace Enterprise.Gpt.Service.Documents.Summarization.Models;

/// <summary>
/// What one summarization run produced, and what it cost.
/// </summary>
/// <remarks>
/// <paramref name="MapUnitCount"/> is zero on the single-pass path. <paramref name="ModelId"/> and
/// <paramref name="DeploymentName"/> are snapshotted so repointing the catalog row cannot rewrite what
/// produced a summary.
/// </remarks>
public sealed record SummarizationRun(
    string Summary,
    long InputTokens,
    long OutputTokens,
    int ModelCallCount,
    int MapUnitCount,
    int CollapsePasses,
    bool Truncated,
    Guid ModelId,
    string DeploymentName,
    string PromptVersion);
