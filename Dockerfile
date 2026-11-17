# Build Stage
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["global.json", "./"]
COPY ["Druckkammer.sln", "./"]
COPY ["src/Druckkammer.Web/Druckkammer.Web.csproj", "src/Druckkammer.Web/"]
COPY ["src/Druckkammer.Tests/Druckkammer.Tests.csproj", "src/Druckkammer.Tests/"]
RUN dotnet restore "src/Druckkammer.Web/Druckkammer.Web.csproj"

COPY . .
WORKDIR "/src/src/Druckkammer.Web"
RUN dotnet publish "Druckkammer.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Runtime Stage
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
WORKDIR /app

# Create directories for persistent storage (SQLite DB + canonical file storage) and folder import scanner
RUN mkdir -p /data/storage /data/thumbnails /data/makes /data/temp /import && \
    chown -R $APP_UID:$APP_UID /data /import

USER $APP_UID

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Druckkammer__DataDirectory=/data \
    Druckkammer__ImportDirectory=/import

EXPOSE 8080
VOLUME ["/data", "/import"]

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Druckkammer.Web.dll"]
