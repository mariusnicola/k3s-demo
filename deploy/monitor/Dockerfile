FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim
COPY --from=build /app /app
USER app
ENTRYPOINT ["dotnet", "/app/monitor.dll"]
