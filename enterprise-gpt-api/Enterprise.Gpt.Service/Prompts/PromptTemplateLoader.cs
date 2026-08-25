namespace Enterprise.Gpt.Service.Prompts;

/// <summary>
/// Reads a prompt template shipped alongside the assembly under the <c>Prompts/</c> folder.
/// </summary>
/// <remarks>
/// Templates are loaded from static field initialisers, so a missing file surfaces as a
/// <see cref="TypeInitializationException"/> on first touch of the prompt class rather than as an
/// empty prompt silently sent to a model. The folder is not globbed in the project file: a template
/// added without its own <c>None Update</c> entry builds green and fails at first use.
/// </remarks>
internal static class PromptTemplateLoader
{
    /// <summary>
    /// Reads a template file's full text.
    /// </summary>
    /// <param name="fileName">The file name, relative to the <c>Prompts/</c> folder.</param>
    /// <returns>The template text.</returns>
    /// <exception cref="FileNotFoundException">The template was not copied to the output.</exception>
    internal static string Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Prompt template '{fileName}' not found at '{path}'.", path);
        }

        return File.ReadAllText(path);
    }
}
