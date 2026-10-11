namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentSpike;

/// <summary>
/// What one attempt at one conversion pair actually did.
/// </summary>
/// <param name="RecordedUtc">When the attempt was graded.</param>
/// <param name="Engine">The libraries that performed it, or <see langword="null"/> if none got that far.</param>
/// <param name="Produced">Whether the output file existed in the sandbox after the attempt, whatever its contents.</param>
/// <param name="Bytes">The output's size in bytes, or 0 when nothing was produced.</param>
/// <param name="Verified">Whether the harness could open the result and find the shape it asked for.</param>
/// <param name="Detail">The verification's own account, or the failure that stopped the attempt.</param>
internal sealed record ConversionEvidence(
    DateTimeOffset RecordedUtc,
    string? Engine,
    bool Produced,
    long Bytes,
    bool Verified,
    string Detail);
