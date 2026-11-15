FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /repo
COPY . .
RUN dotnet restore src/Cohors.AgentHost/Cohors.AgentHost.csproj --locked-mode
RUN dotnet publish src/Cohors.AgentHost/Cohors.AgentHost.csproj -c Release --no-restore -o /out -m:1 -p:UseSharedCompilation=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Cohors.AgentHost.dll"]
