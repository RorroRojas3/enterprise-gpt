using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Model;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service.Mappers;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Mappers;

public class ModelMapperTests
{
    private static Model CreateModel(DateTimeOffset? dateDeactivated = null)
    {
        return new Model
        {
            Id = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(),
            Name = "gpt-test",
            DisplayName = "GPT Test",
            Description = "A test model.",
            ContextWindowSize = 128000m,
            MaxOutputTokens = 16384m,
            IsToolEnabled = true,
            IsDefault = true,
            DateDeactivated = dateDeactivated,
            DateCreated = DateTimeOffset.UtcNow.AddDays(-1),
            DateModified = DateTimeOffset.UtcNow,
            CreatedById = Guid.NewGuid(),
            ModifiedById = Guid.NewGuid()
        };
    }

    private static void AssertMatchesEntity(Model expected, ModelDto actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.ProviderId, actual.ProviderId);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.ContextWindowSize, actual.ContextWindowSize);
        Assert.Equal(expected.MaxOutputTokens, actual.MaxOutputTokens);
        Assert.Equal(expected.IsToolEnabled, actual.IsToolEnabled);
        Assert.Equal(expected.IsDefault, actual.IsDefault);
        Assert.Equal(expected.DateDeactivated, actual.DateDeactivated);
    }

    [Fact]
    public void MapToModelDto_PopulatedEntity_MapsAllProperties()
    {
        var model = CreateModel();

        var dto = model.MapToModelDto();

        AssertMatchesEntity(model, dto);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MapToModelDto_DateDeactivated_RoundTrips(bool deactivated)
    {
        var dateDeactivated = deactivated ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
        var model = CreateModel(dateDeactivated);

        var dto = model.MapToModelDto();

        Assert.Equal(dateDeactivated, dto.DateDeactivated);
    }

    [Fact]
    public void MapToModelDtoExpression_CompiledDelegate_MatchesMapToModelDto()
    {
        var model = CreateModel(DateTimeOffset.UtcNow);
        var projection = ModelMapper.MapToModelDtoExpression.Compile();

        var dto = projection(model);

        Assert.Equal(model.MapToModelDto(), dto);
    }

    [Fact]
    public void FromCreateModelActionDtoToModel_ValidRequest_MapsRequestFieldsAndLeavesAuditDefaults()
    {
        var request = new CreateModelActionDto
        {
            ProviderId = Guid.NewGuid(),
            Name = "gpt-new",
            DisplayName = "GPT New",
            Description = "A brand new model.",
            ContextWindowSize = 200000m,
            MaxOutputTokens = 32768m,
            IsToolEnabled = false,
            IsDefault = true
        };

        var model = request.FromCreateModelActionDtoToModel();

        Assert.Equal(request.ProviderId, model.ProviderId);
        Assert.Equal(request.Name, model.Name);
        Assert.Equal(request.DisplayName, model.DisplayName);
        Assert.Equal(request.Description, model.Description);
        Assert.Equal(request.ContextWindowSize, model.ContextWindowSize);
        Assert.Equal(request.MaxOutputTokens, model.MaxOutputTokens);
        Assert.Equal(request.IsToolEnabled, model.IsToolEnabled);
        Assert.Equal(request.IsDefault, model.IsDefault);
        Assert.Equal(Guid.Empty, model.Id);
        Assert.Equal(default, model.DateCreated);
        Assert.Equal(default, model.DateModified);
        Assert.Equal(Guid.Empty, model.CreatedById);
        Assert.Equal(Guid.Empty, model.ModifiedById);
        Assert.Null(model.DateDeactivated);
    }

    [Fact]
    public void FromUpdateModelActionDtoToModel_ExistingEntity_MutatesInPlaceAndPreservesAudit()
    {
        var model = CreateModel();
        var originalId = model.Id;
        var originalDateCreated = model.DateCreated;
        var originalCreatedById = model.CreatedById;
        var request = new UpdateModelActionDto
        {
            ProviderId = Guid.NewGuid(),
            Name = "gpt-updated",
            DisplayName = "GPT Updated",
            Description = "An updated model.",
            ContextWindowSize = 64000m,
            MaxOutputTokens = 8192m,
            IsToolEnabled = false,
            IsDefault = false
        };

        request.FromUpdateModelActionDtoToModel(model);

        Assert.Equal(request.ProviderId, model.ProviderId);
        Assert.Equal(request.Name, model.Name);
        Assert.Equal(request.DisplayName, model.DisplayName);
        Assert.Equal(request.Description, model.Description);
        Assert.Equal(request.ContextWindowSize, model.ContextWindowSize);
        Assert.Equal(request.MaxOutputTokens, model.MaxOutputTokens);
        Assert.Equal(request.IsToolEnabled, model.IsToolEnabled);
        Assert.Equal(request.IsDefault, model.IsDefault);
        Assert.Equal(originalId, model.Id);
        Assert.Equal(originalDateCreated, model.DateCreated);
        Assert.Equal(originalCreatedById, model.CreatedById);
        Assert.Null(model.DateDeactivated);
    }
}
