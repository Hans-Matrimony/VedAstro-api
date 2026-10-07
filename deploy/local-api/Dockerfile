FROM python:3.13-slim AS source
WORKDIR /source
COPY deploy/local-api/source-lock.json deploy/local-api/fetch_engine.py deploy/local-api/BoundedEngineCache.cs deploy/local-api/engine-packages.lock.json ./
RUN python fetch_engine.py --destination /engine

FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY --from=source /engine/Library /engine/Library
COPY deploy/local-api ./
RUN dotnet restore LocalApi.csproj -p:EngineRoot=/engine --locked-mode --nologo \
    && dotnet publish LocalApi.csproj -c Release -o /publish -p:EngineRoot=/engine --no-restore --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD ["dotnet", "VedAstro.LocalApi.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "VedAstro.LocalApi.dll"]
