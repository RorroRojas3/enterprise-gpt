using Microsoft.AspNetCore.Http;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Enterprise.Gpt.Api.Endpoints;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.BackgroundJobs;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Extraction;
using System.Text;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Endpoints;

public sealed class DocumentEndpointsTests
{
    private static readonly Guid _ownerId = Guid.NewGuid();

    private readonly IDocumentService _documentService = Substitute.For<IDocumentService>();
    private readonly IJobStatusStore _jobStatusStore = Substitute.For<IJobStatusStore>();
    private readonly IDocumentTextExtractorFactory _extractorFactory = Substitute.For<IDocumentTextExtractorFactory>();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();

    public DocumentEndpointsTests()
    {
        _tokenService.GetOid().Returns(_ownerId);
    }

    private static IFormFile CreateFormFile(string fileName = "report.pdf", string content = "%PDF-1.7")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };
    }

    private static JobStatusSnapshot Snapshot(
        JobStatus status = JobStatus.Embedding,
        int progress = 60,
        string? message = "Embedded 12 of 40 chunk(s)",
        Guid? documentId = null,
        string? errorMessage = null,
        Guid? userId = null)
    {
        return new JobStatusSnapshot("job-1", userId ?? _ownerId, status, progress, DateTimeOffset.UtcNow, message, 12, 40, documentId, errorMessage);
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_ValidUpload_ReturnsAcceptedWithTheJobId()
    {
        var conversationId = Guid.NewGuid();
        _documentService.QueueConversationDocumentAsync(conversationId, Arg.Any<FileDto>(), Arg.Any<CancellationToken>())
            .Returns(new JobDto { Id = "job-1" });

        var result = await DocumentEndpoints.CreateConversationDocumentAsync(
            conversationId, CreateFormFile(), _documentService, TestContext.Current.CancellationToken);

        Assert.Equal("job-1", result.Value!.Id);
        Assert.Equal("/api/documents/upload-status/job-1", result.Location);
    }

    [Fact]
    public async Task CreateConversationDocumentAsync_ValidUpload_PassesTheBufferedFileToTheService()
    {
        // The form stream is disposed with the request, so the bytes have to be read before the work is
        // handed to a background scope.
        var conversationId = Guid.NewGuid();
        _documentService.QueueConversationDocumentAsync(conversationId, Arg.Any<FileDto>(), Arg.Any<CancellationToken>())
            .Returns(new JobDto { Id = "job-1" });

        await DocumentEndpoints.CreateConversationDocumentAsync(
            conversationId, CreateFormFile("deck.pptx", "PKbody"), _documentService, TestContext.Current.CancellationToken);

        await _documentService.Received(1).QueueConversationDocumentAsync(
            conversationId,
            Arg.Is<FileDto>(f => f != null
                && f.FileName == "deck.pptx"
                && f.ContentType == "application/pdf"
                && f.Content.Length > 0
                && f.Length == f.Content.Length),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateProjectDocumentAsync_ValidUpload_ReturnsAcceptedPointingAtTheSharedStatusRoute()
    {
        // Project and conversation uploads deliberately share one job-status route, so the location
        // header must not gain a project-specific form.
        var projectId = Guid.NewGuid();
        _documentService.QueueProjectDocumentAsync(projectId, Arg.Any<FileDto>(), Arg.Any<CancellationToken>())
            .Returns(new JobDto { Id = "job-1" });

        var result = await DocumentEndpoints.CreateProjectDocumentAsync(
            projectId, CreateFormFile(), _documentService, TestContext.Current.CancellationToken);

        Assert.Equal("job-1", result.Value!.Id);
        Assert.Equal("/api/documents/upload-status/job-1", result.Location);
    }

    [Fact]
    public async Task CreateProjectDocumentAsync_ValidUpload_PassesTheBufferedFileToTheService()
    {
        var projectId = Guid.NewGuid();
        _documentService.QueueProjectDocumentAsync(projectId, Arg.Any<FileDto>(), Arg.Any<CancellationToken>())
            .Returns(new JobDto { Id = "job-1" });

        await DocumentEndpoints.CreateProjectDocumentAsync(
            projectId, CreateFormFile("deck.pptx", "PKbody"), _documentService, TestContext.Current.CancellationToken);

        await _documentService.Received(1).QueueProjectDocumentAsync(
            projectId,
            Arg.Is<FileDto>(f => f != null
                && f.FileName == "deck.pptx"
                && f.Content.Length > 0
                && f.Length == f.Content.Length),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUserDocumentsAsync_ServiceReturnsAPage_ReturnsOkWithPayload()
    {
        var expected = new PaginatedResponseDto<UserDocumentDto>
        {
            Items = [new UserDocumentDto { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), ConversationName = "Planning", Name = "spec.pdf", Extension = ".pdf", MimeType = "application/pdf", Size = 10 }],
            TotalCount = 1,
            PageSize = 20,
            CurrentPage = 1
        };
        _documentService.GetUserDocumentsAsync(0, 20, null, null, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await DocumentEndpoints.GetUserDocumentsAsync(
            _documentService, 0, 20, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task GetUserDocumentsAsync_NoQueryString_UsesTheDefaultPage()
    {
        _documentService.GetUserDocumentsAsync(0, 20, null, null, Arg.Any<CancellationToken>())
            .Returns(new PaginatedResponseDto<UserDocumentDto>());

        await DocumentEndpoints.GetUserDocumentsAsync(_documentService, cancellationToken: TestContext.Current.CancellationToken);

        await _documentService.Received(1).GetUserDocumentsAsync(0, 20, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUserDocumentsAsync_TypeQueryString_IsPassedToTheService()
    {
        _documentService.GetUserDocumentsAsync(0, 20, "generated", null, Arg.Any<CancellationToken>())
            .Returns(new PaginatedResponseDto<UserDocumentDto>());

        await DocumentEndpoints.GetUserDocumentsAsync(
            _documentService, type: "generated", cancellationToken: TestContext.Current.CancellationToken);

        await _documentService.Received(1).GetUserDocumentsAsync(0, 20, "generated", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUserDocumentsAsync_NameQueryString_IsPassedToTheService()
    {
        _documentService.GetUserDocumentsAsync(0, 20, null, "quarterly", Arg.Any<CancellationToken>())
            .Returns(new PaginatedResponseDto<UserDocumentDto>());

        await DocumentEndpoints.GetUserDocumentsAsync(
            _documentService, name: "quarterly", cancellationToken: TestContext.Current.CancellationToken);

        await _documentService.Received(1).GetUserDocumentsAsync(0, 20, null, "quarterly", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_OwnedDocument_ReturnsTheLinkTheServiceIssued()
    {
        var conversationId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        _documentService.GetConversationDocumentDownloadAsync(conversationId, documentId, Arg.Any<CancellationToken>())
            .Returns(new DocumentDownloadDto
            {
                DownloadUrl = "https://storage.example/documents/blob?sig=signature",
                FileName = "report.pdf",
                ExpiresAt = expiresAt
            });

        var httpResponse = new DefaultHttpContext().Response;

        var result = await DocumentEndpoints.GetConversationDocumentDownloadAsync(
            conversationId, documentId, _documentService, httpResponse, TestContext.Current.CancellationToken);

        var payload = result.Value;
        Assert.NotNull(payload);
        Assert.Equal("https://storage.example/documents/blob?sig=signature", payload.DownloadUrl);
        Assert.Equal("report.pdf", payload.FileName);
        Assert.Equal(expiresAt, payload.ExpiresAt);
        // The link reads the file with no credentials of its own, so the body must not reach a disk cache
        // that outlives it.
        Assert.Equal("no-store", httpResponse.Headers.CacheControl);
    }

    [Fact]
    public async Task GetConversationDocumentDownloadAsync_DocumentTheCallerCannotSee_PropagatesNotFound()
    {
        // The service reports a document owned by someone else the same way it reports one that never
        // existed, and the endpoint adds nothing that would tell the two apart.
        _documentService.GetConversationDocumentDownloadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Throws(new NotFoundException("Document with id 'x' was not found."));

        await Assert.ThrowsAsync<NotFoundException>(
            () => DocumentEndpoints.GetConversationDocumentDownloadAsync(
                Guid.NewGuid(), Guid.NewGuid(), _documentService, new DefaultHttpContext().Response,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeactivateConversationDocumentAsync_OwnedDocument_ReturnsNoContent()
    {
        var conversationId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        var result = await DocumentEndpoints.DeactivateConversationDocumentAsync(
            conversationId, documentId, _documentService, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _documentService.Received(1).DeactivateConversationDocumentAsync(
            conversationId, documentId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeactivateConversationDocumentAsync_DocumentTheCallerCannotSee_PropagatesNotFound()
    {
        _documentService.DeactivateConversationDocumentAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Throws(new NotFoundException("Document with id 'x' was not found."));

        await Assert.ThrowsAsync<NotFoundException>(
            () => DocumentEndpoints.DeactivateConversationDocumentAsync(
                Guid.NewGuid(), Guid.NewGuid(), _documentService, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetProjectDocumentDownloadAsync_OwnedDocument_ReturnsTheLinkTheServiceIssued()
    {
        var projectId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        _documentService.GetProjectDocumentDownloadAsync(projectId, documentId, Arg.Any<CancellationToken>())
            .Returns(new DocumentDownloadDto
            {
                DownloadUrl = "https://storage.example/documents/blob?sig=signature",
                FileName = "handbook.pdf",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            });

        var httpResponse = new DefaultHttpContext().Response;

        var result = await DocumentEndpoints.GetProjectDocumentDownloadAsync(
            projectId, documentId, _documentService, httpResponse, TestContext.Current.CancellationToken);

        var payload = result.Value;
        Assert.NotNull(payload);
        Assert.Equal("https://storage.example/documents/blob?sig=signature", payload.DownloadUrl);
        Assert.Equal("handbook.pdf", payload.FileName);
        Assert.Equal("no-store", httpResponse.Headers.CacheControl);
    }

    [Fact]
    public async Task GetProjectDocumentDownloadAsync_DocumentTheCallerCannotSee_PropagatesNotFound()
    {
        _documentService.GetProjectDocumentDownloadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Throws(new NotFoundException("Document with id 'x' was not found."));

        await Assert.ThrowsAsync<NotFoundException>(
            () => DocumentEndpoints.GetProjectDocumentDownloadAsync(
                Guid.NewGuid(), Guid.NewGuid(), _documentService, new DefaultHttpContext().Response,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GetJobStatus_KnownJob_ReturnsTheFullSnapshot()
    {
        var documentId = Guid.NewGuid();
        _jobStatusStore.Get("job-1").Returns(Snapshot(documentId: documentId));

        var result = DocumentEndpoints.GetJobStatus("job-1", _jobStatusStore, _tokenService);

        var payload = result.Value;
        Assert.NotNull(payload);
        Assert.Equal("job-1", payload.Id);
        Assert.Equal("Processing", payload.State);
        Assert.Equal(nameof(JobStatus.Embedding), payload.Status);
        Assert.Equal(60, payload.Progress);
        Assert.Equal("Embedded 12 of 40 chunk(s)", payload.Message);
        Assert.Equal(12, payload.CompletedUnits);
        Assert.Equal(40, payload.TotalUnits);
        Assert.Equal(documentId, payload.DocumentId);
        Assert.NotEqual(default, payload.UpdatedAt);
    }

    [Fact]
    public void GetJobStatus_UnknownJob_ThrowsNotFoundException()
    {
        _jobStatusStore.Get("missing").Returns((JobStatusSnapshot?)null);

        var exception = Assert.Throws<NotFoundException>(
            () => DocumentEndpoints.GetJobStatus("missing", _jobStatusStore, _tokenService));

        Assert.Equal("Upload job not found.", exception.Message);
    }

    [Fact]
    public void GetJobStatus_JobQueuedByAnotherUser_ThrowsNotFoundException()
    {
        // The snapshot carries the created document id and the failure detail, so another user reading it
        // would learn both. 404 rather than 403 avoids confirming that the job id is even valid.
        _jobStatusStore.Get("job-1").Returns(Snapshot(userId: Guid.NewGuid()));

        var exception = Assert.Throws<NotFoundException>(
            () => DocumentEndpoints.GetJobStatus("job-1", _jobStatusStore, _tokenService));

        Assert.Equal("Upload job not found.", exception.Message);
    }

    [Fact]
    public void GetJobStatus_SnapshotWithNoOwner_ThrowsNotFoundException()
    {
        // Snapshots created by a report for an unregistered job carry Guid.Empty and belong to nobody.
        _jobStatusStore.Get("job-1").Returns(Snapshot(userId: Guid.Empty));

        var exception = Assert.Throws<NotFoundException>(
            () => DocumentEndpoints.GetJobStatus("job-1", _jobStatusStore, _tokenService));

        Assert.Equal("Upload job not found.", exception.Message);
    }

    [Fact]
    public void GetJobStatus_FailedJob_SurfacesTheErrorMessage()
    {
        // The store captured a failure reason from the start; not returning it left clients showing a bare
        // "Failed" with no explanation.
        _jobStatusStore.Get("job-1").Returns(Snapshot(JobStatus.Failed, 45, "Files of type '.xlsx' are not supported.",
            errorMessage: "Files of type '.xlsx' are not supported."));

        var result = DocumentEndpoints.GetJobStatus("job-1", _jobStatusStore, _tokenService);

        var payload = result.Value;
        Assert.NotNull(payload);
        Assert.Equal("Failed", payload.State);
        Assert.Equal("Files of type '.xlsx' are not supported.", payload.ErrorMessage);
    }

    [Theory]
    [InlineData(JobStatus.Queued, "Enqueued")]
    [InlineData(JobStatus.Uploading, "Processing")]
    [InlineData(JobStatus.Extracting, "Processing")]
    [InlineData(JobStatus.Chunking, "Processing")]
    [InlineData(JobStatus.Embedding, "Processing")]
    [InlineData(JobStatus.Persisting, "Processing")]
    [InlineData(JobStatus.Processed, "Succeeded")]
    [InlineData(JobStatus.Failed, "Failed")]
    // Not Processing: a cancelled job never moves again, and the default would leave a client polling it.
    [InlineData(JobStatus.Cancelled, "Failed")]
    public void GetJobStatus_EveryStage_MapsToTheCoarseStateOlderClientsUnderstand(JobStatus status, string expectedState)
    {
        _jobStatusStore.Get("job-1").Returns(Snapshot(status));

        var result = DocumentEndpoints.GetJobStatus("job-1", _jobStatusStore, _tokenService);

        var payload = result.Value;
        Assert.NotNull(payload);
        Assert.Equal(expectedState, payload.State);
    }

    [Fact]
    public void GetJobStatus_CancelledJob_NamesTheStageEvenThoughTheStateReadsFailed()
    {
        _jobStatusStore.Get("job-1").Returns(Snapshot(JobStatus.Cancelled, 60, "Cancelled"));

        var result = DocumentEndpoints.GetJobStatus("job-1", _jobStatusStore, _tokenService);

        var payload = result.Value;
        Assert.NotNull(payload);
        // The coarse vocabulary stays four values wide; the free-form stage is what tells the two apart.
        Assert.Equal("Failed", payload.State);
        Assert.Equal("Cancelled", payload.Status);
    }

    [Fact]
    public async Task CancelUploadAsync_KnownJob_ReturnsNoContent()
    {
        var result = await DocumentEndpoints.CancelUploadAsync("job-1", _documentService, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _documentService.Received(1).CancelUploadAsync("job-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelUploadAsync_JobTheCallerCannotSee_PropagatesNotFound()
    {
        _documentService.CancelUploadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new NotFoundException("Upload job not found."));

        await Assert.ThrowsAsync<NotFoundException>(
            () => DocumentEndpoints.CancelUploadAsync("job-1", _documentService, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GetFileExtensions_ReturnsWhateverTheRegisteredExtractorsCanRead()
    {
        // Derived from the extractors rather than a hardcoded list, so the advertised formats and the
        // readable formats cannot drift apart.
        string[] supported = [".pdf", ".doc", ".docx", ".pptx", ".md", ".txt"];
        _extractorFactory.SupportedExtensionNames.Returns(supported);

        var result = DocumentEndpoints.GetFileExtensions(_extractorFactory);

        Assert.Equal(supported, result.Value);
    }
}
