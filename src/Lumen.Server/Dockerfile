FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

COPY src/Lumen.Shared/Lumen.Shared.csproj src/Lumen.Shared/
COPY src/Lumen.Server/Lumen.Server.csproj src/Lumen.Server/
RUN dotnet restore src/Lumen.Server/Lumen.Server.csproj

COPY src/ src/
RUN dotnet publish src/Lumen.Server/Lumen.Server.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl util-linux \
    && rm -rf /var/lib/apt/lists/* \
    && addgroup --system --gid 10001 lumen \
    && adduser --system --uid 10001 --ingroup lumen --home /app lumen \
    && mkdir -p /data \
    && chown -R lumen:lumen /data /app

COPY --from=build /app/publish .
COPY src/Lumen.Server/docker-entrypoint.sh /usr/local/bin/lumen-entrypoint
RUN chmod +x /usr/local/bin/lumen-entrypoint
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Lumen__DataDir=/data
EXPOSE 8080
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/api/v1/health || exit 1
ENTRYPOINT ["/usr/local/bin/lumen-entrypoint"]
CMD ["dotnet", "Lumen.Server.dll"]
