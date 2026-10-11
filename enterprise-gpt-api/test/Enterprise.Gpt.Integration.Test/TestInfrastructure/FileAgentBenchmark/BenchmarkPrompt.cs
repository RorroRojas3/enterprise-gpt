namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentBenchmark;

/// <summary>
/// One prompt the benchmark sends, and the file it must come back with.
/// </summary>
/// <param name="Id">A stable slug such as <c>create-docx-1</c> that names the prompt in the report.</param>
/// <param name="Verb">Which of the four the prompt exercises, so a failure names the capability.</param>
/// <param name="Extension">The extension the answer's artifact must carry, including its dot.</param>
/// <param name="Instruction">The request sent as the run's only user message, naming the source file when there is one.</param>
/// <param name="Source">
/// A file the run works from, or <see langword="null"/> for a create. Written into the sandbox before
/// the prompt is sent, exactly as a turn's own inputs are.
/// </param>
internal sealed record BenchmarkPrompt(
    string Id, string Verb, string Extension, string Instruction, BenchmarkSource? Source = null);
