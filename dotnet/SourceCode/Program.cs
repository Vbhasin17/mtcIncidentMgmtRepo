using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureFunctionsWorkerDefaults(worker =>
    {
        worker.Services.Configure<JsonSerializerSettings>(options =>
        {
            options.NullValueHandling = NullValueHandling.Ignore;
            options.Formatting = Formatting.Indented;
            // Add any custom settings you need
        });
    });

builder.ConfigureServices(services =>
{
    services.AddApplicationInsightsTelemetryWorkerService();
});

var host = builder.Build();
host.Run();
