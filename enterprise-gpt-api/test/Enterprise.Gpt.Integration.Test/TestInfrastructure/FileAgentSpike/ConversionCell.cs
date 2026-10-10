using System.Text.Json;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentSpike;

/// <summary>
/// One cell of the conversion matrix.
/// </summary>
/// <param name="ProposedTier">The tier expected before anything ran. Kept alongside
/// <paramref name="Tier"/> so a demotion reads as a demotion.</param>
/// <param name="Tier">The confirmed tier, or the proposal until a run confirms it.</param>
/// <param name="Note">What the answer has to tell the user about this pair.</param>
internal sealed record ConversionCell(
    string From,
    string To,
    string ProposedTier,
    string Tier,
    bool Confirmed,
    string? Engine,
    string Note,
    ConversionEvidence? Evidence);
