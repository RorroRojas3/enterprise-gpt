using FluentValidation;

namespace Enterprise.Gpt.Service.Options;

/// <summary>
/// Azure Document Intelligence settings, bound from the <c>DocumentIntelligence</c> configuration section.
/// </summary>
/// <remarks>
/// Endpoint and key are not required at startup; a blank one fails when the client is first built, which
/// the document extractors trigger on their first use.
/// </remarks>
public sealed class DocumentIntelligenceOptions
{
    /// <summary>The configuration section this type binds from.</summary>
    public const string SectionName = "DocumentIntelligence";

    /// <summary>The resource endpoint.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The resource key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The analysis model every OCR request names. Default: <c>prebuilt-read</c>.</summary>
    public string OCRModelId { get; set; } = "prebuilt-read";

    /// <summary>How long one document analysis may run. Default: 10 minutes.</summary>
    public TimeSpan AnalysisTimeout { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>Validates <see cref="DocumentIntelligenceOptions"/>.</summary>
public sealed class DocumentIntelligenceOptionsValidator : AbstractValidator<DocumentIntelligenceOptions>
{
    public DocumentIntelligenceOptionsValidator()
    {
        RuleFor(options => options.Endpoint)
            .Must(endpoint => string.IsNullOrWhiteSpace(endpoint) || Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            .WithMessage("DocumentIntelligence:Endpoint must be an absolute URI when it is set.");
        RuleFor(options => options.OCRModelId).NotEmpty()
            .WithMessage("DocumentIntelligence:OCRModelId is required.");
        RuleFor(options => options.AnalysisTimeout).GreaterThan(TimeSpan.Zero)
            .WithMessage("DocumentIntelligence:AnalysisTimeout must be greater than zero.");
    }
}
