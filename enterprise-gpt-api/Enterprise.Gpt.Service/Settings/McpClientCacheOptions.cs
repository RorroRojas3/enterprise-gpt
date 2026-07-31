namespace Enterprise.Gpt.Service.Settings
{
    /// <summary>
    /// Tuning knobs for the MCP client/tool cache, bound from the <c>Mcp:Cache</c>
    /// configuration section. Defaults assume standard Entra ID access-token lifetimes
    /// (60–90 minutes).
    /// </summary>
    public sealed class McpClientCacheOptions
    {
        /// <summary>
        /// How long before the access token's expiry a cached entry stops being served, so a
        /// token is never presented to an MCP server within this window of expiring (covers
        /// clock skew and long-running tool calls). Default: 5 minutes.
        /// </summary>
        public TimeSpan TokenExpiryBuffer { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Hard cap on a cache entry's age regardless of token lifetime; the only TTL for
        /// servers without authentication, bounding how stale a shared tool list can get.
        /// Default: 30 minutes.
        /// </summary>
        public TimeSpan MaxEntryLifetime { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Cached entries with less remaining lifetime than this are recreated before being
        /// handed to a stream, so a starting stream gets at least this much tool-call
        /// headroom. Applies to cached entries only — a freshly created entry is always
        /// served, preventing recreate loops when the identity provider returns short-lived
        /// tokens. Default: 10 minutes.
        /// </summary>
        public TimeSpan MinRemainingLifetimeForStream { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Timeout for establishing the connection to an MCP server, passed to the transport.
        /// Default: 30 seconds.
        /// </summary>
        public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Interval of the background sweep that disposes expired, unleased entries so idle
        /// connections don't linger until process exit. Default: 5 minutes.
        /// </summary>
        public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);
    }
}
