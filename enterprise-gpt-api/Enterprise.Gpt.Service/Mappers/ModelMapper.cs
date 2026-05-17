using System.Linq.Expressions;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Service.Mappers
{
    public static class ModelMapper
    {
        /// <summary>
        /// LINQ projection from <see cref="Model"/> to <see cref="ModelDto"/> for use
        /// inside <c>IQueryable.Select(...)</c>. <see cref="ModelDto.IsFavorite"/> is
        /// left at its default (<see langword="false"/>) — favorite resolution and
        /// ordering are applied by the service against the caller's user.
        /// </summary>
        public static Expression<Func<Model, ModelDto>> MapToModelDtoExpression { get; } =
            model => new ModelDto
            {
                Id = model.Id,
                Name = model.Name,
                Description = model.Description,
                IsToolEnabled = model.IsToolEnabled
            };
    }
}
