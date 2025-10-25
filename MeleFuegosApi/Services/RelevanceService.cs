using System.Text;
using System.Text.Json;
using MeleFuegosApi.Models;

namespace MeleFuegosApi.Services;

public class RelevanceService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RelevanceService> _logger;

    private const string MakeWebhookUrl = "https://hook.us2.make.com/fffbdrcbztjwqgvfahq6ksbvzumi996t";
    private const string RelevanceTriggerUrl = "https://api-bcbe5a.stack.tryrelevance.com/latest/agents/trigger";
    private const string RelevanceKnowledgeUrl = "https://api-bcbe5a.stack.tryrelevance.com/latest/knowledge/list";
    private const string AgentId = "0a764793-61bc-463c-8b15-563207def72e";

    public RelevanceService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<RelevanceService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ChatResponse> SendMessageAsync(
        string message,
        string restaurantCode,
        string? conversationId = null,
        string? userId = null)
    {
        try
        {
            // Generar userId si no existe
            userId ??= GenerateUserId();

            // Determinar si es el primer mensaje (no hay conversationId)
            bool isFirstMessage = string.IsNullOrEmpty(conversationId);

            string assistantMessage;
            string returnedConversationId;

            if (isFirstMessage)
            {
                _logger.LogInformation("Primera llamada - usando Make webhook");
                var makeResponse = await CallMakeWebhook(message, restaurantCode, userId);
                assistantMessage = makeResponse.message;
                returnedConversationId = makeResponse.conversationId;
            }
            else
            {
                _logger.LogInformation("Llamada subsiguiente - usando Relevance directo");
                var relevanceResponse = await CallRelevanceDirectly(message, conversationId!);
                assistantMessage = relevanceResponse.message;
                returnedConversationId = relevanceResponse.conversationId;
            }

            return new ChatResponse
            {
                Message = assistantMessage,
                ConversationId = returnedConversationId,
                UserId = userId,
                Timestamp = DateTime.UtcNow,
                IsFirstMessage = isFirstMessage
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando mensaje");
            throw;
        }
    }

    private async Task<(string message, string conversationId)> CallMakeWebhook(
        string message,
        string restaurantCode,
        string userId)
    {
        try
        {
            // Make espera FormData
            var formData = new MultipartFormDataContent
            {
                { new StringContent(message), "mensaje" },
                { new StringContent(restaurantCode), "codigo" },
                { new StringContent(userId), "user_id" }
            };

            _logger.LogInformation("Enviando a Make: mensaje={Message}, codigo={Code}, user_id={UserId}",
                message, restaurantCode, userId);

            var response = await _httpClient.PostAsync(MakeWebhookUrl, formData);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("Respuesta de Make: {Response}", responseBody);

            // Parsear la respuesta de Make para extraer mensaje y conversationId
            return ParseMakeResponse(responseBody);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error llamando a Make");
            throw;
        }
    }

    private async Task<(string message, string conversationId)> CallRelevanceDirectly(
        string message,
        string conversationId)
    {
        try
        {
            var apiKey = _configuration["Relevance:ApiKey"];

            var payload = new
            {
                message = new
                {
                    role = "user",
                    content = message
                },
                agent_id = AgentId,
                conversation_id = conversationId
            };

            var request = new HttpRequestMessage(HttpMethod.Post, RelevanceTriggerUrl)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json"
                )
            };

            // Agregar Authorization header de Relevance (formato custom)
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", apiKey);
            }

            _logger.LogInformation("Enviando a Relevance: {Message}", message);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("Respuesta de Relevance: {Response}", responseBody);

            var relevanceResponse = JsonSerializer.Deserialize<JsonElement>(responseBody);
            return ExtractRelevanceMessage(relevanceResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error llamando a Relevance");
            throw;
        }
    }

    private (string message, string conversationId) ParseMakeResponse(string responseBody)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(responseBody);

            // Extraer el mensaje - PRIMERO intentar con "respuesta" que es lo que devuelve Make
            string message = "Sin respuesta";
            if (json.TryGetProperty("respuesta", out var respuestaProp))
                message = respuestaProp.GetString() ?? message;
            else if (json.TryGetProperty("message", out var msgProp))
                message = msgProp.GetString() ?? message;
            else if (json.TryGetProperty("response", out var respProp))
                message = respProp.GetString() ?? message;
            else if (json.TryGetProperty("text", out var textProp))
                message = textProp.GetString() ?? message;
            else if (json.TryGetProperty("answer", out var answerProp))
                message = answerProp.GetString() ?? message;

            // Extraer el conversationId
            string conversationId = "";
            if (json.TryGetProperty("conversation_id", out var convProp))
                conversationId = convProp.GetString() ?? "";
            else if (json.TryGetProperty("conversationId", out var convProp2))
                conversationId = convProp2.GetString() ?? "";

            _logger.LogInformation("Parseado Make - Mensaje: {Msg}, ConversationId: {ConvId}",
                message, conversationId);

            return (message, conversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parseando respuesta de Make");
            // Devolver el texto completo si no se puede parsear
            return (responseBody, "");
        }
    }

    private (string message, string conversationId) ExtractRelevanceMessage(JsonElement response)
    {
        try
        {
            string message = "Sin respuesta";
            string conversationId = "";

            // Extraer mensaje
            if (response.TryGetProperty("output", out var output))
            {
                if (output.TryGetProperty("answer", out var answer))
                    message = answer.GetString() ?? message;
                else
                    message = output.GetString() ?? message;
            }
            else if (response.TryGetProperty("message", out var msgProp))
                message = msgProp.GetString() ?? message;
            else if (response.TryGetProperty("response", out var resp))
                message = resp.GetString() ?? message;

            // Extraer conversationId
            if (response.TryGetProperty("conversation_id", out var convProp))
                conversationId = convProp.GetString() ?? "";
            else if (response.TryGetProperty("conversationId", out var convProp2))
                conversationId = convProp2.GetString() ?? "";

            return (message, conversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extrayendo mensaje de Relevance");
            return ("Error procesando respuesta", "");
        }
    }

    private string GenerateUserId()
    {
        // Generar un ID similar al de Voiceflow (24 caracteres alfanuméricos)
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random();
        return new string(Enumerable.Repeat(chars, 24)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }
}