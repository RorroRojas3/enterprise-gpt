using System.Text;
using System.Text.Json;
using Enterprise.Gpt.Integration.Test.FileAgentSpike;
using Enterprise.Gpt.Service.Agents;
using Enterprise.Gpt.Service.Prompts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.FileAgentBenchmark;

/// <summary>What one benchmark prompt produced.</summary>
/// <param name="Shape">What re-opening the artifact measured, or why it would not open.</param>
internal sealed record BenchmarkResult(
    string Id, string Verb, string Extension, bool Produced, bool Verified, string Shape, string Answer);

/// <summary>The whole run, written as evidence beside the spike's own.</summary>
internal sealed record BenchmarkReport(
    DateTimeOffset RecordedUtc, string Deployment, double PassRate, IReadOnlyList<BenchmarkResult> Results);

/// <summary>
/// The fixed thirty-prompt benchmark, measured against the same verifier that gates a real run.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in and billable: thirty prompts, each provisioning its own Code Interpreter container. It never
/// runs in CI, and <see cref="SpikeConfiguration.BenchmarkSkipReason"/> is the command that turns it on.
/// </para>
/// <para>
/// A fresh session per prompt, deliberately: a shared container would let one prompt read a file
/// another wrote, and a benchmark that scored a run for finding someone else's output would be
/// measuring the harness.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class FileAgentBenchmarkTests
{
    /// <summary>The threshold the artifact-validity criterion is stated at.</summary>
    private const double RequiredPassRate = 0.9;

    private const string EvidenceFileName = "benchmark-report.json";

    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Benchmark_EveryPrompt_ProducesAnArtifactThatVerifies()
    {
        Assert.SkipUnless(SpikeConfiguration.IsBenchmarkEnabled, SpikeConfiguration.BenchmarkSkipReason);

        var verifier = new GeneratedArtifactVerifier(NullLogger<GeneratedArtifactVerifier>.Instance);
        List<BenchmarkResult> results = [];

        foreach (var prompt in BenchmarkPrompts.All)
        {
            results.Add(await RunAsync(prompt, verifier, TestContext.Current.CancellationToken));
        }

        var passed = results.Count(result => result.Verified);
        var rate = (double)passed / results.Count;

        var report = new BenchmarkReport(
            DateTimeOffset.UtcNow, SpikeConfiguration.DeploymentName, rate, results);

        await WriteAsync(report, TestContext.Current.CancellationToken);

        // Both halves of the criterion. The second is what stops a silent text-only answer counting as
        // anything other than a failure: a run that produced no file is a failed run, whatever it said.
        Assert.True(
            rate >= RequiredPassRate,
            $"{passed} of {results.Count} prompts produced a verifying artifact ({rate:P0}), below the "
            + $"{RequiredPassRate:P0} threshold. Failures: {Describe(results)}");

        Assert.DoesNotContain(results, result => result.Produced && !result.Verified && result.Shape.Length == 0);
    }

    private static async Task<BenchmarkResult> RunAsync(
        BenchmarkPrompt prompt, GeneratedArtifactVerifier verifier, CancellationToken cancellationToken)
    {
        // The agent's own instructions, so this measures the prompt the feature ships rather than one
        // written for the benchmark.
        await using var session = SandboxSession.Create(
            maxTurns: 1,
            systemInstruction: ConversationPrompts.BuildFileAgentPrompt(
                prompt.Source is null ? [] : [prompt.Source.FileName]));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(PromptTimeout);

        try
        {
            if (prompt.Source is { } source)
            {
                var uploaded = await session.UploadAsync(
                    source.FileName, source.MediaType, Encoding.UTF8.GetBytes(source.Content), deadline.Token);

                session.SetInputs(uploaded);
            }

            var run = await session.RunAsync(prompt.Instruction, deadline.Token);

            var artifact = run.Artifacts.FirstOrDefault(
                candidate => candidate.Name is { Length: > 0 } name
                    && name.EndsWith(prompt.Extension, StringComparison.OrdinalIgnoreCase));

            if (artifact is null)
            {
                return new BenchmarkResult(
                    prompt.Id, prompt.Verb, prompt.Extension, Produced: false, Verified: false,
                    Shape: string.Empty, run.Text);
            }

            var bytes = artifact.InlineData ?? await session.DownloadAsync(artifact, deadline.Token);
            var verification = verifier.Verify(artifact.Name!, bytes);

            return new BenchmarkResult(
                prompt.Id,
                prompt.Verb,
                prompt.Extension,
                Produced: true,
                verification.Passed,
                verification.Passed ? verification.Shape : verification.Reason ?? "did not verify",
                run.Text);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || deadline.IsCancellationRequested)
        {
            // Recorded rather than thrown: one prompt that failed is a data point, and stopping here
            // would throw away the twenty-nine already paid for.
            return new BenchmarkResult(
                prompt.Id, prompt.Verb, prompt.Extension, Produced: false, Verified: false,
                Shape: exception.GetType().Name, Answer: string.Empty);
        }
    }

    private static async Task WriteAsync(BenchmarkReport report, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "FileAgentBenchmark");
        Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(
            Path.Combine(directory, EvidenceFileName),
            JsonSerializer.Serialize(report, SpikeEvidence.SerializerOptions),
            cancellationToken);
    }

    private static string Describe(IEnumerable<BenchmarkResult> results) =>
        string.Join(
            "; ",
            results
                .Where(result => !result.Verified)
                .Select(result => $"{result.Id} ({(result.Produced ? result.Shape : "no file produced")})"));
}
