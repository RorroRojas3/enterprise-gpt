using Scalar.AspNetCore;

namespace Enterprise.Gpt.Api.Configuration;

internal static class OpenApiConfiguration
{
    #region Public static methods

    internal static IServiceCollection AddEnterpriseGptOpenApi(this IServiceCollection services) => services.AddOpenApi();

    internal static WebApplication MapEnterpriseGptOpenApi(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        return app;
    }

    #endregion
}
