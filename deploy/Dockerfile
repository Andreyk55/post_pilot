# ── Stage 1: build ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore dependencies first (layer-cached unless .csproj changes).
# Copy both project files before any source so the restore layer is reused.
COPY PostPilot.Api.csproj ./
COPY publisher/PostPilot.Publisher.csproj ./publisher/
# Restore via the publisher (which has a ProjectReference to the API),
# so both projects are restored in one pass and their assets files stay consistent.
RUN dotnet restore publisher/PostPilot.Publisher.csproj

# Copy full source and publish both targets
COPY . .
RUN dotnet publish PostPilot.Api.csproj               -c Release -o /app/publish/api       --no-restore
# Publisher references the API project; skip --no-restore so MSBuild re-resolves
# assets consistently (packages are already in the NuGet cache from restore above).
RUN dotnet publish publisher/PostPilot.Publisher.csproj -c Release -o /app/publish/publisher

# ── Stage 2 (target: api) ───────────────────────────────────────────────────
# Runs the ASP.NET Core web server — controllers only, no background loop.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app

COPY --from=build /app/publish/api ./

# Layered config files so AddJsonFile("config/...") resolves correctly
COPY ConfigurationFiles/ ./config/

# Ensure uploads directory exists
RUN mkdir -p /app/uploads

EXPOSE 5122
ENV ASPNETCORE_URLS=http://0.0.0.0:5122

ENTRYPOINT ["dotnet", "PostPilot.Api.dll"]

# ── Stage 3 (target: publisher) ─────────────────────────────────────────────
# Runs the publishing background loop only — no HTTP endpoints, no migrations.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS publisher
WORKDIR /app

COPY --from=build /app/publish/publisher ./

# Same layered config so options validation succeeds (same env file is used)
COPY ConfigurationFiles/ ./config/

ENTRYPOINT ["dotnet", "PostPilot.Publisher.dll"]
