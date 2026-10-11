using Enterprise.Gpt.Service.ChatModels;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers the model catalog.
/// </summary>
internal static class ChatModelsConfiguration
{
    #region Public static methods

    /// <summary>Registers the model service.</summary>
    internal static IServiceCollection AddChatModels(this IServiceCollection services)
    {
        services.AddScoped<IModelService, ModelService>();

        return services;
    }

    #endregion
}
