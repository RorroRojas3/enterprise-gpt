using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Common.Validation;

/// <summary>Wires FluentValidation into the options pipeline.</summary>
public static class OptionsBuilderFluentValidationExtensions
{
    #region Public static methods

    /// <summary>
    /// Validates the options with the <see cref="IValidator{T}"/> registered for <typeparamref name="TOptions"/>.
    /// </summary>
    /// <remarks>The validator must be registered as a singleton: options are validated from the root provider.</remarks>
    public static OptionsBuilder<TOptions> ValidateWithFluentValidation<TOptions>(this OptionsBuilder<TOptions> builder)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IValidateOptions<TOptions>>(services =>
            new FluentValidationOptions<TOptions>(builder.Name, services.GetRequiredService<IValidator<TOptions>>()));

        return builder;
    }

    #endregion
}
