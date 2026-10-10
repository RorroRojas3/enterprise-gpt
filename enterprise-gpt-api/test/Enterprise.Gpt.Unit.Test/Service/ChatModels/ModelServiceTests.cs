using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Enterprise.Gpt.Dto.Actions.Model;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Chat;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Services;

public sealed class ModelServiceTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IChatClientResolver _chatClientResolver = Substitute.For<IChatClientResolver>();
    private readonly ModelService _service;

    public ModelServiceTests()
    {
        _tokenService.GetOid().Returns(KnownIds.SeedUserId);
        _chatClientResolver.Resolve(Arg.Any<Guid>()).Returns(Substitute.For<IChatClient>());
        _service = new ModelService(
            NullLogger<ModelService>.Instance,
            _tokenService,
            _fixture.Context,
            new CreateModelActionDtoValidator(),
            new UpdateModelActionDtoValidator(),
            // The seeded row is the summarizer, which is what the guards below key off.
            Options.Create(new SummarizationOptions { ModelId = KnownIds.SeedModelId }),
            _chatClientResolver);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private async Task<Model> AddModelAsync(
        string name, bool isDefault = false, DateTimeOffset? dateDeactivated = null, bool isUserSelectable = true)
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
            IsUserSelectable = isUserSelectable,
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
    public async Task CreateModelAsync_WithPrices_PersistsThem()
    {
        var request = CreateRequest() with
        {
            InputPricePerMillionTokens = 2.500000m,
            OutputPricePerMillionTokens = 10.000000m
        };

        var result = await _service.CreateModelAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(2.500000m, result.InputPricePerMillionTokens);
        var persisted = await FindModelAsync(result.Id);
        Assert.NotNull(persisted);
        Assert.Equal(2.500000m, persisted.InputPricePerMillionTokens);
        Assert.Equal(10.000000m, persisted.OutputPricePerMillionTokens);
    }

    [Fact]
    public async Task CreateModelAsync_WithoutPrices_LeavesThemNullRatherThanZero()
    {
        // Unpriced and free are different states, and a report has to be able to tell them apart.
        var result = await _service.CreateModelAsync(CreateRequest(), TestContext.Current.CancellationToken);

        var persisted = await FindModelAsync(result.Id);
        Assert.NotNull(persisted);
        Assert.Null(persisted.InputPricePerMillionTokens);
        Assert.Null(persisted.OutputPricePerMillionTokens);
    }

    [Fact]
    public async Task CreateModelAsync_NegativePrice_Throws()
    {
        var request = CreateRequest() with { InputPricePerMillionTokens = -0.01m };

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateModelAsync(request, TestContext.Current.CancellationToken));
    }

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
    }

    [Fact]
    public async Task GetModelsAsync_ActiveAndDeactivatedModelsExist_ReturnsOnlyActiveOrderedByName()
    {
        var active = await AddModelAsync("aaa-active");
        var deactivated = await AddModelAsync("bbb-deactivated", dateDeactivated: DateTimeOffset.UtcNow);

        var result = await _service.GetModelsAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result, x => x.Id == active.Id);
        // The seeded row is the pinned summarizer, hidden from the picker by its own seed.
        Assert.DoesNotContain(result, x => x.Id == KnownIds.SeedModelId);
        Assert.DoesNotContain(result, x => x.Id == deactivated.Id);
        Assert.Equal(result.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Id), result.Select(x => x.Id));
    }

    [Fact]
    public async Task GetModelsAsync_ModelIsNotUserSelectable_ExcludesIt()
    {
        var visible = await AddModelAsync("aaa-visible");
        var hidden = await AddModelAsync("bbb-hidden", isUserSelectable: false);

        var result = await _service.GetModelsAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result, x => x.Id == visible.Id);
        Assert.DoesNotContain(result, x => x.Id == hidden.Id);
    }

    [Fact]
    public async Task GetAllModelsAsync_ModelIsNotUserSelectable_StillIncludesIt()
    {
        var hidden = await AddModelAsync("bbb-hidden", isUserSelectable: false);

        var result = await _service.GetAllModelsAsync(TestContext.Current.CancellationToken);

        var mapped = Assert.Single(result, x => x.Id == hidden.Id);
        Assert.False(mapped.IsUserSelectable);
    }

    /// <summary>
    /// Hiding a model is not withdrawing it: a turn that names one by id still resolves, which is
    /// what keeps <c>IsUserSelectable</c> a picker concern rather than an authorization one.
    /// </summary>
    [Fact]
    public async Task GetModelAsync_ModelIsNotUserSelectable_StillReturnsIt()
    {
        var hidden = await AddModelAsync("bbb-hidden", isUserSelectable: false);

        var result = await _service.GetModelAsync(hidden.Id, TestContext.Current.CancellationToken);

        Assert.Equal(hidden.Id, result.Id);
        Assert.False(result.IsUserSelectable);
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

        using var before = _fixture.CreateContext();
        var seeded = await before.Models.CountAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateModelAsync(request, TestContext.Current.CancellationToken));

        using var ctx = _fixture.CreateContext();
        Assert.Equal(seeded, await ctx.Models.CountAsync(TestContext.Current.CancellationToken));
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

    [Fact]
    public async Task UpdateModelAsync_ClearsIsUserSelectable_PersistsAndLeavesThePickerQuery()
    {
        var model = await AddModelAsync("aaa-original");
        var request = UpdateRequest(name: "aaa-original") with { IsUserSelectable = false };

        var result = await _service.UpdateModelAsync(model.Id, request, TestContext.Current.CancellationToken);

        Assert.False(result.IsUserSelectable);

        var persisted = await FindModelAsync(model.Id);
        Assert.NotNull(persisted);
        Assert.False(persisted.IsUserSelectable);

        var picker = await _service.GetModelsAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(picker, x => x.Id == model.Id);
    }

    /// <summary>
    /// A hidden default resolves to nothing on the picker's own route, which leaves every client
    /// with no model to start a turn on. The same invariant <c>DeactivateModelAsync</c> keeps from
    /// the other side by clearing <c>IsDefault</c> on the row it retires.
    /// </summary>
    [Fact]
    public async Task CreateModelAsync_DefaultAndNotUserSelectable_ThrowsValidationException()
    {
        var request = CreateRequest(name: "aaa-hidden-default", isDefault: true) with { IsUserSelectable = false };

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateModelAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains(exception.Errors, x => x.PropertyName == nameof(CreateModelActionDto.IsDefault));
    }

    [Fact]
    public async Task UpdateModelAsync_DefaultAndNotUserSelectable_ThrowsValidationException()
    {
        var model = await AddModelAsync("aaa-original");
        var request = UpdateRequest(name: "aaa-original", isDefault: true) with { IsUserSelectable = false };

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _service.UpdateModelAsync(model.Id, request, TestContext.Current.CancellationToken));

        Assert.Contains(exception.Errors, x => x.PropertyName == nameof(UpdateModelActionDto.IsDefault));
    }

    /// <summary>
    /// Hiding a model that is not the default is ordinary and must stay allowed — the rule above
    /// is about the combination, not about hiding.
    /// </summary>
    [Fact]
    public async Task CreateModelAsync_NotUserSelectableAndNotDefault_Succeeds()
    {
        var request = CreateRequest(name: "aaa-hidden") with { IsUserSelectable = false };

        var result = await _service.CreateModelAsync(request, TestContext.Current.CancellationToken);

        Assert.False(result.IsUserSelectable);
        Assert.False(result.IsDefault);
    }

    /// <summary>
    /// The startup check treats a deactivated summarizer row as missing, and the seed-correcting
    /// migration is already recorded — so retiring it stops every instance booting, permanently,
    /// from a supported admin route.
    /// </summary>
    [Fact]
    public async Task DeactivateModelAsync_ConfiguredSummarizer_ThrowsSummarizerProtectedException()
    {
        var exception = await Assert.ThrowsAsync<SummarizerProtectedException>(
            () => _service.DeactivateModelAsync(KnownIds.SeedModelId, TestContext.Current.CancellationToken));

        Assert.Equal(KnownIds.SeedModelId, exception.ModelId);
        // The sentence an operator needs is in the message, because a bodyless DELETE gives the
        // client nothing else to render.
        Assert.Contains("Summarization:ModelId", exception.Message);

        var persisted = await FindModelAsync(KnownIds.SeedModelId);
        Assert.NotNull(persisted);
        Assert.Null(persisted.DateDeactivated);
    }

    [Fact]
    public async Task DeactivateModelAsync_AnyOtherModel_StillDeactivates()
    {
        var model = await AddModelAsync("aaa-ordinary");

        await _service.DeactivateModelAsync(model.Id, TestContext.Current.CancellationToken);

        var persisted = await FindModelAsync(model.Id);
        Assert.NotNull(persisted);
        Assert.NotNull(persisted.DateDeactivated);
    }

    /// <summary>
    /// The same startup failure by a different edit: the bootstrapper resolves a chat client for
    /// the summarizer's provider and fails the app when there is none.
    /// </summary>
    [Fact]
    public async Task UpdateModelAsync_SummarizerRepointedAtUnservableProvider_ThrowsValidationException()
    {
        var provider = await AddProviderAsync("unservable");
        _chatClientResolver.Resolve(provider.Id).Throws(new ProviderNotConfiguredException(provider.Id));
        var request = UpdateRequest(providerId: provider.Id, name: "RR GPT 5.6 Luna");

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _service.UpdateModelAsync(KnownIds.SeedModelId, request, TestContext.Current.CancellationToken));

        Assert.Contains(exception.Errors, x => x.PropertyName == nameof(Model.ProviderId));
    }

    [Fact]
    public async Task UpdateModelAsync_OrdinaryModelRepointedAtUnservableProvider_StillSucceeds()
    {
        var model = await AddModelAsync("aaa-ordinary");
        var provider = await AddProviderAsync("unservable");
        _chatClientResolver.Resolve(provider.Id).Throws(new ProviderNotConfiguredException(provider.Id));
        var request = UpdateRequest(providerId: provider.Id, name: "aaa-ordinary");

        var result = await _service.UpdateModelAsync(model.Id, request, TestContext.Current.CancellationToken);

        Assert.Equal(provider.Id, result.ProviderId);
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
