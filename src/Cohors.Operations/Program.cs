using Azure.Identity;
using Cohors.Application;
using Cohors.Infrastructure;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => SqlStateStore.Configure(context.Configuration.GetConnectionString("Cohors")
            ?? throw new InvalidOperationException("Operations SQL connection required."),
            context.Configuration["Cohors:SqlAuthentication"], context.HostingEnvironment.IsDevelopment()));
        services.AddSingleton<IStateStore>(sp => sp.GetRequiredService<SqlStateStore>());
        services.AddSingleton<IWorkspaceReadStore>(sp => sp.GetRequiredService<SqlStateStore>());
        services.AddSingleton<OperationsService>();
        services.AddSingleton(_ =>
        {
            var telemetry = TelemetryConfiguration.CreateDefault();
            var connection = context.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
            if (string.IsNullOrEmpty(connection)) telemetry.DisableTelemetry = true;
            else
            {
                telemetry.ConnectionString = connection;
                telemetry.SetAzureTokenCredential(new DefaultAzureCredential());
            }
            return telemetry;
        });
        services.AddSingleton<TelemetryClient>();
    }).Build();
host.Run();
