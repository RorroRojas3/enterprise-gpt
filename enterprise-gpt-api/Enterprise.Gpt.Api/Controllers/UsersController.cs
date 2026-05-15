using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Enterprise.Gpt.Dto.Actions.User;
using Enterprise.Gpt.Service;

namespace Enterprise.Gpt.Api.Controllers
{
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class UsersController(IUserService userService) : ControllerBase
    {
        private readonly IUserService _userService = userService;

        [HttpPost("me")]
        public async Task<IActionResult> CreateUser(CancellationToken cancellationToken)
        {
            var (user, created) = await _userService.CreateUserAsync(cancellationToken);
            return created
                ? StatusCode(StatusCodes.Status201Created, user)
                : Ok(user);
        }

        [HttpPut]
        public async Task<IActionResult> UpdateUserAsync(UpdateUserActionDto request, CancellationToken cancellationToken)
        {
            var updatedUser = await _userService.UpdateUserAsync(request, cancellationToken);
            return Ok(updatedUser);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeactivateUserAsync(Guid id, CancellationToken cancellationToken)
        {
            await _userService.DeactivateUserAsync(id, cancellationToken);
            return NoContent();
        }
    }
}
