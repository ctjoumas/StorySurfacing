using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VideoProcessorFunction.Models;
using VideoProcessorFunction.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("stationsconfig.json", optional: false, reloadOnChange: true)
            .Build();

        var stationsConfig = configuration.GetSection("StationsConfig").Get<StationsConfig>();
        services.AddSingleton(stationsConfig);
        services.AddSingleton<StationService>();
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        services.AddHttpClient();
    })
    .Build();

host.Run();