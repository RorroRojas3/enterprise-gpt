namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentBenchmark;

/// <summary>The whole run, written as evidence beside the spike's own.</summary>
internal sealed record BenchmarkReport(
    DateTimeOffset RecordedUtc, string Deployment, double PassRate, IReadOnlyList<BenchmarkResult> Results);
