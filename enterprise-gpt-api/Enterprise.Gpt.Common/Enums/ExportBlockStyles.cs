namespace Enterprise.Gpt.Service.Export;

/// <summary>
/// How a block is set apart from running body text.
/// </summary>
/// <remarks>
/// The block model says what a message means; this says what encloses it, which only the renderer
/// walking the tree knows. Flags rather than an enum because a quote inside a prompt is both.
/// </remarks>
[Flags]
public enum ExportBlockStyle
{
    /// <summary>Body text, enclosed by nothing.</summary>
    None = 0,

    /// <summary>Part of a prompt, which every format bands.</summary>
    Prompt = 1,

    /// <summary>Inside a block quote.</summary>
    Quote = 2
}
