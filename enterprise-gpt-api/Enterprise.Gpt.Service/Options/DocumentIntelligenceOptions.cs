using FluentValidation;

namespace Enterprise.Gpt.Service.Options;

/// <summary>
/// Endpoint and key are optional at startup; a blank one fails when an extractor first builds the client.
/// </summary>
public sealed class DocumentIntelligenceOptions
{
    public const string SectionName = "DocumentIntelligence";

    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string OCRModelId { get; set; } = "prebuilt-read";

    public TimeSpan AnalysisTimeout { get; set; } = TimeSpan.FromMinutes(10);
}

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
