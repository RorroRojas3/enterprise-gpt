using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RR.AI_Chat.Service;

namespace RR.AI_Chat.Api.Controllers
{
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class McpsController(IMcpServerService mcpServerService) : ControllerBase
    {
        private readonly IMcpServerService _mcpServerService = mcpServerService;

        [HttpGet]
        public IActionResult GetMcpServers()
        {
            var mcpServers = _mcpServerService.GetMcpServers();
            return Ok(mcpServers);
        }
    }
}
