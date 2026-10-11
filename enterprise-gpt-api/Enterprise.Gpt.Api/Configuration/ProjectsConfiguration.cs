using Enterprise.Gpt.Service.Projects;

namespace Enterprise.Gpt.Api.Configuration;

internal static class ProjectsConfiguration
{
    #region Public static methods

    internal static IServiceCollection AddProjects(this IServiceCollection services)
    {
        services.AddScoped<IProjectService, ProjectService>();

        return services;
    }

    #endregion
}
