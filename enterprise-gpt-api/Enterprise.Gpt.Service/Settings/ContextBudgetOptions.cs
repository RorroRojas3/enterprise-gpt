namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Controls whether replayed history is trimmed to the model's context window, bound from the
/// <c>ContextBudget</c> configuration section.
/// </summary>
public sealed class ContextBudgetOptions
{
    /// <summary>
    /// The configuration section this type binds from.
    /// </summary>
    public const string SectionName = "ContextBudget";

    /// <summary>
    /// Whether to trim a turn's replayed history to fit the model. Default: enabled.
    /// </summary>
    /// <remarks>
    /// The back-out for budgeting as a whole. Turning it off restores unbounded replay — every
    /// message sent on every turn — without a deployment, which is what makes an estimator that
    /// turns out to be wrong a configuration change rather than an incident.
    /// </remarks>
    public bool Enabled { get; set; } = true;
}
