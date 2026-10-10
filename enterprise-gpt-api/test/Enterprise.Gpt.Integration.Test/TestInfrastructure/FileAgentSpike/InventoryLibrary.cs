using System.Text.Json.Serialization;
using System.Text.Json;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentSpike;

/// <summary>
/// One Python library the sandbox was asked about.
/// </summary>
/// <param name="Module">The import name, which is not always the package name.</param>
/// <param name="Package">The distribution name a skill would cite.</param>
internal sealed record InventoryLibrary(string Module, string Package, bool Importable, string? Version);
