FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, against project files only, so the layer caches across code edits.
COPY Ut2004Stats.slnx ./
COPY src/Ut2004Stats.Core/Ut2004Stats.Core.csproj src/Ut2004Stats.Core/
COPY src/Ut2004Stats.Web/Ut2004Stats.Web.csproj src/Ut2004Stats.Web/
COPY tests/Ut2004Stats.Core.Tests/Ut2004Stats.Core.Tests.csproj tests/Ut2004Stats.Core.Tests/
RUN dotnet restore src/Ut2004Stats.Web/Ut2004Stats.Web.csproj

COPY . .
RUN dotnet publish src/Ut2004Stats.Web/Ut2004Stats.Web.csproj \
        -c Release -o /app/publish --no-restore


FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

LABEL org.opencontainers.image.title="UT2004 Stats" \
      org.opencontainers.image.description="Match reports and leaderboards for an Unreal Tournament 2004 server" \
      org.opencontainers.image.source="https://github.com/danpowell88/ut2004-stats"

WORKDIR /app
COPY --from=build /app/publish .

# /data/logs is the game server's stats directory (mount read-only);
# /data/db holds the SQLite database, which must be writable.
RUN mkdir -p /data/logs /data/db && chown -R app:app /data

ENV ASPNETCORE_URLS=http://+:8080 \
    Stats__LogDirectory=/data/logs \
    Stats__DatabasePath=/data/db/ut2004stats.db

EXPOSE 8080
VOLUME ["/data/db"]

USER app

# The app exposes GET /health; probe it from your orchestrator (the runtime image
# deliberately ships without curl/wget, so there is nothing here to call it with).

ENTRYPOINT ["dotnet", "Ut2004Stats.Web.dll"]
