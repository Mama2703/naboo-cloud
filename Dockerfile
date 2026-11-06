# ---------- Build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Naboo.Api/Naboo.Api.csproj src/Naboo.Api/
RUN dotnet restore src/Naboo.Api/Naboo.Api.csproj
COPY src/ src/
RUN dotnet publish src/Naboo.Api/Naboo.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# ---------- Exécution (image minimale, utilisateur non root) ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends tzdata \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Naboo.Api.dll"]
