using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PrinterAgent.Core;

namespace PrinterAgentService
{
    /// <summary>
    /// Print spooler service that manages print jobs from various sources
    /// and routes them to the appropriate printer.
    /// </summary>
    public class PrintSpoolerService
    {
        private readonly ILogger<PrintSpoolerService> _logger;
        private readonly ConcurrentQueue<PrintJob> _printQueue = new ConcurrentQueue<PrintJob>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private Task _processingTask;
        private readonly SemaphoreSlim _printSemaphore = new SemaphoreSlim(1, 1);
        private readonly string _spoolDirectory;

        public PrintSpoolerService(ILogger<PrintSpoolerService> logger)
        {
            _logger = logger;

            // Create spool directory in the current application path
            _spoolDirectory = Path.Combine(AppContext.BaseDirectory, "SpoolFiles");
            if (!Directory.Exists(_spoolDirectory))
            {
                Directory.CreateDirectory(_spoolDirectory);
            }

            // Start the processing task
            _processingTask = Task.Run(() => ProcessPrintQueueAsync(_cancellationTokenSource.Token));

            _logger.LogInformation("Print spooler service initialized with spool directory: {SpoolDirectory}", _spoolDirectory);
        }

        /// <summary>
        /// Enqueues a print job to be processed
        /// </summary>
        public async Task<string> EnqueuePrintJobAsync(PrintJob printJob)
        {
            try
            {
                // Generate a unique job ID
                printJob.JobId ??= Guid.NewGuid().ToString();
                printJob.Status = PrintJobStatus.Queued;
                printJob.QueueTime = DateTime.UtcNow;

                // Save document data to spool file if it's provided as bytes
                if (printJob.DocumentData != null && printJob.DocumentData.Length > 0)
                {
                    string spoolFile = Path.Combine(_spoolDirectory, $"{printJob.JobId}{GetExtensionFromFormat(printJob.DocumentFormat)}");
                    await File.WriteAllBytesAsync(spoolFile, printJob.DocumentData);
                    printJob.SpoolFilePath = spoolFile;
                    // Clear the data to avoid keeping large byte arrays in memory
                    printJob.DocumentData = null;
                }

                // Add to queue
                _printQueue.Enqueue(printJob);
                _logger.LogInformation("Print job {JobId} queued for printer {PrinterName}", printJob.JobId, printJob.PrinterName);

                return printJob.JobId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enqueueing print job: {Message}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Gets the status of a specific print job
        /// </summary>
        public PrintJobStatus GetPrintJobStatus(string jobId)
        {
            // Not implemented yet - would require storing job status in a repository
            return PrintJobStatus.Unknown;
        }

        /// <summary>
        /// Cancels the print spooler service
        /// </summary>
        public async Task StopAsync()
        {
            _cancellationTokenSource.Cancel();
            if (_processingTask != null)
            {
                await _processingTask;
            }
            _logger.LogInformation("Print spooler service stopped");
        }

        /// <summary>
        /// Process print jobs from the queue
        /// </summary>
        private async Task ProcessPrintQueueAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Print queue processor started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_printQueue.TryDequeue(out PrintJob job))
                    {
                        await ProcessJobAsync(job);
                    }
                    else
                    {
                        // Wait a bit before checking queue again
                        await Task.Delay(500, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal cancellation
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in print queue processor: {Message}", ex.Message);
                    await Task.Delay(1000, cancellationToken); // Delay to avoid tight loop on repeated errors
                }
            }

            _logger.LogInformation("Print queue processor stopped");
        }

