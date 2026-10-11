using FluentValidation;

namespace Enterprise.Gpt.Repository.Sql.Options;

/// <summary>
/// The SQL Server connection, bound from the <c>ConnectionStrings</c> configuration section.
/// </summary>
public sealed class SqlDbOptions
{
    public const string SectionName = "ConnectionStrings";

    public string DefaultConnection { get; set; } = string.Empty;
}

internal sealed class SqlDbOptionsValidator : AbstractValidator<SqlDbOptions>
{
    public SqlDbOptionsValidator()
    {
        // Never echo the value: a connection string can carry a password.
        RuleFor(options => options.DefaultConnection).NotEmpty()
            .WithMessage("ConnectionStrings:DefaultConnection is required.");
    }
}
