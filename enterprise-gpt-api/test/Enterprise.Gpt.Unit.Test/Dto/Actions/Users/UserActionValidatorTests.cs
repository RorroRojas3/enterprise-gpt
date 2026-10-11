using Enterprise.Gpt.Dto.Actions.Users;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Dto.Actions.Users;

public class UserActionValidatorTests
{
    [Fact]
    public void Create_AWellFormedEmail_IsAccepted()
    {
        var result = new CreateUserActionDtoValidator().Validate(new CreateUserActionDto { Email = "grace@contoso.com" });

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Create_InvalidEmail_IsRejected(string email)
    {
        var result = new CreateUserActionDtoValidator().Validate(new CreateUserActionDto { Email = email });

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateUserActionDto.Email));
    }

    [Fact]
    public void Update_AWellFormedRequest_IsAccepted()
    {
        var result = new UpdateUserActionDtoValidator().Validate(
            new UpdateUserActionDto { FirstName = "Grace", LastName = "Hopper", Email = "grace@contoso.com" });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Update_EmptyFirstNameAndInvalidEmail_RejectsBoth()
    {
        var result = new UpdateUserActionDtoValidator().Validate(
            new UpdateUserActionDto { FirstName = "", LastName = "Hopper", Email = "not-an-email" });

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateUserActionDto.FirstName));
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateUserActionDto.Email));
    }
}
