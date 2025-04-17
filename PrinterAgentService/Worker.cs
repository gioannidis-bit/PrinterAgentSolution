using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrinterAgent.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;


namespace PrinterAgentService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly HttpClient _httpClient;
        private HubConnection _hub;

        // Server endpoints
        private const string ServerBase = "https://192.168.14.121:7199";
        private readonly string _dataUrl = $"{ServerBase}/api/printerdata";
        private readonly string _hubUrl = $"{ServerBase}/printHub";
        private readonly string _agentId;


        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
            // HttpClient that ignores certificate errors (development only)
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            _httpClient = new HttpClient(handler);

            var path = Path.Combine(AppContext.BaseDirectory, "agent.id");
            if (File.Exists(path))
                _agentId = File.ReadAllText(path).Trim();
            else
            {
                _agentId = Guid.NewGuid().ToString();
                File.WriteAllText(path, _agentId);
            }
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            // SignalR handler that ignores SSL errors (development only)
            var signalRHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

                 // Build the SignalR connection
            _hub = new HubConnectionBuilder()
                .WithUrl(_hubUrl, options =>
                {
                    options.HttpMessageHandlerFactory = _ => signalRHandler;
                })
                .WithAutomaticReconnect()
                .Build();


            // 0) If we ever reconnect, re‑register our agentId
            _hub.Reconnected += async connectionId =>
            {
                _logger.LogInformation("SignalR reconnected with connectionId={conn}, re‑registering agent", connectionId);
                await _hub.InvokeAsync("RegisterAgent", _agentId, Environment.MachineName, cancellationToken);
            }
            ;


            // Handle incoming print requests
            _hub.On<PrintRequest>("Print", req =>
            {
                _logger.LogInformation("Print request received: Printer={printer}", req.PrinterName);
                PrinterHelper.SendTestPrint(
                    req.PrinterName,
                    req.DocumentContent,
                    null,               // no image
                    req.Landscape,
                    req.PaperSize
                );
            });

            await _hub.StartAsync(cancellationToken);
            _logger.LogInformation("Connected to PrintHub at {url}", _hubUrl);

            // Register this agent
            await _hub.InvokeAsync("RegisterAgent", _agentId, Environment.MachineName, cancellationToken);
            _logger.LogInformation("Registered agent ID '{agent}'", Environment.MachineName);

            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Heartbeat: send list of PrinterInfo objects (Name + Status)
                    List<PrinterInfo> printers = PrinterHelper.GetInstalledPrinters();
                    _logger.LogInformation(
                        "Discovered printers: {list}",
                        string.Join(", ", printers.Select(p => p.Name))
                    );

                    var payload = new
                    {

                        AgentId = _agentId,                 // GUID
                        MachineName = Environment.MachineName,  // friendly name
                        Timestamp = DateTime.UtcNow,
                        Printers = printers   // now List<PrinterInfo>
                    };
                    string json = JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var resp = await _httpClient.PostAsync(_dataUrl, content, stoppingToken);
                    if (resp.IsSuccessStatusCode)
                        _logger.LogInformation("Heartbeat sent");
                    else
                        _logger.LogWarning("Heartbeat failed: {status}", resp.StatusCode);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during heartbeat");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }

            _logger.LogInformation("Worker stopping at: {time}", DateTimeOffset.Now);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_hub != null)
            {
                await _hub.StopAsync(cancellationToken);
                await _hub.DisposeAsync();
                _logger.LogInformation("Disconnected from PrintHub");
            }
            await base.StopAsync(cancellationToken);
        }
    }
}
