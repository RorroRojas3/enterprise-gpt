namespace Enterprise.Gpt.Common.Constants;

/// <summary>
/// Host environment names this application branches on beyond the framework's own.
/// </summary>
public static class HostEnvironments
{
    /// <summary>The integration host: no vault, no migrations, no Cosmos provisioning.</summary>
    public const string Testing = "Testing";
}
