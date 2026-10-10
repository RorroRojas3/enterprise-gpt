namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentBenchmark;

/// <summary>What one benchmark prompt produced.</summary>
/// <param name="Shape">What re-opening the artifact measured, or why it would not open.</param>
internal sealed record BenchmarkResult(
    string Id, string Verb, string Extension, bool Produced, bool Verified, string Shape, string Answer);
