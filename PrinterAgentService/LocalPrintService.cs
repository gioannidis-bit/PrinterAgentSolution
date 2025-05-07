using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrinterAgent.Core;

namespace PrinterAgentService
{
    /// <summary>
    /// Service that listens for local print requests via HTTP and named pipes
    /// This allows applications on the local machine to send print jobs to the agent
    /// </summary>
    public class LocalPrintService : IHostedService
    {
        private readonly ILogger<LocalPrintService> _logger;
        private readonly PrintSpoolerService _printSpooler;
        private HttpListener _httpListener;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _httpListenerTask;
        private Task _namedPipeListenerTask;
        private bool _isRunning = false;

        // Default port for the local HTTP API
        private const int DEFAULT_PORT = 18632;

        // Named pipe name
        private const string PIPE_NAME = "PrinterAgentPipe";

        public LocalPrintService(ILogger<LocalPrintService> logger, PrintSpoolerService printSpooler)
        {
            _logger = logger;
            _printSpooler = printSpooler;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting local print service");

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                // Start HTTP listener
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://localhost:{DEFAULT_PORT}/");
                _httpListener.Prefixes.Add($"http://127.0.0.1:{DEFAULT_PORT}/");
                _httpListener.Start();

                _logger.LogInformation("HTTP listener started on port {Port}", DEFAULT_PORT);

                // Start the HTTP listener task
                _httpListenerTask = Task.Run(() => HttpListenerLoop(_cancellationTokenSource.Token));

                // Start the named pipe listener task
                _namedPipeListenerTask = Task.Run(() => NamedPipeListenerLoop(_cancellationTokenSource.Token));

                _isRunning = true;

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting local print service");
                return Task.FromException(ex);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (!_isRunning)
                return;

            _logger.LogInformation("Stopping local print service");

            try
            {
                // Cancel ongoing operations
                _cancellationTokenSource.Cancel();

                // Wait for tasks to complete with timeout
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                var allTasks = Task.WhenAll(_httpListenerTask, _namedPipeListenerTask);

                await Task.WhenAny(allTasks, timeoutTask);

                // Stop HTTP listener
                if (_httpListener != null && _httpListener.IsListening)
                {
                    _httpListener.Stop();
                    _httpListener.Close();
                }

                _isRunning = false;

                _logger.LogInformation("Local print service stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping local print service");
            }
        }

        /// <summary>
        /// Main HTTP listener loop
        /// </summary>
        private async Task HttpListenerLoop(CancellationToken cancellationToken)
        {
            _logger.LogInformation("HTTP listener loop started");

            while (!cancellationToken.IsCancellationRequested && _httpListener.IsListening)
            {
                try
                {
                    // Wait for a request
                    var context = await _httpListener.GetContextAsync();

                    // Process the request in a separate task
                    _ = Task.Run(() => ProcessHttpRequest(context), cancellationToken);
                }
                catch (HttpListenerException ex)
                {
                    if (_httpListener.IsListening)
                    {
                        _logger.LogError(ex, "Error in HTTP listener");
                    }
                    // Otherwise the listener was stopped, which is expected during shutdown
                }
                catch (OperationCanceledException)
                {
                    // Normal cancellation
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in HTTP listener");
                    await Task.Delay(1000, cancellationToken); // Prevent tight loop on errors
                }
            }

            _logger.LogInformation("HTTP listener loop stopped");
        }

