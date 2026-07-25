using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Model;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service
{
    public interface IModelService
    {
        Task<List<ModelDto>> GetModelsAsync(CancellationToken cancellationToken);


        Task<ModelDto> GetModelAsync(Guid id, CancellationToken cancellationToken);

        Task<ModelDto> CreateModelAsync(CreateModelActionDto request, CancellationToken cancellationToken);

        Task<ModelDto> UpdateModelAsync(UpdateModelActionDto request, CancellationToken cancellationToken);

        Task DeactivateModelAsync(Guid id, CancellationToken cancellationToken);
    }

    public class ModelService(ILogger<ModelService> logger,
        ITokenService tokenService,
        EnterpriseGptDbContext ctx) : IModelService
    {
        private readonly ILogger<ModelService> _logger = logger;
        private readonly ITokenService _tokenService = tokenService;
        private readonly EnterpriseGptDbContext _ctx = ctx;

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

            _ctx.Add(newModel);
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
    }
}
