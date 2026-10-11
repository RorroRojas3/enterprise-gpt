namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// The fields <c>GET api/conversations/search</c> can order on.
/// </summary>
/// <remarks>
/// No <c>DateModified</c>: renames, favourites and project moves bump it as readily as turns, and real
/// activity lives in the Cosmos transcript, so a "recently active" sort would mislead.
/// </remarks>
public enum ConversationSortKeys
{
    Created,

    Name
}
