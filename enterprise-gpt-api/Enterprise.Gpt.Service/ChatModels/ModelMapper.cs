using System.Linq.Expressions;
using Enterprise.Gpt.Dto.Actions.ChatModels;
using Enterprise.Gpt.Dto.ChatModels;
using Enterprise.Gpt.Entity.ChatModels;

namespace Enterprise.Gpt.Service.ChatModels;

public static class ModelMapper
{
    /// <summary>
    /// Projection from <see cref="Model"/> to <see cref="ModelDto"/> for <c>IQueryable.Select</c>.
    /// </summary>
    public static Expression<Func<Model, ModelDto>> MapToModelDtoExpression { get; } =
        model => new ModelDto
        {
            Id = model.Id,
            ProviderId = model.ProviderId,
            Name = model.Name,
            DeploymentName = model.DeploymentName,
            Description = model.Description,
            ContextWindowSize = model.ContextWindowSize,
            MaxOutputTokens = model.MaxOutputTokens,
            IsToolEnabled = model.IsToolEnabled,
            IsReasoningEnabled = model.IsReasoningEnabled,
            IsUserSelectable = model.IsUserSelectable,
            IsDefault = model.IsDefault,
            InputPricePerMillionTokens = model.InputPricePerMillionTokens,
            OutputPricePerMillionTokens = model.OutputPricePerMillionTokens,
            DateDeactivated = model.DateDeactivated
        };

    #region Public static methods

    /// <summary>Maps a materialized <see cref="Model"/> to <see cref="ModelDto"/>.</summary>
    public static ModelDto MapToModelDto(this Model model)
    {
        return new ModelDto
        {
            Id = model.Id,
            ProviderId = model.ProviderId,
            Name = model.Name,
            DeploymentName = model.DeploymentName,
            Description = model.Description,
            ContextWindowSize = model.ContextWindowSize,
            MaxOutputTokens = model.MaxOutputTokens,
            IsToolEnabled = model.IsToolEnabled,
            IsReasoningEnabled = model.IsReasoningEnabled,
            IsUserSelectable = model.IsUserSelectable,
            IsDefault = model.IsDefault,
            InputPricePerMillionTokens = model.InputPricePerMillionTokens,
            OutputPricePerMillionTokens = model.OutputPricePerMillionTokens,
            DateDeactivated = model.DateDeactivated
        };
    }

    /// <summary>
    /// Creates an untracked <see cref="Model"/>. Callers set <c>Id</c>, <c>CreatedById</c> and
    /// <c>ModifiedById</c>; the audit interceptor stamps the dates.
    /// </summary>
    public static Model FromCreateModelActionDtoToModel(this CreateModelActionDto dto)
    {
        return new Model
        {
            ProviderId = dto.ProviderId,
            Name = dto.Name,
            DeploymentName = dto.DeploymentName,
            Description = dto.Description,
            ContextWindowSize = dto.ContextWindowSize,
            MaxOutputTokens = dto.MaxOutputTokens,
            IsToolEnabled = dto.IsToolEnabled,
            IsReasoningEnabled = dto.IsReasoningEnabled,
            IsUserSelectable = dto.IsUserSelectable,
            IsDefault = dto.IsDefault,
            InputPricePerMillionTokens = dto.InputPricePerMillionTokens,
            OutputPricePerMillionTokens = dto.OutputPricePerMillionTokens
        };
    }

    /// <summary>
    /// Applies the update to a tracked <see cref="Model"/> in place. Callers set <c>ModifiedById</c>
    /// and save; the audit interceptor stamps <c>DateModified</c>.
    /// </summary>
    public static void FromUpdateModelActionDtoToModel(this UpdateModelActionDto dto, Model model)
    {
        model.ProviderId = dto.ProviderId;
        model.Name = dto.Name;
        model.DeploymentName = dto.DeploymentName;
        model.Description = dto.Description;
        model.ContextWindowSize = dto.ContextWindowSize;
        model.MaxOutputTokens = dto.MaxOutputTokens;
        model.IsToolEnabled = dto.IsToolEnabled;
        model.IsReasoningEnabled = dto.IsReasoningEnabled;
        model.IsUserSelectable = dto.IsUserSelectable;
        model.IsDefault = dto.IsDefault;
        // Full representation: an omitted price clears the stored one.
        model.InputPricePerMillionTokens = dto.InputPricePerMillionTokens;
        model.OutputPricePerMillionTokens = dto.OutputPricePerMillionTokens;
    }

    #endregion
}
