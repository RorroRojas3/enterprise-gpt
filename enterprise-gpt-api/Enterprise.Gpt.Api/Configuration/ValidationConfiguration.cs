using Enterprise.Gpt.Dto.Pagination;
using FluentValidation;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers the request validators.
/// </summary>
internal static class ValidationConfiguration
{
    #region Public static methods

    /// <summary>
    /// Registers every validator in the Dto assembly.
    /// </summary>
    /// <remarks>
    /// Scanned by named assembly, never <c>AppDomain.GetAssemblies()</c>: an assembly no earlier line has touched
    /// is not loaded yet, and its validators would silently go unregistered.
    /// </remarks>
    internal static IServiceCollection AddEnterpriseGptValidation(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(PaginatedResponseDto<>).Assembly, includeInternalTypes: true);

        return services;
    }

    #endregion
}
