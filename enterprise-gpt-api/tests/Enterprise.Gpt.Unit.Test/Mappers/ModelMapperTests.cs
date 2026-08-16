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
            Name = "GPT Test",
            DeploymentName = "gpt-test",
            Description = "A test model.",
            ContextWindowSize = 128000m,
            MaxOutputTokens = 16384m,
            IsToolEnabled = true,
            IsDefault = true,
            InputPricePerMillionTokens = 2.500000m,
            OutputPricePerMillionTokens = 10.000000m,
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
        Assert.Equal(expected.DeploymentName, actual.DeploymentName);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.ContextWindowSize, actual.ContextWindowSize);
        Assert.Equal(expected.MaxOutputTokens, actual.MaxOutputTokens);
        Assert.Equal(expected.IsToolEnabled, actual.IsToolEnabled);
        Assert.Equal(expected.IsDefault, actual.IsDefault);
        // Asserted through the shared helper so it covers the materialized mapper and the LINQ
        // projection alike: the two are hand-duplicated, and a field added to one and not the other
        // works on a create response and is silently null on every list and get.
        Assert.Equal(expected.InputPricePerMillionTokens, actual.InputPricePerMillionTokens);
        Assert.Equal(expected.OutputPricePerMillionTokens, actual.OutputPricePerMillionTokens);
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
            Name = "GPT New",
            DeploymentName = "gpt-new",
            Description = "A brand new model.",
            ContextWindowSize = 200000m,
            MaxOutputTokens = 32768m,
            IsToolEnabled = false,
            IsDefault = true,
            InputPricePerMillionTokens = 1.250000m,
            OutputPricePerMillionTokens = 5m
        };

        var model = request.FromCreateModelActionDtoToModel();

        Assert.Equal(request.ProviderId, model.ProviderId);
        Assert.Equal(request.Name, model.Name);
        Assert.Equal(request.DeploymentName, model.DeploymentName);
        Assert.Equal(request.Description, model.Description);
        Assert.Equal(request.ContextWindowSize, model.ContextWindowSize);
        Assert.Equal(request.MaxOutputTokens, model.MaxOutputTokens);
        Assert.Equal(request.IsToolEnabled, model.IsToolEnabled);
        Assert.Equal(request.IsDefault, model.IsDefault);
        Assert.Equal(request.InputPricePerMillionTokens, model.InputPricePerMillionTokens);
        Assert.Equal(request.OutputPricePerMillionTokens, model.OutputPricePerMillionTokens);
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
            Name = "GPT Updated",
            DeploymentName = "gpt-updated",
            Description = "An updated model.",
            ContextWindowSize = 64000m,
            MaxOutputTokens = 8192m,
            IsToolEnabled = false,
            IsDefault = false
        };

        request.FromUpdateModelActionDtoToModel(model);

        Assert.Equal(request.ProviderId, model.ProviderId);
        Assert.Equal(request.Name, model.Name);
        Assert.Equal(request.DeploymentName, model.DeploymentName);
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

    /// <summary>
    /// The update request is a full representation, so an omitted price clears the stored one. The
    /// alternative — treating null as "leave it alone" — would make an administrator unable to
    /// un-price a model through the API at all.
    /// </summary>
    [Fact]
    public void FromUpdateModelActionDtoToModel_OmittedPrices_ClearsTheStoredPrices()
    {
        var model = CreateModel();
        var request = new UpdateModelActionDto
        {
            ProviderId = model.ProviderId,
            Name = model.Name,
            DeploymentName = model.DeploymentName,
            Description = model.Description,
            ContextWindowSize = model.ContextWindowSize,
            MaxOutputTokens = model.MaxOutputTokens,
            IsToolEnabled = model.IsToolEnabled,
            IsDefault = model.IsDefault
        };

        request.FromUpdateModelActionDtoToModel(model);

        Assert.Null(model.InputPricePerMillionTokens);
        Assert.Null(model.OutputPricePerMillionTokens);
    }

    [Fact]
    public void FromUpdateModelActionDtoToModel_SuppliedPrices_ReplacesTheStoredPrices()
    {
        var model = CreateModel();
        var request = new UpdateModelActionDto
        {
            ProviderId = model.ProviderId,
            Name = model.Name,
            DeploymentName = model.DeploymentName,
            Description = model.Description,
            ContextWindowSize = model.ContextWindowSize,
            MaxOutputTokens = model.MaxOutputTokens,
            IsToolEnabled = model.IsToolEnabled,
            IsDefault = model.IsDefault,
            InputPricePerMillionTokens = 0.150000m,
            OutputPricePerMillionTokens = 0.600000m
        };

        request.FromUpdateModelActionDtoToModel(model);

        Assert.Equal(0.150000m, model.InputPricePerMillionTokens);
        Assert.Equal(0.600000m, model.OutputPricePerMillionTokens);
    }
}
