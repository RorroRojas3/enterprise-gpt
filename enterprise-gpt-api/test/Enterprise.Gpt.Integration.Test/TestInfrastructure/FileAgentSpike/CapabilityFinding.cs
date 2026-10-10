namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentSpike;

/// <summary>
/// One question the spike asked of the live deployment, and what came back.
/// </summary>
/// <param name="Outcome">The short answer, so the file can be skimmed.</param>
/// <param name="Detail">The observation the outcome rests on.</param>
internal sealed record CapabilityFinding(string Question, string Outcome, string Detail);
