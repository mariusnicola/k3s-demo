# Build context: app/
FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
WORKDIR /src
COPY BatchTracker.sln Directory.Build.prop[s] Directory.Packages.prop[s] ./
COPY src/ src/
RUN dotnet publish src/BatchTracker.Worker -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim
COPY --from=build /app /app
# Numeric, so runAsNonRoot can verify it.
USER 1654
ENTRYPOINT ["dotnet", "/app/BatchTracker.Worker.dll"]
