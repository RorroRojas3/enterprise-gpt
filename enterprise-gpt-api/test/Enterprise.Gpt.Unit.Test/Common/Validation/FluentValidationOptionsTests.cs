using Enterprise.Gpt.Common.Validation;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Common.Validation;

public class FluentValidationOptionsTests
{
    public sealed class SampleOptions
    {
        public int Size { get; set; } = 1;

        public string Name { get; set; } = "sample";
    }

    private sealed class SampleOptionsValidator : AbstractValidator<SampleOptions>
    {
        public SampleOptionsValidator()
        {
            RuleFor(options => options.Size).GreaterThan(0).WithMessage("Sample:Size must be greater than zero.");
            RuleFor(options => options.Name).NotEmpty().WithMessage("Sample:Name is required.");
        }
    }

    [Fact]
    public void Validate_ValidOptions_Succeeds()
    {
        var adapter = new FluentValidationOptions<SampleOptions>(Microsoft.Extensions.Options.Options.DefaultName, new SampleOptionsValidator());

        var result = adapter.Validate(Microsoft.Extensions.Options.Options.DefaultName, new SampleOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_SeveralFailures_ReportsEveryMessage()
    {
        var adapter = new FluentValidationOptions<SampleOptions>(Microsoft.Extensions.Options.Options.DefaultName, new SampleOptionsValidator());

        var result = adapter.Validate(Microsoft.Extensions.Options.Options.DefaultName, new SampleOptions { Size = 0, Name = "" });

        Assert.True(result.Failed);
        Assert.Equal(
            ["Sample:Size must be greater than zero.", "Sample:Name is required."],
            result.Failures!);
    }

    [Fact]
    public void Validate_AnotherNamedInstance_Skips()
    {
        var adapter = new FluentValidationOptions<SampleOptions>("primary", new SampleOptionsValidator());

        var result = adapter.Validate("secondary", new SampleOptions { Size = 0 });

        Assert.True(result.Skipped);
    }

    [Fact]
    public void Validate_NoName_ValidatesEveryInstance()
    {
        var adapter = new FluentValidationOptions<SampleOptions>(null, new SampleOptionsValidator());

        var result = adapter.Validate("any", new SampleOptions { Size = 0 });

        Assert.True(result.Failed);
    }

    [Fact]
    public void ValidateWithFluentValidation_InvalidBinding_ThrowsWithTheValidatorsMessage()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IValidator<SampleOptions>, SampleOptionsValidator>();
        services.AddOptions<SampleOptions>()
            .Configure(options => options.Size = -1)
            .ValidateWithFluentValidation();
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<SampleOptions>>().Value);

        Assert.Contains("Sample:Size must be greater than zero.", exception.Failures);
    }
}
