using Enterprise.Gpt.Service.ChatModels;

namespace Enterprise.Gpt.Api.Configuration;

internal static class ChatModelsConfiguration
{
    #region Public static methods

    internal static IServiceCollection AddChatModels(this IServiceCollection services)
    {
        services.AddScoped<IModelService, ModelService>();

        return services;
    }

    #endregion
}
