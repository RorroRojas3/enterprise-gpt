namespace Enterprise.Gpt.Service.Settings
{
    /// <summary>
    /// Tuning knobs for the per-user permission cache, bound from the <c>Permissions:Cache</c>
    /// configuration section.
    /// </summary>
    public sealed class UserPermissionCacheOptions
    {
        /// <summary>
        /// The configuration section this type binds from.
        /// </summary>
        public const string SectionName = "Permissions:Cache";

        /// <summary>
        /// How long a cached grant set is served before it is reloaded. Default: 5 minutes.
        /// </summary>
        /// <remarks>
        /// A safety net, not the primary invalidation mechanism: the services that mutate grants
        /// evict the affected users immediately, and that eviction is exact on the instance that
        /// served the mutation. This lifetime bounds the one case eviction cannot reach — an
        /// instance that did <em>not</em> serve the mutation — so it is the window in which a
        /// deactivated administrator retains access elsewhere in a scaled-out deployment. Shorten it
        /// before scaling out widely; lengthen it only for a single-instance deployment.
        /// </remarks>
        public TimeSpan EntryLifetime { get; set; } = TimeSpan.FromMinutes(5);
    }
}
