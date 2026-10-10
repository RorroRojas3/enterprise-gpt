namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// The fields <c>GET api/conversations/search</c> can order on.
/// </summary>
/// <remarks>
/// <c>DateModified</c> is deliberately absent. It moves on a rename, a favourite, a model change and
/// a move between projects as readily as on a turn, so a "recently active" sort built on it would
/// promise something the column cannot answer — the transcript, which is where activity actually
/// lands, lives in Cosmos and is not queryable from here.
/// </remarks>
public enum ConversationSortKeys
{
    /// <summary>When the conversation was started.</summary>
    Created,

    /// <summary>The conversation name.</summary>
    Name
}
