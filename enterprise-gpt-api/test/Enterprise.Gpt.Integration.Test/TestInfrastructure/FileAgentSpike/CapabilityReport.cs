using System.Text.Json.Serialization;
using System.Text.Json;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentSpike;

/// <summary>
/// What the Responses route actually does in this tenant.
/// </summary>
/// <param name="Deployment">The deployment name, never the endpoint: the resource hostname is the one
/// tenant-identifying string that could land here, and it says nothing about the sandbox.</param>
/// <param name="Findings">Every question asked, in the order asked.</param>
internal sealed record CapabilityReport(
    DateTimeOffset RecordedUtc,
    string Deployment,
    IReadOnlyList<CapabilityFinding> Findings);
