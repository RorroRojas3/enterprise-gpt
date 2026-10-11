namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// The dimensions <c>GET api/reports/usage</c> can group by.
/// </summary>
public enum UsageGroupKeys
{
    /// <summary>The catalog model that served each call.</summary>
    Model,

    /// <summary>The provider that served each call.</summary>
    Provider,

    /// <summary>The user each call was charged to.</summary>
    User,

    /// <summary>The UTC day each call finished on.</summary>
    Day
}
