using System.Text.Json;
using System.Text;
using Enterprise.Gpt.Integration.Test.FileAgentSpike;
using Enterprise.Gpt.Service.Agents;
using Enterprise.Gpt.Service.Prompts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentBenchmark;

/// <summary>The whole run, written as evidence beside the spike's own.</summary>
internal sealed record BenchmarkReport(
    DateTimeOffset RecordedUtc, string Deployment, double PassRate, IReadOnlyList<BenchmarkResult> Results);
