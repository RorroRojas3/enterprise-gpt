namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentSpike;

/// <summary>
/// What one attempt at one conversion pair actually did.
/// </summary>
/// <param name="Engine">The libraries that performed it, or <see langword="null"/> if none got that far.</param>
/// <param name="Verified">Whether the harness could open the result and find the shape it asked for.</param>
/// <param name="Detail">The verification's own account, or the failure that stopped the attempt.</param>
internal sealed record ConversionEvidence(
    DateTimeOffset RecordedUtc,
    string? Engine,
    bool Produced,
    long Bytes,
    bool Verified,
    string Detail);
