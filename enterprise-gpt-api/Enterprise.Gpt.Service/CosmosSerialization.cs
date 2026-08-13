using System.Text.Json;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Service;

/// <summary>
/// The serializer settings the <c>CosmosClient</c> is configured with, exposed so the application,
/// the test host, and round-trip tests all agree on one JSON contract.
/// </summary>
/// <remarks>
/// The SDK's own <c>CosmosSerializationOptions</c> selects a Newtonsoft-backed serializer, which
/// ignores the <see cref="JsonPropertyNameAttribute"/> annotations the document types carry — the
/// two agreed only by coincidence, and a query whose predicates were written against the
/// annotations once silently matched nothing. Configuring System.Text.Json makes those
/// annotations the contract rather than a comment.
/// </remarks>
public static class CosmosSerialization
{
    /// <summary>
    /// Camel-cased property names with string-valued enums.
    /// </summary>
    /// <remarks>
    /// Enums serialize by name so a stored document stays readable and an exported one does not
    /// need the enum's numbering to be interpreted.
    /// </remarks>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}
