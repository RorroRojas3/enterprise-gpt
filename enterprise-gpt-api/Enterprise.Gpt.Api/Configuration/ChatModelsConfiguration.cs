using Enterprise.Gpt.Service.ChatModels;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers the model catalog.
/// </summary>
internal static class ChatModelsConfiguration
{
    /// <summary>Registers the model service.</summary>
    internal static IServiceCollection AddChatModels(this IServiceCollection services)
    {
        services.AddScoped<IModelService, ModelService>();

        return services;
    }
}
