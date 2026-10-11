namespace Enterprise.Gpt.Service.Agents.Models;

/// <summary>
/// The File Agent's catalog row as it stood when it was read.
/// </summary>
/// <param name="ModelId">The catalog row's identifier.</param>
/// <param name="ProviderId">The provider whose chat client serves it.</param>
/// <param name="DeploymentName">The provider-side identifier sent as <c>ChatOptions.ModelId</c>.</param>
public sealed record FileAgentModel(Guid ModelId, Guid ProviderId, string DeploymentName);
