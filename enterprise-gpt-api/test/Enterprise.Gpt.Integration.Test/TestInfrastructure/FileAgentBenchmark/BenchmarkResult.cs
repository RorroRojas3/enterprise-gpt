using System.Text.Json;
using System.Text;
using Enterprise.Gpt.Integration.Test.FileAgentSpike;
using Enterprise.Gpt.Service.Agents;
using Enterprise.Gpt.Service.Prompts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentBenchmark;

/// <summary>What one benchmark prompt produced.</summary>
/// <param name="Shape">What re-opening the artifact measured, or why it would not open.</param>
internal sealed record BenchmarkResult(
    string Id, string Verb, string Extension, bool Produced, bool Verified, string Shape, string Answer);
