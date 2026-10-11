namespace Enterprise.Gpt.Service.Agents.Exceptions;

/// <summary>
/// Thrown when <c>FileAgent:ModelId</c> names no active model row, or one on a provider without a hosted
/// code interpreter. Raised at startup and again per turn, since the row can change while running.
/// </summary>
public class FileAgentNotConfiguredException(Guid modelId, string message) : Exception(message)
{
    /// <summary>For a configured id that resolves to no active catalog row.</summary>
    public FileAgentNotConfiguredException(Guid modelId)
        : this(modelId, $"FileAgent:ModelId '{modelId}' names no active model in the catalog.")
    {
    }

    public Guid ModelId { get; } = modelId;
}
