using Enterprise.Gpt.Api.Startup;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service.Chat;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Summarization;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Summarization;

public sealed class SummarizerBootstrapperTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly IChatClientResolver _chatClientResolver = Substitute.For<IChatClientResolver>();

    public SummarizerBootstrapperTests()
    {
        _chatClientResolver.Resolve(Arg.Any<Guid>()).Returns(Substitute.For<IChatClient>());
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private ServiceProvider CreateServices(Guid modelId)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_chatClientResolver);
        services.AddSingleton<ISummarizerModelResolver>(_ => new SummarizerModelResolver(
            _fixture.Context, Options.Create(new SummarizationOptions { ModelId = modelId })));

        return services.BuildServiceProvider();
    }

    private async Task<Model> AddModelAsync(Guid providerId, DateTimeOffset? dateDeactivated = null)
    {
        var date = DateTimeOffset.UtcNow;
        var model = new Model
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            Name = "bootstrapper-under-test",
            DeploymentName = "bootstrapper-under-test-deployment",
            Description = "A summarizer under test.",
            ContextWindowSize = 1_000m,
            MaxOutputTokens = 100m,
            IsUserSelectable = false,
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

    [Fact]
    public async Task ValidateAsync_SeededSummarizerAndRegisteredProvider_Succeeds()
    {
        using var services = CreateServices(KnownIds.SeedModelId);

        await SummarizerBootstrapper.ValidateAsync(
            services, NullLogger.Instance, TestContext.Current.CancellationToken);

        _chatClientResolver.Received(1).Resolve(Providers.AzureOpenAI);
    }

    [Fact]
    public async Task ValidateAsync_UnknownModelId_ThrowsNamingTheConfiguredId()
    {
        var missingId = Guid.NewGuid();
        using var services = CreateServices(missingId);

        var exception = await Assert.ThrowsAsync<SummarizerNotConfiguredException>(
            () => SummarizerBootstrapper.ValidateAsync(
                services, NullLogger.Instance, TestContext.Current.CancellationToken));

        Assert.Equal(missingId, exception.ModelId);
    }

    [Fact]
    public async Task ValidateAsync_DeactivatedModel_Throws()
    {
        var model = await AddModelAsync(Providers.AzureOpenAI, DateTimeOffset.UtcNow);
        using var services = CreateServices(model.Id);

        var exception = await Assert.ThrowsAsync<SummarizerNotConfiguredException>(
            () => SummarizerBootstrapper.ValidateAsync(
                services, NullLogger.Instance, TestContext.Current.CancellationToken));

        Assert.Equal(model.Id, exception.ModelId);
    }

    /// <summary>
    /// The whole reason the bootstrapper resolves a chat client at all: without this the call is a
    /// statement no test would miss if it were deleted.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_ProviderWithNoChatClient_ThrowsProviderNotConfigured()
    {
        var model = await AddModelAsync(Providers.AmazonBedrock);
        _chatClientResolver.Resolve(Providers.AmazonBedrock)
            .Returns(_ => throw new ProviderNotConfiguredException(Providers.AmazonBedrock));
        using var services = CreateServices(model.Id);

        var exception = await Assert.ThrowsAsync<ProviderNotConfiguredException>(
            () => SummarizerBootstrapper.ValidateAsync(
                services, NullLogger.Instance, TestContext.Current.CancellationToken));

        Assert.Equal(Providers.AmazonBedrock, exception.ProviderId);
    }
}
