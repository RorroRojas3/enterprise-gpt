using System.Linq.Expressions;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Model;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Service.Mappers
{
    public static class ModelMapper
    {
        /// <summary>
        /// Maps a materialized <see cref="Model"/> entity to <see cref="ModelDto"/>.
        /// </summary>
        /// <param name="model">The loaded model entity.</param>
        /// <returns>The mapped DTO.</returns>
        public static ModelDto MapToModelDto(this Model model)
        {
            return new ModelDto
            {
                Id = model.Id,
                ProviderId = model.ProviderId,
                Name = model.Name,
                DisplayName = model.DisplayName,
                Description = model.Description,
                ContextWindowSize = model.ContextWindowSize,
                MaxOutputTokens = model.MaxOutputTokens,
                IsToolEnabled = model.IsToolEnabled,
                IsDefault = model.IsDefault,
                DateDeactivated = model.DateDeactivated
            };
        }

        /// <summary>
        /// LINQ projection from <see cref="Model"/> to <see cref="ModelDto"/> for use inside
        /// <c>IQueryable.Select(...)</c>.
        /// </summary>
        public static Expression<Func<Model, ModelDto>> MapToModelDtoExpression { get; } =
            model => new ModelDto
            {
                Id = model.Id,
                ProviderId = model.ProviderId,
                Name = model.Name,
                DisplayName = model.DisplayName,
                Description = model.Description,
                ContextWindowSize = model.ContextWindowSize,
                MaxOutputTokens = model.MaxOutputTokens,
                IsToolEnabled = model.IsToolEnabled,
                IsDefault = model.IsDefault,
                DateDeactivated = model.DateDeactivated
            };

        /// <summary>
        /// Creates a new <see cref="Model"/> entity from a <see cref="CreateModelActionDto"/>.
        /// Callers are responsible for setting <c>Id</c> and the audit columns
        /// (e.g. <c>DateCreated</c>, <c>CreatedById</c>) before saving.
        /// </summary>
        /// <param name="dto">The action DTO carrying the new values.</param>
        /// <returns>The new, untracked entity.</returns>
        public static Model FromCreateModelActionDtoToModel(this CreateModelActionDto dto)
        {
            return new Model
            {
                ProviderId = dto.ProviderId,
                Name = dto.Name,
                DisplayName = dto.DisplayName,
                Description = dto.Description,
                ContextWindowSize = dto.ContextWindowSize,
                MaxOutputTokens = dto.MaxOutputTokens,
                IsToolEnabled = dto.IsToolEnabled,
                IsDefault = dto.IsDefault
            };
        }

        /// <summary>
        /// Applies an <see cref="UpdateModelActionDto"/> to an existing <see cref="Model"/>
        /// entity in place. Callers are responsible for setting audit columns
        /// (e.g. <c>DateModified</c>) and saving changes.
        /// </summary>
        /// <param name="dto">The action DTO carrying the new values.</param>
        /// <param name="model">The tracked entity to mutate.</param>
        public static void FromUpdateModelActionDtoToModel(this UpdateModelActionDto dto, Model model)
        {
            model.ProviderId = dto.ProviderId;
            model.Name = dto.Name;
            model.DisplayName = dto.DisplayName;
            model.Description = dto.Description;
            model.ContextWindowSize = dto.ContextWindowSize;
            model.MaxOutputTokens = dto.MaxOutputTokens;
            model.IsToolEnabled = dto.IsToolEnabled;
            model.IsDefault = dto.IsDefault;
        }
    }
}
