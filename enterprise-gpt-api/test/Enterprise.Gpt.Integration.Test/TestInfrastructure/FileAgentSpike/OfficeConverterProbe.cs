namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentSpike;

/// <summary>The result of looking for a headless office suite on the sandbox PATH.</summary>
internal sealed record OfficeConverterProbe(bool Present, string? Path, string? Version);
