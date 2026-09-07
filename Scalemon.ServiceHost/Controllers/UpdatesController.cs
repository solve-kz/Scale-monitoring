using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Scalemon.Common.Updates;

namespace Scalemon.ServiceHost.Controllers;

/// <summary>Административное управление обновлениями без передачи путей и команд ОС.</summary>
[ApiController]
[Route("api/updates")]
[Authorize(Roles = "Admin")]
public sealed class UpdatesController(IUpdaterClient client, IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await client.SendAsync(new("status"), ct));
    [HttpGet("token")]
    public IActionResult Token()
    { var token = antiforgery.GetAndStoreTokens(HttpContext); return Ok(new { token.RequestToken, token.HeaderName }); }
    [HttpPost]
    public async Task<IActionResult> Execute(UpdateRequest request, CancellationToken ct)
    {
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return BadRequest("Некорректный токен запроса."); }
        if (request.Command is not ("check" or "install" or "rollback" or "cancel" or "settings")) return BadRequest("Неизвестная команда.");
        return Ok(await client.SendAsync(request with { Actor = User.Identity?.Name }, ct));
    }
}
