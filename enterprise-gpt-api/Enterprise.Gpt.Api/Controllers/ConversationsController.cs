using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RR.AI_Chat.Dto.Actions.Chat;
using RR.AI_Chat.Service;

namespace RR.AI_Chat.Api.Controllers
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
            Response.Headers.Append("Content-Type", "text/event-stream");
            Response.Headers.Append("Cache-Control", "no-cache");
            Response.Headers.Append("Connection", "keep-alive");

            await foreach (var message in _conversationService.StreamConversationAsync(id, request, cancellationToken))
            {
                await Response.WriteAsync($"{message}", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
        }

        [HttpGet("{id}/messages")]
        public async Task<IActionResult> GetConversationMessagesAsync(Guid id, CancellationToken cancellationToken)
        {
            var response = await _conversationService.GetConversationMessagesAsync(id, cancellationToken);
            return Ok(response);
        }
    }
}
