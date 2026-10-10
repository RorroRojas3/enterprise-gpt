namespace Enterprise.Gpt.Service.Documents.Summarization.Models;

/// <summary>
/// What one summarization run produced, and what it cost.
/// </summary>
/// <param name="Summary">The canonical summary text.</param>
/// <param name="InputTokens">Input tokens across every call the run made.</param>
/// <param name="OutputTokens">Output tokens across every call the run made.</param>
/// <param name="ModelCallCount">
/// Every map call, plus every collapse-pass call, plus the final reduce. Exactly one for a document
/// that fitted a single pass.
/// </param>
/// <param name="MapUnitCount">How many units the document was split into. Zero on the single-pass path.</param>
/// <param name="CollapsePasses">How many times the partial summaries had to be re-summarized. Usually zero.</param>
/// <param name="Truncated">Whether any call in the run was cut off at the deployment's output cap.</param>
/// <param name="ModelId">The summarizer's catalog row id, as it stood when the run started.</param>
/// <param name="DeploymentName">The deployment that actually served the run.</param>
/// <param name="PromptVersion">The prompt generation the summary was produced under.</param>
/// <remarks>
/// <paramref name="ModelId"/> and <paramref name="DeploymentName"/> are snapshotted rather than
/// looked up later, for the reason <c>ConversationUsage.DeploymentName</c> is: repointing the
/// catalog row afterwards must not rewrite what an already-generated summary says produced it.
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
