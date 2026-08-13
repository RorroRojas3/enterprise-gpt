using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Tokenization;

/// <summary>
/// Estimates what a whole prompt costs: the messages in it, plus the tokens no message owns.
/// </summary>
public interface IPromptEstimator
{
    /// <summary>
    /// Counts each candidate message's context cost, using its stored count where it has one.
    /// </summary>
    /// <param name="model">The model the prompt will be sent to.</param>
    /// <param name="messages">The candidate messages, as content paired with the count stored on them.</param>
    /// <returns>One count per message, in the order supplied.</returns>
    /// <remarks>
    /// A stored count of zero for non-empty content means the message predates estimation, so it
    /// is counted on the fly rather than treated as free.
    /// </remarks>
    IReadOnlyList<long> EstimateMessages(ModelDto model, IReadOnlyList<(string? Content, long StoredTokens)> messages);

    /// <summary>
    /// Counts the prompt tokens that belong to no message.
    /// </summary>
    /// <param name="model">The model the prompt will be sent to.</param>
    /// <param name="toolCount">How many tools are attached to the turn.</param>
    /// <param name="instructions">The instructions carried on the request rather than as a message.</param>
    /// <param name="messageCount">How many messages the prompt carries.</param>
    /// <returns>The overhead in tokens.</returns>
    /// <remarks>
    /// Three things cost input tokens and belong to no message: each attached tool's injected
    /// schema, the chat framing around every message, and the instructions the request carries
    /// separately. Leaving them out makes the sum of the messages permanently under-report the
    /// prompt, which is exactly the error a context budget cannot afford.
    /// </remarks>
    long EstimateOverhead(ModelDto model, int toolCount, string? instructions, int messageCount);
}

/// <inheritdoc />
/// <param name="resolver">Selects the estimator for the model's provider.</param>
/// <param name="options">The configured per-tool and per-message constants.</param>
public sealed class PromptEstimator(
    ITokenEstimatorResolver resolver,
    IOptions<TokenEstimationOptions> options) : IPromptEstimator
{
    private readonly ITokenEstimatorResolver _resolver = resolver;
    private readonly IOptions<TokenEstimationOptions> _options = options;

    /// <inheritdoc />
    public IReadOnlyList<long> EstimateMessages(ModelDto model, IReadOnlyList<(string? Content, long StoredTokens)> messages)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(messages);

        var estimator = _resolver.Resolve(model.ProviderId);
        var counts = new long[messages.Count];

        for (var index = 0; index < messages.Count; index++)
        {
            var (content, storedTokens) = messages[index];
            counts[index] = storedTokens > 0
                ? storedTokens
                : estimator.CountTokens(model.DeploymentName, content);
        }

        return counts;
    }

    /// <inheritdoc />
    public long EstimateOverhead(ModelDto model, int toolCount, string? instructions, int messageCount)
    {
        ArgumentNullException.ThrowIfNull(model);

        var settings = _options.Value;
        var estimator = _resolver.Resolve(model.ProviderId);

        var toolOverhead = (long)toolCount * settings.PerToolSchemaTokens;
        var framingOverhead = (long)messageCount * settings.PerMessageFramingTokens;
        var instructionOverhead = estimator.CountTokens(model.DeploymentName, instructions);

        return toolOverhead + framingOverhead + instructionOverhead;
    }
}
