using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Enterprise.Gpt.Dto.Actions.Chat;
using Enterprise.Gpt.Service;

namespace Enterprise.Gpt.Api.Controllers
{
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class ConversationsController(IConversationService conversationService) : ControllerBase
    {
        private readonly IConversationService _conversationService = conversationService;

        [HttpGet("{id}")]
        public async Task<IActionResult> GetConversationAsync(Guid id, CancellationToken cancellationToken)
        {
            var response = await _conversationService.GetConversationAsync(id, cancellationToken);
            return Ok(response);
        }

        [HttpPost]
        public async Task<IActionResult> CreateConversationAsync(CreateConversationActionDto request, CancellationToken cancellationToken)
        {
            var response = await _conversationService.CreateConversationAsync(request, cancellationToken);
            return Ok(response);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeactivateConversationAsync(Guid id, CancellationToken cancellationToken)
        {
            await _conversationService.DeactivateConversationAsync(id, cancellationToken);
            return NoContent();
        }

        [HttpDelete("bulk")]
        public async Task<IActionResult> DeactivateConversationsBulkAsync(DeactivateConversationsBulkActionDto request, CancellationToken cancellationToken)
        {
            await _conversationService.DeactivateConversationsBulkAsync(request, cancellationToken);
            return NoContent();
        }

        [HttpGet("search")]
        public async Task<IActionResult> SearchConversationsAsync([FromQuery] string name, [FromQuery] int skip = 0, [FromQuery] int take = 20, CancellationToken cancellationToken = default)
        {
            var response = await _conversationService.SearchConversationsAsync(name, skip, take, cancellationToken);
            return Ok(response);
        }

        [HttpPut]
        public async Task<IActionResult> UpdateConversationAsync(UpdateConversationActionDto request, CancellationToken cancellationToken)
        {
            var response = await _conversationService.UpdateConversationAsync(request, cancellationToken);
            return Ok(response);
        }

        [HttpPost("{id}/stream")]
        public async Task StreamConversationAsync(Guid id, CreateConversationStreamActionDto request, CancellationToken cancellationToken)
        {
            // Headers are set on the first fragment rather than up front: setting them eagerly left
            // an error response (a 409 for a conversation that is already streaming, say) carrying
            // streaming headers alongside its JSON body. Connection is omitted deliberately — it is
            // a hop-by-hop header Kestrel owns, and it is invalid under HTTP/2.
            var headersSet = false;

            await foreach (var message in _conversationService.StreamConversationAsync(id, request, cancellationToken))
            {
                if (!headersSet)
                {
                    SetStreamingHeaders();
                    headersSet = true;
                }

                await Response.WriteAsync($"{message}", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }

            // A model that produced nothing still owes the client a well-formed empty stream
            // rather than a bare 200 the reader cannot interpret.
            if (!headersSet)
            {
                SetStreamingHeaders();
            }
        }

        /// <summary>
        /// Applies the response headers for a server-sent event stream.
        /// </summary>
        private void SetStreamingHeaders()
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
        }

        [HttpGet("{id}/messages")]
        public async Task<IActionResult> GetConversationMessagesAsync(Guid id, CancellationToken cancellationToken)
        {
            var response = await _conversationService.GetConversationMessagesAsync(id, cancellationToken);
            return Ok(response);
        }
    }
}
