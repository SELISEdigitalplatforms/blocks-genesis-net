# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src ./src
RUN dotnet restore ./src/blocks-genesis-net.sln
RUN dotnet publish ./src/TestDriver/TestDriver.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled AS runtime
WORKDIR /app

USER 1654
COPY --from=build /app/publish .

HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD ["/usr/bin/env", "sh", "-c", "wget -qO- http://127.0.0.1:5000/ping || exit 1"]

ENTRYPOINT ["dotnet", "TestDriver.dll"]
