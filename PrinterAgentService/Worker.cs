using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrinterAgent.Core;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PrinterAgentService
{
    public class Worker(ILogger<Worker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("PrinterAgentService started at: {time}", DateTimeOffset.Now);
            while (!stoppingToken.IsCancellationRequested)
            {
                // Perform periodic printer checks and status reporting.
                List<string> printers = PrinterHelper.GetInstalledPrinters();
                logger.LogInformation("Discovered printers: {printers}", string.Join(", ", printers));

                // For demo, simulate a heartbeat and wait 10 seconds.
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            logger.LogInformation("PrinterAgentService is stopping at: {time}", DateTimeOffset.Now);
        }
    }
}
