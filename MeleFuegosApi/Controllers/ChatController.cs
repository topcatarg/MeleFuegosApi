using Microsoft.AspNetCore.Mvc;
using MeleFuegosApi.Models;
using MeleFuegosApi.Services;

namespace MeleFuegosApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly RelevanceService _relevanceService;
    private readonly ILogger<ChatController> _logger;

    public ChatController(RelevanceService relevanceService, ILogger<ChatController> logger)
    {
        _relevanceService = relevanceService;
        _logger = logger;
    }

    [HttpPost("message")]
    public async Task<ActionResult<ChatResponse>> SendMessage([FromBody] ChatRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return BadRequest(new { error = "El mensaje no puede estar vacío" });
            }

            var response = await _relevanceService.SendMessageAsync(
                request.Message,
                request.RestaurantCode,
                request.ConversationId,
                request.UserId
            );

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando mensaje");
            return StatusCode(500, new { error = "Error procesando el mensaje" });
        }
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "API activa",
            timestamp = DateTime.UtcNow,
            service = "Mele Fuegos Chat API"
        });
    }
}