using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Common.Enums
{
    /// <summary>
    /// How a message's token count was arrived at.
    /// </summary>
    /// <remarks>
    /// Serialized by name rather than by number. This application configures no global string-enum
    /// converter for its HTTP responses, so the attribute is what keeps the transcript document,
    /// the API response and an export all reading <c>"Estimated"</c> rather than <c>1</c>.
    /// </remarks>
    [JsonConverter(typeof(JsonStringEnumConverter<TokenAccuracies>))]
    public enum TokenAccuracies
    {
        /// <summary>
        /// Counted locally with a tokenizer that approximates the provider's own.
        /// </summary>
        Estimated = 1,

        /// <summary>
        /// Reported by the provider that billed the tokens.
        /// </summary>
        Exact = 2
    }
}
