namespace Enterprise.Gpt.Dto.Enums
{
    /// <summary>
    /// Contains the fixed identifiers of built-in permissions seeded by the database model.
    /// </summary>
    public static class PermissionIds
    {
        /// <summary>
        /// The built-in Administrator permission. This value must match the seeded
        /// <c>Core.Permission</c> row exactly; out-of-band data migrations must use it verbatim.
        /// </summary>
        public static readonly Guid Administrator = new("a0b1c2d3-e4f5-4a6b-8c7d-9e0f1a2b3c4d");
    }
}
