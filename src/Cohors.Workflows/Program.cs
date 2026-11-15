using Cohors.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton(WorkflowOptions.Read(context.Configuration, context.HostingEnvironment));
        services.AddHttpClient<WorkflowApi>(client => client.Timeout = TimeSpan.FromMinutes(9))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    }).Build();
host.Run();
