using System.Text.Json;

namespace Enterprise.Gpt.Integration.Test.FileAgentSpike;

/// <summary>
/// Reads a probe program's result out of the file it wrote in the sandbox.
/// </summary>
/// <remarks>
/// <para>
/// Out of the file, not out of the model's prose, so a run is recorded as what happened rather than
/// as a summary of what happened. Standard output would have been cheaper, but this deployment
/// surfaces none: a completed code interpreter call arrives with an empty
/// <c>CodeInterpreterToolResultContent.Outputs</c>, and the only trace of a produced file is the
/// citation annotation on the message.
/// </para>
/// <para>
/// Downloading therefore needs the container the file was written in, which makes reading a result an
/// I/O operation against the same scope the artifact was found under.
/// </para>
/// </remarks>
internal static class SpikeResults
{
    /// <summary>
    /// Runs one probe program and reads the result file it was written to produce, repeating the run
    /// once if nothing came back.
    /// </summary>
    /// <param name="session">The session to run in.</param>
    /// <param name="script">The complete program to run.</param>
    /// <param name="fileName">The file the probe was written to produce.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The run that produced the result, and the parsed result the caller owns and disposes.</returns>
    /// <remarks>
    /// A live model occasionally answers a "run this program" turn with something else — the observed
    /// case is executing a file an earlier probe left in the shared container. One repeat costs one
    /// sandbox call; letting it through costs every source format the caller already attempted.
    /// </remarks>
    public static async Task<(SandboxRun Run, JsonDocument Result)> RunProbeAsync(
        SandboxSession session, string script, string fileName, CancellationToken cancellationToken)
    {
        var run = await session.RunPythonAsync(script, cancellationToken);

        try
        {
            return (run, await RequireAsync(session, run, fileName, cancellationToken));
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            var repeat = await session.RunPythonAsync(script, cancellationToken);

            return (repeat, await RequireAsync(session, repeat, fileName, cancellationToken));
        }
    }

    /// <summary>
    /// Downloads and parses the JSON document a probe wrote.
    /// </summary>
    /// <param name="session">The session that ran the probe.</param>
    /// <param name="run">The run to read.</param>
    /// <param name="fileName">The file the probe was written to produce.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The parsed result. The caller owns and disposes it.</returns>
    /// <exception cref="InvalidOperationException">
    /// The probe produced no such file, which means it did not reach its own final statement - so the
    /// run failed, whatever the assistant's prose says about it.
    /// </exception>
    public static async Task<JsonDocument> RequireAsync(
        SandboxSession session, SandboxRun run, string fileName, CancellationToken cancellationToken)
    {
        // The cited artifact first, because using it proves the channel works; the container listing
        // second, because a program's output exists whether or not the assistant thought to mention
        // it, and a probe that ran must not be reported as a probe that did not.
        var artifact = run.Artifacts.FirstOrDefault(candidate =>
            string.Equals(Path.GetFileName(candidate.Name), fileName, StringComparison.OrdinalIgnoreCase));

        var bytes = artifact is not null
            ? await session.DownloadAsync(artifact, cancellationToken)
            : await session.TryReadContainerFileAsync(fileName, cancellationToken);

        if (bytes is null)
        {
            throw new InvalidOperationException(
                $"The probe produced no '{fileName}', in the response or in container "
                + $"{session.ContainerId ?? "(none reported)"}, so it did not run to completion. Artifacts "
                + $"cited: {Describe(run)}. Content shapes: {string.Join(", ", run.ObservedContentKinds)}. "
                + $"The assistant said: {run.Text}");
        }

        return JsonDocument.Parse(bytes);
    }

    private static string Describe(SandboxRun run) =>
        run.Artifacts.Count == 0
            ? "none"
            : string.Join(", ", run.Artifacts.Select(artifact => artifact.Name ?? artifact.FileId ?? "(unnamed)"));
}
