using NSubstitute;
using Enterprise.Gpt.Api.Endpoints;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Model;
using Enterprise.Gpt.Service;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Endpoints;

public class ModelEndpointsTests
{
    private readonly IModelService _modelService = Substitute.For<IModelService>();

    private static ModelDto CreateDto(string name = "gpt-test")
    {
        return new ModelDto
        {
            Id = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(),
            Name = name,
            DisplayName = $"{name} display",
            Description = $"{name} description",
            ContextWindowSize = 128000m,
            MaxOutputTokens = 16384m,
            IsToolEnabled = true,
            IsDefault = false,
            DateDeactivated = null
        };
    }

    [Fact]
    public async Task GetModelsAsync_ServiceReturnsModels_ReturnsOkWithPayload()
    {
        List<ModelDto> expected = [CreateDto()];
        _modelService.GetModelsAsync(Arg.Any<CancellationToken>()).Returns(expected);

        var result = await ModelEndpoints.GetModelsAsync(_modelService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task GetAllModelsAsync_ServiceReturnsModels_ReturnsOkWithPayload()
    {
        List<ModelDto> expected = [CreateDto("gpt-active"), CreateDto("gpt-deactivated")];
        _modelService.GetAllModelsAsync(Arg.Any<CancellationToken>()).Returns(expected);

        var result = await ModelEndpoints.GetAllModelsAsync(_modelService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task GetModelAsync_ServiceReturnsModel_ReturnsOkWithPayload()
    {
        var expected = CreateDto();
        _modelService.GetModelAsync(expected.Id, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await ModelEndpoints.GetModelAsync(expected.Id, _modelService, TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task CreateModelAsync_ServiceCreatesModel_ReturnsCreatedWithLocation()
    {
        var request = new CreateModelActionDto
        {
            ProviderId = Guid.NewGuid(),
            Name = "gpt-new",
            DisplayName = "GPT New",
            Description = "A new model.",
            ContextWindowSize = 128000m,
            MaxOutputTokens = 16384m,
            IsToolEnabled = true,
            IsDefault = false
        };
        var created = CreateDto("gpt-new");
        _modelService.CreateModelAsync(request, Arg.Any<CancellationToken>()).Returns(created);

        var result = await ModelEndpoints.CreateModelAsync(request, _modelService, TestContext.Current.CancellationToken);

        Assert.Equal($"/api/models/{created.Id}", result.Location);
        Assert.Same(created, result.Value);
    }

    [Fact]
    public async Task UpdateModelAsync_ServiceUpdatesModel_ReturnsOkWithPayload()
    {
        var id = Guid.NewGuid();
        var request = new UpdateModelActionDto
        {
            ProviderId = Guid.NewGuid(),
            Name = "gpt-updated",
            DisplayName = "GPT Updated",
            Description = "An updated model.",
            ContextWindowSize = 64000m,
            MaxOutputTokens = 8192m,
            IsToolEnabled = false,
            IsDefault = true
        };
        var updated = CreateDto("gpt-updated");
        _modelService.UpdateModelAsync(id, request, Arg.Any<CancellationToken>()).Returns(updated);

        var result = await ModelEndpoints.UpdateModelAsync(id, request, _modelService, TestContext.Current.CancellationToken);

        Assert.Same(updated, result.Value);
    }

    [Fact]
    public async Task DeactivateModelAsync_ServiceDeactivatesModel_ReturnsNoContent()
    {
        var id = Guid.NewGuid();

        await ModelEndpoints.DeactivateModelAsync(id, _modelService, TestContext.Current.CancellationToken);

        await _modelService.Received(1).DeactivateModelAsync(id, Arg.Any<CancellationToken>());
    }
}
