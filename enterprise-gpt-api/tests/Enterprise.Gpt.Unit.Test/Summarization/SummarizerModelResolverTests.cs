using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Summarization;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Summarization;

public sealed class SummarizerModelResolverTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private SummarizerModelResolver CreateResolver(Guid modelId)
    {
        return new SummarizerModelResolver(
            _fixture.Context, Options.Create(new SummarizationOptions { ModelId = modelId }));
    }

    private async Task<Model> AddModelAsync(
        Guid? providerId = null, decimal contextWindowSize = 1_000m, DateTimeOffset? dateDeactivated = null)
    {
        var date = DateTimeOffset.UtcNow;
        var model = new Model
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId ?? KnownIds.SeedProviderId,
            Name = "summarizer-under-test",
            DeploymentName = "summarizer-under-test-deployment",
            Description = "A summarizer under test.",
            ContextWindowSize = contextWindowSize,
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
    public async Task ResolveAsync_SeededSummarizer_ReturnsTheCorrectedCatalogValues()
    {
        var resolver = CreateResolver(KnownIds.SeedModelId);

        var summarizer = await resolver.ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(KnownIds.SeedModelId, summarizer.ModelId);
        Assert.Equal(Providers.AzureOpenAI, summarizer.ProviderId);
        Assert.Equal("rr-gpt5.6-luna", summarizer.DeploymentName);
        Assert.Equal(1_000_000m, summarizer.ContextWindowSize);
        Assert.Equal(16_384m, summarizer.MaxOutputTokens);
    }

    [Fact]
    public async Task ResolveAsync_UnknownModelId_ThrowsNamingTheConfiguredId()
    {
        var missingId = Guid.NewGuid();
        var resolver = CreateResolver(missingId);

        var exception = await Assert.ThrowsAsync<SummarizerNotConfiguredException>(
            () => resolver.ResolveAsync(TestContext.Current.CancellationToken));

        Assert.Equal(missingId, exception.ModelId);
        Assert.Contains(missingId.ToString(), exception.Message);
    }

    /// <summary>
    /// Retiring the summarizer has to stop summarization rather than keep billing against a
    /// deployment the operator believes they withdrew, so a deactivated row reads as absent.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_DeactivatedModel_ThrowsAsThoughItWereMissing()
    {
        var model = await AddModelAsync(dateDeactivated: DateTimeOffset.UtcNow);
        var resolver = CreateResolver(model.Id);

        var exception = await Assert.ThrowsAsync<SummarizerNotConfiguredException>(
            () => resolver.ResolveAsync(TestContext.Current.CancellationToken));

        Assert.Equal(model.Id, exception.ModelId);
    }

    /// <summary>
    /// A window of zero is reported rather than refused here: the refusal belongs on the request
    /// path, where it can see an edit made after the application started.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_UnconfiguredContextWindow_ReturnsItRatherThanThrowing()
    {
        var model = await AddModelAsync(contextWindowSize: 0m);
        var resolver = CreateResolver(model.Id);

        var summarizer = await resolver.ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0m, summarizer.ContextWindowSize);
    }
}
