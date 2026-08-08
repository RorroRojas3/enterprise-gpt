using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AzureStorage:DocumentsContainer"] = ContainerName })
            .Build();

        _service = new DocumentService(
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
        Guid? userId = null, DateTimeOffset? deactivated = null, Guid? projectId = null)
    {
        var date = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? KnownIds.SeedUserId,
            ProjectId = projectId,
            Name = "Test Conversation",
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
}
