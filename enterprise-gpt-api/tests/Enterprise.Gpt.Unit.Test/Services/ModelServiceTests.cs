using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Enterprise.Gpt.Dto.Actions.Model;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Services;

public sealed class ModelServiceTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly ModelService _service;

    public ModelServiceTests()
    {
        _tokenService.GetOid().Returns(KnownIds.SeedUserId);
        _service = new ModelService(
            NullLogger<ModelService>.Instance,
            _tokenService,
            _fixture.Context,
            new CreateModelActionDtoValidator(),
            new UpdateModelActionDtoValidator());
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private async Task<Model> AddModelAsync(string name, bool isDefault = false, DateTimeOffset? dateDeactivated = null)
    {
        var date = DateTimeOffset.UtcNow;
        var model = new Model
        {
            Id = Guid.NewGuid(),
            ProviderId = KnownIds.SeedProviderId,
            Name = name,
            DeploymentName = $"{name}-deployment",
            Description = $"{name} description",
            ContextWindowSize = 1000m,
            MaxOutputTokens = 100m,
            IsToolEnabled = true,
            IsDefault = isDefault,
            DateDeactivated = dateDeactivated,
            DateCreated = date,
            DateModified = date,
            CreatedById = KnownIds.SeedUserId,
            ModifiedById = KnownIds.SeedUserId
        };

        using var ctx = _fixture.CreateContext();
        ctx.Models.Add(model);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return model;
    }

    private async Task<Provider> AddProviderAsync(string name, DateTimeOffset? dateDeactivated = null)
    {
        var provider = new Provider
        {
            Id = Guid.NewGuid(),
            Name = name,
            DateCreated = DateTimeOffset.UtcNow,
            DateDeactivated = dateDeactivated
        };

        using var ctx = _fixture.CreateContext();
        ctx.Providers.Add(provider);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return provider;
    }

    private async Task<Model?> FindModelAsync(Guid id)
    {
        using var ctx = _fixture.CreateContext();
        return await ctx.Models
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, TestContext.Current.CancellationToken);
    }

    private static CreateModelActionDto CreateRequest(
        Guid? providerId = null, string name = "aaa-created", bool isDefault = false)
    {
        return new CreateModelActionDto
        {
            ProviderId = providerId ?? KnownIds.SeedProviderId,
            Name = name,
            DeploymentName = $"{name}-deployment",
            Description = $"{name} description",
            ContextWindowSize = 128000m,
            MaxOutputTokens = 16384m,
            IsToolEnabled = true,
            IsDefault = isDefault
        };
    }

    private static UpdateModelActionDto UpdateRequest(
        Guid? providerId = null, string name = "aaa-updated", bool isDefault = false)
    {
        return new UpdateModelActionDto
        {
            ProviderId = providerId ?? KnownIds.SeedProviderId,
            Name = name,
            DeploymentName = $"{name}-deployment",
            Description = $"{name} description",
            ContextWindowSize = 64000m,
            MaxOutputTokens = 8192m,
            IsToolEnabled = false,
            IsDefault = isDefault
        };
    }

    [Fact]
    public async Task GetModelsAsync_ActiveAndDeactivatedModelsExist_ReturnsOnlyActiveOrderedByName()
    {
        var active = await AddModelAsync("aaa-active");
        var deactivated = await AddModelAsync("bbb-deactivated", dateDeactivated: DateTimeOffset.UtcNow);

        var result = await _service.GetModelsAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result, x => x.Id == active.Id);
        Assert.Contains(result, x => x.Id == KnownIds.SeedModelId);
        Assert.DoesNotContain(result, x => x.Id == deactivated.Id);
        Assert.Equal(result.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Id), result.Select(x => x.Id));
    }

    [Fact]
    public async Task GetAllModelsAsync_DeactivatedModelExists_IncludesDeactivatedOrderedByName()
    {
        var deactivated = await AddModelAsync("bbb-deactivated", dateDeactivated: DateTimeOffset.UtcNow);

        var result = await _service.GetAllModelsAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result, x => x.Id == deactivated.Id);
        Assert.Contains(result, x => x.Id == KnownIds.SeedModelId);
        Assert.Equal(result.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Id), result.Select(x => x.Id));
    }

    [Fact]
    public async Task GetModelAsync_ActiveModelExists_ReturnsMappedDto()
    {
        var model = await AddModelAsync("aaa-single");

        var result = await _service.GetModelAsync(model.Id, TestContext.Current.CancellationToken);

        Assert.Equal(model.Id, result.Id);
        Assert.Equal(model.ProviderId, result.ProviderId);
        Assert.Equal(model.Name, result.Name);
        Assert.Equal(model.DeploymentName, result.DeploymentName);
        Assert.Equal(model.Description, result.Description);
        Assert.Equal(model.ContextWindowSize, result.ContextWindowSize);
        Assert.Equal(model.MaxOutputTokens, result.MaxOutputTokens);
        Assert.Equal(model.IsToolEnabled, result.IsToolEnabled);
        Assert.Equal(model.IsDefault, result.IsDefault);
        Assert.Null(result.DateDeactivated);
    }

    [Fact]
    public async Task GetModelAsync_UnknownId_ThrowsNotFoundException()
    {
        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetModelAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Equal("Model not found.", exception.Message);
    }

    [Fact]
    public async Task GetModelAsync_DeactivatedModel_ThrowsNotFoundException()
    {
        var deactivated = await AddModelAsync("bbb-deactivated", dateDeactivated: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetModelAsync(deactivated.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateModelAsync_ValidRequest_PersistsModelWithAuditFields()
    {
        var request = CreateRequest();

        var result = await _service.CreateModelAsync(request, TestContext.Current.CancellationToken);

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(request.Name, result.Name);
        Assert.Null(result.DateDeactivated);

        var persisted = await FindModelAsync(result.Id);
        Assert.NotNull(persisted);
        Assert.Equal(request.ProviderId, persisted.ProviderId);
        Assert.Equal(request.Name, persisted.Name);
        Assert.Equal(request.DeploymentName, persisted.DeploymentName);
        Assert.Equal(request.Description, persisted.Description);
        Assert.Equal(KnownIds.SeedUserId, persisted.CreatedById);
        Assert.Equal(KnownIds.SeedUserId, persisted.ModifiedById);
        Assert.Equal(persisted.DateCreated, persisted.DateModified);
        Assert.Null(persisted.DateDeactivated);
    }

    [Fact]
    public async Task CreateModelAsync_InvalidRequest_ThrowsValidationExceptionAndPersistsNothing()
    {
        var request = CreateRequest() with { Name = string.Empty, ContextWindowSize = 0m };

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateModelAsync(request, TestContext.Current.CancellationToken));

        using var ctx = _fixture.CreateContext();
        Assert.Equal(1, await ctx.Models.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateModelAsync_PricesSupplied_PersistsThem()
    {
        var request = CreateRequest() with
        {
            InputPricePerMillionTokens = 1.25m,
            OutputPricePerMillionTokens = 10m
        };

        var result = await _service.CreateModelAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(1.25m, result.InputPricePerMillionTokens);
        Assert.Equal(10m, result.OutputPricePerMillionTokens);

        var persisted = await FindModelAsync(result.Id);
        Assert.NotNull(persisted);
        Assert.Equal(1.25m, persisted.InputPricePerMillionTokens);
        Assert.Equal(10m, persisted.OutputPricePerMillionTokens);
    }

    /// <summary>
    /// An omitted price stores null rather than zero, so "we have not priced this model" stays
    /// distinguishable from "this model is free".
    /// </summary>
    [Fact]
    public async Task CreateModelAsync_PricesOmitted_PersistsNullRatherThanZero()
    {
        var request = CreateRequest();

        var result = await _service.CreateModelAsync(request, TestContext.Current.CancellationToken);

        var persisted = await FindModelAsync(result.Id);
        Assert.NotNull(persisted);
        Assert.Null(persisted.InputPricePerMillionTokens);
        Assert.Null(persisted.OutputPricePerMillionTokens);
    }

    [Theory]
    [InlineData(-1d, null)]
    [InlineData(null, -0.000001d)]
    public async Task CreateModelAsync_NegativePrice_ThrowsValidationException(double? inputPrice, double? outputPrice)
    {
        var request = CreateRequest() with
        {
            InputPricePerMillionTokens = (decimal?)inputPrice,
            OutputPricePerMillionTokens = (decimal?)outputPrice
        };

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateModelAsync(request, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Zero is a legitimate price — a model the platform is not charged for — so it must pass
    /// where a negative fails.
    /// </summary>
    [Fact]
    public async Task CreateModelAsync_ZeroPrice_IsAccepted()
    {
        var request = CreateRequest() with
        {
            InputPricePerMillionTokens = 0m,
            OutputPricePerMillionTokens = 0m
        };

        var result = await _service.CreateModelAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(0m, result.InputPricePerMillionTokens);
        Assert.Equal(0m, result.OutputPricePerMillionTokens);
    }

    [Fact]
    public async Task CreateModelAsync_UnknownProvider_ThrowsNotFoundException()
    {
        var request = CreateRequest(providerId: Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.CreateModelAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("Provider not found.", exception.Message);
    }

    [Fact]
    public async Task CreateModelAsync_DeactivatedProvider_ThrowsNotFoundException()
    {
        var provider = await AddProviderAsync("deactivated-provider", DateTimeOffset.UtcNow);
        var request = CreateRequest(providerId: provider.Id);

        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.CreateModelAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("Provider not found.", exception.Message);
    }

    [Fact]
    public async Task CreateModelAsync_IsDefaultTrue_DemotesExistingDefault()
    {
        var existingDefault = await AddModelAsync("aaa-existing-default", isDefault: true);
        var request = CreateRequest(name: "bbb-new-default", isDefault: true);

        var result = await _service.CreateModelAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.IsDefault);

        var demoted = await FindModelAsync(existingDefault.Id);
        Assert.NotNull(demoted);
        Assert.False(demoted.IsDefault);
        Assert.Equal(KnownIds.SeedUserId, demoted.ModifiedById);
        Assert.True(demoted.DateModified > existingDefault.DateModified);

        using var ctx = _fixture.CreateContext();
        Assert.Equal(1, await ctx.Models.CountAsync(x => x.IsDefault, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateModelAsync_IsDefaultFalse_LeavesExistingDefaultUntouched()
    {
        var existingDefault = await AddModelAsync("aaa-existing-default", isDefault: true);
        var request = CreateRequest(name: "bbb-regular");

        await _service.CreateModelAsync(request, TestContext.Current.CancellationToken);

        var untouched = await FindModelAsync(existingDefault.Id);
        Assert.NotNull(untouched);
        Assert.True(untouched.IsDefault);
        Assert.Equal(existingDefault.DateModified, untouched.DateModified);
    }

    [Fact]
    public async Task UpdateModelAsync_ValidRequest_UpdatesFieldsAndAudit()
    {
        var model = await AddModelAsync("aaa-original");
        var request = UpdateRequest(name: "aaa-renamed");

        var result = await _service.UpdateModelAsync(model.Id, request, TestContext.Current.CancellationToken);

        Assert.Equal(request.Name, result.Name);
        Assert.Equal(request.DeploymentName, result.DeploymentName);
        Assert.Equal(request.Description, result.Description);
        Assert.Equal(request.ContextWindowSize, result.ContextWindowSize);
        Assert.Equal(request.MaxOutputTokens, result.MaxOutputTokens);
        Assert.Equal(request.IsToolEnabled, result.IsToolEnabled);

        var persisted = await FindModelAsync(model.Id);
        Assert.NotNull(persisted);
        Assert.Equal(request.Name, persisted.Name);
        Assert.Equal(model.DateCreated, persisted.DateCreated);
        Assert.Equal(model.CreatedById, persisted.CreatedById);
        Assert.Equal(KnownIds.SeedUserId, persisted.ModifiedById);
        Assert.True(persisted.DateModified > model.DateModified);
    }

    /// <summary>
    /// PUT carries a full representation, so a price left out of the body clears the stored one
    /// rather than preserving it — the same semantics the rest of the catalog already uses.
    /// </summary>
    [Fact]
    public async Task UpdateModelAsync_PricesOmitted_ClearsStoredPrices()
    {
        var model = await AddModelAsync("aaa-original");
        var priced = UpdateRequest() with
        {
            InputPricePerMillionTokens = 2m,
            OutputPricePerMillionTokens = 8m
        };
        await _service.UpdateModelAsync(model.Id, priced, TestContext.Current.CancellationToken);

        var result = await _service.UpdateModelAsync(model.Id, UpdateRequest(), TestContext.Current.CancellationToken);

        Assert.Null(result.InputPricePerMillionTokens);
        Assert.Null(result.OutputPricePerMillionTokens);

        var persisted = await FindModelAsync(model.Id);
        Assert.NotNull(persisted);
        Assert.Null(persisted.InputPricePerMillionTokens);
        Assert.Null(persisted.OutputPricePerMillionTokens);
    }

    [Fact]
    public async Task UpdateModelAsync_NegativePrice_ThrowsValidationException()
    {
        var model = await AddModelAsync("aaa-original");
        var request = UpdateRequest() with { OutputPricePerMillionTokens = -5m };

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.UpdateModelAsync(model.Id, request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateModelAsync_InvalidRequest_ThrowsValidationException()
    {
        var model = await AddModelAsync("aaa-original");
        var request = UpdateRequest() with { DeploymentName = string.Empty, MaxOutputTokens = 0m };

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.UpdateModelAsync(model.Id, request, TestContext.Current.CancellationToken));

        var persisted = await FindModelAsync(model.Id);
        Assert.NotNull(persisted);
        Assert.Equal(model.Name, persisted.Name);
    }

    [Fact]
    public async Task UpdateModelAsync_UnknownId_ThrowsNotFoundException()
    {
        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.UpdateModelAsync(Guid.NewGuid(), UpdateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal("Model not found.", exception.Message);
    }

    [Fact]
    public async Task UpdateModelAsync_DeactivatedModel_ThrowsNotFoundException()
    {
        var deactivated = await AddModelAsync("bbb-deactivated", dateDeactivated: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.UpdateModelAsync(deactivated.Id, UpdateRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateModelAsync_ChangedProviderUnknown_ThrowsNotFoundException()
    {
        var model = await AddModelAsync("aaa-original");
        var request = UpdateRequest(providerId: Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.UpdateModelAsync(model.Id, request, TestContext.Current.CancellationToken));

        Assert.Equal("Provider not found.", exception.Message);
    }

    [Fact]
    public async Task UpdateModelAsync_ProviderUnchanged_SkipsProviderExistenceCheck()
    {
        var model = await AddModelAsync("aaa-original");
        using (var ctx = _fixture.CreateContext())
        {
            var provider = await ctx.Providers.SingleAsync(
                x => x.Id == KnownIds.SeedProviderId, TestContext.Current.CancellationToken);
            provider.DateDeactivated = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await _service.UpdateModelAsync(model.Id, UpdateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(KnownIds.SeedProviderId, result.ProviderId);
    }

    [Fact]
    public async Task UpdateModelAsync_IsDefaultTrue_DemotesOtherDefault()
    {
        var existingDefault = await AddModelAsync("aaa-existing-default", isDefault: true);
        var model = await AddModelAsync("bbb-promoted");
        var request = UpdateRequest(name: "bbb-promoted", isDefault: true);

        var result = await _service.UpdateModelAsync(model.Id, request, TestContext.Current.CancellationToken);

        Assert.True(result.IsDefault);

        var demoted = await FindModelAsync(existingDefault.Id);
        Assert.NotNull(demoted);
        Assert.False(demoted.IsDefault);

        using var ctx = _fixture.CreateContext();
        Assert.Equal(1, await ctx.Models.CountAsync(x => x.IsDefault, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeactivateModelAsync_ActiveModel_SoftDeletesAndClearsDefault()
    {
        var model = await AddModelAsync("aaa-default", isDefault: true);

        await _service.DeactivateModelAsync(model.Id, TestContext.Current.CancellationToken);

        var persisted = await FindModelAsync(model.Id);
        Assert.NotNull(persisted);
        Assert.NotNull(persisted.DateDeactivated);
        Assert.False(persisted.IsDefault);
        Assert.Equal(KnownIds.SeedUserId, persisted.ModifiedById);
        Assert.True(persisted.DateModified > model.DateModified);
    }

    [Fact]
    public async Task DeactivateModelAsync_UnknownId_ThrowsNotFoundException()
    {
        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.DeactivateModelAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Equal("Model not found.", exception.Message);
    }

    [Fact]
    public async Task DeactivateModelAsync_AlreadyDeactivatedModel_ThrowsNotFoundException()
    {
        var model = await AddModelAsync("aaa-once");
        await _service.DeactivateModelAsync(model.Id, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.DeactivateModelAsync(model.Id, TestContext.Current.CancellationToken));
    }
}
