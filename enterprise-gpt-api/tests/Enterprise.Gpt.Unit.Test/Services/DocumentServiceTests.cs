using Azure.Storage.Sas;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Common.Observability;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.BackgroundJobs;
using Enterprise.Gpt.Service.Chunking;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Extraction;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using System.Text;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Services;

public sealed class DocumentServiceTests : IDisposable
{
    private const string ContainerName = "documents";
    private const string GeneratedContainerName = "generated-documents";

    private readonly SqliteDbContextFixture _fixture = new();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly IBlobStorageService _blobStorageService = Substitute.For<IBlobStorageService>();
    private readonly IDocumentTextExtractorFactory _extractorFactory = Substitute.For<IDocumentTextExtractorFactory>();
    private readonly IDocumentTextExtractor _extractor = Substitute.For<IDocumentTextExtractor>();
    private readonly ITextChunker _textChunker = Substitute.For<ITextChunker>();

    // A real validator rather than a substitute: ValidateAndThrowAsync only throws because AbstractValidator
    // honours the ThrowOnFailures strategy, which a substituted IValidator would silently ignore.
    private readonly InlineValidator<FileDto> _fileValidator = [];
    private readonly IBackgroundJobQueue _backgroundJobQueue = Substitute.For<IBackgroundJobQueue>();
    private readonly IJobStatusStore _jobStatusStore = Substitute.For<IJobStatusStore>();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly DocumentService _service;

