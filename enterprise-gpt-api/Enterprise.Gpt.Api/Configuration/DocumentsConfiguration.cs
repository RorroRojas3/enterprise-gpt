using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Service.Documents;
using Enterprise.Gpt.Service.Documents.Chunking;
using Enterprise.Gpt.Service.Documents.Extraction;
using Enterprise.Gpt.Service.Documents.Extraction.Interfaces;
using Enterprise.Gpt.Service.Documents.Retrieval;
using Enterprise.Gpt.Service.Documents.Sheets;
using Enterprise.Gpt.Service.Documents.Summarization;
using Enterprise.Gpt.Service.Options;
using FluentValidation;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers documents: ingestion, extraction, retrieval, the sheet query tool and summarization.
/// </summary>
internal static class DocumentsConfiguration
{
    #region Private methods

    private static void AddValidatedOptions<TOptions, TValidator>(IServiceCollection services, IConfiguration configuration, string sectionName)
        where TOptions : class
        where TValidator : class, IValidator<TOptions>
    {
        services.AddSingleton<IValidator<TOptions>, TValidator>();
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateWithFluentValidation()
            .ValidateOnStart();
    }

    #endregion

    #region Public static methods

    /// <summary>
    /// Registers the document services and their validated options.
    /// </summary>
    internal static IServiceCollection AddDocuments(this IServiceCollection services, IConfiguration configuration)
    {
        AddValidatedOptions<DocumentOptions, DocumentOptionsValidator>(services, configuration, DocumentOptions.SectionName);
        // A workbook is a compressed archive, so these ceilings, not the upload limit, bound what one upload expands into.
        AddValidatedOptions<SheetOptions, SheetOptionsValidator>(services, configuration, SheetOptions.SectionName);
        AddValidatedOptions<DocumentRetrievalOptions, DocumentRetrievalOptionsValidator>(services, configuration, DocumentRetrievalOptions.SectionName);
        AddValidatedOptions<SheetQueryOptions, SheetQueryOptionsValidator>(services, configuration, SheetQueryOptions.SectionName);
        // Only that a model id was supplied; that it resolves to a usable row is checked after migration.
        AddValidatedOptions<SummarizationOptions, SummarizationOptionsValidator>(services, configuration, SummarizationOptions.SectionName);

        // Read through this rather than IOptions, so switching the tool off reaches the next turn rather than the
        // next restart. Singleton, because its consumers are scoped and could not otherwise keep the last
        // configuration that validated.
        services.AddSingleton<ISheetQueryOptionsProvider, SheetQueryOptionsProvider>();

        services.AddSingleton<IDocumentIntelligenceService, DocumentIntelligenceService>();

        // Registering an IDocumentTextExtractor is all a new format needs: the factory, the upload validator and
        // the file-extensions endpoint all read the accepted formats from these registrations.
        services.AddSingleton<ILegacyWordConverter, DocSharpLegacyWordConverter>();
        services.AddSingleton<IDocumentTextExtractor, DocumentIntelligenceTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, PresentationTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, PlainTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, SpreadsheetTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, CsvTextExtractor>();
        services.AddSingleton<IDocumentTextExtractorFactory, DocumentTextExtractorFactory>();

        // A singleton: constructing its tokenizer loads a large vocabulary.
        services.AddSingleton<ITextChunker, TokenTextChunker>();

        // Registered explicitly: it depends on the options and the extractor factory, so no endpoint filter can
        // run it, and a Dto-assembly scan does not reach it.
        services.AddScoped<IValidator<FileDto>, FileDtoValidator>();

        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IGeneratedDocumentService, GeneratedDocumentService>();
        services.AddScoped<IDocumentRetrievalService, DocumentRetrievalService>();
        services.AddScoped<ISheetQueryService, SheetQueryService>();

        services.AddScoped<ISummarizerModelResolver, SummarizerModelResolver>();
        services.AddScoped<IDocumentTextAssembler, DocumentTextAssembler>();
        services.AddScoped<IDocumentSummarizer, DocumentSummarizer>();
        services.AddScoped<IDocumentSummaryService, DocumentSummaryService>();

        return services;
    }

    #endregion
}