        /// <summary>
        /// Process an HTTP request
        /// </summary>
        private async Task ProcessHttpRequest(HttpListenerContext context)
        {
            using var response = context.Response;

            try
            {
                var request = context.Request;
                var path = request.Url.AbsolutePath;

                _logger.LogInformation("Received HTTP request: {Method} {Path}", request.HttpMethod, path);

                // Health check endpoint
                if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "GET")
                {
                    await SendJsonResponse(response, new { Status = "OK" });
                    return;
                }

                // List printers endpoint
                if (path.Equals("/printers", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "GET")
                {
                    var printers = PrinterHelper.GetInstalledPrinters();
                    await SendJsonResponse(response, printers);
                    return;
                }

                // Print text endpoint
                if (path.Equals("/print/text", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "POST")
                {
                    await HandleTextPrintRequest(request, response);
                    return;
                }

                // Print file endpoint
                if (path.Equals("/print/file", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "POST")
                {
                    await HandleFilePrintRequest(request, response);
                    return;
                }

                // Handle job status endpoint
                if (path.StartsWith("/job/", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "GET")
                {
                    var jobId = path.Substring("/job/".Length);
                    if (!string.IsNullOrEmpty(jobId))
                    {
                        var status = _printSpooler.GetPrintJobStatus(jobId);
                        await SendJsonResponse(response, new { JobId = jobId, Status = status.ToString() });
                        return;
                    }
                }

                // Not found for any other path
                response.StatusCode = (int)HttpStatusCode.NotFound;
                await SendJsonResponse(response, new { Error = "Endpoint not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing HTTP request");
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await SendJsonResponse(response, new { Error = ex.Message });
            }
        }

        /// <summary>
        /// Handle a text print request
        /// </summary>
        private async Task HandleTextPrintRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                // Read the request body
                var requestBody = await ReadRequestBody(request);

                // Parse as a text print request
                var printRequest = JsonSerializer.Deserialize<TextPrintRequest>(requestBody, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (string.IsNullOrEmpty(printRequest.PrinterName))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await SendJsonResponse(response, new { Error = "Printer name is required" });
                    return;
                }

                // Create a print job
                var job = new PrintJob
                {
                    JobId = Guid.NewGuid().ToString(),
                    PrinterName = printRequest.PrinterName,
                    DocumentContent = printRequest.Content,
                    DocumentFormat = printRequest.IsRtf ? DocumentFormat.Rtf : DocumentFormat.PlainText,
                    Landscape = printRequest.Landscape,
                    PaperSize = string.IsNullOrEmpty(printRequest.PaperSize) ? "A4" : printRequest.PaperSize,
                    SubmittedBy = "LocalAPI"
                };

                // Enqueue the job
                var jobId = await _printSpooler.EnqueuePrintJobAsync(job);

                // Return the job ID
                await SendJsonResponse(response, new { JobId = jobId });
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error parsing text print request");
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await SendJsonResponse(response, new { Error = "Invalid request format: " + ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling text print request");
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await SendJsonResponse(response, new { Error = ex.Message });
            }
        }

        /// <summary>
        /// Handle a file print request
        /// </summary>
        private async Task HandleFilePrintRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                // Check content type for multipart form data
                if (!request.ContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = (int)HttpStatusCode.UnsupportedMediaType;
                    await SendJsonResponse(response, new { Error = "Content type must be multipart/form-data" });
                    return;
                }

                // Process multipart form data
                var fileData = await ProcessMultipartFormData(request);

                if (fileData.FileBytes == null || fileData.FileBytes.Length == 0)
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await SendJsonResponse(response, new { Error = "No file data provided" });
                    return;
                }

                if (string.IsNullOrEmpty(fileData.PrinterName))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await SendJsonResponse(response, new { Error = "Printer name is required" });
                    return;
                }

                // Determine document format
                DocumentFormat format = GetDocumentFormat(fileData.FileName);

                // Create a print job
                var job = new PrintJob
                {
                    JobId = Guid.NewGuid().ToString(),
                    PrinterName = fileData.PrinterName,
                    DocumentData = fileData.FileBytes,
                    DocumentFormat = format,
                    Landscape = fileData.Landscape,
                    PaperSize = string.IsNullOrEmpty(fileData.PaperSize) ? "A4" : fileData.PaperSize,
                    SubmittedBy = "LocalAPI"
                };

                // Enqueue the job
                var jobId = await _printSpooler.EnqueuePrintJobAsync(job);

                // Return the job ID
                await SendJsonResponse(response, new { JobId = jobId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling file print request");
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await SendJsonResponse(response, new { Error = ex.Message });
            }
        }

        /// <summary>
        /// Process multipart form data to extract file and metadata
        /// </summary>
        private async Task<FileUploadData> ProcessMultipartFormData(HttpListenerRequest request)
        {
            var result = new FileUploadData();

            try
            {
                // Read the boundary from content type
                string boundary = "--" + request.ContentType.Split('=')[1];

                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                string line;

                // Skip to first boundary
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (line == boundary)
                        break;
                }

                // Read parts
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (line.StartsWith("Content-Disposition"))
                    {
                        // Parse content disposition
                        string contentDisposition = line;
                        string name = ExtractFieldName(contentDisposition);

                        // Check if this is a file
                        if (contentDisposition.Contains("filename="))
                        {
                            result.FileName = ExtractFileName(contentDisposition);

                            // Skip Content-Type line and empty line
                            await reader.ReadLineAsync(); // Content-Type
                            await reader.ReadLineAsync(); // Empty line

                            // Read file data
                            using var memoryStream = new MemoryStream();
                            bool endOfFile = false;

                            while (!endOfFile)
                            {
                                // Check for boundary
                                var buffer = new char[boundary.Length + 4]; // +4 for possible "--\r\n"
                                var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);

                                if (read == 0)
                                    break;

                                string chunk = new string(buffer, 0, read);

                                if (chunk.StartsWith(boundary))
                                {
                                    endOfFile = true;
                                }
                                else
                                {
                                    // Add data to memory stream
                                    byte[] data = request.ContentEncoding.GetBytes(chunk);
                                    memoryStream.Write(data, 0, data.Length);

                                    // Read more data
                                    int nextByte;
                                    while ((nextByte = reader.Read()) != -1)
                                    {
                                        // Check for boundary
                                        if (nextByte == boundary[0])
                                        {
                                            bool isBoundary = true;
                                            for (int i = 1; i < boundary.Length; i++)
                                            {
                                                if (reader.Read() != boundary[i])
                                                {
                                                    isBoundary = false;
                                                    break;
                                                }
                                            }

                                            if (isBoundary)
                                            {
                                                endOfFile = true;
                                                break;
                                            }
                                        }

                                        // Add byte to memory stream
                                        memoryStream.WriteByte((byte)nextByte);
                                    }
                                }
                            }

                            // Get file bytes
                            result.FileBytes = memoryStream.ToArray();
                        }
                        else
                        {
                            // For form fields
                            await reader.ReadLineAsync(); // Skip empty line
                            string value = await reader.ReadLineAsync();

                            // Process form field
                            switch (name?.ToLowerInvariant())
                            {
                                case "printername":
                                    result.PrinterName = value;
                                    break;
                                case "landscape":
                                    result.Landscape = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                                    break;
                                case "papersize":
                                    result.PaperSize = value;
                                    break;
                            }

                            // Skip boundary
                            await reader.ReadLineAsync();
                        }
                    }

                    // End of form data
                    if (line.StartsWith(boundary + "--"))
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing multipart form data");
            }

            return result;
        }

