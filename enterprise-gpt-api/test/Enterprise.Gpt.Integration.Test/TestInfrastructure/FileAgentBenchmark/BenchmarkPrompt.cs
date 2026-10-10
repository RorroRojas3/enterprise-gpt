namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentBenchmark;

/// <summary>
/// One prompt the benchmark sends, and the file it must come back with.
/// </summary>
/// <param name="Verb">Which of the four the prompt exercises, so a failure names the capability.</param>
/// <param name="Extension">The extension the answer's artifact must carry, including its dot.</param>
/// <param name="Source">
/// A file the run works from, or <see langword="null"/> for a create. Written into the sandbox before
/// the prompt is sent, exactly as a turn's own inputs are.
/// </param>
internal sealed record BenchmarkPrompt(
    string Id, string Verb, string Extension, string Instruction, BenchmarkSource? Source = null);
