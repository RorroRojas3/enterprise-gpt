using System.Net;
using System.Text.Json.Serialization;

namespace RR.AI_Chat.Dto
{
    public class ErrorDto
    {
        [JsonPropertyName("statusCode")]
        public HttpStatusCode StatusCode { get; set; }

        [JsonPropertyName("errors")]
        public List<string> Errors { get; set; } = [];

        [JsonPropertyName("traceId")]
        public string TraceId { get; set; } = null!;

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    }
}