    public DocumentServiceTests()
    {
        _tokenService.GetOid().Returns(KnownIds.SeedUserId);
        _extractorFactory.Resolve(Arg.Any<FileExtensions>()).Returns(_extractor);

        _service = CreateService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureStorage:DocumentsContainer"] = ContainerName,
                ["AzureStorage:GeneratedContainer"] = GeneratedContainerName
            })
            .Build());
    }

    private DocumentService CreateService(IConfiguration configuration)
    {
        return new DocumentService(
            NullLogger<DocumentService>.Instance,
            _embeddingGenerator,
            _blobStorageService,
            configuration,
            Options.Create(new DocumentOptions { EmbeddingBatchSize = 2 }),
            _extractorFactory,
            _textChunker,
            _fileValidator,
            _backgroundJobQueue,
            _jobStatusStore,
            _tokenService,
            _fixture.Context);
    }

    public void Dispose() => _fixture.Dispose();

    #region Helpers
    private static FileDto CreateFile(string fileName = "report.pdf")
    {
        var content = Encoding.UTF8.GetBytes("%PDF-1.7 pretend document");
        return new FileDto { FileName = fileName, ContentType = "application/pdf", Length = content.Length, Content = content };
    }

    private async Task<Conversation> AddConversationAsync(
        Guid? userId = null, DateTimeOffset? deactivated = null, Guid? projectId = null, string? name = null)
    {
        var date = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? KnownIds.SeedUserId,
            ProjectId = projectId,
            Name = name ?? "Test Conversation",
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated
        };

        using var ctx = _fixture.CreateContext();
        ctx.Conversations.Add(conversation);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return conversation;
    }

    private async Task<Project> AddProjectAsync(Guid? userId = null, DateTimeOffset? deactivated = null)
    {
        var date = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? KnownIds.SeedUserId,
            Name = $"Project {Guid.NewGuid():N}",
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated
        };

        using var ctx = _fixture.CreateContext();
        ctx.Projects.Add(project);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return project;
    }

    private async Task<User> AddUserAsync()
    {
        var date = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            FirstName = "Other",
            LastName = "User",
            Email = "other.user@example.com",
            DateCreated = date,
            DateModified = date
        };

        using var ctx = _fixture.CreateContext();
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return user;
    }

    private void SetUpPipeline(int chunkCount, int? segmentCount = null)
    {
        var segments = Enumerable.Range(1, segmentCount ?? Math.Max(1, chunkCount))
            .Select(i => new DocumentSegmentDto { SourceNumber = i, Text = $"segment {i}" })
            .ToList();

        _extractor.ExtractAsync(Arg.Any<FileDto>(), Arg.Any<IProgress<DocumentExtractionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(segments);

        var chunks = Enumerable.Range(0, chunkCount)
            .Select(i => new TextChunkDto { Index = i, SourceNumber = i + 1, TokenCount = 10 + i, Text = $"chunk {i}" })
            .ToList();

        _textChunker.Chunk(Arg.Any<IReadOnlyList<DocumentSegmentDto>>(), Arg.Any<CancellationToken>()).Returns(chunks);

        _embeddingGenerator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var inputs = (call.Arg<IEnumerable<string>>() ?? []).ToList();
                return new GeneratedEmbeddings<Embedding<float>>(
                    inputs.Select(_ => new Embedding<float>(new float[] { 0.1f, 0.2f })));
            });
    }
    #endregion

    #region QueueConversationDocumentAsync
    [Fact]
    public async Task QueueConversationDocumentAsync_OwnedConversation_RegistersAndEnqueuesTheJob()
    {
        var conversation = await AddConversationAsync();

        var job = await _service.QueueConversationDocumentAsync(conversation.Id, CreateFile(), TestContext.Current.CancellationToken);

        Assert.True(Guid.TryParse(job.Id, out _));
        // Registered against the caller so the status endpoint can scope reads to them.
        _jobStatusStore.Received(1).Register(job.Id, KnownIds.SeedUserId);
        await _backgroundJobQueue.Received(1).EnqueueAsync(
            Arg.Is<JobWorkItem>(item => item != null && item.JobId == job.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueConversationDocumentAsync_ConversationOwnedByAnotherUser_ThrowsNotFound()
    {
        // Indistinguishable from "does not exist" so the endpoint cannot be used to probe for other
        // users' conversation ids.
        var otherUser = await AddUserAsync();
        var conversation = await AddConversationAsync(userId: otherUser.Id);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.QueueConversationDocumentAsync(conversation.Id, CreateFile(), TestContext.Current.CancellationToken));

        await _backgroundJobQueue.DidNotReceive().EnqueueAsync(Arg.Any<JobWorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueConversationDocumentAsync_DeactivatedConversation_ThrowsNotFound()
    {
        var conversation = await AddConversationAsync(deactivated: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.QueueConversationDocumentAsync(conversation.Id, CreateFile(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueueConversationDocumentAsync_UnknownConversation_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.QueueConversationDocumentAsync(Guid.NewGuid(), CreateFile(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueueConversationDocumentAsync_InvalidFile_ThrowsBeforeTouchingTheQueue()
    {
        // Rejecting synchronously is the point: otherwise the caller gets a 202 and only learns the upload
        // was never viable when the job fails minutes later.
        var conversation = await AddConversationAsync();
        _fileValidator.RuleFor(x => x.FileName)
            .Must(_ => false)
            .WithMessage("Files of type '.xlsx' are not supported.");

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.QueueConversationDocumentAsync(conversation.Id, CreateFile("book.xlsx"), TestContext.Current.CancellationToken));

        _jobStatusStore.DidNotReceive().Register(Arg.Any<string>(), Arg.Any<Guid>());
        await _backgroundJobQueue.DidNotReceive().EnqueueAsync(Arg.Any<JobWorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueConversationDocumentAsync_EnqueueFails_MarksTheRegisteredJobFailed()
    {
        // Register happens before Enqueue, so a failed enqueue would otherwise leave the snapshot in
        // Queued forever — the store only evicts terminal states.
        var conversation = await AddConversationAsync();
        _backgroundJobQueue.EnqueueAsync(Arg.Any<JobWorkItem>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("queue closed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.QueueConversationDocumentAsync(conversation.Id, CreateFile(), TestContext.Current.CancellationToken));

        _jobStatusStore.Received(1).Fail(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task QueueConversationDocumentAsync_NullFile_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.QueueConversationDocumentAsync(Guid.NewGuid(), null!, TestContext.Current.CancellationToken));
    }
    #endregion

    #region CreateConversationDocumentAsync
    [Fact]
    public async Task CreateConversationDocumentAsync_ValidDocument_PersistsTheDocumentAndItsChunks()
    {
        var conversation = await AddConversationAsync();
        SetUpPipeline(chunkCount: 3);

        var result = await _service.CreateConversationDocumentAsync(
            "job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var document = await ctx.ConversationDocuments
            .AsNoTracking()
            .Include(d => d.Chunks)
            .SingleAsync(d => d.Id == result.Id, TestContext.Current.CancellationToken);

        Assert.Equal(conversation.Id, document.ConversationId);
        Assert.Equal(KnownIds.SeedUserId, document.UserId);
        Assert.Equal("report.pdf", document.Name);
        Assert.Equal(".pdf", document.Extension);
        Assert.Equal(3, document.Chunks.Count);
        Assert.Equal([0, 1, 2], document.Chunks.OrderBy(c => c.Index).Select(c => c.Index));
        Assert.Equal(["chunk 0", "chunk 1", "chunk 2"], document.Chunks.OrderBy(c => c.Index).Select(c => c.Text));
        Assert.Equal([10, 11, 12], document.Chunks.OrderBy(c => c.Index).Select(c => c.TokenCount));
        Assert.Equal([1, 2, 3], document.Chunks.OrderBy(c => c.Index).Select(c => c.SourceNumber));
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_ConversationDeactivatedDuringIngestion_DeactivatesTheDocumentAndThrows()
    {
        // The enqueue-time ownership check lives in QueueConversationDocumentAsync; by the time this
        // method persists, the conversation can be gone. A deactivated seed models that window.
        var conversation = await AddConversationAsync(deactivated: DateTimeOffset.UtcNow);
        SetUpPipeline(chunkCount: 2);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.CreateConversationDocumentAsync("job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken));

        using var ctx = _fixture.CreateContext();
        var document = await ctx.ConversationDocuments
            .AsNoTracking()
            .Include(d => d.Chunks)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(document.DateDeactivated);
        Assert.All(document.Chunks, chunk => Assert.NotNull(chunk.DateDeactivated));
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_ValidDocument_StoresTheBlobUnderTheDocumentIdNotTheFileName()
    {
        // Uploading the same file name into the same conversation twice is legitimate; a name-keyed path
        // made the second upload collide with the first and fail the job.
        var conversation = await AddConversationAsync();
        SetUpPipeline(chunkCount: 1);

        var result = await _service.CreateConversationDocumentAsync(
            "job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken);

        var expectedPath = $"{KnownIds.SeedUserId}/{conversation.Id}/{result.Id}.pdf";

        await _blobStorageService.Received(1).UploadAsync(
            ContainerName, expectedPath, Arg.Any<byte[]>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());

        using var ctx = _fixture.CreateContext();
        var document = await ctx.ConversationDocuments.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expectedPath, document.Path);
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_ValidDocument_ReportsEveryStageThenCompletes()
    {
        var conversation = await AddConversationAsync();
        SetUpPipeline(chunkCount: 3);

        var result = await _service.CreateConversationDocumentAsync(
            "job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken);

        _jobStatusStore.Received().Report("job-1", JobStatus.Uploading, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>());
        _jobStatusStore.Received().Report("job-1", JobStatus.Extracting, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>());
        _jobStatusStore.Received().Report("job-1", JobStatus.Chunking, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>());
        _jobStatusStore.Received().Report("job-1", JobStatus.Embedding, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>());
        _jobStatusStore.Received().Report("job-1", JobStatus.Persisting, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>());
        _jobStatusStore.Received(1).Complete("job-1", result.Id, Arg.Any<string>());
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_ManyChunks_ReportsEmbeddingProgressPerBatch()
    {
        var conversation = await AddConversationAsync();
        SetUpPipeline(chunkCount: 5);

        await _service.CreateConversationDocumentAsync(
            "job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken);

        // Batch size 2 over 5 chunks: 1-2, 3-4, 5.
        _jobStatusStore.Received(1).Report("job-1", JobStatus.Embedding, Arg.Any<int>(), Arg.Any<string>(), 2, 5);
        _jobStatusStore.Received(1).Report("job-1", JobStatus.Embedding, Arg.Any<int>(), Arg.Any<string>(), 4, 5);
        _jobStatusStore.Received(1).Report("job-1", JobStatus.Embedding, Arg.Any<int>(), Arg.Any<string>(), 5, 5);
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_ManyChunks_SendsThemToTheGeneratorInBatches()
    {
        var conversation = await AddConversationAsync();
        SetUpPipeline(chunkCount: 5);

        await _service.CreateConversationDocumentAsync(
            "job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken);

        // Three batched calls, not five single-input ones.
        await _embeddingGenerator.Received(3).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_ExtractorFindsNoText_ThrowsValidationException()
    {
        var conversation = await AddConversationAsync();
        SetUpPipeline(chunkCount: 0, segmentCount: 0);

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateConversationDocumentAsync("job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken));

        Assert.Contains("No readable text", Assert.Single(exception.Errors).ErrorMessage);
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_ChunkerProducesNothing_ThrowsValidationException()
    {
        var conversation = await AddConversationAsync();
        SetUpPipeline(chunkCount: 0, segmentCount: 2);

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateConversationDocumentAsync("job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_GeneratorReturnsTooFewVectors_ThrowsRatherThanMisalignEmbeddings()
    {
        // Vectors are matched to chunks by position, so a short response would silently attach the wrong
        // embedding to every chunk after the gap.
        var conversation = await AddConversationAsync();
        SetUpPipeline(chunkCount: 2);
        _embeddingGenerator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateConversationDocumentAsync("job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_ExtractionFails_DoesNotPersistAnything()
    {
        var conversation = await AddConversationAsync();
        SetUpPipeline(chunkCount: 1);
        _extractor.ExtractAsync(Arg.Any<FileDto>(), Arg.Any<IProgress<DocumentExtractionProgress>?>(), Arg.Any<CancellationToken>())
            .Throws(new ValidationException("unreadable"));

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateConversationDocumentAsync("job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken));

        using var ctx = _fixture.CreateContext();
        Assert.False(await ctx.ConversationDocuments.AnyAsync(TestContext.Current.CancellationToken));
        Assert.False(await ctx.ConversationDocumentChunks.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_IngestionFails_RemovesTheBlobItAlreadyStored()
    {
        // The blob is written before extraction runs, so a later failure would otherwise leave user content
        // in the container that no database row references — unreachable and billed for indefinitely.
        var conversation = await AddConversationAsync();
        SetUpPipeline(chunkCount: 1);
        _extractor.ExtractAsync(Arg.Any<FileDto>(), Arg.Any<IProgress<DocumentExtractionProgress>?>(), Arg.Any<CancellationToken>())
            .Throws(new ValidationException("unreadable"));

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateConversationDocumentAsync("job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken));

        await _blobStorageService.Received(1).DeleteAsync(ContainerName, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_EmbeddingFails_RemovesTheBlobItAlreadyStored()
    {
        var conversation = await AddConversationAsync();
        SetUpPipeline(chunkCount: 2);
        _embeddingGenerator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("rate limited"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateConversationDocumentAsync("job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken));

        await _blobStorageService.Received(1).DeleteAsync(ContainerName, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_CleanupAlsoFails_StillReportsTheOriginalFailure()
    {
        // Losing the cleanup must not replace the real error with a storage one.
        var conversation = await AddConversationAsync();
        SetUpPipeline(chunkCount: 1);
        _extractor.ExtractAsync(Arg.Any<FileDto>(), Arg.Any<IProgress<DocumentExtractionProgress>?>(), Arg.Any<CancellationToken>())
            .Throws(new ValidationException("unreadable"));
        _blobStorageService.DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("storage unreachable"));

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateConversationDocumentAsync("job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_Succeeds_DoesNotDeleteTheBlob()
    {
        var conversation = await AddConversationAsync();
        SetUpPipeline(chunkCount: 2);

        await _service.CreateConversationDocumentAsync(
            "job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken);

        await _blobStorageService.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateConversationDocumentAsync_BlankJobId_Throws(string jobId)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateConversationDocumentAsync(jobId, CreateFile(), KnownIds.SeedUserId, Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_NullFile_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.CreateConversationDocumentAsync("job-1", null!, KnownIds.SeedUserId, Guid.NewGuid(), TestContext.Current.CancellationToken));
    }
    #endregion

    #region QueueProjectDocumentAsync
    [Fact]
    public async Task QueueProjectDocumentAsync_OwnedProject_RegistersAndEnqueuesTheJob()
    {
        var project = await AddProjectAsync();

        var job = await _service.QueueProjectDocumentAsync(project.Id, CreateFile(), TestContext.Current.CancellationToken);

        Assert.True(Guid.TryParse(job.Id, out _));
        _jobStatusStore.Received(1).Register(job.Id, KnownIds.SeedUserId);
        await _backgroundJobQueue.Received(1).EnqueueAsync(
            Arg.Is<JobWorkItem>(item => item != null && item.JobId == job.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueProjectDocumentAsync_ProjectOwnedByAnotherUser_ThrowsNotFound()
    {
        // Indistinguishable from "does not exist" so the endpoint cannot be used to probe for other
        // users' project ids.
        var otherUser = await AddUserAsync();
        var project = await AddProjectAsync(userId: otherUser.Id);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.QueueProjectDocumentAsync(project.Id, CreateFile(), TestContext.Current.CancellationToken));

        await _backgroundJobQueue.DidNotReceive().EnqueueAsync(Arg.Any<JobWorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueProjectDocumentAsync_DeactivatedProject_ThrowsNotFound()
    {
        var project = await AddProjectAsync(deactivated: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.QueueProjectDocumentAsync(project.Id, CreateFile(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueueProjectDocumentAsync_UnknownProject_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.QueueProjectDocumentAsync(Guid.NewGuid(), CreateFile(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueueProjectDocumentAsync_InvalidFile_ThrowsBeforeTouchingTheQueue()
    {
        var project = await AddProjectAsync();
        _fileValidator.RuleFor(x => x.FileName)
            .Must(_ => false)
            .WithMessage("Files of type '.xlsx' are not supported.");

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.QueueProjectDocumentAsync(project.Id, CreateFile("book.xlsx"), TestContext.Current.CancellationToken));

        _jobStatusStore.DidNotReceive().Register(Arg.Any<string>(), Arg.Any<Guid>());
        await _backgroundJobQueue.DidNotReceive().EnqueueAsync(Arg.Any<JobWorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueProjectDocumentAsync_EnqueueFails_MarksTheRegisteredJobFailed()
    {
        var project = await AddProjectAsync();
        _backgroundJobQueue.EnqueueAsync(Arg.Any<JobWorkItem>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("queue closed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.QueueProjectDocumentAsync(project.Id, CreateFile(), TestContext.Current.CancellationToken));

        _jobStatusStore.Received(1).Fail(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task QueueProjectDocumentAsync_NullFile_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.QueueProjectDocumentAsync(Guid.NewGuid(), null!, TestContext.Current.CancellationToken));
    }
    #endregion

    #region CreateProjectDocumentAsync
    [Fact]
    public async Task CreateProjectDocumentAsync_ValidDocument_PersistsTheDocumentAndItsChunks()
    {
        var project = await AddProjectAsync();
        SetUpPipeline(chunkCount: 3);

        var result = await _service.CreateProjectDocumentAsync(
            "job-1", CreateFile(), KnownIds.SeedUserId, project.Id, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var document = await ctx.ProjectDocuments
            .AsNoTracking()
            .Include(d => d.Chunks)
            .SingleAsync(d => d.Id == result.Id, TestContext.Current.CancellationToken);

        Assert.Equal(project.Id, document.ProjectId);
        Assert.Equal(KnownIds.SeedUserId, document.UserId);
        Assert.Equal("report.pdf", document.Name);
        Assert.Equal(".pdf", document.Extension);
        Assert.Equal(3, document.Chunks.Count);
        Assert.Equal([0, 1, 2], document.Chunks.OrderBy(c => c.Index).Select(c => c.Index));
        Assert.Equal(["chunk 0", "chunk 1", "chunk 2"], document.Chunks.OrderBy(c => c.Index).Select(c => c.Text));
        Assert.Equal([10, 11, 12], document.Chunks.OrderBy(c => c.Index).Select(c => c.TokenCount));
        Assert.Equal([1, 2, 3], document.Chunks.OrderBy(c => c.Index).Select(c => c.SourceNumber));
    }

    [Fact]
    public async Task CreateProjectDocumentAsync_ProjectDeactivatedDuringIngestion_DeactivatesTheDocumentAndThrows()
    {
        var project = await AddProjectAsync(deactivated: DateTimeOffset.UtcNow);
        SetUpPipeline(chunkCount: 2);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.CreateProjectDocumentAsync("job-1", CreateFile(), KnownIds.SeedUserId, project.Id, TestContext.Current.CancellationToken));

        using var ctx = _fixture.CreateContext();
        var document = await ctx.ProjectDocuments
            .AsNoTracking()
            .Include(d => d.Chunks)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(document.DateDeactivated);
        Assert.All(document.Chunks, chunk => Assert.NotNull(chunk.DateDeactivated));
    }

    [Fact]
    public async Task CreateProjectDocumentAsync_ValidDocument_StoresTheBlobUnderAProjectScopedKey()
    {
        // The literal "projects" segment is what keeps a project key from ever colliding with a
        // conversation one; both are "{userId}/{ownerId}/{documentId}{extension}" otherwise.
        var project = await AddProjectAsync();
        SetUpPipeline(chunkCount: 1);

        var result = await _service.CreateProjectDocumentAsync(
            "job-1", CreateFile(), KnownIds.SeedUserId, project.Id, TestContext.Current.CancellationToken);

        var expectedPath = $"{KnownIds.SeedUserId}/projects/{project.Id}/{result.Id}.pdf";

        await _blobStorageService.Received(1).UploadAsync(
            ContainerName, expectedPath, Arg.Any<byte[]>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());

        using var ctx = _fixture.CreateContext();
        var document = await ctx.ProjectDocuments.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expectedPath, document.Path);
    }

    [Fact]
    public async Task CreateProjectDocumentAsync_ValidDocument_TagsTheBlobWithTheProjectId()
    {
        var project = await AddProjectAsync();
        SetUpPipeline(chunkCount: 1);

        await _service.CreateProjectDocumentAsync(
            "job-1", CreateFile(), KnownIds.SeedUserId, project.Id, TestContext.Current.CancellationToken);

        await _blobStorageService.Received(1).UploadAsync(
            ContainerName,
            Arg.Any<string>(),
            Arg.Any<byte[]>(),
            Arg.Is<Dictionary<string, string>>(m => m != null
                && m["projectId"] == project.Id.ToString()
                && !m.ContainsKey("conversationId")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateProjectDocumentAsync_ValidDocument_ReportsEveryStageThenCompletes()
    {
        var project = await AddProjectAsync();
        SetUpPipeline(chunkCount: 3);

        var result = await _service.CreateProjectDocumentAsync(
            "job-1", CreateFile(), KnownIds.SeedUserId, project.Id, TestContext.Current.CancellationToken);

        _jobStatusStore.Received().Report("job-1", JobStatus.Uploading, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>());
        _jobStatusStore.Received().Report("job-1", JobStatus.Extracting, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>());
        _jobStatusStore.Received().Report("job-1", JobStatus.Chunking, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>());
        _jobStatusStore.Received().Report("job-1", JobStatus.Embedding, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>());
        _jobStatusStore.Received().Report("job-1", JobStatus.Persisting, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>());
        _jobStatusStore.Received(1).Complete("job-1", result.Id, Arg.Any<string>());
    }

    [Fact]
    public async Task CreateProjectDocumentAsync_ExtractorFindsNoText_ThrowsValidationException()
    {
        var project = await AddProjectAsync();
        SetUpPipeline(chunkCount: 0, segmentCount: 0);

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateProjectDocumentAsync("job-1", CreateFile(), KnownIds.SeedUserId, project.Id, TestContext.Current.CancellationToken));

        Assert.Contains("No readable text", Assert.Single(exception.Errors).ErrorMessage);
    }

    [Fact]
    public async Task CreateProjectDocumentAsync_IngestionFails_RemovesTheBlobAndPersistsNothing()
    {
        var project = await AddProjectAsync();
        SetUpPipeline(chunkCount: 1);
        _extractor.ExtractAsync(Arg.Any<FileDto>(), Arg.Any<IProgress<DocumentExtractionProgress>?>(), Arg.Any<CancellationToken>())
            .Throws(new ValidationException("unreadable"));

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateProjectDocumentAsync("job-1", CreateFile(), KnownIds.SeedUserId, project.Id, TestContext.Current.CancellationToken));

        await _blobStorageService.Received(1).DeleteAsync(ContainerName, Arg.Any<string>(), Arg.Any<CancellationToken>());

        using var ctx = _fixture.CreateContext();
        Assert.False(await ctx.ProjectDocuments.AnyAsync(TestContext.Current.CancellationToken));
        Assert.False(await ctx.ProjectDocumentChunks.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateProjectDocumentAsync_ValidDocument_DoesNotTouchConversationDocuments()
    {
        // A project upload is shared by the project's conversations; it must never appear as one
        // conversation's own document.
        var project = await AddProjectAsync();
        SetUpPipeline(chunkCount: 2);

        await _service.CreateProjectDocumentAsync(
            "job-1", CreateFile(), KnownIds.SeedUserId, project.Id, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        Assert.False(await ctx.ConversationDocuments.AnyAsync(TestContext.Current.CancellationToken));
        Assert.False(await ctx.ConversationDocumentChunks.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_ValidDocument_DoesNotTouchProjectDocuments()
    {
        // The other direction of the same rule: uploading into a conversation that belongs to a
        // project must not add the file to the project.
        var project = await AddProjectAsync();
        var conversation = await AddConversationAsync(projectId: project.Id);
        SetUpPipeline(chunkCount: 2);

        await _service.CreateConversationDocumentAsync(
            "job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        Assert.False(await ctx.ProjectDocuments.AnyAsync(TestContext.Current.CancellationToken));
        Assert.False(await ctx.ProjectDocumentChunks.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateProjectDocumentAsync_BlankJobId_Throws(string jobId)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateProjectDocumentAsync(jobId, CreateFile(), KnownIds.SeedUserId, Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateProjectDocumentAsync_NullFile_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.CreateProjectDocumentAsync("job-1", null!, KnownIds.SeedUserId, Guid.NewGuid(), TestContext.Current.CancellationToken));
    }
    #endregion

    #region Downloads
    private const string SignedUri = "https://storage.example/documents/blob?sig=signature";

    private void SetUpSigning(string uri = SignedUri)
    {
        _blobStorageService.GenerateSasUri(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<BlobSasPermissions>(),
                Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new Uri(uri));
    }

    private async Task<ConversationDocument> AddConversationDocumentAsync(
        Conversation conversation, string name = "quarterly report.pdf",
        string mimeType = "application/pdf", DateTimeOffset? deactivated = null, Guid? uploadedBy = null,
        DateTimeOffset? dateCreated = null, ConversationDocumentTypes type = ConversationDocumentTypes.Uploaded)
    {
        var date = dateCreated ?? DateTimeOffset.UtcNow;
        var document = new ConversationDocument
        {
            Id = Guid.NewGuid(),
            UserId = uploadedBy ?? conversation.UserId,
            ConversationId = conversation.Id,
            Type = type,
            Name = name,
            Extension = System.IO.Path.GetExtension(name).ToLowerInvariant(),
            MimeType = mimeType,
            Size = 2048,
            Path = $"{conversation.UserId}/{conversation.Id}/{Guid.NewGuid()}.pdf",
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated
        };

        using var ctx = _fixture.CreateContext();
        ctx.ConversationDocuments.Add(document);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return document;
    }

    private async Task<ProjectDocument> AddProjectDocumentAsync(
        Project project, string name = "handbook.pdf",
        string mimeType = "application/pdf", DateTimeOffset? deactivated = null, Guid? uploadedBy = null)
    {
        var date = DateTimeOffset.UtcNow;
        var document = new ProjectDocument
        {
            Id = Guid.NewGuid(),
            UserId = uploadedBy ?? project.UserId,
            ProjectId = project.Id,
            Name = name,
            Extension = System.IO.Path.GetExtension(name).ToLowerInvariant(),
            MimeType = mimeType,
            Size = 4096,
            Path = $"{project.UserId}/projects/{project.Id}/{Guid.NewGuid()}.pdf",
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated
        };

        using var ctx = _fixture.CreateContext();
        ctx.ProjectDocuments.Add(document);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return document;
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_OwnedDocument_ReturnsTheSignedLinkAndFileName()
    {
        SetUpSigning();
        var conversation = await AddConversationAsync();
        var document = await AddConversationDocumentAsync(conversation);

        var result = await _service.GetConversationDocumentDownloadAsync(
            conversation.Id, document.Id, TestContext.Current.CancellationToken);

        Assert.Equal(SignedUri, result.DownloadUrl);
        Assert.Equal("quarterly report.pdf", result.FileName);
        // The default lifetime, so the client knows when to ask for a new link.
        Assert.InRange(result.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(4), DateTimeOffset.UtcNow.AddMinutes(6));
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_OwnedDocument_SignsTheStoredBlobForReadingOnly()
    {
        SetUpSigning();
        var conversation = await AddConversationAsync();
        var document = await AddConversationDocumentAsync(conversation);

        await _service.GetConversationDocumentDownloadAsync(conversation.Id, document.Id, TestContext.Current.CancellationToken);

        _blobStorageService.Received(1).GenerateSasUri(
            ContainerName,
            document.Path,
            TimeSpan.FromMinutes(5),
            BlobSasPermissions.Read,
            Arg.Any<string?>(),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_GeneratedDocument_SignsAgainstTheGeneratedContainer()
    {
        // Both containers hold the same seven formats, so the row's own type is the only thing that
        // says which one holds this blob. Signing the wrong one produces a link that 404s.
        SetUpSigning();
        var conversation = await AddConversationAsync();
        var document = await AddConversationDocumentAsync(
            conversation, name: "summary.xlsx", type: ConversationDocumentTypes.Generated);

        await _service.GetConversationDocumentDownloadAsync(conversation.Id, document.Id, TestContext.Current.CancellationToken);

        _blobStorageService.Received(1).GenerateSasUri(
            GeneratedContainerName,
            document.Path,
            Arg.Any<TimeSpan>(),
            BlobSasPermissions.Read,
            Arg.Any<string?>(),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_OwnedDocument_OverridesTheStoredBlobsHeaders()
    {
        // The blob was uploaded without content headers of its own, so without these overrides the
        // browser saves the file under its guid storage key as application/octet-stream.
        SetUpSigning();
        var conversation = await AddConversationAsync();
        var document = await AddConversationDocumentAsync(conversation);

        await _service.GetConversationDocumentDownloadAsync(conversation.Id, document.Id, TestContext.Current.CancellationToken);

        _blobStorageService.Received(1).GenerateSasUri(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<BlobSasPermissions>(),
            "attachment; filename=\"quarterly report.pdf\"; filename*=UTF-8''quarterly%20report.pdf",
            "application/pdf");
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_NonAsciiFileName_KeepsAnAsciiFallbackBesideTheEncodedName()
    {
        // RFC 6266: filename* carries the real name and the quoted filename is the fallback, which is the
        // only part not percent-encoded and so has its quotes and non-ASCII characters stripped.
        SetUpSigning();
        var conversation = await AddConversationAsync();
        var document = await AddConversationDocumentAsync(conversation, name: "informe \"año\".pdf");

        await _service.GetConversationDocumentDownloadAsync(conversation.Id, document.Id, TestContext.Current.CancellationToken);

        _blobStorageService.Received(1).GenerateSasUri(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<BlobSasPermissions>(),
            "attachment; filename=\"informe ao.pdf\"; filename*=UTF-8''informe%20%22a%C3%B1o%22.pdf",
            Arg.Any<string?>());
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_ContentType_ComesFromTheExtensionNotTheDeclaredMimeType()
    {
        // Nothing validates the MimeType column — it is whatever the client sent at upload time — and the
        // value is signed into the link as the header storage will actually return. Serving an uploader's
        // chosen media type back would leave the attachment disposition as the only thing preventing
        // stored XSS. The extension is derived server-side and validated, so it is the trustworthy half.
        SetUpSigning();
        var conversation = await AddConversationAsync();
        var document = await AddConversationDocumentAsync(conversation, name: "report.pdf", mimeType: "text/html");

        await _service.GetConversationDocumentDownloadAsync(conversation.Id, document.Id, TestContext.Current.CancellationToken);

        _blobStorageService.Received(1).GenerateSasUri(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<BlobSasPermissions>(),
            Arg.Any<string?>(), "application/pdf");
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_UnrecognisedExtension_FallsBackToOctetStream()
    {
        // Safe by construction: every link is an attachment, so the type only decides which application
        // opens the saved file.
        SetUpSigning();
        var conversation = await AddConversationAsync();
        var document = await AddConversationDocumentAsync(conversation, name: "archive.zip", mimeType: "application/zip");

        await _service.GetConversationDocumentDownloadAsync(conversation.Id, document.Id, TestContext.Current.CancellationToken);

        _blobStorageService.Received(1).GenerateSasUri(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<BlobSasPermissions>(),
            Arg.Any<string?>(), "application/octet-stream");
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_FileNameWithPathSeparators_StripsThemFromTheAsciiFallback()
    {
        // The fallback is signed into the token and cannot be corrected afterwards, so a name that would
        // read as a path never reaches it.
        SetUpSigning();
        var conversation = await AddConversationAsync();
        var document = await AddConversationDocumentAsync(conversation, name: "c:/reports/q3.pdf");

        await _service.GetConversationDocumentDownloadAsync(conversation.Id, document.Id, TestContext.Current.CancellationToken);

        _blobStorageService.Received(1).GenerateSasUri(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<BlobSasPermissions>(),
            "attachment; filename=\"creportsq3.pdf\"; filename*=UTF-8''c%3A%2Freports%2Fq3.pdf",
            Arg.Any<string?>());
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_DocumentUploadedByAnotherUser_StillIssuesALinkToTheConversationOwner()
    {
        // Authorization deliberately follows the conversation, not ConversationDocument.UserId, so the
        // rule that makes a document downloadable is the one that made it visible.
        SetUpSigning();
        var uploader = await AddUserAsync();
        var conversation = await AddConversationAsync();
        var document = await AddConversationDocumentAsync(conversation, uploadedBy: uploader.Id);

        var result = await _service.GetConversationDocumentDownloadAsync(
            conversation.Id, document.Id, TestContext.Current.CancellationToken);

        Assert.Equal(SignedUri, result.DownloadUrl);
    }

    [Fact]
    public async Task GetProjectDocumentDownloadAsync_DocumentUploadedByAnotherUser_StillIssuesALinkToTheProjectOwner()
    {
        SetUpSigning();
        var uploader = await AddUserAsync();
        var project = await AddProjectAsync();
        var document = await AddProjectDocumentAsync(project, uploadedBy: uploader.Id);

        var result = await _service.GetProjectDocumentDownloadAsync(
            project.Id, document.Id, TestContext.Current.CancellationToken);

        Assert.Equal(SignedUri, result.DownloadUrl);
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_ContainerNotConfigured_ThrowsStorageNotConfigured()
    {
        // Otherwise a missing container name lands in the unmapped 500 arm with its message suppressed,
        // which is the same operator condition this exception exists to make diagnosable.
        SetUpSigning();
        var conversation = await AddConversationAsync();
        var document = await AddConversationDocumentAsync(conversation);
        var service = CreateService(new ConfigurationBuilder().Build());

        await Assert.ThrowsAsync<StorageNotConfiguredException>(
            () => service.GetConversationDocumentDownloadAsync(conversation.Id, document.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_ConversationOwnedByAnotherUser_ThrowsNotFoundAndSignsNothing()
    {
        // 404 rather than 403, and nothing is signed, so the route cannot be used to probe for document
        // ids or to leak a link to a file the caller cannot see.
        SetUpSigning();
        var otherUser = await AddUserAsync();
        var conversation = await AddConversationAsync(userId: otherUser.Id);
        var document = await AddConversationDocumentAsync(conversation);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetConversationDocumentDownloadAsync(conversation.Id, document.Id, TestContext.Current.CancellationToken));

        _blobStorageService.DidNotReceive().GenerateSasUri(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<BlobSasPermissions>(),
            Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_DeactivatedDocument_ThrowsNotFound()
    {
        SetUpSigning();
        var conversation = await AddConversationAsync();
        var document = await AddConversationDocumentAsync(conversation, deactivated: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetConversationDocumentDownloadAsync(conversation.Id, document.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_DeactivatedConversation_ThrowsNotFound()
    {
        // Deleting a conversation soft-deletes its documents, but the parent is checked too so a row
        // missed by that cascade still cannot be downloaded.
        SetUpSigning();
        var conversation = await AddConversationAsync(deactivated: DateTimeOffset.UtcNow);
        var document = await AddConversationDocumentAsync(conversation);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetConversationDocumentDownloadAsync(conversation.Id, document.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_DocumentFromAnotherConversation_ThrowsNotFound()
    {
        SetUpSigning();
        var conversation = await AddConversationAsync();
        var otherConversation = await AddConversationAsync();
        var document = await AddConversationDocumentAsync(otherConversation);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetConversationDocumentDownloadAsync(conversation.Id, document.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_UnknownDocument_ThrowsNotFound()
    {
        SetUpSigning();
        var conversation = await AddConversationAsync();

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetConversationDocumentDownloadAsync(conversation.Id, Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_StorageCannotSign_PropagatesTheConfigurationFailure()
    {
        // Surfaces as 503 rather than the 400 an InvalidOperationException would have produced: the
        // request is fine and the document exists, the deployment just cannot sign links.
        var conversation = await AddConversationAsync();
        var document = await AddConversationDocumentAsync(conversation);
        _blobStorageService.GenerateSasUri(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<BlobSasPermissions>(),
                Arg.Any<string?>(), Arg.Any<string?>())
            .Throws(new StorageNotConfiguredException());

        await Assert.ThrowsAsync<StorageNotConfiguredException>(
            () => _service.GetConversationDocumentDownloadAsync(conversation.Id, document.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetProjectDocumentDownloadAsync_OwnedDocument_ReturnsTheSignedLinkAndFileName()
    {
        SetUpSigning();
        var project = await AddProjectAsync();
        var document = await AddProjectDocumentAsync(project);

        var result = await _service.GetProjectDocumentDownloadAsync(
            project.Id, document.Id, TestContext.Current.CancellationToken);

        Assert.Equal(SignedUri, result.DownloadUrl);
        Assert.Equal("handbook.pdf", result.FileName);
        Assert.InRange(result.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(4), DateTimeOffset.UtcNow.AddMinutes(6));
    }

    [Fact]
    public async Task GetProjectDocumentDownloadAsync_OwnedDocument_SignsTheStoredBlobForReadingOnly()
    {
        SetUpSigning();
        var project = await AddProjectAsync();
        var document = await AddProjectDocumentAsync(project);

        await _service.GetProjectDocumentDownloadAsync(project.Id, document.Id, TestContext.Current.CancellationToken);

        _blobStorageService.Received(1).GenerateSasUri(
            ContainerName,
            document.Path,
            TimeSpan.FromMinutes(5),
            BlobSasPermissions.Read,
            "attachment; filename=\"handbook.pdf\"; filename*=UTF-8''handbook.pdf",
            "application/pdf");
    }

    [Fact]
    public async Task GetProjectDocumentDownloadAsync_ProjectOwnedByAnotherUser_ThrowsNotFoundAndSignsNothing()
    {
        // Ownership is read off the project, matching the rule that decided the document was visible in
        // the project's document listing to begin with.
        SetUpSigning();
        var otherUser = await AddUserAsync();
        var project = await AddProjectAsync(userId: otherUser.Id);
        var document = await AddProjectDocumentAsync(project);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetProjectDocumentDownloadAsync(project.Id, document.Id, TestContext.Current.CancellationToken));

        _blobStorageService.DidNotReceive().GenerateSasUri(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<BlobSasPermissions>(),
            Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GetProjectDocumentDownloadAsync_DeactivatedDocument_ThrowsNotFound()
    {
        SetUpSigning();
        var project = await AddProjectAsync();
        var document = await AddProjectDocumentAsync(project, deactivated: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetProjectDocumentDownloadAsync(project.Id, document.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetProjectDocumentDownloadAsync_DeactivatedProject_ThrowsNotFound()
    {
        SetUpSigning();
        var project = await AddProjectAsync(deactivated: DateTimeOffset.UtcNow);
        var document = await AddProjectDocumentAsync(project);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetProjectDocumentDownloadAsync(project.Id, document.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetProjectDocumentDownloadAsync_DocumentFromAnotherProject_ThrowsNotFound()
    {
        SetUpSigning();
        var project = await AddProjectAsync();
        var otherProject = await AddProjectAsync();
        var document = await AddProjectDocumentAsync(otherProject);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetProjectDocumentDownloadAsync(project.Id, document.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetProjectDocumentDownloadAsync_UnknownDocument_ThrowsNotFound()
    {
        SetUpSigning();
        var project = await AddProjectAsync();

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetProjectDocumentDownloadAsync(project.Id, Guid.NewGuid(), TestContext.Current.CancellationToken));
    }
    #endregion

    #region GetUserDocumentsAsync
    [Fact]
    public async Task GetUserDocumentsAsync_DocumentsAcrossConversations_ReturnsThemNewestFirstWithConversationNames()
    {
        var date = DateTimeOffset.UtcNow;
        var planning = await AddConversationAsync(name: "Planning");
        var research = await AddConversationAsync(name: "Research");
        var older = await AddConversationDocumentAsync(planning, "budget.pdf", dateCreated: date.AddMinutes(-5));
        var newer = await AddConversationDocumentAsync(research, "findings.pdf", dateCreated: date);

        var result = await _service.GetUserDocumentsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal([newer.Id, older.Id], result.Items.Select(x => x.Id));
        Assert.Equal([research.Id, planning.Id], result.Items.Select(x => x.ConversationId));
        Assert.Equal(["Research", "Planning"], result.Items.Select(x => x.ConversationName));
    }

    [Fact]
    public async Task GetUserDocumentsAsync_AnotherUsersDocuments_AreExcluded()
    {
        var otherUser = await AddUserAsync();
        await AddConversationDocumentAsync(await AddConversationAsync(userId: otherUser.Id));
        var document = await AddConversationDocumentAsync(await AddConversationAsync());

        var result = await _service.GetUserDocumentsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(document.Id, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task GetUserDocumentsAsync_DeactivatedDocument_IsExcluded()
    {
        var conversation = await AddConversationAsync();
        await AddConversationDocumentAsync(conversation, deactivated: DateTimeOffset.UtcNow);
        var live = await AddConversationDocumentAsync(conversation);

        var result = await _service.GetUserDocumentsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(live.Id, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task GetUserDocumentsAsync_ProjectDocuments_AreNotIncluded()
    {
        await AddProjectDocumentAsync(await AddProjectAsync());
        var document = await AddConversationDocumentAsync(await AddConversationAsync());

        var result = await _service.GetUserDocumentsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(document.Id, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task GetUserDocumentsAsync_SecondPage_ReturnsTheRemainderWithTheFullCount()
    {
        var conversation = await AddConversationAsync();
        var date = DateTimeOffset.UtcNow;
        var oldest = await AddConversationDocumentAsync(conversation, dateCreated: date.AddMinutes(-2));
        await AddConversationDocumentAsync(conversation, dateCreated: date.AddMinutes(-1));
        await AddConversationDocumentAsync(conversation, dateCreated: date);

        var result = await _service.GetUserDocumentsAsync(skip: 2, take: 2, TestContext.Current.CancellationToken);

        Assert.Equal(oldest.Id, Assert.Single(result.Items).Id);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.PageSize);
        Assert.Equal(2, result.CurrentPage);
    }

    [Fact]
    public async Task GetUserDocumentsAsync_DocumentsCreatedInTheSameInstant_TieBreakOnIdKeepsPagesDisjoint()
    {
        var conversation = await AddConversationAsync();
        var date = DateTimeOffset.UtcNow;
        await AddConversationDocumentAsync(conversation, dateCreated: date);
        await AddConversationDocumentAsync(conversation, dateCreated: date);
        await AddConversationDocumentAsync(conversation, dateCreated: date);

        var firstPage = await _service.GetUserDocumentsAsync(skip: 0, take: 2, TestContext.Current.CancellationToken);
        var secondPage = await _service.GetUserDocumentsAsync(skip: 2, take: 2, TestContext.Current.CancellationToken);

        var seen = firstPage.Items.Concat(secondPage.Items).Select(x => x.Id).ToList();
        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public async Task GetUserDocumentsAsync_TakeOutsideTheAllowedRange_IsClamped(int take)
    {
        await AddConversationDocumentAsync(await AddConversationAsync());

        var result = await _service.GetUserDocumentsAsync(skip: 0, take: take, TestContext.Current.CancellationToken);

        Assert.Single(result.Items);
        Assert.InRange(result.PageSize, 1, 100);
    }
    #endregion

    #region Sheet ingestion
    [Fact]
    public async Task CreateConversationDocumentAsync_Spreadsheet_PersistsItsSheetsColumnsAndRows()
    {
        var conversation = await AddConversationAsync();
        SetUpSheetPipeline();

        var result = await _service.CreateConversationDocumentAsync(
            "job-sheet", CreateFile("budget.xlsx"), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var sheet = await ctx.ConversationDocumentSheets
            .AsNoTracking()
            .Include(s => s.Columns)
            .Include(s => s.Rows)
            .SingleAsync(s => s.ConversationDocumentId == result.Id, TestContext.Current.CancellationToken);

        Assert.Equal(1, sheet.SheetIndex);
        Assert.Equal("Regional Revenue", sheet.SheetName);
        Assert.Equal(2, sheet.RowCount);
        Assert.Equal(2, sheet.ColumnCount);
        Assert.Equal(["SKU", "Revenue"], sheet.Columns.OrderBy(c => c.ColumnIndex).Select(c => c.ColumnName));
        Assert.Equal([SheetColumnType.Text, SheetColumnType.Number], sheet.Columns.OrderBy(c => c.ColumnIndex).Select(c => c.InferredType));
        Assert.Equal(
            ["""{"SKU":"W-1","Revenue":"1200"}""", """{"SKU":"W-2","Revenue":"800"}"""],
            sheet.Rows.OrderBy(r => r.RowIndex).Select(r => r.Cells));
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_Spreadsheet_CommitsTheSheetsInTheSameSaveAsTheChunks()
    {
        var conversation = await AddConversationAsync();
        SetUpSheetPipeline();

        var saves = 0;
        _fixture.Context.SavedChanges += (_, _) => saves++;

        await _service.CreateConversationDocumentAsync(
            "job-sheet", CreateFile("budget.xlsx"), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken);

        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_ConversationDeactivatedDuringIngestion_DeactivatesTheSheetsToo()
    {
        var conversation = await AddConversationAsync(deactivated: DateTimeOffset.UtcNow);
        SetUpSheetPipeline();

        await Assert.ThrowsAsync<NotFoundException>(() => _service.CreateConversationDocumentAsync(
            "job-sheet", CreateFile("budget.xlsx"), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken));

        using var ctx = _fixture.CreateContext();
        var sheet = await ctx.ConversationDocumentSheets
            .AsNoTracking()
            .Include(s => s.Columns)
            .Include(s => s.Rows)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(sheet.DateDeactivated);
        Assert.All(sheet.Columns, column => Assert.NotNull(column.DateDeactivated));
        Assert.All(sheet.Rows, row => Assert.NotNull(row.DateDeactivated));
    }

    [Fact]
    public async Task CreateProjectDocumentAsync_Spreadsheet_PersistsItsSheetsColumnsAndRows()
    {
        var project = await AddProjectAsync();
        SetUpSheetPipeline();

        var result = await _service.CreateProjectDocumentAsync(
            "job-sheet", CreateFile("budget.xlsx"), KnownIds.SeedUserId, project.Id, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var sheet = await ctx.ProjectDocumentSheets
            .AsNoTracking()
            .Include(s => s.Columns)
            .Include(s => s.Rows)
            .SingleAsync(s => s.ProjectDocumentId == result.Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, sheet.Columns.Count);
        Assert.Equal(2, sheet.Rows.Count);
        Assert.Empty(await ctx.ConversationDocumentSheets.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateProjectDocumentAsync_ProjectDeactivatedDuringIngestion_DeactivatesTheSheetsToo()
    {
        var project = await AddProjectAsync(deactivated: DateTimeOffset.UtcNow);
        SetUpSheetPipeline();

        await Assert.ThrowsAsync<NotFoundException>(() => _service.CreateProjectDocumentAsync(
            "job-sheet", CreateFile("budget.xlsx"), KnownIds.SeedUserId, project.Id, TestContext.Current.CancellationToken));

        using var ctx = _fixture.CreateContext();
        var sheet = await ctx.ProjectDocumentSheets
            .AsNoTracking()
            .Include(s => s.Columns)
            .Include(s => s.Rows)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(sheet.DateDeactivated);
        Assert.All(sheet.Columns, column => Assert.NotNull(column.DateDeactivated));
        Assert.All(sheet.Rows, row => Assert.NotNull(row.DateDeactivated));
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_ExtractorWithoutSheets_PersistsNoSheets()
    {
        var conversation = await AddConversationAsync();
        SetUpPipeline(chunkCount: 2);

        var result = await _service.CreateConversationDocumentAsync(
            "job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();

        Assert.Empty(await ctx.ConversationDocumentSheets
            .AsNoTracking()
            .Where(s => s.ConversationDocumentId == result.Id)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_Spreadsheet_RecordsWhatItReadAndWhatItCost()
    {
        var conversation = await AddConversationAsync();
        SetUpSheetPipeline();

        using var sheets = Collect<int>("enterprise_gpt.sheet_ingestion.sheets");
        using var rows = Collect<int>("enterprise_gpt.sheet_ingestion.rows");
        using var columns = Collect<int>("enterprise_gpt.sheet_ingestion.columns");
        using var duration = Collect<double>("enterprise_gpt.sheet_ingestion.duration");

        await _service.CreateConversationDocumentAsync(
            "job-sheet", CreateFile("budget.xlsx"), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken);

        Assert.Contains(sheets.GetMeasurementSnapshot(), m => m.Value == 1 && Tagged(m, "xlsx", "success"));
        Assert.Contains(rows.GetMeasurementSnapshot(), m => m.Value == 2 && Tagged(m, "xlsx", "success"));
        Assert.Contains(columns.GetMeasurementSnapshot(), m => m.Value == 2 && Tagged(m, "xlsx", "success"));
        Assert.Contains(duration.GetMeasurementSnapshot(), m => Tagged(m, "xlsx", "success"));
    }

    /// <summary>
    /// A workbook turned away by an ingest ceiling is an outcome worth counting: it is the signal that
    /// the ceilings are set where uploads actually land.
    /// </summary>
    [Fact]
    public async Task CreateConversationDocumentAsync_SpreadsheetRefusedByTheExtractor_RecordsTheRefusal()
    {
        var conversation = await AddConversationAsync();
        var extractor = SetUpSheetPipeline();

        extractor
            .ExtractSheetsAsync(Arg.Any<FileDto>(), Arg.Any<IProgress<DocumentExtractionProgress>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ValidationException("The sheet has more rows than the configured limit."));

        using var duration = Collect<double>("enterprise_gpt.sheet_ingestion.duration");

        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateConversationDocumentAsync(
            "job-sheet", CreateFile("budget.xlsx"), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken));

        Assert.Contains(duration.GetMeasurementSnapshot(), m => Tagged(m, "xlsx", "refused"));
    }

    /// <summary>
    /// The instruments describe spreadsheet reads, so a format with no grid must not appear in them at
    /// all — otherwise the row and column distributions are diluted by documents that have neither.
    /// </summary>
    [Fact]
    public async Task CreateConversationDocumentAsync_ExtractorWithoutSheets_RecordsNoIngestion()
    {
        var conversation = await AddConversationAsync();
        SetUpPipeline(chunkCount: 2);

        using var duration = Collect<double>("enterprise_gpt.sheet_ingestion.duration");

        await _service.CreateConversationDocumentAsync(
            "job-1", CreateFile(), KnownIds.SeedUserId, conversation.Id, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            duration.GetMeasurementSnapshot(),
            m => m.Tags["document.type"] as string == "pdf");
    }

    private static MetricCollector<T> Collect<T>(string instrument) where T : struct =>
        new(meterScope: null, TelemetryNames.ChatSource, instrument);

    /// <remarks>
    /// The instruments are process-global, so this looks for a matching measurement rather than
    /// asserting on the only one.
    /// </remarks>
    private static bool Tagged<T>(CollectedMeasurement<T> measurement, string documentType, string outcome)
        where T : struct =>
        measurement.Tags["document.type"] as string == documentType
            && measurement.Tags["sheet.outcome"] as string == outcome;

    /// <summary>
    /// Points the factory at an extractor that also reports a grid, which is the only way the sheet
    /// path is reached — every other extractor fails the type check the ingestion path makes.
    /// </summary>
    private ISheetStructureExtractor SetUpSheetPipeline()
    {
        SetUpPipeline(chunkCount: 2);

        var sheetExtractor = Substitute.For<ISheetStructureExtractor>();
        _extractorFactory.Resolve(Arg.Any<FileExtensions>()).Returns(sheetExtractor);

        SheetStructureDto[] sheets =
        [
            new SheetStructureDto(1, "Regional Revenue", 2, 2,
            [
                new SheetColumnDto(0, "SKU", SheetColumnType.Text),
                new SheetColumnDto(1, "Revenue", SheetColumnType.Number)
            ],
            [
                new SheetRowDto(0, """{"SKU":"W-1","Revenue":"1200"}"""),
                new SheetRowDto(1, """{"SKU":"W-2","Revenue":"800"}""")
            ])
        ];

        sheetExtractor
            .ExtractSheetsAsync(Arg.Any<FileDto>(), Arg.Any<IProgress<DocumentExtractionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(new SheetExtractionResult(
                [new DocumentSegmentDto { SourceNumber = 1, Text = "Sheet: Regional Revenue\nSKU | Revenue" }],
                sheets));

        return sheetExtractor;
    }
    #endregion
}
