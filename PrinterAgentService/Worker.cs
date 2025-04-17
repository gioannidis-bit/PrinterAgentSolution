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
        private int _reconnectAttempts = 0;
        private const int MAX_RECONNECT_ATTEMPTS = 10;
        private const int RECONNECT_DELAY_MS = 5000;

        // Server endpoints
        private const string ServerBase = "https://192.168.14.121:7199";
        private readonly string _dataUrl = $"{ServerBase}/api/printerdata";
        private readonly string _hubUrl = $"{ServerBase}/printHub";
        private readonly string _agentId;
        private string _location;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
            // HttpClient that ignores certificate errors (development only)
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            _httpClient = new HttpClient(handler);

            // Διασφάλιση ότι το GUID του agent είναι σταθερό
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
            // Αναδιοργάνωση της μεθόδου για καλύτερο χειρισμό σφαλμάτων
            await InitializeHubConnection();

            try
            {
                await ConnectToHub(cancellationToken);
                await base.StartAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting the worker, will retry on next cycle");
                // Δεν θέλουμε να σταματήσει την εκτέλεση του service αν αποτύχει η αρχική σύνδεση
            }
        }

        private async Task InitializeHubConnection()
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
                .WithAutomaticReconnect(new CustomRetryPolicy(MAX_RECONNECT_ATTEMPTS, RECONNECT_DELAY_MS))
                .Build();

            // Event handlers for connection management
            _hub.Closed += async (error) =>
            {
                _logger.LogWarning("Connection closed: {Error}", error?.Message);
                await Task.Delay(RECONNECT_DELAY_MS); // Περιμένουμε λίγο πριν προσπαθήσουμε reconnect
            };

            // Όταν επανασυνδεθούμε, κάνουμε register ξανά
            _hub.Reconnected += async (connectionId) =>
            {
                _logger.LogInformation("SignalR reconnected with connectionId={ConnectionId}, re-registering agent", connectionId);
                _reconnectAttempts = 0;
                await RegisterAgent(CancellationToken.None);
            };

            // Όταν προσπαθούμε να επανασυνδεθούμε, καταγράφουμε το γεγονός
            _hub.Reconnecting += (error) =>
            {
                _logger.LogWarning("Attempting to reconnect: {Error}", error?.Message);
                return Task.CompletedTask;
            };

            // Handle incoming print requests
            _hub.On<PrintRequest>("Print", req =>
            {
                _logger.LogInformation("Print request received: Printer={Printer}", req.PrinterName);
                PrinterHelper.SendTestPrint(
                    req.PrinterName,
                    req.DocumentContent,
                    null,               // no image
                    req.Landscape,
                    req.PaperSize
                );
            });

            // Νέο handler για ενημέρωση της τοποθεσίας
            _hub.On<string>("UpdateLocation", async (newLocation) =>
            {
                _logger.LogInformation("Location update request received: {NewLocation}", newLocation);
                _location = newLocation;
                SaveAgentSettings();

                // Στέλνουμε επιβεβαίωση πίσω στον server
                try
                {
                    await _hub.InvokeAsync("LocationUpdated", _agentId, _location);
                    _logger.LogInformation("Location updated successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error confirming location update");
                }
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
                // Αυξάνουμε τις προσπάθειες και δοκιμάζουμε αργότερα
                if (_reconnectAttempts < MAX_RECONNECT_ATTEMPTS)
                {
                    _reconnectAttempts++;
                    _logger.LogInformation("Will retry connection (attempt {Attempt}/{MaxAttempts})",
                                         _reconnectAttempts, MAX_RECONNECT_ATTEMPTS);
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
                // Στέλνουμε και την τοποθεσία μαζί με τα άλλα στοιχεία
                await _hub.InvokeAsync("RegisterAgent", _agentId, Environment.MachineName, _location, cancellationToken);
                _logger.LogInformation("Registered agent ID '{AgentId}' successfully", _agentId);
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
                    // Αν δεν είμαστε συνδεδεμένοι, προσπαθούμε να συνδεθούμε
                    if (_hub.State != HubConnectionState.Connected)
                    {
                        _logger.LogWarning("Hub not connected (state: {State}), attempting to reconnect", _hub.State);
                        try
                        {
                            await ConnectToHub(stoppingToken);
                        }
                        catch
                        {
                            // Αν αποτύχει, θα ξαναπροσπαθήσουμε στον επόμενο κύκλο
                            await Task.Delay(RECONNECT_DELAY_MS, stoppingToken);
                            continue;
                        }
                    }

                    // Heartbeat: send list of PrinterInfo objects (Name + Status)
                    List<PrinterInfo> printers = PrinterHelper.GetInstalledPrinters();
                    _logger.LogInformation(
                        "Discovered printers: {List}",
                        string.Join(", ", printers.Select(p => p.Name))
                    );

                    // Ενημερώνουμε τη λίστα εκτυπωτών και στο hub
                    if (_hub.State == HubConnectionState.Connected)
                    {
                        try
                        {
                            await _hub.InvokeAsync("UpdatePrinters", _agentId, printers, stoppingToken);
                            _logger.LogInformation("Printer list updated in hub");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error updating printers in hub");
                        }
                    }

                    // Στέλνουμε heartbeat και στο API endpoint
                    var payload = new
                    {
                        AgentId = _agentId,
                        MachineName = Environment.MachineName,
                        Location = _location, // Προσθήκη του πεδίου τοποθεσίας
                        Timestamp = DateTime.UtcNow,
                        Printers = printers
                    };

                    string json = JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var resp = await _httpClient.PostAsync(_dataUrl, content, stoppingToken);
                    if (resp.IsSuccessStatusCode)
                        _logger.LogInformation("Heartbeat sent");
                    else
                        _logger.LogWarning("Heartbeat failed: {Status}", resp.StatusCode);
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
            if (_hub != null)
            {
                try
                {
                    // Στέλνουμε ειδοποίηση αποσύνδεσης πριν κλείσουμε
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

    // Κλάση για τις ρυθμίσεις του agent
    public class AgentSettings
    {
        public string AgentId { get; set; }
        public string Location { get; set; }
    }

    // Custom retry policy για το SignalR reconnect
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
            {
                return null;
            }

            // Προσθήκη ενός μικρού τυχαίου χρόνου για να αποφύγουμε συγχρονισμένες επαναπροσπάθειες
            var random = new Random();
            var jitter = random.Next(-1000, 1000);

            return TimeSpan.FromMilliseconds(_delayMs + jitter);
        }
    }
}