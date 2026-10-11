using Enterprise.Gpt.Dto.Actions.ChatModels;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Dto.Actions.ChatModels;

public class ModelActionValidatorTests
{
    [Fact]
    public void Create_TheBaselineRequest_IsAccepted()
    {
        Assert.Empty(CreateFailures(CreateRequest()));
    }

    [Fact]
    public void Update_TheBaselineRequest_IsAccepted()
    {
        Assert.Empty(UpdateFailures(UpdateRequest()));
    }

    [Fact]
    public void Create_EmptyNameAndZeroContextWindow_RejectsBoth()
    {
        var failures = CreateFailures(CreateRequest() with { Name = string.Empty, ContextWindowSize = 0m });

        Assert.Contains(nameof(CreateModelActionDto.Name), failures);
        Assert.Contains(nameof(CreateModelActionDto.ContextWindowSize), failures);
    }

    [Fact]
    public void Create_NegativePrice_IsRejected()
    {
        var failures = CreateFailures(CreateRequest() with { InputPricePerMillionTokens = -0.01m });

        Assert.Contains(nameof(CreateModelActionDto.InputPricePerMillionTokens), failures);
    }

    /// <summary>
    /// A hidden default resolves to nothing on the picker's own route, which leaves every client with
    /// no model to start a turn on.
    /// </summary>
    [Fact]
    public void Create_DefaultAndNotUserSelectable_IsRejectedOnIsDefault()
    {
        var failures = CreateFailures(CreateRequest() with { IsDefault = true, IsUserSelectable = false });

        Assert.Contains(nameof(CreateModelActionDto.IsDefault), failures);
    }

    [Fact]
    public void Update_EmptyDeploymentNameAndZeroMaxOutput_RejectsBoth()
    {
        var failures = UpdateFailures(UpdateRequest() with { DeploymentName = string.Empty, MaxOutputTokens = 0m });

        Assert.Contains(nameof(UpdateModelActionDto.DeploymentName), failures);
        Assert.Contains(nameof(UpdateModelActionDto.MaxOutputTokens), failures);
    }

    [Fact]
    public void Update_DefaultAndNotUserSelectable_IsRejectedOnIsDefault()
    {
        var failures = UpdateFailures(UpdateRequest() with { IsDefault = true, IsUserSelectable = false });

        Assert.Contains(nameof(UpdateModelActionDto.IsDefault), failures);
    }

    #region Private methods

    private static CreateModelActionDto CreateRequest() =>
        new()
        {
            ProviderId = Guid.NewGuid(),
            Name = "aaa-created",
            DeploymentName = "aaa-created-deployment",
            Description = "aaa-created description",
            ContextWindowSize = 128000m,
            MaxOutputTokens = 16384m,
            IsToolEnabled = true
        };

    private static UpdateModelActionDto UpdateRequest() =>
        new()
        {
            ProviderId = Guid.NewGuid(),
            Name = "aaa-updated",
            DeploymentName = "aaa-updated-deployment",
            Description = "aaa-updated description",
            ContextWindowSize = 64000m,
            MaxOutputTokens = 8192m
        };

    private static IEnumerable<string> CreateFailures(CreateModelActionDto request) =>
        new CreateModelActionDtoValidator().Validate(request).Errors.Select(e => e.PropertyName);

    private static IEnumerable<string> UpdateFailures(UpdateModelActionDto request) =>
        new UpdateModelActionDtoValidator().Validate(request).Errors.Select(e => e.PropertyName);

    #endregion
}
