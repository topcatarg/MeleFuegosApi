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

    private const int MaxPollingAttempts = 40; // 40 intentos x 500ms = 20 segundos
    private const int PollingDelayMs = 500;
    private const int InitialDelayBeforePollingMs = 1000; // 1 segundo antes de empezar a hacer polling

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
            userId ??= GenerateUserId();
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
            var formData = new MultipartFormDataContent
            {
                { new StringContent(message), "mensaje" },
                { new StringContent(restaurantCode), "codigo" },
                { new StringContent(userId), "user_id" }
            };

            _logger.LogInformation("Enviando a Make: mensaje={Message}, codigo={Code}, user_id={UserId}",
                message, restaurantCode, userId);

            var response = await _httpClient.PostAsync(MakeWebhookUrl, formData);
            var responseBody = await response.Content.ReadAsStringAsync();

            // LOG CRÍTICO: Ver la respuesta RAW completa
            _logger.LogInformation("=== RESPUESTA RAW DE MAKE ===");
            _logger.LogInformation(responseBody);
            _logger.LogInformation("=== FIN RESPUESTA RAW ===");

            // Log del status code
            _logger.LogInformation("Status Code: {StatusCode}", response.StatusCode);

            response.EnsureSuccessStatusCode();

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

            // PASO 1: Trigger el agente
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

            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", apiKey);
            }

            _logger.LogInformation("PASO 1: Triggering Relevance agent con mensaje: {Message}", message);

            var triggerResponse = await _httpClient.SendAsync(request);
            var triggerResponseBody = await triggerResponse.Content.ReadAsStringAsync();

            _logger.LogInformation("=== RESPUESTA RAW DE TRIGGER ===");
            _logger.LogInformation(triggerResponseBody);
            _logger.LogInformation("Status Code: {StatusCode}", triggerResponse.StatusCode);
            _logger.LogInformation("=== FIN RESPUESTA TRIGGER ===");

            // No hacer EnsureSuccessStatusCode aquí - manejar 409 específicamente
            if (triggerResponse.StatusCode == System.Net.HttpStatusCode.Conflict) // 409
            {
                _logger.LogWarning("Recibido 409 Conflict - Relevance está procesando otra request. Continuando con polling...");
            }
            else if (!triggerResponse.IsSuccessStatusCode)
            {
                _logger.LogError("Error triggering agent: {StatusCode} - {Body}",
                    triggerResponse.StatusCode, triggerResponseBody);
                triggerResponse.EnsureSuccessStatusCode();
            }

            // PASO 2: Obtener los IDs de mensajes existentes ANTES de esperar
            _logger.LogInformation("PASO 2: Capturando mensajes existentes antes del trigger...");
            var existingMessageIds = await GetExistingMessageIds(conversationId, apiKey);
            _logger.LogInformation("Encontrados {Count} mensajes existentes", existingMessageIds.Count);

            // PASO 3: Esperar 1 segundo antes de empezar el polling
            _logger.LogInformation("PASO 3: Esperando {DelayMs}ms antes de iniciar polling...", InitialDelayBeforePollingMs);
            await Task.Delay(InitialDelayBeforePollingMs);

            // PASO 4: Poll el knowledge endpoint hasta obtener respuesta NUEVA
            _logger.LogInformation("PASO 4: Iniciando polling al knowledge endpoint...");

            var knowledgeResponse = await PollKnowledgeEndpoint(conversationId, apiKey, existingMessageIds);

            if (knowledgeResponse == null)
            {
                _logger.LogWarning("Timeout esperando respuesta de Relevance después de {Seconds} segundos",
                    (MaxPollingAttempts * PollingDelayMs) / 1000);
                return ("Se perdió la conexión. Por favor, intenta de nuevo.", conversationId);
            }

            return ExtractRelevanceMessage(knowledgeResponse.Value, conversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error llamando a Relevance");
            throw;
        }
    }

    private async Task<HashSet<string>> GetExistingMessageIds(string conversationId, string apiKey)
    {
        var existingIds = new HashSet<string>();

        try
        {
            var payload = new
            {
                knowledge_set = conversationId,
                page_size = 20,
                sort = new[]
                {
                    new Dictionary<string, string>
                    {
                        { "insert_date_", "desc" }
                    }
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, RelevanceKnowledgeUrl)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json"
                )
            };

            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", apiKey);
            }

            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            response.EnsureSuccessStatusCode();

            var knowledgeData = JsonSerializer.Deserialize<JsonElement>(responseBody);

            if (knowledgeData.TryGetProperty("results", out var results) &&
                results.ValueKind == JsonValueKind.Array)
            {
                foreach (var result in results.EnumerateArray())
                {
                    // Capturar IDs de mensajes del agente
                    if (result.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("role", out var role) &&
                        role.GetString() == "agent")
                    {
                        if (result.TryGetProperty("document_id", out var docId))
                        {
                            var id = docId.GetString();
                            if (!string.IsNullOrEmpty(id))
                            {
                                existingIds.Add(id);
                                _logger.LogInformation("ID existente capturado: {Id}", id);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo IDs existentes (continuando sin filtro)");
        }

        return existingIds;
    }

    private async Task<JsonElement?> PollKnowledgeEndpoint(string conversationId, string apiKey, HashSet<string> existingMessageIds)
    {
        for (int attempt = 1; attempt <= MaxPollingAttempts; attempt++)
        {
            try
            {
                var payload = new
                {
                    knowledge_set = conversationId,
                    page_size = 20,
                    sort = new[]
                    {
                        new Dictionary<string, string>
                        {
                            { "insert_date_", "desc" }
                        }
                    }
                };

                var request = new HttpRequestMessage(HttpMethod.Post, RelevanceKnowledgeUrl)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(payload),
                        Encoding.UTF8,
                        "application/json"
                    )
                };

                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", apiKey);
                }

                _logger.LogInformation("Polling intento {Attempt}/{Max}", attempt, MaxPollingAttempts);

                var response = await _httpClient.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (attempt == 1 || attempt % 5 == 0) // Log cada 5 intentos para no saturar
                {
                    _logger.LogInformation("=== RESPUESTA RAW DE KNOWLEDGE (intento {Attempt}) ===", attempt);
                    _logger.LogInformation(responseBody);
                    _logger.LogInformation("=== FIN RESPUESTA KNOWLEDGE ===");
                }

                response.EnsureSuccessStatusCode();

                var knowledgeData = JsonSerializer.Deserialize<JsonElement>(responseBody);

                // Verificar si hay un mensaje NUEVO del agente (que no estaba antes)
                if (HasNewAgentMessage(knowledgeData, existingMessageIds))
                {
                    _logger.LogInformation("✅ Nuevo mensaje del agente recibido en intento {Attempt}", attempt);
                    return knowledgeData;
                }

                _logger.LogInformation("Sin nuevo mensaje, esperando {DelayMs}ms...",
                    PollingDelayMs);
                await Task.Delay(PollingDelayMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en intento {Attempt} de polling", attempt);

                if (attempt < MaxPollingAttempts)
                {
                    await Task.Delay(PollingDelayMs);
                }
            }
        }

        return null; // Timeout alcanzado
    }

    private bool HasNewAgentMessage(JsonElement knowledgeData, HashSet<string> existingMessageIds)
    {
        try
        {
            _logger.LogInformation("Verificando mensajes nuevos del agente...");

            if (knowledgeData.ValueKind == JsonValueKind.Object)
            {
                // Buscar en results array (ordenado por insert_date_ DESC = más reciente primero)
                if (knowledgeData.TryGetProperty("results", out var results) &&
                    results.ValueKind == JsonValueKind.Array &&
                    results.GetArrayLength() > 0)
                {
                    _logger.LogInformation("Analizando {Count} resultados...", results.GetArrayLength());

                    // Buscar el primer mensaje del agente que NO esté en existingMessageIds
                    foreach (var result in results.EnumerateArray())
                    {
                        if (result.TryGetProperty("data", out var data) &&
                            data.TryGetProperty("message", out var message))
                        {
                            // Verificar que sea del agente
                            if (message.TryGetProperty("role", out var role) &&
                                role.GetString() == "agent")
                            {
                                // Verificar el ID del mensaje
                                if (result.TryGetProperty("document_id", out var docIdProp))
                                {
                                    var messageId = docIdProp.GetString();

                                    if (!string.IsNullOrEmpty(messageId))
                                    {
                                        // Verificar si es un mensaje NUEVO (no estaba antes)
                                        if (!existingMessageIds.Contains(messageId))
                                        {
                                            _logger.LogInformation("✓ Mensaje NUEVO encontrado. ID: {Id}", messageId);

                                            // Verificar que tenga contenido
                                            if (message.TryGetProperty("content", out var content))
                                            {
                                                var text = content.GetString();
                                                if (!string.IsNullOrWhiteSpace(text))
                                                {
                                                    _logger.LogInformation("✓ Mensaje tiene contenido válido");
                                                    return true;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            _logger.LogInformation("✗ Mensaje ya existía antes del trigger. ID: {Id}", messageId);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            _logger.LogInformation("✗ No se encontró ningún mensaje nuevo del agente");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verificando nuevo mensaje");
            return false;
        }
    }

    private (string message, string conversationId) ParseMakeResponse(string responseBody)
    {
        try
        {
            // Intentar parsear como JSON
            var json = JsonSerializer.Deserialize<JsonElement>(responseBody);

            _logger.LogInformation("=== ESTRUCTURA DEL JSON ===");
            _logger.LogInformation("Tipo: {Type}", json.ValueKind);

            // Mostrar todas las propiedades del objeto
            if (json.ValueKind == JsonValueKind.Object)
            {
                _logger.LogInformation("Propiedades encontradas:");
                foreach (var property in json.EnumerateObject())
                {
                    _logger.LogInformation("  - {Name}: {Value} (Tipo: {Type})",
                        property.Name,
                        property.Value.ToString(),
                        property.Value.ValueKind);
                }
            }

            _logger.LogInformation("=== FIN ESTRUCTURA ===");

            // Buscar el mensaje en diferentes propiedades posibles
            string message = "Sin respuesta";
            string conversationId = "";

            // Intentar diferentes estructuras comunes de Make
            // Opción 1: "respuesta" (estructura actual de Make)
            if (json.TryGetProperty("respuesta", out var respuestaProp))
            {
                message = respuestaProp.GetString() ?? message;
                _logger.LogInformation("Mensaje encontrado en 'respuesta'");
            }
            // Opción 2: "message"
            else if (json.TryGetProperty("message", out var msgProp))
            {
                message = msgProp.GetString() ?? message;
                _logger.LogInformation("Mensaje encontrado en 'message'");
            }
            // Opción 3: "response"
            else if (json.TryGetProperty("response", out var respProp))
            {
                message = respProp.GetString() ?? message;
                _logger.LogInformation("Mensaje encontrado en 'response'");
            }
            // Opción 4: "text"
            else if (json.TryGetProperty("text", out var textProp))
            {
                message = textProp.GetString() ?? message;
                _logger.LogInformation("Mensaje encontrado en 'text'");
            }
            // Opción 5: "answer"
            else if (json.TryGetProperty("answer", out var answerProp))
            {
                message = answerProp.GetString() ?? message;
                _logger.LogInformation("Mensaje encontrado en 'answer'");
            }
            // Opción 6: "output.answer"
            else if (json.TryGetProperty("output", out var outputProp) &&
                     outputProp.TryGetProperty("answer", out var outputAnswer))
            {
                message = outputAnswer.GetString() ?? message;
                _logger.LogInformation("Mensaje encontrado en 'output.answer'");
            }

            // Buscar conversationId en diferentes propiedades
            if (json.TryGetProperty("conversation_id", out var convProp))
            {
                conversationId = convProp.GetString() ?? "";
                _logger.LogInformation("ConversationId encontrado en 'conversation_id'");
            }
            else if (json.TryGetProperty("conversationId", out var convProp2))
            {
                conversationId = convProp2.GetString() ?? "";
                _logger.LogInformation("ConversationId encontrado en 'conversationId'");
            }
            else if (json.TryGetProperty("conv_id", out var convProp3))
            {
                conversationId = convProp3.GetString() ?? "";
                _logger.LogInformation("ConversationId encontrado en 'conv_id'");
            }

            _logger.LogInformation("Parseado Make - Mensaje: {Msg}, ConversationId: {ConvId}",
                message, conversationId);

            return (message, conversationId);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Error parseando JSON de Make. Respuesta no es JSON válido.");
            _logger.LogInformation("Devolviendo respuesta como texto plano");
            return (responseBody, "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado parseando respuesta de Make");
            return (responseBody, "");
        }
    }

    private (string message, string conversationId) ExtractRelevanceMessage(JsonElement response, string conversationId)
    {
        try
        {
            _logger.LogInformation("=== EXTRAYENDO MENSAJE DE KNOWLEDGE RESPONSE ===");
            _logger.LogInformation("Tipo: {Type}", response.ValueKind);

            string message = "Sin respuesta";

            // Buscar en diferentes estructuras posibles
            // Opción 1: results array - buscar el PRIMER mensaje del agente
            if (response.TryGetProperty("results", out var results) &&
                results.ValueKind == JsonValueKind.Array &&
                results.GetArrayLength() > 0)
            {
                _logger.LogInformation("Buscando mensaje del agente en {Count} resultados...", results.GetArrayLength());

                // Iterar sobre los resultados hasta encontrar un mensaje del agente
                foreach (var result in results.EnumerateArray())
                {
                    var extractedMessage = ExtractMessageFromItem(result);

                    // Si encontramos un mensaje válido (no "Sin respuesta"), usarlo
                    if (extractedMessage != "Sin respuesta")
                    {
                        message = extractedMessage;
                        _logger.LogInformation("✓ Mensaje del agente encontrado");
                        break;
                    }
                }

                if (message == "Sin respuesta")
                {
                    _logger.LogWarning("No se encontró ningún mensaje del agente en los resultados");
                }
            }
            // Opción 2: data array
            else if (response.TryGetProperty("data", out var data) &&
                     data.ValueKind == JsonValueKind.Array &&
                     data.GetArrayLength() > 0)
            {
                var firstItem = data[0];
                message = ExtractMessageFromItem(firstItem);
                _logger.LogInformation("Mensaje extraído de 'data[0]'");
            }
            // Opción 3: items array
            else if (response.TryGetProperty("items", out var items) &&
                     items.ValueKind == JsonValueKind.Array &&
                     items.GetArrayLength() > 0)
            {
                var firstItem = items[0];
                message = ExtractMessageFromItem(firstItem);
                _logger.LogInformation("Mensaje extraído de 'items[0]'");
            }
            // Opción 4: respuesta directa (fallback a estructura anterior)
            else if (response.TryGetProperty("output", out var output))
            {
                if (output.TryGetProperty("answer", out var answer))
                {
                    message = answer.GetString() ?? message;
                    _logger.LogInformation("Mensaje encontrado en 'output.answer'");
                }
                else
                {
                    message = output.GetString() ?? message;
                    _logger.LogInformation("Mensaje encontrado en 'output'");
                }
            }
            else if (response.TryGetProperty("respuesta", out var respuestaProp))
            {
                message = respuestaProp.GetString() ?? message;
                _logger.LogInformation("Mensaje encontrado en 'respuesta'");
            }
            else if (response.TryGetProperty("message", out var msgProp))
            {
                message = msgProp.GetString() ?? message;
                _logger.LogInformation("Mensaje encontrado en 'message'");
            }

            _logger.LogInformation("Mensaje final extraído: {Msg}", message);
            return (message, conversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extrayendo mensaje de Relevance");
            return ("Error procesando respuesta", conversationId);
        }
    }

    private string ExtractMessageFromItem(JsonElement item)
    {
        try
        {
            _logger.LogInformation("Extrayendo mensaje del item, propiedades:");
            foreach (var prop in item.EnumerateObject())
            {
                _logger.LogInformation("  - {Name}: (Tipo: {Type})", prop.Name, prop.Value.ValueKind);
            }

            // Estructura específica de Relevance: results[0].data.message.content
            if (item.TryGetProperty("data", out var data))
            {
                _logger.LogInformation("Encontrado 'data', buscando 'message'...");

                if (data.TryGetProperty("message", out var message))
                {
                    _logger.LogInformation("Encontrado 'message', verificando estructura...");

                    // Verificar que sea un mensaje del agente (no del usuario)
                    if (message.TryGetProperty("role", out var role))
                    {
                        var roleValue = role.GetString();
                        _logger.LogInformation("Role: {Role}", roleValue);

                        // Solo procesar si es del agente
                        if (roleValue == "agent")
                        {
                            if (message.TryGetProperty("content", out var content))
                            {
                                var text = content.GetString();
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    _logger.LogInformation("✓ Mensaje del agente encontrado en 'data.message.content'");
                                    return text;
                                }
                            }
                        }
                        else
                        {
                            _logger.LogInformation("Mensaje ignorado (role: {Role}), buscando siguiente...", roleValue);
                        }
                    }
                }
            }

            // Fallback: intentar otras estructuras
            string[] possibleKeys = { "answer", "respuesta", "response", "text", "content", "value" };

            foreach (var key in possibleKeys)
            {
                if (item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _logger.LogInformation("✓ Mensaje encontrado en propiedad '{Key}' (fallback)", key);
                        return text;
                    }
                }
            }

            _logger.LogWarning("No se encontró mensaje en el item");
            return "Sin respuesta";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extrayendo mensaje del item");
            return "Sin respuesta";
        }
    }

    private string GenerateUserId()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random();
        return new string(Enumerable.Repeat(chars, 24)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }
}