        /// <summary>
        /// Extract field name from content disposition
        /// </summary>
        private string ExtractFieldName(string contentDisposition)
        {
            var nameMatch = System.Text.RegularExpressions.Regex.Match(contentDisposition, "name=\"([^\"]+)\"");
            return nameMatch.Success ? nameMatch.Groups[1].Value : null;
        }

        /// <summary>
        /// Extract file name from content disposition
        /// </summary>
        private string ExtractFileName(string contentDisposition)
        {
            var fileNameMatch = System.Text.RegularExpressions.Regex.Match(contentDisposition, "filename=\"([^\"]+)\"");
            return fileNameMatch.Success ? fileNameMatch.Groups[1].Value : null;
        }

        /// <summary>
        /// Read the request body as a string
        /// </summary>
        private async Task<string> ReadRequestBody(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            return await reader.ReadToEndAsync();
        }

        /// <summary>
        /// Send a JSON response
        /// </summary>
        private async Task SendJsonResponse(HttpListenerResponse response, object data)
        {
            response.ContentType = "application/json";
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;

            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Main named pipe listener loop
        /// </summary>
        private async Task NamedPipeListenerLoop(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Named pipe listener loop started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Create a new named pipe server
                    using var pipeServer = new NamedPipeServerStream(
                        PIPE_NAME,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous);

                    _logger.LogInformation("Waiting for named pipe client connection...");

                    // Wait for a client to connect
                    await pipeServer.WaitForConnectionAsync(cancellationToken);

                    _logger.LogInformation("Named pipe client connected");

                    // Process the client request
                    await ProcessNamedPipeRequest(pipeServer, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Normal cancellation
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in named pipe listener");
                    await Task.Delay(1000, cancellationToken); // Prevent tight loop on errors
                }
            }

            _logger.LogInformation("Named pipe listener loop stopped");
        }

