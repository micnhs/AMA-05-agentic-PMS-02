FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /repo
COPY . .
RUN dotnet restore src/Cohors.Operations/Cohors.Operations.csproj --locked-mode
RUN dotnet publish src/Cohors.Operations/Cohors.Operations.csproj -c Release --no-restore -o /out -m:1 -p:UseSharedCompilation=false

FROM scratch AS package
COPY --from=build /out/ /

FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated10.0
ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true \
    FUNCTIONS_WORKER_RUNTIME=dotnet-isolated
COPY --from=build /out/ /home/site/wwwroot/
EXPOSE 80
