using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Model;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Chat;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Mappers;
using Enterprise.Gpt.Service.Settings;

namespace Enterprise.Gpt.Service
{
    public interface IModelService
    {
        /// <summary>
        /// Gets the active, user-selectable models, ordered by name. This is the chat picker's
        /// source.
        /// </summary>
        /// <remarks>
        /// The only query that filters on <c>IsUserSelectable</c>. A purpose-built deployment — the
        /// pinned document summarizer — is hidden here and returned by every other member, which is
        /// the same split <c>DateDeactivated</c> already draws between this route and
        /// <see cref="GetAllModelsAsync"/>. Hiding is not withdrawing: a turn naming a hidden model
        /// by id is still served, because <see cref="GetModelAsync"/> does not filter on the flag.
        /// </remarks>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The list of active, user-selectable models.</returns>
        Task<List<ModelDto>> GetModelsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets every model, active and deactivated, ordered by name. Intended for
        /// administrators toggling model availability.
        /// </summary>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The list of all models.</returns>
        Task<List<ModelDto>> GetAllModelsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a single active model by id.
        /// </summary>
        /// <param name="id">The model id.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The matching model.</returns>
        /// <exception cref="NotFoundException">No active model with <paramref name="id"/> exists.</exception>
        Task<ModelDto> GetModelAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a new model after validating the request. When the request marks the
        /// model as default, any other default model is demoted in the same transaction.
        /// </summary>
        /// <param name="request">The creation request.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The created model.</returns>
        /// <exception cref="ValidationException">The request fails validation.</exception>
        /// <exception cref="NotFoundException">The referenced provider does not exist.</exception>
        Task<ModelDto> CreateModelAsync(CreateModelActionDto request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates an existing active model after validating the request. When the request
        /// marks the model as default, any other default model is demoted in the same transaction.
        /// </summary>
        /// <param name="id">The id of the model to update.</param>
        /// <param name="request">The update request.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The updated model.</returns>
        /// <exception cref="ValidationException">The request fails validation.</exception>
        /// <exception cref="NotFoundException">No active model with <paramref name="id"/> exists, or the referenced provider does not exist.</exception>
        Task<ModelDto> UpdateModelAsync(Guid id, UpdateModelActionDto request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Soft-deletes a model by setting its <c>DateDeactivated</c>.
        /// </summary>
        /// <param name="id">The id of the model to deactivate.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <exception cref="NotFoundException">No active model with <paramref name="id"/> exists.</exception>
        /// <exception cref="SummarizerProtectedException">
        /// The model is the configured document summarizer, which the application refuses to start
        /// without.
        /// </exception>
        Task DeactivateModelAsync(Guid id, CancellationToken cancellationToken = default);
    }

    public class ModelService(ILogger<ModelService> logger,
        ITokenService tokenService,
        EnterpriseGptDbContext ctx,
        IValidator<CreateModelActionDto> createValidator,
        IValidator<UpdateModelActionDto> updateValidator,
        IOptions<SummarizationOptions> summarizationOptions,
        IChatClientResolver chatClientResolver) : IModelService
    {
        private readonly ILogger<ModelService> _logger = logger;
        private readonly ITokenService _tokenService = tokenService;
        private readonly EnterpriseGptDbContext _ctx = ctx;
        private readonly IValidator<CreateModelActionDto> _createValidator = createValidator;
        private readonly IValidator<UpdateModelActionDto> _updateValidator = updateValidator;
        private readonly IOptions<SummarizationOptions> _summarizationOptions = summarizationOptions;
        private readonly IChatClientResolver _chatClientResolver = chatClientResolver;

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
            await _createValidator.ValidateAndThrowAsync(request, cancellationToken);
            await EnsureProviderExistsAsync(request.ProviderId, cancellationToken);

            var oid = _tokenService.GetOid();
            var date = DateTimeOffset.UtcNow;

            var newModel = request.FromCreateModelActionDtoToModel();
            newModel.Id = Guid.NewGuid();
            newModel.DateCreated = date;
            newModel.CreatedById = oid;
            newModel.DateModified = date;
            newModel.ModifiedById = oid;

            if (request.IsDefault)
            {
                await DemoteOtherDefaultsAsync(newModel.Id, oid, date, cancellationToken);
            }

            _ctx.Add(newModel);
            await _ctx.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Model {ModelId} created by {UserId}", newModel.Id, oid);

            return newModel.MapToModelDto();
        }

        /// <inheritdoc />
        public async Task<ModelDto> UpdateModelAsync(Guid id, UpdateModelActionDto request, CancellationToken cancellationToken = default)
        {
            await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

            var model = await _ctx.Models
                .Where(x => x.Id == id && !x.DateDeactivated.HasValue)
                .FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("Model not found.");

            if (model.ProviderId != request.ProviderId)
            {
                await EnsureProviderExistsAsync(request.ProviderId, cancellationToken);
                EnsureSummarizerProviderIsServable(id, request.ProviderId);
            }

            var oid = _tokenService.GetOid();
            var date = DateTimeOffset.UtcNow;

            request.FromUpdateModelActionDtoToModel(model);
            model.DateModified = date;
            model.ModifiedById = oid;

            if (request.IsDefault)
            {
                await DemoteOtherDefaultsAsync(model.Id, oid, date, cancellationToken);
            }

            await _ctx.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Model {ModelId} updated by {UserId}", model.Id, oid);

            return model.MapToModelDto();
        }

        /// <inheritdoc />
        public async Task DeactivateModelAsync(Guid id, CancellationToken cancellationToken = default)
        {
            EnsureNotTheSummarizer(id);

            var oid = _tokenService.GetOid();
            var date = DateTimeOffset.UtcNow;

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

            _logger.LogInformation("Model {ModelId} deactivated by {UserId}", id, oid);
        }

        /// <summary>
        /// Refuses to retire the row named by <c>Summarization:ModelId</c>.
        /// </summary>
        /// <remarks>
        /// The startup check resolves that row and treats a deactivated one as missing, so retiring
        /// it stops the application booting — and the seed-correcting migration is already recorded
        /// in <c>__EFMigrationsHistory</c>, so a redeploy does not undo it. Without this guard an
        /// administrator can brick every instance from a supported route, with recovery only by
        /// hand-editing the database. Which model is the summarizer is an operator's configuration
        /// decision, not something the admin catalog administers.
        /// </remarks>
        private void EnsureNotTheSummarizer(Guid id)
        {
            if (id != _summarizationOptions.Value.ModelId)
            {
                return;
            }

            // Not a ValidationException: that handler leaves Detail null and puts every message in
            // the errors dictionary, and a bodyless DELETE gives a client no field to render one
            // against — so the sentence naming the setting to change would never reach the
            // administrator who needs it.
            throw new SummarizerProtectedException(id);
        }

        /// <summary>
        /// Refuses to repoint the summarizer at a provider this deployment has no chat client for.
        /// </summary>
        /// <remarks>
        /// The same startup failure <see cref="EnsureNotTheSummarizer"/> guards against, reached by
        /// a different edit: the bootstrapper resolves a chat client for the summarizer's provider
        /// and fails the app when there is none.
        /// </remarks>
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
        /// Demotes every other default model (active or not) so at most one default exists.
        /// Uses tracked entities instead of <c>ExecuteUpdate</c> so the demotion commits
        /// atomically with the caller's <c>SaveChangesAsync</c>; the rowversion column
        /// guards against concurrent administrators.
        /// </summary>
        private async Task DemoteOtherDefaultsAsync(Guid keepId, Guid oid, DateTimeOffset date, CancellationToken cancellationToken)
        {
            var currentDefaults = await _ctx.Models
                .Where(x => x.IsDefault && x.Id != keepId)
                .ToListAsync(cancellationToken);

            foreach (var other in currentDefaults)
            {
                other.IsDefault = false;
                other.DateModified = date;
                other.ModifiedById = oid;
            }
        }
    }
}
