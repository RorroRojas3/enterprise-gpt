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

        /// <summary>
        /// The built-in Upload File permission, seeded with <c>IsDefault</c> set so every user receives it.
        /// This value must match the seeded <c>Core.Permission</c> row exactly; out-of-band data migrations
        /// must use it verbatim.
        /// </summary>
        public static readonly Guid UploadFile = new("b1c2d3e4-f5a6-4b7c-8d9e-0f1a2b3c4d5e");
    }
}
