using System.Text.Json;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Agents.Models;

/// <summary>One cell of the confirmed conversion matrix.</summary>
/// <param name="Note">What the answer has to say about this pair, in the matrix's own words.</param>
public sealed record ConversionCell(string From, string To, ConversionTiers Tier, string Note);
