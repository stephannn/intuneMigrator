using intuneMigratorService;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddHostedService<Worker>();
        if (OperatingSystem.IsWindows())
        {
            //#if WINDOWS
            services.AddWindowsService();
            //#endif
        }
    })
    .ConfigureLogging((context, logging) =>
    {
        if (OperatingSystem.IsWindows())
        {
            logging.ClearProviders();
            logging.AddEventLog(options =>
            {
                options.SourceName = "IntuneMigratorService";
                options.LogName = "Application";
            });
            // The default filter for EventLog is Warning or higher. We need Information to see connection events.
            //logging.AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>((category, logLevel) => logLevel >= LogLevel.Information);
            logging.SetMinimumLevel(LogLevel.Information);
        }
    })
    .Build();

await host.RunAsync();