using Enterprise.Gpt.Api.Problems.Constants;
using FluentValidation.Results;

namespace Enterprise.Gpt.Api.Problems;

/// <summary>
/// Builds the 400 validation problem shared by the validation filter and the exception handler.
/// </summary>
internal static class ValidationProblems
{
    #region Public static methods

    /// <summary>
    /// Creates a validation problem whose <c>errors</c> are keyed by the failing property name.
    /// </summary>
    internal static HttpValidationProblemDetails Create(IEnumerable<ValidationFailure> failures)
    {
        // Grouped because one property can fail several rules at once.
        var errors = failures
            .GroupBy(failure => failure.PropertyName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(failure => failure.ErrorMessage).ToArray(),
                StringComparer.Ordinal);

        // Detail stays null: the errors dictionary carries every message.
        return new HttpValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Type = ProblemTypes.Validation.Type,
            Title = ProblemTypes.Validation.Title
        };
    }

    #endregion
}
