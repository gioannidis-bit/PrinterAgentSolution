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
        private HubConnection _hub;
        private int _reconnectAttempts = 0;
        private const int MAX_RECONNECT_ATTEMPTS = 10;
        private const int RECONNECT_DELAY_MS = 5000;
        private readonly HttpClientHandler _handler;
        private bool _serverWasDown = false;
        private Timer _serverCheckTimer;
        private readonly TimeSpan _serverCheckInterval = TimeSpan.FromSeconds(5);

        // Server endpoints
        private const string ServerBase = "https://192.168.14.121:7199";
        private readonly string _dataUrl = $"{ServerBase}/api/printerdata";
        private readonly string _hubUrl = $"{ServerBase}/printHub";
        private readonly string _agentId;
        private string _location;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
            // Shared handler that ignores certificate errors (development only)
            _handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };

            // Ensure stable GUID and location
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "agent.settings.json");
            if (File.Exists(settingsPath))
            {
                try
                {
                    var settingsJson = File.ReadAllText(settingsPath);
                    var settings = JsonSerializer.Deserialize<AgentSettings>(settingsJson);
                    _agentId = settings.AgentId;
                    _location = settings.Location;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reading agent settings, creating new ID");
                    _agentId = Guid.NewGuid().ToString();
                    _location = Environment.MachineName;
                    SaveAgentSettings();
                }
            }
            else
            {
                _agentId = Guid.NewGuid().ToString();
                _location = Environment.MachineName;
                SaveAgentSettings();
            }

            _logger.LogInformation("Agent initialized with ID: {AgentId}, Location: {Location}", _agentId, _location);
        }

        private void SaveAgentSettings()
        {
            try
            {
                var settingsPath = Path.Combine(AppContext.BaseDirectory, "agent.settings.json");
                var settings = new AgentSettings
                {
                    AgentId = _agentId,
                    Location = _location
                };
                var settingsJson = JsonSerializer.Serialize(settings);
                File.WriteAllText(settingsPath, settingsJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving agent settings");
            }
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            await InitializeHubConnection();

            // Start timer to check server status
            _serverCheckTimer = new Timer(CheckServerStatus, null, TimeSpan.Zero, _serverCheckInterval);

            try
            {
                await ConnectToHub(cancellationToken);
                await base.StartAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting the worker, will retry");
                _serverWasDown = true;
                // We'll retry from the timer
            }
        }

        private void CheckServerStatus(object state)
        {
            _ = CheckServerAndReconnectAsync();
        }

        private async Task CheckServerAndReconnectAsync()
        {
            try
            {
                // New HttpClient per request, do not dispose shared handler
                using var httpClient = new HttpClient(_handler, disposeHandler: false)
                {
                    Timeout = TimeSpan.FromSeconds(3)
                };

                var response = await httpClient.GetAsync($"{ServerBase}/health", HttpCompletionOption.ResponseHeadersRead);
                bool serverIsUp = response.IsSuccessStatusCode;

                if (serverIsUp && _serverWasDown && (_hub == null || _hub.State != HubConnectionState.Connected))
                {
                    _logger.LogInformation("Server is back online. Attempting immediate reconnection...");
                    _serverWasDown = false;
                    _reconnectAttempts = 0;
                    await ReconnectNowAsync();
                }
                else if (!serverIsUp)
                {
                    _serverWasDown = true;
                    _logger.LogWarning("Server appears to be down. Will retry later.");
                }
            }
            catch (Exception ex)
            {
                _serverWasDown = true;
                _logger.LogWarning("Failed to check server status: {Message}. Will retry.", ex.Message);
            }
        }

        private async Task ReconnectNowAsync()
        {
            if (_hub?.State == HubConnectionState.Connected)
                return;
            if (_hub?.State == HubConnectionState.Reconnecting || _hub?.State == HubConnectionState.Connecting)
                return;

            await InitializeHubConnection();
            await _hub.StartAsync();
            _logger.LogInformation("Successfully reconnected to server");

            await _hub.InvokeAsync("RegisterAgent", _agentId, Environment.MachineName, _location);
            _logger.LogInformation("Successfully re-registered agent with server");
        }

        private async Task InitializeHubConnection()
        {
            if (_hub != null)
            {
                try { await _hub.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error disposing previous hub connection"); }
            }

            var signalRHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            _hub = new HubConnectionBuilder()
                .WithUrl(_hubUrl, options => options.HttpMessageHandlerFactory = _ => signalRHandler)
                .WithAutomaticReconnect(new CustomRetryPolicy(MAX_RECONNECT_ATTEMPTS, RECONNECT_DELAY_MS))
                .Build();

            _hub.Closed += async error =>
            {
                _logger.LogWarning("Connection closed: {Error}", error?.Message);
                await Task.Delay(RECONNECT_DELAY_MS);
            };
            _hub.Reconnected += async connectionId =>
            {
                _logger.LogInformation("SignalR reconnected with connectionId={ConnectionId}, re-registering agent", connectionId);
                _reconnectAttempts = 0;
                await RegisterAgent(CancellationToken.None);
            };
            _hub.Reconnecting += error =>
            {
                _logger.LogWarning("Attempting to reconnect: {Error}", error?.Message);
                return Task.CompletedTask;
            };

            _hub.On<PrintRequest>("Print", req =>
            {
                _logger.LogInformation("Print request received: Printer={Printer}", req.PrinterName);
                PrinterHelper.SendTestPrint(req.PrinterName, req.DocumentContent, null, req.Landscape, req.PaperSize);
            });

            _hub.On<string>("UpdateLocation", async newLocation =>
            {
                _logger.LogInformation("Location update request received: {NewLocation}", newLocation);
                _location = newLocation;
                SaveAgentSettings();
                try { await _hub.InvokeAsync("LocationUpdated", _agentId, _location); }
                catch (Exception ex) { _logger.LogError(ex, "Error confirming location update"); }
            });
        }

        private async Task ConnectToHub(CancellationToken cancellationToken)
        {
            try
            {
                await _hub.StartAsync(cancellationToken);
                _logger.LogInformation("Connected to PrintHub at {Url}", _hubUrl);
                await RegisterAgent(cancellationToken);
                _reconnectAttempts = 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to hub");
                if (_reconnectAttempts < MAX_RECONNECT_ATTEMPTS)
                {
                    _reconnectAttempts++;
                    _logger.LogInformation("Will retry connection (attempt {Attempt}/{MaxAttempts})", _reconnectAttempts, MAX_RECONNECT_ATTEMPTS);
                }
                else
                {
                    _logger.LogWarning("Max reconnect attempts reached, will retry in next cycle");
                    _reconnectAttempts = 0;
                }
                throw;
            }
        }

        private async Task RegisterAgent(CancellationToken cancellationToken)
        {
            try
            {
                // Επιστρέφουμε στην αρχική υπογραφή της μεθόδου, χωρίς τη λίστα εκτυπωτών
                await _hub.InvokeAsync("RegisterAgent", _agentId, Environment.MachineName, _location, cancellationToken);
                _logger.LogInformation("Registered agent ID '{AgentId}' successfully", _agentId);

                // Αφού ολοκληρωθεί η εγγραφή, στέλνουμε ξεχωριστά τους εκτυπωτές
                var printers = PrinterHelper.GetInstalledPrinters();
                await _hub.InvokeAsync("UpdatePrinters", _agentId, printers, cancellationToken);
                _logger.LogInformation("Sent initial printer list with {Count} printers", printers.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering agent");
                throw;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker running at: {Time}", DateTimeOffset.Now);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_hub.State != HubConnectionState.Connected)
                    {
                        _logger.LogWarning("Hub not connected (state: {State}), attempting to reconnect", _hub.State);
                        try { await ConnectToHub(stoppingToken); }
                        catch { await Task.Delay(RECONNECT_DELAY_MS, stoppingToken); continue; }
                    }

                    var printers = PrinterHelper.GetInstalledPrinters();
                    _logger.LogInformation("Discovered printers: {List}", string.Join(", ", printers.Select(p => p.Name)));

                    if (_hub.State == HubConnectionState.Connected)
                    {
                        try
                        {
                            await _hub.InvokeAsync("UpdatePrinters", _agentId, printers, stoppingToken);
                            _logger.LogInformation("Sent printer information to hub including driver and IP data");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error updating printers in hub");
                        }
                    }

                    using var httpClient = new HttpClient(_handler, disposeHandler: false);
                    var payload = new
                    {
                        AgentId = _agentId,
                        MachineName = Environment.MachineName,
                        Location = _location,
                        Timestamp = DateTime.UtcNow,
                        Printers = printers,
                        IsOnline = true
                    };

                    // FIX: Use PascalCase for property names to match server models
                    var options = new JsonSerializerOptions
                    {
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
                        PropertyNamingPolicy = null // Use null instead of JsonNamingPolicy.CamelCase to preserve PascalCase
                    };

                    var json = JsonSerializer.Serialize(payload, options);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var resp = await httpClient.PostAsync(_dataUrl, content, stoppingToken);

                    if (resp.IsSuccessStatusCode)
                        _logger.LogInformation("Heartbeat sent");
                    else
                    {
                        _logger.LogWarning("Heartbeat failed: {Status}", resp.StatusCode);
                        // Για αποσφαλμάτωση, καταγράψτε το σώμα της απάντησης αν είναι διαθέσιμο
                        try
                        {
                            var errorBody = await resp.Content.ReadAsStringAsync(stoppingToken);
                            _logger.LogWarning("Error details: {Details}", errorBody);
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during heartbeat cycle");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }

            _logger.LogInformation("Worker stopping at: {Time}", DateTimeOffset.Now);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _serverCheckTimer?.Dispose();

            if (_hub != null)
            {
                try
                {
                    if (_hub.State == HubConnectionState.Connected)
                    {
                        await _hub.InvokeAsync("UnregisterAgent", _agentId, cancellationToken);
                        _logger.LogInformation("Agent unregistered");
                    }

                    await _hub.StopAsync(cancellationToken);
                    await _hub.DisposeAsync();
                    _logger.LogInformation("Disconnected from PrintHub");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during hub disconnection");
                }
            }

            await base.StopAsync(cancellationToken);
        }
    }

    public class AgentSettings
    {
        public string AgentId { get; set; }
        public string Location { get; set; }
    }

    public class CustomRetryPolicy : IRetryPolicy
    {
        private readonly int _maxAttempts;
        private readonly int _delayMs;

        public CustomRetryPolicy(int maxAttempts, int delayMs)
        {
            _maxAttempts = maxAttempts;
            _delayMs = delayMs;
        }

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            if (retryContext.PreviousRetryCount >= _maxAttempts)
                return null;

            var random = new Random();
            var jitter = random.Next(-1000, 1000);
            return TimeSpan.FromMilliseconds(_delayMs + jitter);
        }
    }
}