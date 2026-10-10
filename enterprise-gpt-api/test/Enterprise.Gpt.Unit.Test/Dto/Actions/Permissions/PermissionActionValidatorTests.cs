using Enterprise.Gpt.Dto.Actions.Permissions;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Dto.Actions.Permissions;

public class PermissionActionValidatorTests
{
    [Fact]
    public void Create_ANamedPermission_IsAccepted()
    {
        var result = new CreatePermissionActionDtoValidator().Validate(new CreatePermissionActionDto { Name = "aaa-permission" });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Create_EmptyName_IsRejected()
    {
        var result = new CreatePermissionActionDtoValidator().Validate(new CreatePermissionActionDto { Name = string.Empty });

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreatePermissionActionDto.Name));
    }
}
