using Scalar.AspNetCore;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers the OpenAPI document and serves it, with Scalar, in development.
/// </summary>
internal static class OpenApiConfiguration
{
    #region Public static methods

    /// <summary>Registers OpenAPI document generation.</summary>
    internal static IServiceCollection AddEnterpriseGptOpenApi(this IServiceCollection services) => services.AddOpenApi();

    /// <summary>Maps the document and the Scalar reference in development only.</summary>
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
