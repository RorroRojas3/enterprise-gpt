using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RR.AI_Chat.Common.Extensions;
using RR.AI_Chat.Dto;
using RR.AI_Chat.Dto.Enums;
using RR.AI_Chat.Service;
using RR.AI_Chat.Service.BackgroundJobs;

namespace RR.AI_Chat.Api.Controllers
{
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class DocumentsController(IDocumentService service,
        IBackgroundJobQueue backgroundJobQueue,
        IJobStatusStore jobStatusStore,
        ITokenService tokenService) : ControllerBase
    {
        private readonly IDocumentService _service = service;
        private readonly IBackgroundJobQueue _backgroundJobQueue = backgroundJobQueue;
        private readonly IJobStatusStore _jobStatusStore = jobStatusStore;
        private readonly ITokenService _tokenService = tokenService;

        [HttpPost("conversations/{conversationId}")]
        public async Task<IActionResult> CreateConversationDocumentAsync([FromRoute] Guid conversationId, IFormFile file, CancellationToken cancellationToken)
        {
            if (file is null || file.Length == 0)
            {
                return BadRequest("File is required and cannot be empty.");
            }

            var fileData = new FileDto
            {
                FileName = file.FileName,
                ContentType = file.ContentType,
                Length = file.Length,
                Content = await ReadFileAsync(file, cancellationToken)
            };

            var jobId = Guid.NewGuid().ToString();
            var oid = _tokenService.GetOid();
            _jobStatusStore.Register(jobId);

            try
            {
                await _backgroundJobQueue.EnqueueAsync(new JobWorkItem(
                    jobId,
                    async (sp, ct) =>
                    {
                        var documentService = sp.GetRequiredService<IDocumentService>();
                        await documentService.CreateConversationDocumentAsync(jobId, fileData, oid, conversationId, ct);
                    }), cancellationToken);
            }
            catch
            {
                // Register succeeded but enqueue did not — the snapshot would otherwise sit in
                // Queued forever (JobStatusStore only evicts terminal states). Mark it Failed so
                // retention can reclaim it and any racing poll sees a deterministic terminal state.
                _jobStatusStore.Fail(jobId, "Failed to enqueue background job.");
                throw;
            }

            return Accepted(new JobDto { Id = jobId });
        }

        [HttpGet("upload-status/{jobId}")]
        public IActionResult GetJobStatus(string jobId)
        {
            var snapshot = _jobStatusStore.Get(jobId);
            if (snapshot is null)
            {
                return NotFound();
            }

            return Ok(new JobStatusDto
            {
                Id = jobId,
                State = MapState(snapshot.Status),
                Status = snapshot.Status.ToString(),
                Progress = snapshot.Progress
            });
        }

        [HttpGet("conversations/{conversationId}/histories")]
        public async Task<IActionResult> GenerateConversationHistoryFileAsync(
            Guid conversationId,
            [FromQuery] DocumentFormats documentFormat,
            CancellationToken cancellationToken)
        {
            var dto = await _service.GenerateConversationHistoryAsync(conversationId, documentFormat, cancellationToken);
            if (dto is null)
            {
                return NotFound();
            }

            return File(dto.Content, dto.ContentType, dto.FileName);
        }

        [HttpGet("file-extensions")]
        public IActionResult GetFileExtensions()
        {
            var fileExtensions = Enum.GetValues<FileExtensions>()
                .Select(e => e.GetDescription())
                .ToList();

            return Ok(fileExtensions);
        }

        private static async Task<byte[]> ReadFileAsync(IFormFile file, CancellationToken cancellationToken)
        {
            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream, cancellationToken);
            return memoryStream.ToArray();
        }

        // Mirrors the Hangfire job-state vocabulary the frontend already understands.
        private static string MapState(JobStatus status) => status switch
        {
            JobStatus.Queued => "Enqueued",
            JobStatus.Uploading or JobStatus.Extracting or JobStatus.Embedding => "Processing",
            JobStatus.Processed => "Succeeded",
            JobStatus.Failed => "Failed",
            _ => "Processing",
        };
    }
}
