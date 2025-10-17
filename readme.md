# Mele Fuegos Chat API - Backend .NET 8

API backend para reemplazar Voiceflow y conectar directamente con Relevance AI.

## 🚀 Setup Local

### 1. Crear el proyecto

```bash
# Crear carpeta del proyecto
mkdir MeleFuegosApi
cd MeleFuegosApi

# Crear solución y proyecto Web API
dotnet new webapi -n MeleFuegosApi
cd MeleFuegosApi
```

### 2. Estructura de carpetas

Crea la siguiente estructura:

```
MeleFuegosApi/
├── Controllers/
│   └── ChatController.cs
├── Services/
│   └── RelevanceService.cs
├── Models/
│   └── Models.cs
├── Program.cs
├── appsettings.json
├── MeleFuegosApi.csproj
└── .gitignore
```

### 3. Configurar API Key

Crea un archivo `appsettings.Development.json` (este NO se sube a Git):

```json
{
  "Relevance": {
    "ApiKey": "TU_API_KEY_REAL_AQUI"
  }
}
```

### 4. Ejecutar localmente

```bash
dotnet run
```

La API estará disponible en: `http://localhost:5000` o `https://localhost:5001`

## 📡 Endpoints

### Health Check
```bash
GET /api/chat/health
```

Respuesta:
```json
{
  "status": "API activa",
  "timestamp": "2025-10-15T12:00:00Z",
  "service": "Mele Fuegos Chat API"
}
```

### Enviar Mensaje
```bash
POST /api/chat/message
Content-Type: application/json

{
  "message": "Hola, quisiera ver la carta de vinos",
  "conversationId": "opcional-uuid"
}
```

Respuesta:
```json
{
  "message": "Respuesta de Relevance AI",
  "conversationId": "uuid-de-la-conversacion",
  "timestamp": "2025-10-15T12:00:00Z"
}
```

## 🔧 Testing con cURL

```bash
# Health check
curl http://localhost:5000/api/chat/health

# Enviar mensaje
curl -X POST http://localhost:5000/api/chat/message \
  -H "Content-Type: application/json" \
  -d '{"message":"Hola, quiero reservar una mesa"}'
```

## 🚢 Deploy en Render

### 1. Preparar para deploy

Asegúrate de tener todos los archivos copiados en tu proyecto.

### 2. Crear repositorio en GitHub

```bash
git init
git add .
git commit -m "Initial commit - Backend API"
git branch -M main
git remote add origin TU_REPO_URL
git push -u origin main
```

### 3. Deploy en Render

1. Ve a [render.com](https://render.com)
2. Click en **"New +"** → **"Web Service"**
3. Conecta tu repositorio de GitHub
4. Configuración:
   - **Name**: `mele-fuegos-api`
   - **Region**: Oregon (US West)
   - **Branch**: `main`
   - **Runtime**: `.NET`
   - **Build Command**: `dotnet publish -c Release -o out`
   - **Start Command**: `cd out && dotnet MeleFuegosApi.dll`
   - **Plan**: Free

5. **Variables de entorno** (en Render):
   ```
   Relevance__ApiKey = TU_API_KEY_REAL
   ASPNETCORE_URLS = http://0.0.0.0:5000
   ```

6. Click en **"Create Web Service"**

### 4. Obtener URL

Una vez deployado, Render te dará una URL tipo:
```
https://mele-fuegos-api.onrender.com
```

## 📝 Notas

- El tier gratuito de Render se "duerme" después de 15 min de inactividad
- La primera request después de dormir tarda ~30-60 segundos
- Para producción, considera el plan de $7/mes que mantiene el servicio activo

## 🔍 Debugging

Si algo no funciona:

1. Revisa los logs en Render
2. Verifica que la API Key esté correctamente configurada
3. Testea primero localmente antes de deployar

## ⚠️ IMPORTANTE

La estructura de respuesta de Relevance AI puede variar. En el método `ExtractAssistantMessage` del `RelevanceService.cs` necesitarás ajustar cómo se parsea la respuesta según la estructura real que devuelva Relevance.

Para ver la estructura exacta, revisa los logs cuando hagas la primera request.