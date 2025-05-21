using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Runtime.Versioning;
using System.Printing;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.IO;

namespace PrinterAgent.Core
{
    // Windows-only printing utilities.
    [SupportedOSPlatform("windows")]
    public static class PrinterHelper
    {
        // Static lock for RTF printing
        private static readonly object _rtfPrintLock = new object();

        /// <summary>Get installed printers on the machine.</summary>
        public static List<PrinterInfo> GetInstalledPrinters()
        {
            var server = new LocalPrintServer();
            var queues = server.GetPrintQueues(new[] {
                EnumeratedPrintQueueTypes.Local,
                EnumeratedPrintQueueTypes.Connections
            });
            var list = new List<PrinterInfo>();
            foreach (var pq in queues)
            {
                // Refresh to get current status
                pq.Refresh();
                string status;
                if (pq.IsPaused || pq.QueueStatus.HasFlag(PrintQueueStatus.Paused))
                    status = "Paused";
                else if (pq.QueueStatus.HasFlag(PrintQueueStatus.Offline))
                    status = "Offline";
                else if (pq.QueueStatus.HasFlag(PrintQueueStatus.PaperJam))
                    status = "Jammed";
                else
                    status = "Online";

                // Λήψη πληροφοριών driver
                string driverName = "Unknown";
                try
                {
                    if (pq.QueueDriver != null && !string.IsNullOrEmpty(pq.QueueDriver.Name))
                    {
                        driverName = pq.QueueDriver.Name;
                    }
                }
                catch
                {
                    // Αγνοούμε τυχόν σφάλματα
                }

                // Λήψη IP και ping test
                string ipAddress = "Not Available";
                int responseTime = -1; // Χρήση -1 αντί για null για να δείξουμε ότι δεν έχουμε τιμή

                try
                {
                    // Προσπάθεια εξαγωγής IP από το port name
                    string portName = pq.QueuePort?.Name ?? "";
                    Console.WriteLine($"Printer: {pq.Name}, Port: {portName}"); // Debug logging

                    // Handle format: "IP_192.168.1.1"
                    if (portName.StartsWith("IP_"))
                    {
                        ipAddress = portName.Substring(3);
                        // Try ping
                        PingIPAddress(ipAddress, ref responseTime);
                    }
                    // Handle format: "192.168.1.1"
                    else if (System.Net.IPAddress.TryParse(portName, out _))
                    {
                        ipAddress = portName;
                        // Try ping
                        PingIPAddress(ipAddress, ref responseTime);
                    }
                    // Handle format: "192.168.14.16_ip" or similar IP formats with suffix
                    else if (portName.Contains(".") && portName.Split('.').Length == 4)
                    {
                        // Try to extract IP pattern from port name
                        var ipPattern = @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})";
                        var match = Regex.Match(portName, ipPattern);
                        if (match.Success && System.Net.IPAddress.TryParse(match.Groups[1].Value, out _))
                        {
                            ipAddress = match.Groups[1].Value;
                            Console.WriteLine($"Extracted IP {ipAddress} from port {portName}"); // Debug

                            // Try ping
                            PingIPAddress(ipAddress, ref responseTime);
                        }
                    }
                    // Additional check for TCP/IP in the port name
                    else if (portName.Contains("TCP") || portName.Contains("IP"))
                    {
                        // Check if there's anything that looks like an IP in the name
                        var ipPattern = @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})";
                        var match = Regex.Match(portName, ipPattern);
                        if (match.Success && System.Net.IPAddress.TryParse(match.Groups[1].Value, out _))
                        {
                            ipAddress = match.Groups[1].Value;
                            Console.WriteLine($"Extracted IP {ipAddress} from TCP/IP port {portName}"); // Debug

                            // Try ping
                            PingIPAddress(ipAddress, ref responseTime);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log exception but continue
                    Console.WriteLine($"Error extracting IP: {ex.Message}");
                }

