using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Dto
{
    /// <summary>
    /// Upload job status returned to polling clients.
    /// </summary>
    /// <remarks>
    /// <see cref="Id"/>, <see cref="State"/>, <see cref="Status"/> and <see cref="Progress"/> keep their
    /// original names and semantics; everything else is additive, so a client written against the previous
    /// shape keeps working unchanged.
    /// </remarks>
    public class JobStatusDto
    {
        /// <summary>The job identifier.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = null!;

        /// <summary>Coarse state: <c>Enqueued</c>, <c>Processing</c>, <c>Succeeded</c> or <c>Failed</c>.</summary>
        [JsonPropertyName("state")]
        public string State { get; set; } = null!;

        /// <summary>The fine-grained pipeline stage.</summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = null!;

        /// <summary>Completion percentage in the range 0..100. Never decreases across polls.</summary>
        [JsonPropertyName("progress")]
        public int Progress { get; set; } = 0;

        /// <summary>A short description of what the job is doing right now.</summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        /// <summary>Units of work finished in the current stage, when countable.</summary>
        [JsonPropertyName("completedUnits")]
        public int? CompletedUnits { get; set; }

        /// <summary>Total units of work in the current stage, when countable.</summary>
        [JsonPropertyName("totalUnits")]
        public int? TotalUnits { get; set; }

        /// <summary>The created document, available once the job has succeeded.</summary>
        [JsonPropertyName("documentId")]
        public Guid? DocumentId { get; set; }

        /// <summary>A user-safe failure description, populated only when the job failed.</summary>
        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        /// <summary>When the status last changed, so clients can show staleness.</summary>
        [JsonPropertyName("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
