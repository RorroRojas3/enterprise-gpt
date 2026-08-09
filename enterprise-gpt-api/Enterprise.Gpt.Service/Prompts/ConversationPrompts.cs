using System.Globalization;

namespace Enterprise.Gpt.Service.Prompts
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

        private static readonly string ProjectInstructionsPromptTemplate =
            LoadTemplate("project-instructions-prompt.md");

        private static readonly string DocumentRetrievalPromptTemplate =
            LoadTemplate("document-retrieval-prompt.md");

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

        /// <summary>
        /// Wraps a project's standing instructions in the framing that tells the model how much
        /// authority they carry.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The instructions are user-authored free text carried on <c>ChatOptions.Instructions</c>,
        /// so the frame exists to bound them: it marks where they start and end, states that they
        /// rank below the platform system prompt, and tells the model not to treat them as content
        /// to echo back.
        /// </para>
        /// <para>
        /// The delimiter is an unguessable per-call token rather than a fixed string such as
        /// <c>---</c>. A fixed delimiter can be reproduced inside the instruction body, which lets
        /// the text appear to close its own block and continue as if it were part of the frame.
        /// </para>
        /// <para>
        /// <c>project-instructions-prompt.md</c> is the one template that is a composite format
        /// string, not literal text: <c>{0}</c> is the instruction body and <c>{1}</c> the
        /// delimiter. Any literal brace added to that file must be doubled, or every project
        /// conversation fails with a <see cref="FormatException"/>. The instruction body itself is
        /// safe — it is an argument, never re-parsed.
        /// </para>
        /// </remarks>
        /// <param name="instructions">The project's instructions.</param>
        /// <returns>The system prompt text carrying the instructions.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="instructions"/> is <see langword="null"/> or whitespace.</exception>
        public static string BuildProjectInstructionsPrompt(string instructions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instructions);

            var delimiter = $"<<<project-instructions:{Guid.NewGuid():N}>>>";

            return string.Format(CultureInfo.InvariantCulture, ProjectInstructionsPromptTemplate, instructions, delimiter);
        }

        /// <summary>
        /// Builds the instructions that tell the model documents are attached, which ones, and how to
        /// search and cite them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The model cannot see document contents — only the retrieval tool can — so without this it
        /// treats attached files as background it may ignore and answers from its own knowledge instead.
        /// Naming the documents is what lets it decide whether a question is worth a search at all.
        /// </para>
        /// <para>
        /// Carried on <c>ChatOptions.Instructions</c> rather than written into the transcript, for the
        /// same reason project instructions are: the corpus is derived from the request and changes as
        /// documents are added or removed, while the transcript is the conversation's own fixed record.
        /// </para>
        /// <para>
        /// <c>document-retrieval-prompt.md</c> is a composite format string: <c>{0}</c> is the tool name
        /// and <c>{1}</c> the document list. Any literal brace added to that file must be doubled, or
        /// every conversation with a document fails with a <see cref="FormatException"/>. The names
        /// themselves are arguments, never re-parsed, so a brace in a file name is safe.
        /// </para>
        /// <para>
        /// The names do reach the system instructions verbatim, which is only safe because a project
        /// holds one user's conversations: the worst an attacker-chosen file name can do today is
        /// influence that same user's turn. Should projects ever become shareable, a name would become a
        /// cross-user injection channel <em>inside</em> the instructions, where the template's trust
        /// section — which speaks only about passage text — would not cover it.
        /// </para>
        /// </remarks>
        /// <param name="toolName">The name the retrieval tool is registered under.</param>
        /// <param name="documentNames">The names of the attached documents.</param>
        /// <returns>The system prompt text describing the attached documents.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="toolName"/> is <see langword="null"/> or whitespace.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="documentNames"/> is <see langword="null"/>.</exception>
        public static string BuildDocumentRetrievalPrompt(string toolName, IReadOnlyList<string> documentNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
            ArgumentNullException.ThrowIfNull(documentNames);

            var list = string.Join("\n", documentNames.Select(name => $"- {name}"));

            return string.Format(CultureInfo.InvariantCulture, DocumentRetrievalPromptTemplate, toolName, list);
        }

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
