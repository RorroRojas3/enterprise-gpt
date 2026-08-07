using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
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
    public void GetJobStatus_EveryStage_MapsToTheCoarseStateOlderClientsUnderstand(JobStatus status, string expectedState)
    {
        _jobStatusStore.Get("job-1").Returns(Snapshot(status));

        var result = DocumentEndpoints.GetJobStatus("job-1", _jobStatusStore, _tokenService);

        var payload = result.Value;
        Assert.NotNull(payload);
        Assert.Equal(expectedState, payload.State);
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
