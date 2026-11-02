FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0-bookworm-slim
COPY --from=build /app /app
USER 1654
ENTRYPOINT ["dotnet", "/app/netprobe.dll"]
