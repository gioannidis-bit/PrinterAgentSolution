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
                //.UseWindowsService()  // Uncomment if deploying as a Windows Service.
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddHostedService<Worker>();
                });
    }
}
