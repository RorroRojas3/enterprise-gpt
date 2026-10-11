namespace Enterprise.Gpt.Service.Prompts;

/// <summary>Reads a prompt template copied to the output under <c>Prompts/</c>.</summary>
/// <remarks>
/// The folder is not globbed in the project file: a template added without its own <c>None Update</c> entry
/// builds green and fails at first use.
/// </remarks>
internal static class PromptTemplateLoader
{
    #region Public static methods

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

    #endregion
}
