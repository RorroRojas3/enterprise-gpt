using Enterprise.Gpt.Api.Problems;
using FluentValidation;

namespace Enterprise.Gpt.Api.Filters;

/// <remarks>
/// Add it after any permission filter, so a caller without the grant learns nothing about the body rules.
/// </remarks>
internal sealed class ValidationEndpointFilter<TRequest>(IValidator<TRequest> validator) : IEndpointFilter
    where TRequest : class
{
    private readonly IValidator<TRequest> _validator = validator;

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"{nameof(ValidationEndpointFilter<>)} is attached to an endpoint with no {typeof(TRequest).Name} argument.");

        var result = await _validator.ValidateAsync(request, context.HttpContext.RequestAborted);

        if (result.IsValid)
        {
            return await next(context);
        }

        var problemDetails = ValidationProblems.Create(result.Errors);

        // TypedResults.Problem writes directly when the client's Accept excludes JSON, skipping
        // CustomizeProblemDetails; Enrich is idempotent, so running it on both paths is harmless.
        ProblemDetailsRegistration.Enrich(context.HttpContext, problemDetails);

        return TypedResults.Problem(problemDetails);
    }
}
