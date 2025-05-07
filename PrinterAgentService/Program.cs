using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PrinterAgentService
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        // Host builder; if planning to run as a Windows Service, UseWindowsService() can be enabled.
        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseWindowsService()  // Enable to run as a Windows Service
                .ConfigureServices((hostContext, services) =>
                {
                    // Register the print spooler service as a singleton
                    services.AddSingleton<PrintSpoolerService>();

                    // Register the local print service as a hosted service
                    services.AddHostedService<LocalPrintService>();

                    // Register the main worker service
                    services.AddHostedService<Worker>();
                });
    }
}