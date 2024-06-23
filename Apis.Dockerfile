# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy and restore
COPY src/Apis/. ./src/Apis/
COPY src/Services/. ./src/Services/
RUN dotnet restore ./src/Apis/Apis.csproj

# Build and publish the application
WORKDIR /app/src/Apis
RUN dotnet publish -c Release -o /app/out

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/out .
ENTRYPOINT ["dotnet", "Apis.dll"]
