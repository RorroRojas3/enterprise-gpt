using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Dto
{
    public sealed record UserDto
    {
        [JsonPropertyName("id")]
        public Guid Id { get; init; }

        [JsonPropertyName("firstName")]
        public string FirstName { get; init; } = null!;

        [JsonPropertyName("lastName")]
        public string LastName { get; init; } = null!;

        [JsonPropertyName("email")]
        public string Email { get; init; } = null!;

        [JsonPropertyName("permissions")]
        public IReadOnlyList<Permissions> Permissions { get; init; } = [];

        [JsonPropertyName("fullName")]
        public string FullName => $"{FirstName} {LastName}".Trim();
    }
}
