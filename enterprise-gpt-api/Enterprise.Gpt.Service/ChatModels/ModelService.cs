using Enterprise.Gpt.Dto.Actions.ChatModels;
using Enterprise.Gpt.Dto.ChatModels;
using Enterprise.Gpt.Entity.ChatModels;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.ChatModels.Exceptions;
using Enterprise.Gpt.Service.Conversations.Chat;
using Enterprise.Gpt.Service.Conversations.Chat.Exceptions;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Extensions;
using Enterprise.Gpt.Service.Options;
using Enterprise.Gpt.Service.Security;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.ChatModels;

public interface IModelService
{
    /// <summary>Gets the active, user-selectable models ordered by name; the chat picker's source.</summary>
    /// <remarks>
    /// The only query that filters on <c>IsUserSelectable</c>. Hiding is not withdrawing: a turn naming a
    /// hidden model by id is still served, because <see cref="GetModelAsync"/> ignores the flag.
    /// </remarks>
    Task<List<ModelDto>> GetModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets every model, active and deactivated, ordered by name, for administrators.</summary>
    Task<List<ModelDto>> GetAllModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets an active model by id.</summary>
    /// <exception cref="NotFoundException">No active model with <paramref name="id"/> exists.</exception>
    Task<ModelDto> GetModelAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Creates a model; marking it default demotes the others in the same save.</summary>
    /// <exception cref="NotFoundException">The referenced provider does not exist.</exception>
    Task<ModelDto> CreateModelAsync(CreateModelActionDto request, CancellationToken cancellationToken = default);

    /// <summary>Updates an active model; marking it default demotes the others in the same save.</summary>
    /// <exception cref="ValidationException">The summarizer's new provider has no chat client.</exception>
    /// <exception cref="NotFoundException">The model is inactive, or the provider does not exist.</exception>
    Task<ModelDto> UpdateModelAsync(Guid id, UpdateModelActionDto request, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes a model.</summary>
    /// <exception cref="NotFoundException">No active model with <paramref name="id"/> exists.</exception>
    /// <exception cref="SummarizerProtectedException">The model is the configured summarizer.</exception>
    Task DeactivateModelAsync(Guid id, CancellationToken cancellationToken = default);
}

public partial class ModelService(ILogger<ModelService> logger,
    ITokenService tokenService,
    EnterpriseGptDbContext ctx,
    IOptions<SummarizationOptions> summarizationOptions,
    IChatClientResolver chatClientResolver,
    TimeProvider timeProvider) : IModelService
{
    private readonly ILogger<ModelService> _logger = logger;
    private readonly ITokenService _tokenService = tokenService;
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly IOptions<SummarizationOptions> _summarizationOptions = summarizationOptions;
    private readonly IChatClientResolver _chatClientResolver = chatClientResolver;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <inheritdoc />
    public async Task<List<ModelDto>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        return await _ctx.Models
            .AsNoTracking()
            .Where(x => !x.DateDeactivated.HasValue && x.IsUserSelectable)
            .OrderBy(x => x.Name)
            .Select(ModelMapper.MapToModelDtoExpression)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<ModelDto>> GetAllModelsAsync(CancellationToken cancellationToken = default)
    {
        return await _ctx.Models
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(ModelMapper.MapToModelDtoExpression)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ModelDto> GetModelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _ctx.Models
            .AsNoTracking()
            .Where(x => x.Id == id && !x.DateDeactivated.HasValue)
            .Select(ModelMapper.MapToModelDtoExpression)
            .FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("Model not found.");
    }

    /// <inheritdoc />
    public async Task<ModelDto> CreateModelAsync(CreateModelActionDto request, CancellationToken cancellationToken = default)
    {
        await EnsureProviderExistsAsync(request.ProviderId, cancellationToken);

        var oid = _tokenService.GetOid();

        var newModel = request.FromCreateModelActionDtoToModel();
        newModel.Id = Guid.NewGuid();
        newModel.CreatedById = oid;
        newModel.ModifiedById = oid;

        if (request.IsDefault)
        {
            await DemoteOtherDefaultsAsync(newModel.Id, oid, cancellationToken);
        }

        _ctx.Add(newModel);
        await _ctx.SaveChangesOrConflictAsync("The default model was changed by another request. Reload the catalog and try again.", cancellationToken);

        LogModelCreated(_logger, newModel.Id, oid);

        return newModel.MapToModelDto();
    }

    /// <inheritdoc />
    public async Task<ModelDto> UpdateModelAsync(Guid id, UpdateModelActionDto request, CancellationToken cancellationToken = default)
    {
        var model = await _ctx.Models
            .Where(x => x.Id == id && !x.DateDeactivated.HasValue)
            .FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("Model not found.");

        if (model.ProviderId != request.ProviderId)
        {
            await EnsureProviderExistsAsync(request.ProviderId, cancellationToken);
            EnsureSummarizerProviderIsServable(id, request.ProviderId);
        }

        var oid = _tokenService.GetOid();

        request.FromUpdateModelActionDtoToModel(model);
        model.ModifiedById = oid;

        if (request.IsDefault)
        {
            await DemoteOtherDefaultsAsync(model.Id, oid, cancellationToken);
        }

        await _ctx.SaveChangesOrConflictAsync($"Model '{id}' was changed by another request. Reload it and try again.", cancellationToken);

        LogModelUpdated(_logger, model.Id, oid);

        return model.MapToModelDto();
    }

    /// <inheritdoc />
    public async Task DeactivateModelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureNotTheSummarizer(id);

        var oid = _tokenService.GetOid();
        var date = _timeProvider.GetUtcNow();

        // Also clear IsDefault so a tombstoned row can't remain the catalog default.
        var rows = await _ctx.Models
            .Where(x => x.Id == id && !x.DateDeactivated.HasValue)
            .ExecuteUpdateAsync(x => x
                .SetProperty(p => p.DateDeactivated, date)
                .SetProperty(p => p.IsDefault, false)
                .SetProperty(p => p.DateModified, date)
                .SetProperty(p => p.ModifiedById, oid), cancellationToken);

        if (rows == 0)
        {
            throw new NotFoundException("Model not found.");
        }

        LogModelDeactivated(_logger, id, oid);
    }

