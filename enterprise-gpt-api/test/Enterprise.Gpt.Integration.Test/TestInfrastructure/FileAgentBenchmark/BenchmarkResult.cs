namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentBenchmark;

/// <summary>What one benchmark prompt produced.</summary>
/// <param name="Id">The slug of the prompt that produced this result.</param>
/// <param name="Verb">Which of the four capabilities the prompt exercised.</param>
/// <param name="Extension">The extension the artifact had to carry, including its dot.</param>
/// <param name="Produced">Whether the run returned a file carrying that extension.</param>
/// <param name="Verified">Whether that file re-opened as its format; always <see langword="false"/> when nothing was produced.</param>
/// <param name="Shape">What re-opening the artifact measured, or why it would not open.</param>
/// <param name="Answer">The model's text reply, which explains a missing artifact; empty when the run threw.</param>
internal sealed record BenchmarkResult(
    string Id, string Verb, string Extension, bool Produced, bool Verified, string Shape, string Answer);
