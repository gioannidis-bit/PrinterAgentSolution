using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrinterAgent.Core;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PrinterAgentService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("PrinterAgentService started at: {time}", DateTimeOffset.Now);
            while (!stoppingToken.IsCancellationRequested)
            {
                // Perform periodic printer checks and status reporting.
                List<string> printers = PrinterHelper.GetInstalledPrinters();
                _logger.LogInformation("Discovered printers: {printers}", string.Join(", ", printers));

                // For demo, simulate a heartbeat and wait 10 seconds.
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            _logger.LogInformation("PrinterAgentService is stopping at: {time}", DateTimeOffset.Now);
        }
    }
}
