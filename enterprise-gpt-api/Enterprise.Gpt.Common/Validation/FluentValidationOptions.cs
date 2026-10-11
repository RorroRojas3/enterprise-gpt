using FluentValidation;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Common.Validation;

/// <summary>Runs the registered FluentValidation validator for an options instance.</summary>
/// <param name="name">The options name this validates; <see langword="null"/> validates every name.</param>
/// <param name="validator">The validator for <typeparamref name="TOptions"/>.</param>
public sealed class FluentValidationOptions<TOptions>(string? name, IValidator<TOptions> validator) : IValidateOptions<TOptions>
    where TOptions : class
{
    private readonly string? _name = name;
    private readonly IValidator<TOptions> _validator = validator;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        if (_name is not null && _name != name)
        {
            return ValidateOptionsResult.Skip;
        }

        ArgumentNullException.ThrowIfNull(options);

        var result = _validator.Validate(options);

        return result.IsValid
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(result.Errors.Select(failure => failure.ErrorMessage));
    }
}
