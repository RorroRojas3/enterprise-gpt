using Enterprise.Gpt.Service.Projects;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers projects.
/// </summary>
internal static class ProjectsConfiguration
{
    /// <summary>Registers the project service.</summary>
    internal static IServiceCollection AddProjects(this IServiceCollection services)
    {
        services.AddScoped<IProjectService, ProjectService>();

        return services;
    }
}
