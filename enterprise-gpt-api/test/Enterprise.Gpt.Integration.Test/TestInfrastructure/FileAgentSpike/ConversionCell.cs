namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentSpike;

/// <summary>
/// One cell of the conversion matrix.
/// </summary>
/// <param name="From">The source format's extension, lower-case and without a dot.</param>
/// <param name="To">The target format, on the same terms.</param>
/// <param name="ProposedTier">The tier expected before anything ran. Kept alongside
/// <paramref name="Tier"/> so a demotion reads as a demotion.</param>
/// <param name="Tier">The confirmed tier, or the proposal until a run confirms it.</param>
/// <param name="Confirmed">Whether a run has attempted the pair against a sandbox; a <c>notOffered</c> pair never is.</param>
/// <param name="Engine">The libraries that performed the conversion, or <see langword="null"/> until an attempt reports any.</param>
/// <param name="Note">What the answer has to tell the user about this pair.</param>
/// <param name="Evidence">What the confirming attempt actually did, or <see langword="null"/> for a pair never attempted.</param>
internal sealed record ConversionCell(
    string From,
    string To,
    string ProposedTier,
    string Tier,
    bool Confirmed,
    string? Engine,
    string Note,
    ConversionEvidence? Evidence);
