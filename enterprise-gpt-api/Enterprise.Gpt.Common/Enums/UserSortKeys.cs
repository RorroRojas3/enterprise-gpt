namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// The fields <c>GET api/users</c> can order on.
/// </summary>
public enum UserSortKeys
{
    /// <summary>Last name then first name — the directory's own default order.</summary>
    Name,

    Email,

    /// <summary>When the row was provisioned, by an administrator or by first sign-in.</summary>
    Created
}
