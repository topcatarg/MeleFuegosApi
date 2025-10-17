using MeleFuegosApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Configurar CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("MeleFuegos", policy =>
    {
        policy.WithOrigins(
                "http://localhost:5173",  // Vue local
                "https://*.vercel.app"     // Producción en Vercel
              )
              .SetIsOriginAllowedToAllowWildcardSubdomains()
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Agregar servicios
builder.Services.AddControllers();
builder.Services.AddHttpClient<RelevanceService>();
builder.Services.AddSingleton<RelevanceService>();

// Agregar Swagger para testing
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Habilitar Swagger en desarrollo
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Deshabilitar redirección HTTPS para desarrollo y Render
// app.UseHttpsRedirection();

app.UseCors("MeleFuegos");
app.UseAuthorization();
app.MapControllers();

app.Run();