# Etapa 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiar el archivo de proyecto y restaurar dependencias
COPY ["MeleFuegosApi/MeleFuegosApi.csproj", "MeleFuegosApi/"]
RUN dotnet restore "MeleFuegosApi/MeleFuegosApi.csproj"

# Copiar todo el código y compilar
COPY . .
WORKDIR "/src/MeleFuegosApi"
RUN dotnet build "MeleFuegosApi.csproj" -c Release -o /app/build

# Etapa 2: Publish
FROM build AS publish
RUN dotnet publish "MeleFuegosApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Etapa 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 5000

# Copiar los archivos publicados
COPY --from=publish /app/publish .

# Punto de entrada
ENTRYPOINT ["dotnet", "MeleFuegosApi.dll"]