using Enterprise.Gpt.Dto;
using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>
/// The File Agent, leased for the life of one turn.
/// </summary>
/// <remarks>
/// Disposal releases what the run borrowed — the sandbox uploads it made, and the skill provider it
/// built — so a lease that is not disposed leaks quota rather than merely memory.
/// </remarks>
public interface IFileAgentToolLease : IAsyncDisposable
{
    /// <summary>Gets the agent, wrapped as one tracked tool for the assistant's tool list.</summary>
    AITool Tool { get; }

    /// <summary>
    /// Gets the files the agent produced and stored during this turn, in the order it produced them.
    /// </summary>
    /// <remarks>
    /// Read when the turn is recorded, which happens inside the lease's own scope. Empty until the
    /// agent actually runs, and empty for a run that produced nothing.
    /// </remarks>
    IReadOnlyList<MessageAttachmentDto> GeneratedDocuments { get; }
}

/// <summary>
/// Composes the File Agent for a turn.
/// </summary>
/// <remarks>
/// Declared here and implemented in <c>Enterprise.Gpt.Api</c>: the agent is built on
/// <c>Microsoft.Agents.AI</c>, which this project deliberately does not reference. The shape mirrors
/// <see cref="IMcpToolProvider"/> for the same reason it exists — a turn borrows a tool and gives it
/// back.
/// </remarks>
public interface IFileAgentToolProvider
{
    /// <summary>
    /// Builds the agent for one turn.
    /// </summary>
    /// <param name="conversationId">The conversation the turn belongs to.</param>
    /// <param name="userId">The caller, whose ownership decides which files the agent may read.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The tool plus a disposable bounding its lifetime to the turn.</returns>
    /// <exception cref="Exceptions.FileAgentNotConfiguredException">
    /// The pinned model row is absent, deactivated, or on a provider that cannot carry a hosted code
    /// interpreter.
    /// </exception>
    /// <exception cref="Exceptions.ProviderNotConfiguredException">
    /// This deployment has no chat client for the agent.
    /// </exception>
    Task<IFileAgentToolLease> AcquireAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken);
}
