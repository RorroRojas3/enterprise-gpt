namespace Enterprise.Gpt.Common.Constants
{
    public static class ModelDefaults
    {
        // Seeded "gpt-5-mini" model (see ModelConfiguration.HasData). Used as the
        // implicit favorite for users who have not picked one yet. The value must
        // stay in sync with the seed GUID — do not change without a data migration.
        public const string DefaultModelId = "c36e22ed-262a-47a1-b2ba-06a38355ae0f";
    }
}