        /// <summary>
        /// Process a single print job
        /// </summary>
        private async Task ProcessJobAsync(PrintJob job)
        {
            job.Status = PrintJobStatus.Processing;
            _logger.LogInformation("Processing print job {JobId} for printer {PrinterName}", job.JobId, job.PrinterName);

            try
            {
                // Ensure only one print job is processed at a time
                await _printSemaphore.WaitAsync();

                try
                {
                    // Get document data if a spool file was used
                    byte[] documentData = null;
                    if (!string.IsNullOrEmpty(job.SpoolFilePath) && File.Exists(job.SpoolFilePath))
                    {
                        documentData = await File.ReadAllBytesAsync(job.SpoolFilePath);
                    }
                    else if (!string.IsNullOrEmpty(job.DocumentPath) && File.Exists(job.DocumentPath))
                    {
                        documentData = await File.ReadAllBytesAsync(job.DocumentPath);
                    }
                    else if (job.DocumentData != null)
                    {
                        documentData = job.DocumentData;
                    }

                    if (documentData == null && string.IsNullOrEmpty(job.DocumentContent))
                    {
                        throw new InvalidOperationException("No document data or content available for printing");
                    }

                    // Print based on format
                    bool success = await PrintDocumentAsync(job, documentData);

                    if (success)
                    {
                        job.Status = PrintJobStatus.Completed;
                        _logger.LogInformation("Print job {JobId} completed successfully", job.JobId);
                    }
                    else
                    {
                        job.Status = PrintJobStatus.Failed;
                        _logger.LogWarning("Print job {JobId} failed", job.JobId);
                    }
                }
                finally
                {
                    _printSemaphore.Release();

                    // Clean up spool file
                    if (!string.IsNullOrEmpty(job.SpoolFilePath) && File.Exists(job.SpoolFilePath))
                    {
                        try
                        {
                            File.Delete(job.SpoolFilePath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete spool file: {Path}", job.SpoolFilePath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                job.Status = PrintJobStatus.Failed;
                job.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Error processing print job {JobId}: {Message}", job.JobId, ex.Message);
            }
        }

        /// <summary>
        /// Prints a document based on its format
        /// </summary>
        private async Task<bool> PrintDocumentAsync(PrintJob job, byte[] documentData)
        {
            switch (job.DocumentFormat)
            {
                case DocumentFormat.PlainText:
                    return PrinterHelper.SendTestPrint(
                        job.PrinterName,
                        job.DocumentContent ?? "",
                        null,
                        job.Landscape,
                        job.PaperSize);

                case DocumentFormat.Rtf:
                    return PrinterHelper.PrintRtf(
                        job.PrinterName,
                        job.DocumentContent ?? "",
                        job.Landscape,
                        job.PaperSize);

                case DocumentFormat.Raw:
                case DocumentFormat.Pdf:
                case DocumentFormat.Xps:
                case DocumentFormat.Image:
                case DocumentFormat.Office:
                    // Use the UniversalPrinter to handle advanced formats
                    return await UniversalPrinter.PrintAsync(
                        job.PrinterName,
                        documentData,
                        job.DocumentFormat,
                        job.Landscape,
                        job.PaperSize);

                default:
                    _logger.LogWarning("Unsupported document format: {Format}", job.DocumentFormat);
                    return false;
            }
        }

        /// <summary>
        /// Gets the file extension for a document format
        /// </summary>
        private string GetExtensionFromFormat(DocumentFormat format)
        {
            return format switch
            {
                DocumentFormat.PlainText => ".txt",
                DocumentFormat.Rtf => ".rtf",
                DocumentFormat.Pdf => ".pdf",
                DocumentFormat.Xps => ".xps",
                DocumentFormat.Image => ".bin",
                DocumentFormat.Office => ".bin",
                DocumentFormat.Raw => ".bin",
                _ => ".dat"
            };
        }
    }

    /// <summary>
    /// Represents a print job in the system
    /// </summary>
    public class PrintJob
    {
        // Job metadata
        public string JobId { get; set; }
        public string PrinterName { get; set; }
        public DateTime QueueTime { get; set; }
        public string SubmittedBy { get; set; }
        public PrintJobStatus Status { get; set; }
        public string ErrorMessage { get; set; }

        // Print options
        public bool Landscape { get; set; }
        public string PaperSize { get; set; } = "A4";
        public DocumentFormat DocumentFormat { get; set; }

        // Document source - one of these should be set
        public string DocumentContent { get; set; }
        public byte[] DocumentData { get; set; }
        public string DocumentPath { get; set; }

        // Internal state
        public string SpoolFilePath { get; set; }
    }

    /// <summary>
    /// Defines the status of a print job
    /// </summary>
    public enum PrintJobStatus
    {
        Unknown,
        Queued,
        Processing,
        Completed,
        Failed,
        Canceled
    }

    /// <summary>
    /// Defines supported document formats
    /// </summary>
    public enum DocumentFormat
    {
        PlainText,
        Rtf,
        Pdf,
        Xps,
        Image,
        Office,
        Raw
    }
}