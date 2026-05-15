using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Enterprise.Gpt.Service;

namespace Enterprise.Gpt.Api.Controllers
{
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class ModelsController(IModelService modelService) : ControllerBase
    {
        private readonly IModelService _modelService = modelService;

        [HttpGet]
        public async Task<IActionResult> GetModelsAsync(CancellationToken cancellationToken)
        {
            var response = await _modelService.GetModelsAsync(cancellationToken);
            return Ok(response);
        }
    }
}
