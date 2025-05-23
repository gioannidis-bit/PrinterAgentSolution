﻿using Microsoft.AspNetCore.SignalR.Client;
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
        private const int MAX_RECONNECT_ATTEMPTS = 5;
        private const int RECONNECT_DELAY_MS = 5000;
        private readonly HttpClientHandler _handler;
        private bool _serverWasDown = false;
        private Timer _serverCheckTimer;
        private readonly TimeSpan _serverCheckInterval = TimeSpan.FromSeconds(5);
        private readonly PrintSpoolerService _printSpooler;

        // Server endpoints
        private const string ServerBase = "https://print.hitweb.com.gr";
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

            // Initialize the print spooler service
            // Δημιουργία print spooler service
            _printSpooler = new PrintSpoolerService(new LoggerFactory().CreateLogger<PrintSpoolerService>());

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
                _logger.LogInformation("===== Saving to path: {Path} =====", settingsPath);

                var settings = new AgentSettings
                {
                    AgentId = _agentId,
                    Location = _location
                };

                _logger.LogInformation("===== Settings object created with Location: {Location} =====", settings.Location);

                var options = new JsonSerializerOptions { WriteIndented = true };
                var settingsJson = JsonSerializer.Serialize(settings, options);

                _logger.LogInformation("===== JSON serialized: {Json} =====", settingsJson);

                File.WriteAllText(settingsPath, settingsJson);

                _logger.LogInformation("===== File written successfully =====");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "===== ERROR in SaveAgentSettings: {Message} =====", ex.Message);
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
                    Timeout = TimeSpan.FromSeconds(5) // Αύξηση από 3
                };

                var response = await httpClient.GetAsync($"{ServerBase}/health", HttpCompletionOption.ResponseHeadersRead);
                bool serverIsUp = response.IsSuccessStatusCode;

                if (serverIsUp)
                {
                    if (_serverWasDown || _hub == null || _hub.State != HubConnectionState.Connected)
                    {
                        _logger.LogInformation("Server is back online. Attempting immediate reconnection...");
                        _serverWasDown = false;
                        _reconnectAttempts = 0;
                        await ReconnectNowAsync();
                    }
                }
                else
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

            try
            {
                _logger.LogInformation("===== Starting immediate reconnection procedure =====");
                await InitializeHubConnection();
                await _hub.StartAsync();
                _logger.LogInformation("Successfully reconnected to server");

                // Εγγραφή του agent
                await RegisterAgent(CancellationToken.None);

                // Επιπλέον, στείλτε άμεσα ένα heartbeat για να ενημερωθεί η κατάσταση
                await SendHeartbeatAsync(CancellationToken.None);

                _reconnectAttempts = 0;
                _serverWasDown = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reconnect immediately");
                _serverWasDown = true;
            }
        }


        // 2. Νέα μέθοδος SendHeartbeatAsync που μπορεί να κληθεί άμεσα
        private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("===== Sending immediate heartbeat =====");

                // Πρώτα στείλτε το heartbeat μέσω SignalR
                if (_hub.State == HubConnectionState.Connected)
                {
                    try
                    {
                        await _hub.InvokeAsync("Heartbeat", _agentId, cancellationToken);
                        _logger.LogInformation("===== SignalR heartbeat sent =====");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "===== Error sending SignalR heartbeat =====");
                    }
                }

                // Στη συνέχεια στείλτε το HTTP POST για πλήρη ενημέρωση δεδομένων
                using var httpClient = new HttpClient(_handler, disposeHandler: false);
                var printers = PrinterHelper.GetInstalledPrinters();

                var payload = new
                {
                    AgentId = _agentId,
                    MachineName = Environment.MachineName,
                    Location = _location,
                    Timestamp = DateTime.UtcNow,
                    Printers = printers,
                    IsOnline = true
                };

                // Χρησιμοποιούμε PascalCase για τα properties
                var options = new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
                    PropertyNamingPolicy = null // Διατηρούμε το PascalCase
                };

                var json = JsonSerializer.Serialize(payload, options);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var resp = await httpClient.PostAsync(_dataUrl, content, cancellationToken);

                if (resp.IsSuccessStatusCode)
                    _logger.LogInformation("===== HTTP heartbeat sent successfully =====");
                else
                {
                    _logger.LogWarning("===== HTTP heartbeat failed: {Status} =====", resp.StatusCode);
                    try
                    {
                        var errorBody = await resp.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogWarning("===== Error details: {Details} =====", errorBody);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "===== Error during manual heartbeat =====");
            }
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

            // Basic text/RTF print handler
            _hub.On<PrintRequest>("Print", req =>
            {
                _logger.LogInformation("Print request received: Printer={Printer}", req.PrinterName);

                // Create print job
                var printJob = new PrintJob
                {
                    JobId = Guid.NewGuid().ToString(),
                    PrinterName = req.PrinterName,
                    DocumentContent = req.DocumentContent,
                    DocumentFormat = req.DocumentContent.Contains("\\rtf") ? DocumentFormat.Rtf : DocumentFormat.PlainText,
                    Landscape = req.Landscape,
                    PaperSize = req.PaperSize
                };

                // Enqueue for printing
                _ = _printSpooler.EnqueuePrintJobAsync(printJob);
            });

            // New universal print handler
            _hub.On<UniversalPrintRequest>("UniversalPrint", async req =>
            {
                _logger.LogInformation("Universal print request received: Printer={Printer}, Format={Format}",
                    req.PrinterName, req.DocumentFormat);

                // Convert format string to enum
                DocumentFormat format = DocumentFormat.PlainText;
                if (Enum.TryParse<DocumentFormat>(req.DocumentFormat, true, out var parsedFormat))
                {
                    format = parsedFormat;
                }

                // Create print job
                var printJob = new PrintJob
                {
                    JobId = Guid.NewGuid().ToString(),
                    PrinterName = req.PrinterName,
                    DocumentData = req.DocumentData,
                    DocumentFormat = format,
                    Landscape = req.Landscape,
                    PaperSize = req.PaperSize
                };

                // Enqueue for printing
                await _printSpooler.EnqueuePrintJobAsync(printJob);
            });

            _hub.On<string>("UpdateLocation", async newLocation =>
            {
                _logger.LogInformation("===== Location update request received: {NewLocation} =====", newLocation);
                _location = newLocation;
                _logger.LogInformation("===== Location variable updated: {Location} =====", _location);

                // Αποθήκευση με έλεγχο επιτυχίας
                try
                {
                    SaveAgentSettings();
                    _logger.LogInformation("===== Settings saved successfully =====");

                    // Διαβάστε ξανά το αρχείο για επιβεβαίωση
                    var settingsPath = Path.Combine(AppContext.BaseDirectory, "agent.settings.json");
                    if (File.Exists(settingsPath))
                    {
                        var settingsJson = File.ReadAllText(settingsPath);
                        var settings = JsonSerializer.Deserialize<AgentSettings>(settingsJson);
                        _logger.LogInformation("===== Verification: Read location from file: {Location} =====", settings.Location);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "===== ERROR SAVING SETTINGS =====");
                }

                try
                {
                    await _hub.InvokeAsync("LocationUpdated", _agentId, _location);
                    _logger.LogInformation("===== Location update confirmed to server: {Location} =====", _location);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "===== Error confirming location update =====");
                }
            });
        }

        // 4. Βελτιωμένο ConnectToHub
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

        // 3. Βελτιωμένο RegisterAgent
        private async Task RegisterAgent(CancellationToken cancellationToken)
        {
            try
            {
                // Βεβαιωνόμαστε ότι έχουμε μια τοποθεσία για να στείλουμε
                if (string.IsNullOrEmpty(_location))
                {
                    _location = Environment.MachineName;
                    SaveAgentSettings();
                    _logger.LogInformation("===== Location was empty, using machine name: {Location} =====", _location);
                }

                _logger.LogInformation("===== Registering agent with ID: {AgentId}, Location: {Location} =====",
                                     _agentId, _location);

                // Εγγραφή του agent
                await _hub.InvokeAsync("RegisterAgent", _agentId, Environment.MachineName, _location, cancellationToken);
                _logger.LogInformation("===== Agent ID '{AgentId}' registered successfully =====", _agentId);

                // Στείλτε τους εκτυπωτές
                var printers = PrinterHelper.GetInstalledPrinters();
                await _hub.InvokeAsync("UpdatePrinters", _agentId, printers, cancellationToken);
                _logger.LogInformation("===== Sent initial printer list with {Count} printers =====", printers.Count);

                // ΣΗΜΑΝΤΙΚΟ: Στείλτε επίσης ένα HTTP POST για πλήρη ενημέρωση των δεδομένων
                // Αυτό είναι απαραίτητο γιατί το UI βασίζεται στα δεδομένα από το HTTP endpoint
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

                var options = new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
                    PropertyNamingPolicy = null
                };

                var json = JsonSerializer.Serialize(payload, options);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var resp = await httpClient.PostAsync(_dataUrl, content, cancellationToken);

                if (resp.IsSuccessStatusCode)
                    _logger.LogInformation("===== Initial HTTP data update sent successfully =====");
                else
                    _logger.LogWarning("===== Initial HTTP data update failed: {Status} =====", resp.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "===== Error registering agent =====");
                throw;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker running at: {Time}", DateTimeOffset.Now);

            // Αρχικός έλεγχος σύνδεσης
            await CheckServerAndReconnectAsync();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_hub.State != HubConnectionState.Connected)
                    {
                        _logger.LogWarning("Hub not connected (state: {State}), attempting to reconnect", _hub.State);
                        try
                        {
                            await ConnectToHub(stoppingToken);
                        }
                        catch
                        {
                            // Άμεσος έλεγχος αν ο server είναι διαθέσιμος
                            await CheckServerAndReconnectAsync();
                            await Task.Delay(RECONNECT_DELAY_MS, stoppingToken);
                            continue;
                        }
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

                    await SendHeartbeatAsync(stoppingToken);
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

            // Stop the print spooler
            await _printSpooler.StopAsync();

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

    // Model for universal print requests from the hub
    public class UniversalPrintRequest
    {
        public string AgentId { get; set; }
        public string MachineName { get; set; }
        public string PrinterName { get; set; }
        public byte[] DocumentData { get; set; }
        public string DocumentFormat { get; set; }
        public bool Landscape { get; set; }
        public string PaperSize { get; set; }
        public string Location { get; set; }
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