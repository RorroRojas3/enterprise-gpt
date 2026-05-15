namespace RR.AI_Chat.Service.Prompts
{
    /// <summary>
    /// Loads and parameterises conversation system prompts from markdown templates
    /// shipped alongside the assembly under the <c>Prompts/</c> folder.
    /// </summary>
    /// <remarks>
    /// Templates are read once at type initialisation. If a template file is missing,
    /// the type initialiser throws <see cref="FileNotFoundException"/>, surfaced to
    /// callers as <see cref="TypeInitializationException"/>.
    /// </remarks>
    public static class ConversationPrompts
    {
        private static readonly string DefaultSystemPromptTemplate =
            LoadTemplate("conversation-default-system-prompt.md");

        private static readonly string NamingPromptTemplate =
            LoadTemplate("conversation-naming-prompt.md");

        #region Public static methods

        /// <summary>
        /// Returns the default system prompt for a conversation.
        /// </summary>
        /// <returns>The system prompt text.</returns>
        public static string BuildDefaultSystemPrompt() => DefaultSystemPromptTemplate;

        /// <summary>
        /// Returns the system prompt used to generate short, PII-safe conversation titles.
        /// </summary>
        /// <returns>The naming prompt text.</returns>
        public static string BuildNamingPrompt() => NamingPromptTemplate;

        #endregion

        #region Private methods

        private static string LoadTemplate(string fileName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Prompts", fileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Conversation prompt template '{fileName}' not found at '{path}'.", path);
            }

            return File.ReadAllText(path);
        }

        #endregion
    }
}
