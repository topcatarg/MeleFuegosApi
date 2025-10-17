namespace MeleFuegosApi.Models;

public class ChatRequest
{
    public string Message { get; set; } = string.Empty;
    public string? ConversationId { get; set; }  // Solo para mensajes subsiguientes
    public string? UserId { get; set; }
    public string RestaurantCode { get; set; } = "RES-010"; // Default Mele Fuegos
}

public class ChatResponse
{
    public string Message { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;  // Viene de Relevance/Make
    public string UserId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool IsFirstMessage { get; set; }
}