                list.Add(new PrinterInfo
                {
                    Name = pq.Name,
                    Status = status,
                    DriverName = driverName,
                    IPAddress = ipAddress,
                    ResponseTime = responseTime
                });
            }
            return list;
        }

        private static void PingIPAddress(string ipAddress, ref int responseTime)
        {
            try
            {
                using (var ping = new System.Net.NetworkInformation.Ping())
                {
                    var reply = ping.Send(ipAddress, 1000); // 1s timeout
                    if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                    {
                        responseTime = (int)reply.RoundtripTime;
                        Console.WriteLine($"Successful ping to {ipAddress}: {responseTime}ms"); // Debug
                    }
                    else
                    {
                        Console.WriteLine($"Failed ping to {ipAddress}: {reply.Status}"); // Debug
                    }
                }
            }
            catch (Exception ex)
            {
                // Αγνοούμε σφάλματα ping
                Console.WriteLine($"Ping error for {ipAddress}: {ex.Message}"); // Debug
            }
        }

        /// <summary>
        /// Print plain text and optional image to the specified printer.
        /// </summary>
        public static bool SendTestPrint(
            string printerName,
            string documentText,
            Image imageToPrint,
            bool landscape,
            string paperSizeName)
        {
            if (string.IsNullOrWhiteSpace(printerName)) return false;
            try
            {
                using var pd = new PrintDocument();
                pd.PrinterSettings.PrinterName = printerName;
                if (!pd.PrinterSettings.IsValid)
                {
                    Console.WriteLine($"Printer '{printerName}' is invalid.");
                    return false;
                }
                pd.DefaultPageSettings.Landscape = landscape;
                pd.DefaultPageSettings.PaperSize = paperSizeName.Equals("Letter", StringComparison.OrdinalIgnoreCase)
                    ? new PaperSize("Letter", 850, 1100)
                    : new PaperSize("A4", 827, 1169);

                pd.PrintPage += (s, e) =>
                {
                    float margin = 40, width = e.PageBounds.Width - margin * 2, y = margin;
                    using var font = new Font("Arial", 12);
                    e.Graphics.DrawString(documentText ?? string.Empty, font, Brushes.Black, new RectangleF(margin, y, width, 200));
                    if (imageToPrint != null)
                    {
                        y += 220;
                        float size = 150, x = (e.PageBounds.Width - size) / 2;
                        e.Graphics.DrawImage(imageToPrint, new RectangleF(x, y, size, size));
                    }
                };
                pd.Print();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Print error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Print RTF content using shell printing instead of RichTextBox
        /// </summary>
        public static bool PrintRtf(
            string printerName,
            string rtfContent,
            bool landscape,
            string paperSizeName)
        {
            if (string.IsNullOrWhiteSpace(printerName)) return false;

            // Static lock to prevent concurrent access
            lock (_rtfPrintLock)
            {
                string tempRtfPath = Path.Combine(Path.GetTempPath(), $"PrintJob_{Guid.NewGuid()}.rtf");

                try
                {
                    // Write RTF content to temporary file
                    File.WriteAllText(tempRtfPath, rtfContent, System.Text.Encoding.Default);

                    // Try shell printing first
                    if (TryShellPrintRtf(printerName, tempRtfPath))
                    {
                        Console.WriteLine("RTF printed successfully via shell");
                        return true;
                    }

                    // Try Word automation as fallback
                    if (TryWordPrintRtf(printerName, tempRtfPath))
                    {
                        Console.WriteLine("RTF printed successfully via Word automation");
                        return true;
                    }

                    // Last resort: convert to plain text and print
                    Console.WriteLine("Trying plain text fallback for RTF");
                    return TryPlainTextPrintRtf(printerName, rtfContent, landscape, paperSizeName);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("RTF print error: " + ex.Message);
                    return false;
                }
                finally
                {
                    // Clean up temporary file
                    CleanupTempFileRtf(tempRtfPath);
                }
            }
        }

        // Shell printing method for RTF
        private static bool TryShellPrintRtf(string printerName, string filePath)
        {
            try
            {
                Console.WriteLine($"Attempting shell print: {filePath} to {printerName}");

                using (var process = new System.Diagnostics.Process())
                {
                    process.StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "rundll32.exe",
                        Arguments = $"shell32.dll,ShellExec_RunDLL printto \"{filePath}\" \"{printerName}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    process.Start();

                    // Read any output for debugging
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();

                    if (!string.IsNullOrEmpty(output))
                    {
                        Console.WriteLine($"Shell print output: {output}");
                    }

                    if (!string.IsNullOrEmpty(error))
                    {
                        Console.WriteLine($"Shell print error output: {error}");
                    }

                    bool finished = process.WaitForExit(30000); // 30 second timeout

                    if (!finished)
                    {
                        Console.WriteLine("Shell print process timed out");
                        try { process.Kill(); } catch { }
                        return false;
                    }

                    Console.WriteLine($"Shell print exit code: {process.ExitCode}");
                    return (process.ExitCode == 0);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Shell print exception: {ex.Message}");
                return false;
            }
        }

        // Word automation method for RTF
        private static bool TryWordPrintRtf(string printerName, string filePath)
        {
            dynamic wordApp = null;
            dynamic doc = null;

            try
            {
                Console.WriteLine($"Attempting Word automation print: {filePath} to {printerName}");

                // Create Word application instance
                Type wordType = Type.GetTypeFromProgID("Word.Application");
                if (wordType == null)
                {
                    Console.WriteLine("Microsoft Word is not installed");
                    return false;
                }

                wordApp = Activator.CreateInstance(wordType);
                wordApp.Visible = false;
                wordApp.DisplayAlerts = false;

                // Open the RTF document
                doc = wordApp.Documents.Open(filePath, ReadOnly: true);

                // Set the printer
                wordApp.ActivePrinter = printerName;

                // Print the document
                doc.PrintOut(
                    Background: false,
                    Copies: 1,
                    Range: 0, // wdPrintAllDocument
                    PrintToFile: false,
                    Collate: true
                );

                // Wait for print to complete
                System.Threading.Thread.Sleep(2000);

                Console.WriteLine("Word automation print completed");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Word automation error: {ex.Message}");
                return false;
            }
            finally
            {
                // Clean up COM objects
                try
                {
                    if (doc != null)
                    {
                        doc.Close(false);
                        System.Runtime.InteropServices.Marshal.ReleaseComObject(doc);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error closing Word document: {ex.Message}");
                }

                try
                {
                    if (wordApp != null)
                    {
                        wordApp.Quit(false);
                        System.Runtime.InteropServices.Marshal.ReleaseComObject(wordApp);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error closing Word application: {ex.Message}");
                }

                // Force garbage collection
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        // Plain text fallback method
        private static bool TryPlainTextPrintRtf(string printerName, string rtfContent, bool landscape, string paperSizeName)
        {
            try
            {
                Console.WriteLine("Converting RTF to plain text for fallback printing");

                // Convert RTF to plain text (basic conversion)
                string plainText = ConvertRtfToPlainTextHelper(rtfContent);

                Console.WriteLine($"Converted text length: {plainText.Length} characters");

                // Use the existing SendTestPrint method
                return SendTestPrint(printerName, plainText, null, landscape, paperSizeName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Plain text fallback error: {ex.Message}");
                return false;
            }
        }

        // Helper method to convert RTF to plain text
        private static string ConvertRtfToPlainTextHelper(string rtfText)
        {
            try
            {
                // Remove RTF control words and formatting
                string text = rtfText;

                // Remove RTF header
                if (text.StartsWith(@"{\rtf"))
                {
                    int firstSpace = text.IndexOf(' ');
                    if (firstSpace > 0)
                    {
                        text = text.Substring(firstSpace);
                    }
                }

                // Remove control words
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\\[a-zA-Z0-9]+(-?[0-9]+)?[ ]?", "");

                // Remove braces
                text = text.Replace("{", "").Replace("}", "");

                // Remove escape sequences
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\\'[0-9a-fA-F]{2}", " ");

                // Clean up whitespace
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

                return text;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RTF to text conversion error: {ex.Message}");
                return "Error converting RTF content";
            }
        }

        // Helper method to clean up temporary files
        private static void CleanupTempFileRtf(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;

            // Try to delete the file up to 5 times with delays
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    File.Delete(filePath);
                    Console.WriteLine($"Successfully deleted temp file: {filePath}");
                    return; // Success
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"File cleanup attempt {i + 1} failed: {ex.Message}");
                    if (i < 4) // Not the last attempt
                    {
                        System.Threading.Thread.Sleep(500); // Wait 500ms
                    }
                }
            }

            Console.WriteLine($"Failed to delete temporary RTF file: {filePath}");
        }
    }
}