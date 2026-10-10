using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Tokenization;

/// <summary>
/// Accounts for the prompt tokens that belong to no message.
/// </summary>
/// <remarks>
/// <para>
/// Two things cost input tokens on every turn and are attributable to no transcript message. Tool
/// and function JSON schemas are injected into the prompt for every leased tool, which with several
/// MCP servers attached is easily thousands of tokens. Project instructions ride on
/// <c>ChatOptions.Instructions</c> rather than as a message, deliberately, so that editing a
/// project's instructions affects the conversations already in it — they are billed and belong to
/// no message either.
/// </para>
/// <para>
/// Without these terms the sum of a conversation's message estimates would always under-report the
/// prompt, and a budget built on that sum would authorise a turn the provider then rejects.
/// </para>
/// </remarks>
/// <param name="options">The estimator settings.</param>
public sealed class PromptOverheadCalculator(IOptions<TokenEstimationOptions> options)
{
    private readonly TokenEstimationOptions _options = options.Value;

    /// <summary>
    /// Estimates the tokens a turn spends outside its messages' own text.
    /// </summary>
    /// <param name="messageCount">How many messages the prompt carries.</param>
    /// <param name="toolCount">How many tools are attached to the turn.</param>
    /// <param name="instructionTokens">
    /// What the instruction text carried on the chat options was counted at. Zero when there is
    /// none.
    /// </param>
    /// <returns>The overhead in tokens.</returns>
    /// <remarks>
    /// Takes an already-counted instruction figure rather than counting it here, so that every call
    /// into a tokenizer goes through the caller's guarded path. This runs while the turn is being
    /// set up — before a single token has reached the user — so a tokenizer fault escaping from
    /// here would fail a turn that the write path is careful to preserve.
    /// </remarks>
    /// <example>
    /// A turn with no tools and no instructions contributes only the per-message framing:
    /// <code language="csharp">
    /// var overhead = calculator.Estimate(messageCount: 10, toolCount: 0, instructionTokens: 0);
    /// // overhead == 10 * PerMessageFramingTokens
    /// </code>
    /// </example>
    public long Estimate(int messageCount, int toolCount, long instructionTokens)
    {
        var framing = (long)Math.Max(0, messageCount) * _options.PerMessageFramingTokens;
        var schemas = (long)Math.Max(0, toolCount) * _options.PerToolSchemaTokens;

        return framing + schemas + Math.Max(0, instructionTokens);
    }
}
