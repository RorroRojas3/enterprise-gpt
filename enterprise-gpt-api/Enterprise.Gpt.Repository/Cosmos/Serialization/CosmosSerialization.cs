using System.Text.Json;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Service.Serialization;

/// <summary>
/// Builds the System.Text.Json options the Cosmos DB client serializes every document with.
/// </summary>
/// <remarks>
/// <para>
/// Exposed as a factory rather than inlined at client registration so the storage tests and the
/// export path can serialize a document exactly as it is stored, and so the naming contract has
/// one definition. The SDK's own default is Newtonsoft, which never reads the
/// <c>[JsonPropertyName]</c> attributes the document types carry; the two agreed only because the
/// SDK's camel-case policy happened to produce the same names, and a cross-partition query whose
/// predicates did not match those names is a bug this repository has already recorded once.
/// </para>
/// <para>
/// A fresh instance per call, not a cached singleton: <see cref="JsonSerializerOptions"/> becomes
/// immutable on first use, and a shared instance handed to a caller that adds a converter would
/// throw at an unrelated call site.
/// </para>
/// </remarks>
public static class CosmosSerialization
{
    /// <summary>
    /// Creates the serializer options for the Cosmos DB client.
    /// </summary>
    /// <returns>Options with camel-case naming, string enums, and UTC fixed-width timestamps.</returns>
    /// <remarks>
    /// Enums are written as strings so a stored document stays readable — and so <c>role</c> reads
    /// <c>"assistant"</c> rather than <c>2</c>, matching the streaming contract. The naming policy
    /// has to be handed to the converter explicitly: <see cref="JsonSerializerOptions.PropertyNamingPolicy"/>
    /// applies to property names only and leaves enum values at their declared casing.
    /// This is all scoped to the Cosmos client on purpose: the HTTP API registers no string-enum
    /// converter, so its DTOs serialize enums as integers and clients already depend on that.
    /// </remarks>
    public static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
                new CosmosUtcDateTimeOffsetConverter()
            }
        };
    }
}
