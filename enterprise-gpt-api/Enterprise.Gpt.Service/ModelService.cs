using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Model;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Mappers;

namespace Enterprise.Gpt.Service
{
    public interface IModelService
    {
        Task<List<ModelDto>> GetModelsAsync(CancellationToken cancellationToken);


        Task<ModelDto> GetModelAsync(Guid id, CancellationToken cancellationToken);

        Task<ModelDto> CreateModelAsync(CreateModelActionDto request, CancellationToken cancellationToken);

        Task<ModelDto> UpdateModelAsync(UpdateModelActionDto request, CancellationToken cancellationToken);

        Task DeactivateModelAsync(Guid id, CancellationToken cancellationToken);

        Task<List<ModelDto>> GetFavoriteModelsAsync(CancellationToken cancellationToken);

        Task<List<ModelDto>> SetFavoriteModelAsync(SetFavoriteModelActionDto request, CancellationToken cancellationToken);
    }

    public class ModelService(ILogger<ModelService> logger,
        ITokenService tokenService,
        IValidator<SetFavoriteModelActionDto> setFavoriteValidator,
        AIChatDbContext ctx) : IModelService
    {
        private readonly ILogger<ModelService> _logger = logger;
        private readonly ITokenService _tokenService = tokenService;
        private readonly IValidator<SetFavoriteModelActionDto> _setFavoriteValidator = setFavoriteValidator;
        private readonly AIChatDbContext _ctx = ctx;

        /// <inheritdoc />
        public async Task<List<ModelDto>> GetModelsAsync(CancellationToken cancellationToken)
        {
            var models = await _ctx.Models
                            .AsNoTracking()
                            .OrderBy(x => x.Name)
                            .Select(x => new ModelDto
                            {
                                Id = x.Id,
                                Name = x.Name,
                                Description = x.Description,
                                IsToolEnabled = x.IsToolEnabled
                            })
                            .ToListAsync(cancellationToken);

            return models;
        }

        /// <inheritdoc />
        public async Task<ModelDto> GetModelAsync(Guid id, CancellationToken cancellationToken)
        {
            return await _ctx.Models
                .AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new ModelDto
                {
                    Id = x.Id,
                    Name = x.Name,
                    Description = x.Description,
                    IsToolEnabled = x.IsToolEnabled
                })
                .FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("Model not found.");
        }

        public async Task<ModelDto> CreateModelAsync(CreateModelActionDto request, CancellationToken cancellationToken)
        {
            var oid = _tokenService.GetOid();
            var date = DateTimeOffset.UtcNow;

            var newModel = new Model
            {
                Id = Guid.NewGuid(),
                Name = request.Name,
                Description = request.Description,
                IsToolEnabled = request.IsToolEnabled,
                DateCreated = date,
                CreatedById = oid,
                DateModified = date,
                ModifiedById = oid
            };

            await _ctx.AddAsync(newModel, cancellationToken);
            await _ctx.SaveChangesAsync(cancellationToken);

            return new ModelDto
            {
                Id = newModel.Id,
                Name = newModel.Name,
                Description = newModel.Description,
                IsToolEnabled = newModel.IsToolEnabled
            };
        }

        public async Task<ModelDto> UpdateModelAsync(UpdateModelActionDto request, CancellationToken cancellationToken)
        {
            var oid = _tokenService.GetOid();
            var date = DateTimeOffset.UtcNow;

            var model = await _ctx.Models.Where(x => x.Id == request.Id)
                        .FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("Model not found.");

            model.Name = request.Name;
            model.Description = request.Description;
            model.IsToolEnabled = request.IsToolEnabled;
            model.DateModified = DateTimeOffset.UtcNow;
            model.ModifiedById = oid;

            await _ctx.SaveChangesAsync(cancellationToken);

            return new ModelDto
            {
                Id = model.Id,
                Name = model.Name,
                Description = model.Description,
                IsToolEnabled = model.IsToolEnabled
            };
        }

        public async Task DeactivateModelAsync(Guid id, CancellationToken cancellationToken)
        {
            var oid = _tokenService.GetOid();
            var date = DateTimeOffset.UtcNow;

            await _ctx.Models.Where(x => x.Id == id)
                        .ExecuteUpdateAsync(x => x
                            .SetProperty(p => p.DateDeactivated, date)
                            .SetProperty(p => p.DateModified, date)
                            .SetProperty(p => p.ModifiedById, oid), cancellationToken);
        }

        /// <inheritdoc />
        public async Task<List<ModelDto>> GetFavoriteModelsAsync(CancellationToken cancellationToken)
        {
            var oid = _tokenService.GetOid();

            var favoriteModelId = await _ctx.UserModels
                .Where(x => x.UserId == oid && !x.DateDeactivated.HasValue)
                .Select(x => (Guid?)x.ModelId)
                .FirstOrDefaultAsync(cancellationToken)
                ?? Guid.Parse(ModelDefaults.DefaultModelId);

            var models = await _ctx.Models
                .AsNoTracking()
                .Select(ModelMapper.MapToModelDtoExpression)
                .ToListAsync(cancellationToken);

            return [.. models
                .Select(m => m with { IsFavorite = m.Id == favoriteModelId })
                .OrderByDescending(m => m.IsFavorite)
                .ThenBy(m => m.Name)];
        }

        /// <inheritdoc />
        public async Task<List<ModelDto>> SetFavoriteModelAsync(SetFavoriteModelActionDto request, CancellationToken cancellationToken)
        {
            await _setFavoriteValidator.ValidateAndThrowAsync(request, cancellationToken);

            var oid = _tokenService.GetOid();

            var modelExists = await _ctx.Models
                .AnyAsync(x => x.Id == request.ModelId && !x.DateDeactivated.HasValue, cancellationToken);
            if (!modelExists)
            {
                throw new NotFoundException("Model not found.");
            }

            var date = DateTimeOffset.UtcNow;
            var userModel = await _ctx.UserModels
                .FirstOrDefaultAsync(x => x.UserId == oid && !x.DateDeactivated.HasValue, cancellationToken);

            if (userModel is not null)
            {
                userModel.ModelId = request.ModelId;
                userModel.DateModified = date;
                userModel.ModifiedById = oid;
            }
            else
            {
                _ctx.Add(new UserModel
                {
                    Id = Guid.NewGuid(),
                    UserId = oid,
                    ModelId = request.ModelId,
                    DateCreated = date,
                    CreatedById = oid,
                    DateModified = date,
                    ModifiedById = oid
                });
            }

            await _ctx.SaveChangesAsync(cancellationToken);

            return await GetFavoriteModelsAsync(cancellationToken);
        }
    }
}
