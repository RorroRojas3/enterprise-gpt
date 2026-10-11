using System.Text.Json;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Repository.Cosmos.Serialization;

/// <summary>
/// Builds the System.Text.Json options the Cosmos DB client serializes every document with.
/// </summary>
/// <remarks>
/// One definition so tests and export serialize documents exactly as stored; the SDK's Newtonsoft default
/// ignores <c>[JsonPropertyName]</c>.
/// </remarks>
public static class CosmosSerialization
{
    #region Public static methods

    /// <summary>Creates the serializer options for the Cosmos DB client.</summary>
    /// <remarks>
    /// A fresh instance per call because options freeze on first use. String enums are Cosmos-only: the
    /// HTTP API serializes enums as integers and clients depend on that.
    /// </remarks>
    public static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters =
            {
                // PropertyNamingPolicy does not reach enum values; without this, stored roles change casing.
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
                new CosmosUtcDateTimeOffsetConverter()
            }
        };
    }

    #endregion
}
