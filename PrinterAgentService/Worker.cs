using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrinterAgent.Core;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PrinterAgentService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly HttpClient _httpClient;
        // URL of the central web service endpoint that accepts agent data.
        private readonly string _serverUrl = "https://192.168.14.121:7199/api/printerdata"; // Adjust port as needed.

        public Worker(ILogger<Worker> logger)
        {
            // Create an HttpClientHandler that ignores certificate errors.
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) =>
                {
                    // Log sslPolicyErrors if needed.
                    return true; // Always accept the certificate (not secure for production).
                }
            };


            _logger = logger;
            // Instead of: _httpClient = new HttpClient();
            _httpClient = new HttpClient(handler);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("PrinterAgentService started at: {time}", DateTimeOffset.Now);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Get list of printers using PrinterHelper.
                    List<string> printers = PrinterHelper.GetInstalledPrinters();
                    _logger.LogInformation("Discovered printers: {printers}", string.Join(", ", printers));

                    // Create payload that includes an Agent ID and the printer list.
                    var payload = new
                    {
                        AgentId = Environment.MachineName, // Simplest ID: the machine name.
                        Timestamp = DateTime.UtcNow,
                        Printers = printers
                    };

                    string json = JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    // Send the data (via outbound connection) to the central server.
                    var response = await _httpClient.PostAsync(_serverUrl, content, stoppingToken);
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("Data sent successfully to server.");
                    }
                    else
                    {
                        _logger.LogWarning("Failed to send data. Server responded with status: {status}", response.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred sending printer data.");
                }

                // Wait for 30 seconds (or your preferred interval) before sending the next update.
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            _logger.LogInformation("PrinterAgentService is stopping at: {time}", DateTimeOffset.Now);
        }
    }
}
