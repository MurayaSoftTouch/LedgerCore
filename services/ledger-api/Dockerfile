# syntax=docker/dockerfile:1
# Build context: services/ledger-api. Targets: `runtime` (the API) and `migrate` (a one-shot EF
# migrations bundle that runs as the schema owner; the API never migrates at startup, ADR-007).

FROM mcr.microsoft.com/dotnet/sdk:10.0.401-alpine3.24 AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY global.json Directory.Build.props dotnet-tools.json ./
COPY src/LedgerCore.Ledger.Domain/LedgerCore.Ledger.Domain.csproj src/LedgerCore.Ledger.Domain/
COPY src/LedgerCore.Ledger.Api/LedgerCore.Ledger.Api.csproj src/LedgerCore.Ledger.Api/
RUN dotnet restore src/LedgerCore.Ledger.Api/LedgerCore.Ledger.Api.csproj -r linux-musl-x64 && dotnet tool restore
COPY src/ src/
RUN dotnet publish src/LedgerCore.Ledger.Api -c Release -r linux-musl-x64 --self-contained false --no-restore -o /app
RUN dotnet ef migrations bundle --project src/LedgerCore.Ledger.Api -r linux-musl-x64 --self-contained -o /bundle/efbundle

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.12-alpine3.24 AS migrate
COPY --from=build /bundle/efbundle /efbundle
USER $APP_UID
# Reads LEDGER_MIGRATIONS_CONNECTION (schema owner) via the design-time factory.
ENTRYPOINT ["/efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-alpine3.24 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_CLI_TELEMETRY_OPTOUT=1
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=5s --timeout=3s --start-period=20s --retries=12 \
  CMD wget -qO- http://127.0.0.1:8080/health/ready >/dev/null || exit 1
ENTRYPOINT ["dotnet", "LedgerCore.Ledger.Api.dll"]
