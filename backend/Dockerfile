# ── Stage 1: build ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore dependencies first (layer-cached unless .csproj changes)
COPY PostPilot.Api.csproj ./
RUN dotnet restore PostPilot.Api.csproj

# Copy source and publish
COPY . .
RUN dotnet publish PostPilot.Api.csproj -c Release -o /app/publish --no-restore

# ── Stage 2: runtime ───────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

# Copy layered config files so AddJsonFile("config/...") resolves correctly
COPY ConfigurationFiles/ ./config/

# Ensure uploads directory exists
RUN mkdir -p /app/uploads

EXPOSE 5122
ENV ASPNETCORE_URLS=http://0.0.0.0:5122

ENTRYPOINT ["dotnet", "PostPilot.Api.dll"]
