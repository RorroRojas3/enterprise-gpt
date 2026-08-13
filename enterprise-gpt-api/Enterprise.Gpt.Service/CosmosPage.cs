namespace Enterprise.Gpt.Service;

/// <summary>
/// One page of a Cosmos query, with the token needed to request the next.
/// </summary>
/// <typeparam name="T">The item type the query projects to.</typeparam>
/// <param name="Items">The items in this page, in query order.</param>
/// <param name="ContinuationToken">
/// The token to pass back for the following page, or <see langword="null"/> when this page is the
/// last. A page can be empty and still carry a token, because Cosmos may return a page whose
/// matching items were all filtered out on the server.
/// </param>
public sealed record CosmosPage<T>(IReadOnlyList<T> Items, string? ContinuationToken);
