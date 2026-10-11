using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Agents.Models;

/// <summary>One cell of the confirmed conversion matrix.</summary>
/// <param name="From">The source format, lower-cased and without a leading dot.</param>
/// <param name="To">The target format, on the same terms.</param>
/// <param name="Tier">How much of the source survives the conversion; a tier this build does not recognise reads as <see cref="ConversionTiers.NotOffered"/>.</param>
/// <param name="Note">What the answer has to say about this pair, in the matrix's own words.</param>
public sealed record ConversionCell(string From, string To, ConversionTiers Tier, string Note);
