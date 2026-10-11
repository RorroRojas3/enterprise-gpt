using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Conversations.Tokenization;

/// <summary>
/// Accounts for the prompt tokens that belong to no message.
/// </summary>
/// <remarks>
/// Tool schemas and project instructions (on <c>ChatOptions.Instructions</c>) are billed but belong to no
/// message; without them the budget would authorise turns the provider then rejects.
/// </remarks>
public sealed class PromptOverheadCalculator(IOptions<TokenEstimationOptions> options)
{
    private readonly TokenEstimationOptions _options = options.Value;

    /// <summary>
    /// Estimates the tokens a turn spends outside its messages' own text.
    /// </summary>
    /// <remarks>
    /// <paramref name="instructionTokens"/> arrives already counted so every tokenizer call stays on the
    /// caller's guarded path; a fault here, during turn setup, would fail the turn.
    /// </remarks>
    public long Estimate(int messageCount, int toolCount, long instructionTokens)
    {
        var framing = (long)Math.Max(0, messageCount) * _options.PerMessageFramingTokens;
        var schemas = (long)Math.Max(0, toolCount) * _options.PerToolSchemaTokens;

        return framing + schemas + Math.Max(0, instructionTokens);
    }
}
