using Enterprise.Gpt.Dto.Actions.Projects;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Dto.Actions.Projects;

public class ProjectActionValidatorTests
{
    [Fact]
    public void Create_ANamedProject_IsAccepted()
    {
        var result = new CreateProjectActionDtoValidator().Validate(new CreateProjectActionDto { Name = "aaa-project" });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Create_EmptyName_IsRejected()
    {
        var result = new CreateProjectActionDtoValidator().Validate(new CreateProjectActionDto { Name = string.Empty });

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateProjectActionDto.Name));
    }
}
