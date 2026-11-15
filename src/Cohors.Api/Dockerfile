FROM node:24-bookworm-slim AS web
WORKDIR /repo/src/Cohors.Web
COPY src/Cohors.Web/package.json src/Cohors.Web/package-lock.json ./
RUN npm ci --no-fund
COPY src/Cohors.Web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /repo
COPY . .
COPY --from=web /repo/src/Cohors.Api/wwwroot/ ./src/Cohors.Api/wwwroot/
RUN dotnet restore src/Cohors.Api/Cohors.Api.csproj --locked-mode
RUN dotnet publish src/Cohors.Api/Cohors.Api.csproj -c Release --no-restore -o /out -m:1 -p:UseSharedCompilation=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_HTTP_PORTS=8080 COHORS_HOME=/app
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Cohors.Api.dll"]
