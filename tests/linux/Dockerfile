FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS sdk
FROM mcr.microsoft.com/powershell:7.5-ubuntu-24.04 AS powershell
FROM node:24-bookworm-slim AS node
FROM docker:29-cli AS docker
FROM mcr.microsoft.com/playwright:v1.56.1-noble
COPY --from=sdk /usr/share/dotnet/ /usr/share/dotnet/
COPY --from=powershell /opt/microsoft/powershell/7/ /opt/microsoft/powershell/7/
COPY --from=node /usr/local/ /usr/local/
COPY --from=docker /usr/local/bin/docker /usr/local/bin/docker
ENV DOTNET_ROOT=/usr/share/dotnet \
    PATH=/usr/share/dotnet:/opt/microsoft/powershell/7:$PATH \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    POWERSHELL_TELEMETRY_OPTOUT=1
ENTRYPOINT ["pwsh", "-NoProfile"]
