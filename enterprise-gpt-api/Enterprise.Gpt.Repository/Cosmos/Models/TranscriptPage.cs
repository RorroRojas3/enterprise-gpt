using System.Net;
using System.Text.Json.Serialization;
using Enterprise.Gpt.Repository.Cosmos.Models;
using Enterprise.Gpt.Repository.Cosmos.Options;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Repository.Cosmos.Models;

/// <summary>
/// One page of a transcript query.
/// </summary>
/// <typeparam name="T">The projected document type.</typeparam>
/// <param name="Items">The documents on this page, in query order.</param>
/// <param name="ContinuationToken">
/// The token that fetches the next page, or <see langword="null"/> when this is the last page.
/// </param>
public sealed record TranscriptPage<T>(IReadOnlyList<T> Items, string? ContinuationToken);
