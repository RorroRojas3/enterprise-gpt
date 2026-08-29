using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Web;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Endpoints;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DocumentEndpointsIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan _jobTimeout = TimeSpan.FromSeconds(30);

    private readonly IntegrationTestFixture _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetConversationsAndDocumentsAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    #region Helpers
    private static MultipartFormDataContent Upload(string fileName, byte[] content, string contentType = "application/octet-stream")
    {
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        return new MultipartFormDataContent { { fileContent, "file", fileName } };
    }

    private static MultipartFormDataContent TextUpload(string fileName = "notes.txt", int sentences = 60)
    {
        var builder = new StringBuilder();
        for (var i = 1; i <= sentences; i++)
        {
            builder.Append($"Sentence {i} describes an entirely unremarkable and deliberately verbose subject. ");
        }

        return Upload(fileName, Encoding.UTF8.GetBytes(builder.ToString()), "text/plain");
    }


    /// <summary>
    /// Polls the status endpoint until the job reaches a terminal state, mirroring what the client does.
    /// </summary>
    /// <remarks>
    /// <strong>Every test that receives a 202 must await this before it returns</strong>, whether or
    /// not it asserts on the outcome. Ingestion runs on the host's background pipeline and outlives
    /// the request, and the per-test reset deletes <c>ConversationDocument</c> before
    /// <c>Conversation</c> — so a job still running when the next test starts inserts its row between
    /// those two deletes and fails that test on a foreign key, a long way from the test that leaked
    /// it. A terminal state is a sound barrier because <c>DocumentService</c> reports completion only
    /// after <c>SaveChangesAsync</c> has committed.
    /// </remarks>
    private static async Task<JobStatusDto> WaitForTerminalStateAsync(HttpClient client, string jobId)
    {
        var deadline = DateTimeOffset.UtcNow + _jobTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.GetAsync($"api/documents/upload-status/{jobId}", TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();

            var status = await response.Content.ReadFromJsonAsync<JobStatusDto>(TestContext.Current.CancellationToken);
            Assert.NotNull(status);

            if (status.State is "Succeeded" or "Failed")
            {
                return status;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Job {jobId} did not reach a terminal state within {_jobTimeout}.");
    }

    private static async Task<JobDto> PostUploadAsync(HttpClient client, Guid conversationId, MultipartFormDataContent content)
    {
        var response = await client.PostAsync($"api/documents/conversations/{conversationId}", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var job = await response.Content.ReadFromJsonAsync<JobDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(job);
        return job;
    }

    private static async Task<JobDto> PostProjectUploadAsync(HttpClient client, Guid projectId, MultipartFormDataContent content)
    {
        var response = await client.PostAsync($"api/documents/projects/{projectId}", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var job = await response.Content.ReadFromJsonAsync<JobDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(job);
        return job;
    }
    #endregion

    #region Authorization
    [Fact]
    public async Task Upload_Anonymous_ReturnsUnauthorized()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.PostAsync($"api/documents/conversations/{conversationId}", TextUpload(), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal("Unauthorized", problem.Title);
    }

    [Fact]
    public async Task Upload_UserWithoutTheUploadFilePermission_ReturnsForbidden()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.RevokePermissionAsync(TestUsers.RegularUserId, PermissionIds.UploadFile, TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsync($"api/documents/conversations/{conversationId}", TextUpload(), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Forbidden);
        Assert.Contains("Upload File", problem.Detail);

        // The machine-readable half of the denial: a client can name the missing grant without
        // parsing the sentence in detail.
        var permissions = problem.Extensions["permissions"].EnumerateArray().Select(x => x.GetString());
        Assert.Equal(["Upload File"], permissions);
    }

    [Fact]
    public async Task GetFileExtensions_Anonymous_ReturnsUnauthorized()
    {
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.GetAsync("api/documents/file-extensions", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal("Unauthorized", problem.Title);
    }
    #endregion

    #region Validation
    [Fact]
    public async Task Upload_UnsupportedExtension_ReturnsBadRequestImmediately()
    {
        // Rejected on the request rather than as a job that fails minutes later.
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsync(
            $"api/documents/conversations/{conversationId}",
            Upload("macros.xlsm", [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]),
            TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadValidationAsync(response);
        Assert.Contains(problem.Errors["FileName"], message => message.Contains("not supported"));
    }

    [Fact]
    public async Task Upload_ContentNotMatchingTheExtension_ReturnsBadRequest()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsync(
            $"api/documents/conversations/{conversationId}",
            Upload("report.pdf", Encoding.UTF8.GetBytes("definitely not a pdf")),
            TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadValidationAsync(response);
        Assert.Contains(problem.Errors["Content"], message => message.Contains("do not match its extension"));
    }

    [Fact]
    public async Task Upload_EmptyFile_ReturnsBadRequest()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsync(
            $"api/documents/conversations/{conversationId}",
            Upload("notes.txt", []),
            TestContext.Current.CancellationToken);

        await ProblemAssert.ReadValidationAsync(response);
    }

    [Fact]
    public async Task Upload_FileLargerThanTheConfiguredLimit_ReturnsBadRequest()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        // The test host configures Documents:MaxFileSizeBytes as 64 KB.
        var oversize = Encoding.UTF8.GetBytes(new string('a', 100_000));

        var response = await client.PostAsync(
            $"api/documents/conversations/{conversationId}",
            Upload("notes.txt", oversize, "text/plain"),
            TestContext.Current.CancellationToken);

        // The size cap is enforced by MaxUploadSizeEndpointFilter, not a validator, so this 400
        // carries a detail rather than an errors dictionary.
        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.BadRequest);
        Assert.Contains("maximum upload size", problem.Detail);
        Assert.Equal(64 * 1024, problem.Extensions["maxBytes"].GetInt64());
    }

    [Fact]
    public async Task Upload_ConversationBelongingToAnotherUser_ReturnsNotFound()
    {
        var conversationId = await _fixture.AddConversationAsync(TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsync(
            $"api/documents/conversations/{conversationId}", TextUpload(), TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upload_UnknownConversation_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsync(
            $"api/documents/conversations/{Guid.NewGuid()}", TextUpload(), TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upload_DeactivatedConversation_ReturnsNotFound()
    {
        var conversationId = await _fixture.AddConversationAsync(deactivated: true, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsync(
            $"api/documents/conversations/{conversationId}", TextUpload(), TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }
    #endregion

    #region Ingestion
    [Fact]
    public async Task Upload_ValidTextFile_RunsTheWholePipelineAndPersistsChunks()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostUploadAsync(client, conversationId, TextUpload());
        var status = await WaitForTerminalStateAsync(client, job.Id);

        Assert.Equal("Succeeded", status.State);
        Assert.Equal(nameof(JobStatus.Processed), status.Status);
        Assert.Equal(100, status.Progress);
        Assert.NotNull(status.DocumentId);
        Assert.Null(status.ErrorMessage);

        var document = await _fixture.FindDocumentAsync(status.DocumentId.Value, TestContext.Current.CancellationToken);
        Assert.NotNull(document);
        Assert.Equal("notes.txt", document.Name);
        Assert.Equal(".txt", document.Extension);
        Assert.Equal(conversationId, document.ConversationId);
        Assert.Equal(TestUsers.RegularUserId, document.UserId);

        var chunks = await _fixture.FindDocumentChunksAsync(status.DocumentId.Value, TestContext.Current.CancellationToken);
        Assert.True(chunks.Count > 1);
        Assert.Equal([.. Enumerable.Range(0, chunks.Count)], chunks.Select(c => c.Index));
        Assert.All(chunks, chunk => Assert.True(chunk.TokenCount is > 0 and <= 64));
        Assert.All(chunks, chunk => Assert.False(string.IsNullOrWhiteSpace(chunk.Text)));
        // A plain text file has no page or slide structure to attribute a chunk to.
        Assert.All(chunks, chunk => Assert.Null(chunk.SourceNumber));
    }

    [Fact]
    public async Task Upload_ValidTextFile_StoresTheBlobUnderTheDocumentId()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostUploadAsync(client, conversationId, TextUpload());
        var status = await WaitForTerminalStateAsync(client, job.Id);

        Assert.Equal("Succeeded", status.State);
        Assert.Contains(
            $"{TestUsers.RegularUserId}/{conversationId}/{status.DocumentId}.txt",
            _fixture.Factory.BlobStorage.UploadedPaths);
    }

    [Fact]
    public async Task Upload_SameFileNameTwice_BothSucceedWithoutColliding()
    {
        // A name-keyed blob path made the second upload collide with the first and fail the job.
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var first = await WaitForTerminalStateAsync(client, (await PostUploadAsync(client, conversationId, TextUpload())).Id);
        var second = await WaitForTerminalStateAsync(client, (await PostUploadAsync(client, conversationId, TextUpload())).Id);

        Assert.Equal("Succeeded", first.State);
        Assert.Equal("Succeeded", second.State);
        Assert.NotEqual(first.DocumentId, second.DocumentId);
    }

    [Fact]
    public async Task Upload_MarkdownFile_IsIngested()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostUploadAsync(client, conversationId, TextUpload("guide.md"));
        var status = await WaitForTerminalStateAsync(client, job.Id);

        Assert.Equal("Succeeded", status.State);
        Assert.NotNull(status.DocumentId);

        var document = await _fixture.FindDocumentAsync(status.DocumentId.Value, TestContext.Current.CancellationToken);
        Assert.NotNull(document);
        Assert.Equal(".md", document.Extension);
    }

    [Fact]
    public async Task Upload_PowerPointFile_IsIngestedWithSlideNumbers()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();
        var content = PresentationFixture.Create(slideCount: 4);

        var job = await PostUploadAsync(client, conversationId,
            Upload("deck.pptx", content, "application/vnd.openxmlformats-officedocument.presentationml.presentation"));
        var status = await WaitForTerminalStateAsync(client, job.Id);

        Assert.Equal("Succeeded", status.State);
        Assert.NotNull(status.DocumentId);

        var chunks = await _fixture.FindDocumentChunksAsync(status.DocumentId.Value, TestContext.Current.CancellationToken);
        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.True(chunk.SourceNumber is >= 1 and <= 4));
    }

    [Fact]
    public async Task Upload_ExcelWorkbook_IsIngestedWithSheetNumbers()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();
        var content = SpreadsheetFixture.CreateWorkbook(sheetCount: 3);

        var job = await PostUploadAsync(client, conversationId,
            Upload("budget.xlsx", content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        var status = await WaitForTerminalStateAsync(client, job.Id);

        Assert.Equal("Succeeded", status.State);
        Assert.NotNull(status.DocumentId);

        var document = await _fixture.FindDocumentAsync(status.DocumentId.Value, TestContext.Current.CancellationToken);
        Assert.NotNull(document);
        Assert.Equal(".xlsx", document.Extension);

        var chunks = await _fixture.FindDocumentChunksAsync(status.DocumentId.Value, TestContext.Current.CancellationToken);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.SourceNumber is >= 1 and <= 3));
        Assert.Contains(chunks, chunk => chunk.Text.Contains("SKU | Region | Revenue | Quarter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upload_ExcelWorkbook_PersistsEachSheetsColumnsAndRows()
    {
        // The only place the native json cells column is exercised: the unit-test fixture runs on SQLite,
        // which derives its own store type for it.
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();
        var content = SpreadsheetFixture.CreateWorkbook(sheetCount: 2, rowsPerSheet: 5);

        var job = await PostUploadAsync(client, conversationId,
            Upload("budget.xlsx", content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        var status = await WaitForTerminalStateAsync(client, job.Id);

        Assert.Equal("Succeeded", status.State);
        Assert.NotNull(status.DocumentId);

        var sheets = await _fixture.FindDocumentSheetsAsync(status.DocumentId.Value, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], sheets.Select(s => s.SheetIndex));
        Assert.Equal(["Region 1", "Region 2"], sheets.Select(s => s.SheetName));
        Assert.All(sheets, sheet =>
        {
            Assert.Equal(5, sheet.RowCount);
            Assert.Equal(4, sheet.ColumnCount);
            Assert.Equal(
                ["SKU", "Region", "Revenue", "Quarter"],
                sheet.Columns.OrderBy(c => c.ColumnIndex).Select(c => c.ColumnName));
            Assert.Equal([0, 1, 2, 3, 4], sheet.Rows.OrderBy(r => r.RowIndex).Select(r => r.RowIndex));
        });

        var first = sheets[0].Rows.Single(r => r.RowIndex == 0);
        Assert.Equal("""{"SKU":"SKU-1-0001","Region":"West","Revenue":"137","Quarter":"Q2"}""", first.Cells);
    }

    [Fact]
    public async Task Upload_CsvFile_PersistsOneSheetWithItsRows()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostUploadAsync(client, conversationId,
            Upload("orders.csv", SpreadsheetFixture.CreateCsv(rowCount: 4), "text/csv"));
        var status = await WaitForTerminalStateAsync(client, job.Id);

        Assert.Equal("Succeeded", status.State);
        Assert.NotNull(status.DocumentId);

        var sheet = Assert.Single(await _fixture.FindDocumentSheetsAsync(status.DocumentId.Value, TestContext.Current.CancellationToken));

        Assert.Equal(1, sheet.SheetIndex);
        Assert.Equal("orders", sheet.SheetName);
        Assert.Equal(4, sheet.RowCount);
        Assert.Equal(["SKU", "Region", "Revenue", "Quarter"], sheet.Columns.OrderBy(c => c.ColumnIndex).Select(c => c.ColumnName));
    }

    [Fact]
    public async Task Upload_CsvFile_IsIngestedAsASingleSheet()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostUploadAsync(client, conversationId,
            Upload("orders.csv", SpreadsheetFixture.CreateCsv(), "text/csv"));
        var status = await WaitForTerminalStateAsync(client, job.Id);

        Assert.Equal("Succeeded", status.State);
        Assert.NotNull(status.DocumentId);

        var chunks = await _fixture.FindDocumentChunksAsync(status.DocumentId.Value, TestContext.Current.CancellationToken);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.Equal(1, chunk.SourceNumber));
        Assert.Contains(chunks, chunk => chunk.Text.Contains("SKU | Region | Revenue | Quarter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upload_FileWithoutReadableText_FailsTheJobWithAnExplanation()
    {
        // Whitespace passes the binary-content check but yields nothing to index.
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostUploadAsync(client, conversationId,
            Upload("blank.txt", Encoding.UTF8.GetBytes("   \n\n\t  "), "text/plain"));
        var status = await WaitForTerminalStateAsync(client, job.Id);

        Assert.Equal("Failed", status.State);
        Assert.Equal(nameof(JobStatus.Failed), status.Status);
        Assert.NotNull(status.ErrorMessage);
        Assert.Contains("No readable text", status.ErrorMessage);
    }
    #endregion

    #region Project ingestion
    [Fact]
    public async Task ProjectUpload_Anonymous_ReturnsUnauthorized()
    {
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.PostAsync($"api/documents/projects/{projectId}", TextUpload(), TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProjectUpload_UserWithoutTheUploadFilePermission_ReturnsForbidden()
    {
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.RevokePermissionAsync(TestUsers.RegularUserId, PermissionIds.UploadFile, TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsync($"api/documents/projects/{projectId}", TextUpload(), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Forbidden);
        Assert.Contains("Upload File", problem.Detail);
    }

    [Fact]
    public async Task ProjectUpload_ProjectBelongingToAnotherUser_ReturnsNotFound()
    {
        var projectId = await _fixture.AddProjectAsync("it-theirs", TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsync($"api/documents/projects/{projectId}", TextUpload(), TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ProjectUpload_UnknownProject_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsync($"api/documents/projects/{Guid.NewGuid()}", TextUpload(), TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ProjectUpload_UnsupportedExtension_ReturnsBadRequestImmediately()
    {
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsync(
            $"api/documents/projects/{projectId}",
            Upload("macros.xlsm", [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]),
            TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadValidationAsync(response);
        Assert.Contains(problem.Errors["FileName"], message => message.Contains("not supported"));
    }

    [Fact]
    public async Task ProjectUpload_FileLargerThanTheConfiguredLimit_ReturnsBadRequest()
    {
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();
        var oversize = Encoding.UTF8.GetBytes(new string('a', 100_000));

        var response = await client.PostAsync(
            $"api/documents/projects/{projectId}",
            Upload("notes.txt", oversize, "text/plain"),
            TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(64 * 1024, problem.Extensions["maxBytes"].GetInt64());
    }

    [Fact]
    public async Task ProjectUpload_ValidTextFile_RunsTheWholePipelineAndPersistsChunks()
    {
        // Runs against real SQL Server 2025, so this is what proves the vector(1536) column accepts
        // what the ingestion pipeline writes.
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostProjectUploadAsync(client, projectId, TextUpload());
        var status = await WaitForTerminalStateAsync(client, job.Id);

        Assert.Equal("Succeeded", status.State);
        Assert.Equal(nameof(JobStatus.Processed), status.Status);
        Assert.Equal(100, status.Progress);
        Assert.NotNull(status.DocumentId);

        var document = await _fixture.FindProjectDocumentAsync(status.DocumentId.Value, TestContext.Current.CancellationToken);
        Assert.NotNull(document);
        Assert.Equal("notes.txt", document.Name);
        Assert.Equal(".txt", document.Extension);
        Assert.Equal(projectId, document.ProjectId);
        Assert.Equal(TestUsers.RegularUserId, document.UserId);

        var chunks = await _fixture.FindProjectDocumentChunksAsync(status.DocumentId.Value, TestContext.Current.CancellationToken);
        Assert.True(chunks.Count > 1);
        Assert.Equal([.. Enumerable.Range(0, chunks.Count)], chunks.Select(c => c.Index));
        Assert.All(chunks, chunk => Assert.True(chunk.TokenCount is > 0 and <= 64));
        Assert.All(chunks, chunk => Assert.False(string.IsNullOrWhiteSpace(chunk.Text)));
    }

    [Fact]
    public async Task ProjectUpload_ValidTextFile_StoresTheBlobUnderAProjectScopedKey()
    {
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostProjectUploadAsync(client, projectId, TextUpload());
        var status = await WaitForTerminalStateAsync(client, job.Id);

        Assert.Equal("Succeeded", status.State);
        Assert.Contains(
            $"{TestUsers.RegularUserId}/projects/{projectId}/{status.DocumentId}.txt",
            _fixture.Factory.BlobStorage.UploadedPaths);
    }

    [Fact]
    public async Task ProjectUpload_ValidTextFile_IsListedByTheProjectDocumentsEndpoint()
    {
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostProjectUploadAsync(client, projectId, TextUpload());
        var status = await WaitForTerminalStateAsync(client, job.Id);
        Assert.Equal("Succeeded", status.State);

        var documents = await client.GetFromJsonAsync<List<ProjectDocumentDto>>(
            $"api/projects/{projectId}/documents", TestContext.Current.CancellationToken);

        Assert.NotNull(documents);
        var only = Assert.Single(documents);
        Assert.Equal(status.DocumentId, only.Id);
        Assert.Equal("notes.txt", only.Name);
    }

    [Fact]
    public async Task ProjectUpload_ThenDeleteTheDocument_DeactivatesItAndItsChunks()
    {
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostProjectUploadAsync(client, projectId, TextUpload());
        var status = await WaitForTerminalStateAsync(client, job.Id);
        Assert.Equal("Succeeded", status.State);

        var response = await client.DeleteAsync(
            $"api/projects/{projectId}/documents/{status.DocumentId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var document = await _fixture.FindProjectDocumentAsync(status.DocumentId!.Value, TestContext.Current.CancellationToken);
        Assert.NotNull(document);
        Assert.NotNull(document.DateDeactivated);

        var chunks = await _fixture.FindProjectDocumentChunksAsync(status.DocumentId.Value, TestContext.Current.CancellationToken);
        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.NotNull(chunk.DateDeactivated));
    }

    [Fact]
    public async Task ProjectUpload_ThenDeleteTheProject_DeactivatesTheDocumentAndItsChunks()
    {
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostProjectUploadAsync(client, projectId, TextUpload());
        var status = await WaitForTerminalStateAsync(client, job.Id);
        Assert.Equal("Succeeded", status.State);

        var response = await client.DeleteAsync($"api/projects/{projectId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var document = await _fixture.FindProjectDocumentAsync(status.DocumentId!.Value, TestContext.Current.CancellationToken);
        Assert.NotNull(document);
        Assert.NotNull(document.DateDeactivated);
        Assert.All(
            await _fixture.FindProjectDocumentChunksAsync(status.DocumentId.Value, TestContext.Current.CancellationToken),
            chunk => Assert.NotNull(chunk.DateDeactivated));
    }

    [Fact]
    public async Task ConversationUpload_InAProjectConversation_DoesNotBecomeAProjectDocument()
    {
        // The rule that separates the two: uploading into a conversation adds the file to that
        // conversation only, never to the project it belongs to.
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        var conversationId = await _fixture.AddConversationAsync(projectId: projectId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostUploadAsync(client, conversationId, TextUpload());
        var status = await WaitForTerminalStateAsync(client, job.Id);
        Assert.Equal("Succeeded", status.State);

        var documents = await client.GetFromJsonAsync<List<ProjectDocumentDto>>(
            $"api/projects/{projectId}/documents", TestContext.Current.CancellationToken);

        Assert.NotNull(documents);
        Assert.Empty(documents);
    }

    [Fact]
    public async Task ProjectUpload_SameFileNameTwice_BothSucceedWithoutColliding()
    {
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var first = await WaitForTerminalStateAsync(client, (await PostProjectUploadAsync(client, projectId, TextUpload())).Id);
        var second = await WaitForTerminalStateAsync(client, (await PostProjectUploadAsync(client, projectId, TextUpload())).Id);

        Assert.Equal("Succeeded", first.State);
        Assert.Equal("Succeeded", second.State);
        Assert.NotEqual(first.DocumentId, second.DocumentId);
    }

    [Fact]
    public async Task ProjectUpload_FileWithoutReadableText_FailsTheJobWithAnExplanation()
    {
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostProjectUploadAsync(client, projectId,
            Upload("blank.txt", Encoding.UTF8.GetBytes("   \n\n\t  "), "text/plain"));
        var status = await WaitForTerminalStateAsync(client, job.Id);

        Assert.Equal("Failed", status.State);
        Assert.NotNull(status.ErrorMessage);
        Assert.Contains("No readable text", status.ErrorMessage);
    }
    #endregion

    #region Downloads
    [Fact]
    public async Task Download_Anonymous_ReturnsUnauthorized()
    {
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"api/documents/conversations/{Guid.NewGuid()}/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal("Unauthorized", problem.Title);
    }

    [Fact]
    public async Task Download_OwnedConversationDocument_ReturnsALinkToTheStoredBlob()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostUploadAsync(client, conversationId, TextUpload());
        var status = await WaitForTerminalStateAsync(client, job.Id);
        Assert.Equal("Succeeded", status.State);

        var download = await client.GetFromJsonAsync<DocumentDownloadDto>(
            $"api/documents/conversations/{conversationId}/{status.DocumentId}", TestContext.Current.CancellationToken);

        Assert.NotNull(download);
        Assert.Equal("notes.txt", download.FileName);
        Assert.True(download.ExpiresAt > DateTimeOffset.UtcNow);
        // The same key ingestion wrote, so the link points at this document's blob and not another's.
        Assert.Contains($"{TestUsers.RegularUserId}/{conversationId}/{status.DocumentId}.txt", download.DownloadUrl);
    }

    [Fact]
    public async Task Download_OwnedConversationDocument_RenamesTheBlobBackToTheUploadedFileName()
    {
        // The blob is stored under a guid key with no content headers of its own, so the link has to
        // carry the Content-Disposition and Content-Type overrides or the browser saves the guid.
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostUploadAsync(client, conversationId, TextUpload());
        var status = await WaitForTerminalStateAsync(client, job.Id);
        Assert.Equal("Succeeded", status.State);

        var download = await client.GetFromJsonAsync<DocumentDownloadDto>(
            $"api/documents/conversations/{conversationId}/{status.DocumentId}", TestContext.Current.CancellationToken);

        Assert.NotNull(download);
        var query = HttpUtility.ParseQueryString(new Uri(download.DownloadUrl).Query);
        Assert.Equal("attachment; filename=\"notes.txt\"; filename*=UTF-8''notes.txt", query["rscd"]);
        Assert.Equal("text/plain", query["rsct"]);
    }

    [Fact]
    public async Task Download_ConversationDocumentBelongingToAnotherUser_ReturnsNotFound()
    {
        // The link needs no credentials of its own, so handing one to the wrong caller would give away
        // the file outright. 404 rather than 403, matching how the job status endpoint answers.
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var owner = _fixture.Factory.CreateUserClient();
        using var otherUser = _fixture.Factory.CreateAdminClient();

        var job = await PostUploadAsync(owner, conversationId, TextUpload());
        var status = await WaitForTerminalStateAsync(owner, job.Id);
        Assert.Equal("Succeeded", status.State);

        var response = await otherUser.GetAsync(
            $"api/documents/conversations/{conversationId}/{status.DocumentId}", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
        Assert.Equal($"Document with id '{status.DocumentId}' was not found.", problem.Detail);

        // The owner still gets a link, so the 404 is authorization rather than the document having gone.
        var ownerResponse = await owner.GetAsync(
            $"api/documents/conversations/{conversationId}/{status.DocumentId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
    }

    [Fact]
    public async Task Download_UnknownConversationDocument_ReturnsNotFound()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync(
            $"api/documents/conversations/{conversationId}/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_OwnedProjectDocument_ReturnsALinkToTheStoredBlob()
    {
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostProjectUploadAsync(client, projectId, TextUpload());
        var status = await WaitForTerminalStateAsync(client, job.Id);
        Assert.Equal("Succeeded", status.State);

        var download = await client.GetFromJsonAsync<DocumentDownloadDto>(
            $"api/documents/projects/{projectId}/{status.DocumentId}", TestContext.Current.CancellationToken);

        Assert.NotNull(download);
        Assert.Equal("notes.txt", download.FileName);
        Assert.True(download.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.Contains($"{TestUsers.RegularUserId}/projects/{projectId}/{status.DocumentId}.txt", download.DownloadUrl);
    }

    [Fact]
    public async Task Download_ProjectDocumentBelongingToAnotherUser_ReturnsNotFound()
    {
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var owner = _fixture.Factory.CreateUserClient();
        using var otherUser = _fixture.Factory.CreateAdminClient();

        var job = await PostProjectUploadAsync(owner, projectId, TextUpload());
        var status = await WaitForTerminalStateAsync(owner, job.Id);
        Assert.Equal("Succeeded", status.State);

        var response = await otherUser.GetAsync(
            $"api/documents/projects/{projectId}/{status.DocumentId}", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_DeletedProjectDocument_ReturnsNotFound()
    {
        // Soft delete has to close the download route too, or a deleted document stays retrievable by id.
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostProjectUploadAsync(client, projectId, TextUpload());
        var status = await WaitForTerminalStateAsync(client, job.Id);
        Assert.Equal("Succeeded", status.State);

        var deleteResponse = await client.DeleteAsync(
            $"api/projects/{projectId}/documents/{status.DocumentId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var response = await client.GetAsync(
            $"api/documents/projects/{projectId}/{status.DocumentId}", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }
    #endregion

    #region Listing
    [Fact]
    public async Task GetDocuments_Anonymous_ReturnsUnauthorized()
    {
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.GetAsync("api/documents", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal("Unauthorized", problem.Title);
    }

    [Fact]
    public async Task GetDocuments_DocumentsAcrossConversations_ReturnsThemWithConversationIdAndName()
    {
        var planningId = await _fixture.AddConversationAsync(name: "Planning", cancellationToken: TestContext.Current.CancellationToken);
        var researchId = await _fixture.AddConversationAsync(name: "Research", cancellationToken: TestContext.Current.CancellationToken);
        var budgetId = await _fixture.AddConversationDocumentAsync(
            planningId, "budget.pdf", [new SeedChunk(0, "chunk", SeedChunk.UnitVector(0))],
            cancellationToken: TestContext.Current.CancellationToken);
        var findingsId = await _fixture.AddConversationDocumentAsync(
            researchId, "findings.pdf", [new SeedChunk(0, "chunk", SeedChunk.UnitVector(1))],
            cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var page = await client.GetFromJsonAsync<PaginatedResponseDto<UserDocumentDto>>(
            "api/documents", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(2, page.TotalCount);
        var budget = Assert.Single(page.Items, x => x.Id == budgetId);
        Assert.Equal(planningId, budget.ConversationId);
        Assert.Equal("Planning", budget.ConversationName);
        var findings = Assert.Single(page.Items, x => x.Id == findingsId);
        Assert.Equal(researchId, findings.ConversationId);
        Assert.Equal("Research", findings.ConversationName);
    }

    [Fact]
    public async Task GetDocuments_AnotherUsersDocuments_AreExcluded()
    {
        var theirConversation = await _fixture.AddConversationAsync(TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationDocumentAsync(
            theirConversation, "theirs.pdf", [new SeedChunk(0, "chunk", SeedChunk.UnitVector(0))],
            userId: TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        var myConversation = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        var mineId = await _fixture.AddConversationDocumentAsync(
            myConversation, "mine.pdf", [new SeedChunk(0, "chunk", SeedChunk.UnitVector(1))],
            cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var page = await client.GetFromJsonAsync<PaginatedResponseDto<UserDocumentDto>>(
            "api/documents", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(mineId, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task GetDocuments_NoDocuments_ReturnsAnEmptyPage()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var page = await client.GetFromJsonAsync<PaginatedResponseDto<UserDocumentDto>>(
            "api/documents", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task GetDocuments_TakeOfZero_IsClampedRatherThanReturningServerError()
    {
        // Straight off the query string: unclamped, this would divide by zero computing CurrentPage.
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationDocumentAsync(
            conversationId, "paging.pdf", [new SeedChunk(0, "chunk", SeedChunk.UnitVector(0))],
            cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync("api/documents?take=0&skip=-1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PaginatedResponseDto<UserDocumentDto>>(TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.InRange(page.PageSize, 1, 100);
    }

    // End to end through the real pipeline: what ingestion persists is what both listings serve.
    [Fact]
    public async Task GetDocuments_AfterAnUpload_ListsTheIngestedDocument()
    {
        var conversationId = await _fixture.AddConversationAsync(name: "Ingestion Conversation", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostUploadAsync(client, conversationId, TextUpload());
        var status = await WaitForTerminalStateAsync(client, job.Id);
        Assert.Equal("Succeeded", status.State);

        var conversationDocuments = await client.GetFromJsonAsync<List<ConversationDocumentDto>>(
            $"api/conversations/{conversationId}/documents", TestContext.Current.CancellationToken);
        Assert.NotNull(conversationDocuments);
        var listed = Assert.Single(conversationDocuments);
        Assert.Equal(status.DocumentId, listed.Id);
        Assert.Equal("notes.txt", listed.Name);

        var page = await client.GetFromJsonAsync<PaginatedResponseDto<UserDocumentDto>>(
            "api/documents", TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        var userDocument = Assert.Single(page.Items, x => x.Id == status.DocumentId);
        Assert.Equal(conversationId, userDocument.ConversationId);
        Assert.Equal("Ingestion Conversation", userDocument.ConversationName);
    }

    [Fact]
    public async Task GetDocuments_UploadedAndGeneratedDocuments_ReturnsBothCarryingTheirOrigin()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        var uploadedId = await _fixture.AddConversationDocumentAsync(
            conversationId, "brief.pdf", [new SeedChunk(0, "chunk", SeedChunk.UnitVector(0))],
            cancellationToken: TestContext.Current.CancellationToken);
        var generatedId = await _fixture.AddConversationDocumentAsync(
            conversationId, "summary.docx", [],
            type: ConversationDocumentTypes.Generated, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var page = await client.GetFromJsonAsync<PaginatedResponseDto<UserDocumentDto>>(
            "api/documents", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(ConversationDocumentTypes.Uploaded, Assert.Single(page.Items, x => x.Id == uploadedId).Type);
        Assert.Equal(ConversationDocumentTypes.Generated, Assert.Single(page.Items, x => x.Id == generatedId).Type);
    }

    [Fact]
    public async Task GetDocuments_Serialized_CarriesTheOriginAsItsMemberName()
    {
        // The TypeScript mirror in the client is written against these exact tokens, and the
        // converter that produces them is applied per property rather than to the serializer.
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationDocumentAsync(
            conversationId, "summary.docx", [],
            type: ConversationDocumentTypes.Generated, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var payload = await client.GetStringAsync("api/documents?type=generated", TestContext.Current.CancellationToken);

        Assert.Contains("\"type\":\"Generated\"", payload);
    }

    [Fact]
    public async Task GetDocuments_AnotherUsersGeneratedDocument_IsExcluded()
    {
        var theirConversation = await _fixture.AddConversationAsync(
            TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationDocumentAsync(
            theirConversation, "theirs.docx", [], userId: TestUsers.AdminUserId,
            type: ConversationDocumentTypes.Generated, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var page = await client.GetFromJsonAsync<PaginatedResponseDto<UserDocumentDto>>(
            "api/documents", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task GetDocuments_GeneratedRequested_ReturnsOnlyGeneratedDocuments()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationDocumentAsync(
            conversationId, "brief.pdf", [new SeedChunk(0, "chunk", SeedChunk.UnitVector(0))],
            cancellationToken: TestContext.Current.CancellationToken);
        var generatedId = await _fixture.AddConversationDocumentAsync(
            conversationId, "summary.docx", [],
            type: ConversationDocumentTypes.Generated, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var page = await client.GetFromJsonAsync<PaginatedResponseDto<UserDocumentDto>>(
            "api/documents?type=generated", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(generatedId, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task GetDocuments_UnsupportedType_ReturnsValidationProblemNamingTheParameter()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync("api/documents?type=assistant", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadValidationAsync(response);
        Assert.Equal("/problems/validation-error", problem.Type);
        Assert.Single(Assert.Contains("type", problem.Errors));
    }

    [Fact]
    public async Task GetConversationDocuments_GeneratedDocument_IsNotListedAmongTheConversationsUploads()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        var uploadedId = await _fixture.AddConversationDocumentAsync(
            conversationId, "brief.pdf", [new SeedChunk(0, "chunk", SeedChunk.UnitVector(0))],
            cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationDocumentAsync(
            conversationId, "summary.docx", [],
            type: ConversationDocumentTypes.Generated, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var documents = await client.GetFromJsonAsync<List<ConversationDocumentDto>>(
            $"api/conversations/{conversationId}/documents", TestContext.Current.CancellationToken);

        Assert.NotNull(documents);
        var listed = Assert.Single(documents);
        Assert.Equal(uploadedId, listed.Id);
        Assert.Equal(ConversationDocumentTypes.Uploaded, listed.Type);
    }
    #endregion

    #region Status
    [Fact]
    public async Task GetJobStatus_UnknownJob_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync($"api/documents/upload-status/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("Upload job not found.", problem.Detail);
    }

    [Fact]
    public async Task GetJobStatus_JobQueuedByAnotherUser_ReturnsNotFound()
    {
        // The snapshot carries the created document id and the failure detail, so it must not be readable
        // by anyone but the uploader.
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var owner = _fixture.Factory.CreateUserClient();
        using var otherUser = _fixture.Factory.CreateAdminClient();

        var job = await PostUploadAsync(owner, conversationId, TextUpload());

        var response = await otherUser.GetAsync($"api/documents/upload-status/{job.Id}", TestContext.Current.CancellationToken);

        // Identical to the unknown-job detail, so the response cannot confirm the id exists.
        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("Upload job not found.", problem.Detail);

        // The owner still sees it, so the 404 is authorization rather than the job having gone missing.
        var ownerResponse = await owner.GetAsync($"api/documents/upload-status/{job.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);

        // Not an assertion — a barrier, and the only test in this file that needs one stated rather
        // than implied. Everything above is answered while the job is still ingesting, so without
        // this the test returns with background work outstanding and the insert lands inside the
        // *next* test's ResetConversationsAndDocumentsAsync: after it has emptied ConversationDocument
        // and before it deletes Conversation, which fails that unrelated test on a foreign key.
        await WaitForTerminalStateAsync(owner, job.Id);
    }

    [Fact]
    public async Task GetJobStatus_QueuedJob_ExposesTheLegacyFieldsOlderClientsRelyOn()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var job = await PostUploadAsync(client, conversationId, TextUpload());
        var status = await WaitForTerminalStateAsync(client, job.Id);

        Assert.Equal(job.Id, status.Id);
        Assert.Contains(status.State, new[] { "Enqueued", "Processing", "Succeeded", "Failed" });
        Assert.False(string.IsNullOrWhiteSpace(status.Status));
        Assert.InRange(status.Progress, 0, 100);
        Assert.NotEqual(default, status.UpdatedAt);
    }
    #endregion

    #region Supported formats
    [Fact]
    public async Task GetFileExtensions_ReturnsEveryFormatTheExtractorsCanRead()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var extensions = await client.GetFromJsonAsync<List<string>>("api/documents/file-extensions", TestContext.Current.CancellationToken);

        Assert.NotNull(extensions);
        Assert.Equal<string[]>([".csv", ".doc", ".docx", ".md", ".pdf", ".pptx", ".txt", ".xlsx"], [.. extensions.Order()]);
    }
    #endregion
}
