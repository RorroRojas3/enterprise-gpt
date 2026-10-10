namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentBenchmark;

/// <summary>A file mounted into the sandbox before a prompt runs.</summary>
internal sealed record BenchmarkSource(string FileName, string MediaType, string Content);