        /// <summary>
        /// Process a named pipe request
        /// </summary>
        private async Task ProcessNamedPipeRequest(NamedPipeServerStream pipeServer, CancellationToken cancellationToken)
        {
            try
            {
                using var reader = new StreamReader(pipeServer, Encoding.UTF8, false, 1024, true);
                using var writer = new StreamWriter(pipeServer, Encoding.UTF8, 1024, true);

                // Read the command
                var command = await reader.ReadLineAsync();

                if (string.IsNullOrEmpty(command))
                {
                    await writer.WriteLineAsync("ERROR: Empty command");
                    await writer.FlushAsync();
                    return;
                }

                _logger.LogInformation("Received named pipe command: {Command}", command);

                // Process the command
                if (command.Equals("PRINTERS", StringComparison.OrdinalIgnoreCase))
                {
                    // List available printers
                    var printers = PrinterHelper.GetInstalledPrinters();
                    var json = JsonSerializer.Serialize(printers);
                    await writer.WriteLineAsync(json);
                    await writer.FlushAsync();
                }
                else if (command.StartsWith("PRINT_TEXT:", StringComparison.OrdinalIgnoreCase))
                {
                    // Print text command
                    var json = command.Substring("PRINT_TEXT:".Length);
                    var printRequest = JsonSerializer.Deserialize<TextPrintRequest>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    var jobId = await HandleNamedPipeTextPrint(printRequest);
                    await writer.WriteLineAsync($"OK:{jobId}");
                    await writer.FlushAsync();
                }
                else if (command.StartsWith("PRINT_FILE:", StringComparison.OrdinalIgnoreCase))
                {
                    // Print file command
                    var json = command.Substring("PRINT_FILE:".Length);
                    var printRequest = JsonSerializer.Deserialize<FilePrintRequest>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    var jobId = await HandleNamedPipeFilePrint(printRequest, reader);
                    await writer.WriteLineAsync($"OK:{jobId}");
                    await writer.FlushAsync();
                }
                else if (command.StartsWith("JOB_STATUS:", StringComparison.OrdinalIgnoreCase))
                {
                    // Job status command
                    var jobId = command.Substring("JOB_STATUS:".Length);
                    var status = _printSpooler.GetPrintJobStatus(jobId);
                    await writer.WriteLineAsync($"STATUS:{status}");
                    await writer.FlushAsync();
                }
                else
                {
                    // Unknown command
                    await writer.WriteLineAsync("ERROR:Unknown command");
                    await writer.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing named pipe request");
                try
                {
                    using var writer = new StreamWriter(pipeServer);
                    await writer.WriteLineAsync($"ERROR:{ex.Message}");
                    await writer.FlushAsync();
                }
                catch { }
            }
        }

        /// <summary>
        /// Handle a text print request from named pipe
        /// </summary>
        private async Task<string> HandleNamedPipeTextPrint(TextPrintRequest request)
        {
            // Create a print job
            var job = new PrintJob
            {
                JobId = Guid.NewGuid().ToString(),
                PrinterName = request.PrinterName,
                DocumentContent = request.Content,
                DocumentFormat = request.IsRtf ? DocumentFormat.Rtf : DocumentFormat.PlainText,
                Landscape = request.Landscape,
                PaperSize = string.IsNullOrEmpty(request.PaperSize) ? "A4" : request.PaperSize,
                SubmittedBy = "NamedPipe"
            };

            // Enqueue the job
            return await _printSpooler.EnqueuePrintJobAsync(job);
        }

        /// <summary>
        /// Handle a file print request from named pipe
        /// </summary>
        private async Task<string> HandleNamedPipeFilePrint(FilePrintRequest request, StreamReader reader)
        {
            // Read the file data
            var base64Data = await reader.ReadLineAsync();
            byte[] fileData = Convert.FromBase64String(base64Data);

            // Determine document format
            DocumentFormat format = GetDocumentFormat(request.FileName);

            // Create a print job
            var job = new PrintJob
            {
                JobId = Guid.NewGuid().ToString(),
                PrinterName = request.PrinterName,
                DocumentData = fileData,
                DocumentFormat = format,
                Landscape = request.Landscape,
                PaperSize = string.IsNullOrEmpty(request.PaperSize) ? "A4" : request.PaperSize,
                SubmittedBy = "NamedPipe"
            };

            // Enqueue the job
            return await _printSpooler.EnqueuePrintJobAsync(job);
        }

        /// <summary>
        /// Determine document format based on file extension
        /// </summary>
        private DocumentFormat GetDocumentFormat(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return DocumentFormat.PlainText;

            string extension = Path.GetExtension(fileName).ToLowerInvariant();

            return extension switch
            {
                ".txt" => DocumentFormat.PlainText,
                ".rtf" => DocumentFormat.Rtf,
                ".pdf" => DocumentFormat.Pdf,
                ".xps" => DocumentFormat.Xps,
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" => DocumentFormat.Image,
                ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" => DocumentFormat.Office,
                _ => DocumentFormat.Raw
            };
        }
    }

    /// <summary>
    /// Model for text print requests
    /// </summary>
    public class TextPrintRequest
    {
        public string PrinterName { get; set; }
        public string Content { get; set; }
        public bool IsRtf { get; set; }
        public bool Landscape { get; set; }
        public string PaperSize { get; set; }
    }

    /// <summary>
    /// Model for file print requests
    /// </summary>
    public class FilePrintRequest
    {
        public string PrinterName { get; set; }
        public string FileName { get; set; }
        public bool Landscape { get; set; }
        public string PaperSize { get; set; }
    }

    /// <summary>
    /// Model for file upload data
    /// </summary>
    public class FileUploadData
    {
        public string PrinterName { get; set; }
        public string FileName { get; set; }
        public byte[] FileBytes { get; set; }
        public bool Landscape { get; set; }
        public string PaperSize { get; set; }
    }
}