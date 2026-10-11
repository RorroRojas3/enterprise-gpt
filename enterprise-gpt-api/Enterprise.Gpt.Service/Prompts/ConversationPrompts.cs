using System.Globalization;
using System.Text;

namespace Enterprise.Gpt.Service.Prompts;

/// <summary>Loads and parameterises the conversation system prompts in the <c>Prompts/</c> templates.</summary>
/// <remarks>
/// Templates load once at type initialisation, so a missing file surfaces as
/// <see cref="TypeInitializationException"/>. Templates with placeholders are composite format strings: double
/// any literal brace added to one, or every prompt built from it throws <see cref="FormatException"/>.
/// </remarks>
public static class ConversationPrompts
{
    private static readonly string DefaultSystemPromptTemplate =
        LoadTemplate("conversation-default-system-prompt.md");

    private static readonly string NamingPromptTemplate =
        LoadTemplate("conversation-naming-prompt.md");

    private static readonly CompositeFormat ProjectInstructionsPromptTemplate =
        CompositeFormat.Parse(LoadTemplate("project-instructions-prompt.md"));

    private static readonly CompositeFormat DocumentRetrievalPromptTemplate =
        CompositeFormat.Parse(LoadTemplate("document-retrieval-prompt.md"));

    private static readonly CompositeFormat DocumentSummaryToolPromptTemplate =
        CompositeFormat.Parse(LoadTemplate("document-summary-tool-prompt.md"));

    private static readonly CompositeFormat SheetQueryToolPromptTemplate =
        CompositeFormat.Parse(LoadTemplate("sheet-query-tool-prompt.md"));

    private static readonly CompositeFormat FileAgentInstructionsTemplate =
        CompositeFormat.Parse(LoadTemplate("file-agent-instructions.md"));

    #region Private methods

    private static string LoadTemplate(string fileName) => PromptTemplateLoader.Load(fileName);

    #endregion

    #region Public static methods

    /// <summary>Returns the default conversation system prompt.</summary>
    public static string BuildDefaultSystemPrompt() => DefaultSystemPromptTemplate;

    /// <summary>Returns the system prompt used to generate short, PII-safe conversation titles.</summary>
    public static string BuildNamingPrompt() => NamingPromptTemplate;

    /// <summary>
    /// Wraps a project's user-authored instructions in a frame that ranks them below the platform prompt.
    /// </summary>
    /// <remarks>
    /// The delimiter is a random per-call token because a fixed one could be reproduced inside the body to
    /// close the block early and impersonate the frame.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="instructions"/> is null or whitespace.</exception>
    public static string BuildProjectInstructionsPrompt(string instructions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);

        var delimiter = $"<<<project-instructions:{Guid.NewGuid():N}>>>";

        return string.Format(CultureInfo.InvariantCulture, ProjectInstructionsPromptTemplate, instructions, delimiter);
    }

    /// <summary>
    /// Builds the instructions that name the attached documents and tell the model to search and cite them.
    /// </summary>
    /// <remarks>
    /// Goes on <c>ChatOptions.Instructions</c>, not the transcript, because the document set changes per request.
    /// File names reach the instructions verbatim, which is safe only while a project has a single user.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="toolName"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="documentNames"/> is null.</exception>
    public static string BuildDocumentRetrievalPrompt(string toolName, IReadOnlyList<string> documentNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(documentNames);

        var list = string.Join("\n", documentNames.Select(name => $"- {name}"));

        return string.Format(CultureInfo.InvariantCulture, DocumentRetrievalPromptTemplate, toolName, list);
    }

    /// <summary>Builds the prompt describing the summarization tool and when to use it over retrieval.</summary>
    /// <remarks>
    /// Separate from <see cref="BuildDocumentRetrievalPrompt"/> because the tools attach under different
    /// conditions, and a prompt must never describe an absent tool. Document names are deliberately left to the
    /// retrieval prompt.
    /// </remarks>
    /// <exception cref="ArgumentException">Either name is <see langword="null"/> or whitespace.</exception>
    public static string BuildDocumentSummaryToolPrompt(string summaryToolName, string retrievalToolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryToolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(retrievalToolName);

        return string.Format(
            CultureInfo.InvariantCulture, DocumentSummaryToolPromptTemplate, summaryToolName, retrievalToolName);
    }

    /// <summary>Builds the prompt describing the sheet-query tool and when to prefer it over retrieval.</summary>
    /// <remarks>
    /// Re-lists the spreadsheets so the model knows which files it can compute over, rather than summing
    /// figures read from passages.
    /// </remarks>
    /// <exception cref="ArgumentException">Either name is <see langword="null"/> or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="spreadsheetNames"/> is null.</exception>
    public static string BuildSheetQueryToolPrompt(
        string sheetToolName, string retrievalToolName, IReadOnlyList<string> spreadsheetNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sheetToolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(retrievalToolName);
        ArgumentNullException.ThrowIfNull(spreadsheetNames);

        var list = string.Join("\n", spreadsheetNames.Select(name => $"- {name}"));

        return string.Format(
            CultureInfo.InvariantCulture, SheetQueryToolPromptTemplate, sheetToolName, list, retrievalToolName);
    }

    /// <summary>Builds the File Agent's own instructions, naming the files it may be asked to work from.</summary>
    /// <param name="documentNames">The files available to this run; may be empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="documentNames"/> is null.</exception>
    public static string BuildFileAgentPrompt(IReadOnlyList<string> documentNames)
    {
        ArgumentNullException.ThrowIfNull(documentNames);

        var list = documentNames.Count == 0
            ? "There are no files in this conversation, so every request is a create."
            : string.Join("\n", documentNames.Select(name => $"- {name}"));

        return string.Format(CultureInfo.InvariantCulture, FileAgentInstructionsTemplate, list);
    }

    #endregion
}