    #region Private methods

    /// <summary>Refuses to retire the row named by <c>Summarization:ModelId</c>.</summary>
    /// <remarks>
    /// Startup treats a deactivated summarizer as missing and the seed migration is already applied, so
    /// retiring it would stop every instance booting, recoverable only by hand-editing the database.
    /// </remarks>
    private void EnsureNotTheSummarizer(Guid id)
    {
        if (id != _summarizationOptions.Value.ModelId)
        {
            return;
        }

        // Not a ValidationException: its handler leaves Detail null, and a bodyless DELETE has no
        // field to render errors against, so the administrator would never see which setting to change.
        throw new SummarizerProtectedException(id);
    }

    /// <summary>
    /// Refuses to repoint the summarizer at a provider this deployment has no chat client for.
    /// </summary>
    /// <remarks>Startup fails when the summarizer's provider has no chat client.</remarks>
    private void EnsureSummarizerProviderIsServable(Guid id, Guid providerId)
    {
        if (id != _summarizationOptions.Value.ModelId)
        {
            return;
        }

        try
        {
            _chatClientResolver.Resolve(providerId);
        }
        catch (ProviderNotConfiguredException)
        {
            throw new ValidationException([
                new ValidationFailure(
                    nameof(Model.ProviderId),
                    "This model is the configured document summarizer, and this deployment has no chat client for that provider.")
            ]);
        }
    }

    /// <summary>
    /// Throws when the provider does not exist or is deactivated, so callers get a
    /// precise 404 instead of an opaque FK violation at save time.
    /// </summary>
    private async Task EnsureProviderExistsAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var exists = await _ctx.Providers
            .AnyAsync(p => p.Id == providerId && !p.DateDeactivated.HasValue, cancellationToken);

        if (!exists)
        {
            throw new NotFoundException("Provider not found.");
        }
    }

    /// <summary>
    /// Demotes every other default model (active or not). Tracked rather than <c>ExecuteUpdate</c> so it
    /// commits with the caller's save, where the rowversion guards against concurrent administrators.
    /// </summary>
    private async Task DemoteOtherDefaultsAsync(Guid keepId, Guid oid, CancellationToken cancellationToken)
    {
        var currentDefaults = await _ctx.Models
            .Where(x => x.IsDefault && x.Id != keepId)
            .ToListAsync(cancellationToken);

        foreach (var other in currentDefaults)
        {
            other.IsDefault = false;
            other.ModifiedById = oid;
        }
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Model {ModelId} created by {UserId}")]
    private static partial void LogModelCreated(ILogger logger, Guid modelId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Model {ModelId} updated by {UserId}")]
    private static partial void LogModelUpdated(ILogger logger, Guid modelId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Model {ModelId} deactivated by {UserId}")]
    private static partial void LogModelDeactivated(ILogger logger, Guid modelId, Guid userId);

    #endregion
}
