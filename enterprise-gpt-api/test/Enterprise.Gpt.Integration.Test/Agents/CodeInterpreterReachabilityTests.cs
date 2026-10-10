using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.FileAgentSpike;

/// <summary>
/// Establishes, against the live deployment, whether Code Interpreter runs over the Responses-route
/// Azure OpenAI client this application registers, and by what mechanism files reach the sandbox and
/// artifacts come back.
/// </summary>
/// <remarks>
/// A spike, not a regression suite: each test records its finding into
/// <c>Evidence/capability-report.json</c> <em>before</em> asserting, so a failing probe still leaves
/// behind what it saw. Opt-in, because every execution provisions a billed sandbox container.
/// </remarks>
[Collection(SandboxSpikeCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CodeInterpreterReachabilityTests(SandboxSpikeFixture fixture)
{
    private readonly SandboxSpikeFixture _fixture = fixture;

    [Fact]
    public async Task CodeInterpreter_OnResponsesRouteClient_ProducesAnArtifact()
    {
        Assert.SkipUnless(_fixture.IsEnabled, SpikeConfiguration.SkipReason);

        var session = _fixture.Session;
        session.SetInputs();

        var run = await session.RunPythonAsync(
            SpikePaths.ReadScript("write-artifact.py"), TestContext.Current.CancellationToken);

        var channels = run.Artifacts.Count > 0
            ? string.Join(", ", run.Artifacts.Select(artifact => artifact.Channel).Distinct())
            : "none";

        _fixture.Record(
            "Does the Responses-route client carry HostedCodeInterpreterTool in this tenant?",
            run.Artifacts.Count > 0 ? "yes" : "no",
            $"Deployment {session.DeploymentName}. Artifact channels: {channels}. Container: "
            + $"{run.ContainerId ?? "not reported"}. Conversation: {run.ConversationId ?? "not reported"}. "
            + $"Content shapes observed: {string.Join(", ", run.ObservedContentKinds)}.");

        using var result = await SpikeResults.RequireAsync(
            session, run, "probe-artifact-result.json", TestContext.Current.CancellationToken);

        Assert.Equal("enterprise-gpt-file-agent-ep0", result.RootElement.GetProperty("marker").GetString());
        Assert.NotEmpty(run.Artifacts);
    }

    [Fact]
    public async Task Sandbox_AttemptingAnOutboundCall_IsBlocked()
    {
        Assert.SkipUnless(_fixture.IsEnabled, SpikeConfiguration.SkipReason);

        var session = _fixture.Session;
        session.SetInputs();

        var run = await session.RunPythonAsync(
            SpikePaths.ReadScript("network-probe.py"), TestContext.Current.CancellationToken);

        using var result = await SpikeResults.RequireAsync(
            session, run, "network-probe-result.json", TestContext.Current.CancellationToken);

        var root = result.RootElement;

        var http = root.GetProperty("http").GetProperty("blocked").GetBoolean();
        var socket = root.GetProperty("socket").GetProperty("blocked").GetBoolean();
        var pip = root.GetProperty("pip").GetProperty("blocked").GetBoolean();

        _fixture.Record(
            "Is the sandbox denied outbound network access in this deployment?",
            http && socket && pip ? "yes, on every path probed" : "no, at least one path succeeded",
            $"https request blocked: {http}. Raw socket blocked: {socket}. pip install blocked: {pip}. "
            + "A reachable package index would mean the library inventory is not a fixed fact, and a "
            + "skill could install its way around it.");

        Assert.True(http, "The sandbox reached an outbound HTTPS endpoint, which the design assumes it cannot.");
        Assert.True(socket, "The sandbox opened a raw outbound socket, which the design assumes it cannot.");
        Assert.True(pip, "The sandbox installed a package from the network, which the library inventory assumes it cannot.");
    }

    [Fact]
    public async Task Inputs_SuppliedAsHostedFileContent_AreVisibleToGeneratedCode()
    {
        Assert.SkipUnless(_fixture.IsEnabled, SpikeConfiguration.SkipReason);

        var session = _fixture.Session;
        var marker = $"hosted-{Guid.NewGuid():N}";

        var uploaded = await session.UploadAsync(
            "ep0-hosted-input.txt",
            SandboxSession.PlainTextMediaType,
            Encoding.UTF8.GetBytes(marker),
            TestContext.Current.CancellationToken);

        session.SetInputs(uploaded);

        var run = await session.RunPythonAsync(
            ReadInputScript(marker), TestContext.Current.CancellationToken);

        var probe = await ProbeMarkerAsync(session, run, TestContext.Current.CancellationToken);

        _fixture.Record(
            "Does a HostedFileContent input reach the sandbox?",
            probe.Delivered switch { true => "yes", false => "no", null => "the probe did not complete" },
            $"Uploaded as file {uploaded.FileId} with purpose assistants, then supplied on "
            + $"HostedCodeInterpreterTool.Inputs. {probe.Detail} A hosted file reference is the input "
            + "path that works; raw bytes are not.");

        Assert.True(probe.Delivered == true, probe.Detail);
    }

    [Fact]
    public async Task Inputs_SuppliedAsDataContent_AreSilentlyDropped()
    {
        Assert.SkipUnless(_fixture.IsEnabled, SpikeConfiguration.SkipReason);

        var session = _fixture.Session;
        var marker = $"inline-{Guid.NewGuid():N}";

        // Microsoft.Extensions.AI.OpenAI 10.9.0 maps this tool by reading
        // Inputs.OfType<HostedFileContent>() and nothing else, so raw bytes should never reach the
        // request — and should not throw on the way to being ignored. The marker is unique per run
        // because the container persists files across probes.
        var inline = new DataContent(Encoding.UTF8.GetBytes(marker), SandboxSession.PlainTextMediaType)
        {
            Name = "ep0-inline-input.txt",
        };

        session.SetInputs(inline);

        var run = await session.RunPythonAsync(
            ReadInputScript(marker), TestContext.Current.CancellationToken);

        var probe = await ProbeMarkerAsync(session, run, TestContext.Current.CancellationToken);

        _fixture.Record(
            "Does a DataContent input reach the sandbox?",
            probe.Delivered switch { false => "no, silently dropped", true => "yes", null => "the probe did not complete" },
            "Supplied as raw bytes on HostedCodeInterpreterTool.Inputs. The request was accepted "
            + $"without error. {probe.Detail} Dropped silently, so anything built on raw-byte inputs "
            + "cannot work and must upload through the Files API instead.");

        // Explicitly false, not merely "not true": a probe that never printed a result also finds
        // no marker, and reading that as a dropped input would pass by accident.
        Assert.False(probe.Delivered, probe.Detail);
    }

    [Fact]
    public async Task ProducedArtifact_IsDownloadableThroughTheContainerScope()
    {
        Assert.SkipUnless(_fixture.IsEnabled, SpikeConfiguration.SkipReason);

        var session = _fixture.Session;
        session.SetInputs();

        var run = await session.RunPythonAsync(
            SpikePaths.ReadScript("write-artifact.py"), TestContext.Current.CancellationToken);

        var artifact = run.Artifacts.FirstOrDefault(candidate => candidate.Name?.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) == true)
            ?? run.Artifacts.FirstOrDefault();

        _fixture.Record(
            "Can a produced artifact be read back by the API?",
            artifact is null ? "no artifact was surfaced" : "an artifact was surfaced",
            artifact is null
                ? $"Content shapes observed: {string.Join(", ", run.ObservedContentKinds)}."
                : $"Channel {artifact.Channel}, file {artifact.FileId ?? "(inline bytes)"}, "
                  + $"container scope {artifact.ContainerId ?? run.ContainerId ?? "not reported"}.");

        Assert.NotNull(artifact);

        var bytes = await session.DownloadAsync(artifact, TestContext.Current.CancellationToken);

        _fixture.Record(
            "Does the container-scoped download route answer under this deployment's api-key auth?",
            "yes",
            $"Downloaded {bytes.Length} bytes for {artifact.FileId ?? artifact.Name} through "
            + "IHostedFileClient.DownloadAsync with Scope set to the container id, which resolves to "
            + "GET {endpoint}/openai/v1/containers/{container}/files/{file}/content.");

        Assert.Contains("enterprise-gpt-file-agent-ep0", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    private static string ReadInputScript(string marker) =>
        SpikePaths.ReadScript("read-input.py").Replace("__MARKER__", marker, StringComparison.Ordinal);

    // Never throws, so the finding is recorded before anything can fail on it. Delivered is
    // nullable because "the sandbox could not see the marker" and "the probe never ran" are different
    // answers, and only one is evidence about the input mechanism.
    private static async Task<(bool? Delivered, string Detail)> ProbeMarkerAsync(
        SandboxSession session, SandboxRun run, CancellationToken cancellationToken)
    {
        try
        {
            using var result = await SpikeResults.RequireAsync(
                session, run, "input-probe-result.json", cancellationToken);

            return result.RootElement.GetProperty("markerFoundIn").GetString() is { Length: > 0 } location
                ? (true, $"The sandbox found the marker in {location}.")
                : (false, "The sandbox listed the mount and found no file carrying the marker.");
        }
        // Three realistic shapes: no RESULT line (InvalidOperationException); a truncated one
        // (JsonException — read-input.py puts the whole accumulating mount on that single line); an
        // edited script whose result lost the key (KeyNotFoundException, and ReadScript copies to
        // output precisely so a probe can be edited in place). Any escaping loses the finding.
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or KeyNotFoundException)
        {
            return (null, $"The probe did not run to completion, so nothing was established: {exception.Message}");
        }
    }
}
