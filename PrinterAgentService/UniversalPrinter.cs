using System;
using System.Drawing;
using System.Drawing.Printing;
using System.Text;
using System.Threading;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;
using System.Management;
using System.Runtime.Versioning;
using System.Collections.Generic;
using System.Printing;
using PrinterAgent.Core;

namespace PrinterAgentService
{
    /// <summary>
    /// Provides universal printing capabilities for various document formats
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class UniversalPrinter
    {
        #region Native Methods for Raw Printing

        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, ref PRINTER_DEFAULTS pDefault);

        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DOC_INFO_1 docInfo);

        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PRINTER_DEFAULTS
        {
            public IntPtr pDatatype;
            public IntPtr pDevMode;
            public int DesiredAccess;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DOC_INFO_1
        {
            public string pDocName;
            public string pOutputFile;
            public string pDatatype;
        }

        // Access rights
        private const int PRINTER_ACCESS_USE = 0x00000008;
        private const int PRINTER_ACCESS_ADMINISTER = 0x00000004;

        #endregion

        // Στατικό αντικείμενο κλειδώματος για RTF printing
        private static readonly object _printLock = new object();

        /// <summary>
        /// Prints a document of any supported format
        /// </summary>
        /// <param name="printerName">Name of the printer to print to</param>
        /// <param name="documentData">Raw document data bytes</param>
        /// <param name="format">Format of the document</param>
        /// <param name="landscape">Whether to print in landscape mode</param>
        /// <param name="paperSizeName">Name of the paper size (e.g., "A4", "Letter")</param>
        /// <returns>True if printing was successful, false otherwise</returns>
        public static async Task<bool> PrintAsync(string printerName, byte[] documentData, DocumentFormat format, bool landscape, string paperSizeName)
        {
            if (string.IsNullOrWhiteSpace(printerName))
                return false;

            if (documentData == null || documentData.Length == 0)
                return false;

            try
            {
                switch (format)
                {
                    case DocumentFormat.Pdf:
                        return await PrintPdfAsync(printerName, documentData, landscape, paperSizeName);

                    case DocumentFormat.Xps:
                        return await PrintXpsAsync(printerName, documentData, landscape, paperSizeName);

                    case DocumentFormat.Image:
                        return PrintImage(printerName, documentData, landscape, paperSizeName);

                    case DocumentFormat.Rtf:
                        // Decode the incoming bytes into an RTF string and hand off to our RTF printer
                        var rtfText = Encoding.Default.GetString(documentData);
                        return PrintRtf(printerName, rtfText);

                    case DocumentFormat.Raw:
                        return SendRawDataToPrinter(printerName, documentData);

                    case DocumentFormat.Office:
                        return await PrintOfficeDocumentAsync(printerName, documentData, landscape, paperSizeName);

                    default:
                        Console.WriteLine($"Unsupported format: {format}");
                        return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Print error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Prints RTF content without using RichTextBox to avoid threading issues
        /// </summary>
        private static bool PrintRtf(string printerName, string rtfContent)
        {
            // Κλείδωμα για να αποτρέψουμε ταυτόχρονες κλήσεις
            lock (_printLock)
            {
                string tempRtfPath = Path.Combine(Path.GetTempPath(), $"PrintJob_{Guid.NewGuid()}.rtf");

                try
                {
                    // Γράφουμε το RTF σε temporary file
                    File.WriteAllText(tempRtfPath, rtfContent, Encoding.Default);

                    // Προσπαθούμε πρώτα με shell printing (πιο αξιόπιστο)
                    if (TryShellPrint(printerName, tempRtfPath))
                    {
                        return true;
                    }

                    // Αν το shell printing αποτύχει, δοκιμάζουμε Word automation
                    if (TryWordAutomationPrint(printerName, tempRtfPath))
                    {
                        return true;
                    }

                    // Τελευταία επιλογή: Μετατροπή σε plain text και εκτύπωση
                    return TryPlainTextFallback(printerName, rtfContent);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"RTF print error: {ex.Message}");
                    return false;
                }
                finally
                {
                    // Καθαρισμός temporary file
                    CleanupTempFile(tempRtfPath);
                }
            }
        }

        // Μέθοδος για shell printing (πιο αξιόπιστη)
        private static bool TryShellPrint(string printerName, string filePath)
        {
            try
            {
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

                    // Διαβάζουμε output για debugging
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();

                    if (!string.IsNullOrEmpty(error))
                    {
                        Console.WriteLine($"Shell print stderr: {error}");
                    }

                    bool finished = process.WaitForExit(30000); // 30 second timeout

                    if (!finished)
                    {
                        Console.WriteLine("Shell print process timed out");
                        try { process.Kill(); } catch { }
                        return false;
                    }

                    bool success = (process.ExitCode == 0);
                    if (success)
                    {
                        Console.WriteLine("Shell print completed successfully");
                    }
                    else
                    {
                        Console.WriteLine($"Shell print failed with exit code: {process.ExitCode}");
                    }

                    return success;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Shell print error: {ex.Message}");
                return false;
            }
        }

        // Μέθοδος για Word automation printing
        private static bool TryWordAutomationPrint(string printerName, string filePath)
        {
            dynamic wordApp = null;
            dynamic doc = null;

            try
            {
                // Δημιουργούμε instance του Word
                Type wordType = Type.GetTypeFromProgID("Word.Application");
                if (wordType == null)
                {
                    Console.WriteLine("Microsoft Word is not installed");
                    return false;
                }

                wordApp = Activator.CreateInstance(wordType);
                wordApp.Visible = false;
                wordApp.DisplayAlerts = false; // Απενεργοποιούμε alerts

                // Ανοίγουμε το RTF document
                doc = wordApp.Documents.Open(filePath, ReadOnly: true);

                // Ρυθμίζουμε τον εκτυπωτή
                wordApp.ActivePrinter = printerName;

                // Εκτυπώνουμε το έγγραφο
                doc.PrintOut(
                    Background: false,  // Περιμένουμε να ολοκληρωθεί η εκτύπωση
                    Copies: 1,
                    Range: 0, // wdPrintAllDocument
                    OutputFileName: "",
                    From: "",
                    To: "",
                    Item: 7, // wdPrintDocumentContent
                    PrintToFile: false,
                    Collate: true,
                    FileName: "",
                    ManualDuplexPrint: false
                );

                // Περιμένουμε λίγο για να ολοκληρωθεί η εκτύπωση
                System.Threading.Thread.Sleep(2000);

                Console.WriteLine("Word automation print completed successfully");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Word automation error: {ex.Message}");
                return false;
            }
            finally
            {
                // Καθαρισμός COM objects
                try
                {
                    if (doc != null)
                    {
                        doc.Close(false); // Κλείνουμε χωρίς αποθήκευση
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
                        wordApp.Quit(false); // Κλείνουμε χωρίς αποθήκευση
                        System.Runtime.InteropServices.Marshal.ReleaseComObject(wordApp);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error closing Word application: {ex.Message}");
                }

                // Επιβάλλουμε garbage collection για καθαρισμό COM objects
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        // Fallback μέθοδος: Μετατροπή RTF σε plain text και εκτύπωση
        private static bool TryPlainTextFallback(string printerName, string rtfContent)
        {
            try
            {
                Console.WriteLine("Attempting plain text fallback for RTF printing");

                // Απλούστευση RTF σε plain text (βασική μετατροπή)
                string plainText = ConvertRtfToPlainText(rtfContent);

                // Χρησιμοποιούμε την υπάρχουσα μέθοδο για εκτύπωση plain text
                return PrinterHelper.SendTestPrint(printerName, plainText, null, false, "A4");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Plain text fallback error: {ex.Message}");
                return false;
            }
        }

        // Βοηθητική μέθοδος για μετατροπή RTF σε plain text (βασική)
        private static string ConvertRtfToPlainText(string rtfText)
        {
            try
            {
                // Αφαιρούμε RTF control words και formatting
                string text = rtfText;

                // Αφαιρούμε RTF header
                if (text.StartsWith(@"{\rtf"))
                {
                    int firstSpace = text.IndexOf(' ');
                    if (firstSpace > 0)
                    {
                        text = text.Substring(firstSpace);
                    }
                }

                // Αφαιρούμε control words
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\\[a-zA-Z0-9]+(-?[0-9]+)?[ ]?", "");

                // Αφαιρούμε άγκιστρα
                text = text.Replace("{", "").Replace("}", "");

                // Αφαιρούμε escape sequences
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\\'[0-9a-fA-F]{2}", " ");

                // Καθαρίζουμε whitespace
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

                return text;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RTF to text conversion error: {ex.Message}");
                return "Error converting RTF content to text";
            }
        }

        // Βοηθητική μέθοδος για καθαρισμό temporary files
        private static void CleanupTempFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;

            // Δοκιμάζουμε να διαγράψουμε το αρχείο μέχρι 5 φορές με καθυστέρηση
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    File.Delete(filePath);
                    return; // Επιτυχής διαγραφή
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Cleanup attempt {i + 1} failed: {ex.Message}");
                    if (i < 4) // Αν δεν είναι η τελευταία προσπάθεια
                    {
                        System.Threading.Thread.Sleep(500); // Περιμένουμε 500ms
                    }
                }
            }

            Console.WriteLine($"Failed to delete temporary file: {filePath}");
        }

        /// <summary>
        /// Prints a PDF document
        /// </summary>
        private static async Task<bool> PrintPdfAsync(string printerName, byte[] pdfData, bool landscape, string paperSizeName)
        {
            // Save to temp file
            string tempPdfPath = Path.Combine(Path.GetTempPath(), $"PrintJob_{Guid.NewGuid()}.pdf");
            try
            {
                await File.WriteAllBytesAsync(tempPdfPath, pdfData);

                // Try direct PDF printing via XPS if available
                bool success = false;

                // First check if XPS printing is supported
                if (HasXpsPrintSupport(printerName))
                {
                    success = await PrintPdfViaXpsAsync(printerName, tempPdfPath, landscape, paperSizeName);
                }

                // If XPS didn't work, try external PDF reader
                if (!success)
                {
                    success = await PrintPdfViaExternalAppAsync(printerName, tempPdfPath, landscape, paperSizeName);
                }

                return success;
            }
            finally
            {
                // Clean up temp file
                if (File.Exists(tempPdfPath))
                {
                    try { File.Delete(tempPdfPath); } catch { /* Ignore cleanup errors */ }
                }
            }
        }

        /// <summary>
        /// Try to print PDF via XPS document API if supported
        /// </summary>
        private static async Task<bool> PrintPdfViaXpsAsync(string printerName, string pdfPath, bool landscape, string paperSizeName)
        {
            // This is a placeholder for converting PDF to XPS and printing
            // In a real implementation, you would convert PDF to XPS and print using XPS API
            // For now, return false to fall back to external PDF reader
            await Task.Delay(0); // Suppress compiler warning about async method with no await
            return false;
        }

        /// <summary>
        /// Print PDF via an external PDF application
        /// </summary>
        private static async Task<bool> PrintPdfViaExternalAppAsync(string printerName, string pdfPath, bool landscape, string paperSizeName)
        {
            // Find PDF viewer applications 
            List<string> pdfReaders = FindPdfReaders();

            if (pdfReaders.Count == 0)
            {
                Console.WriteLine("No PDF reader applications found");
                return false;
            }

            // Try each reader until one works
            foreach (string pdfReader in pdfReaders)
            {
                try
                {
                    using var process = new System.Diagnostics.Process();
                    process.StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = pdfReader,
                        Arguments = $"/p /h \"{pdfPath}\" \"{printerName}\"", // Common print parameters, may need adjustment
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    process.Start();
                    await process.WaitForExitAsync();

                    if (process.ExitCode == 0)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error using PDF reader {pdfReader}: {ex.Message}");
                }
            }

            return false;
        }

        /// <summary>
        /// Find PDF reader applications on the system
        /// </summary>
        private static List<string> FindPdfReaders()
        {
            var readers = new List<string>();

            // Common PDF reader paths
            string[] commonReaders = {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
                @"C:\Program Files (x86)\Adobe\Acrobat Reader DC\Reader\AcroRd32.exe",
                @"C:\Program Files\SumatraPDF\SumatraPDF.exe",
                @"C:\Program Files (x86)\SumatraPDF\SumatraPDF.exe"
            };

            foreach (string reader in commonReaders)
            {
                if (File.Exists(reader))
                {
                    readers.Add(reader);
                }
            }

            return readers;
        }

        /// <summary>
        /// Prints an XPS document
        /// </summary>
        private static async Task<bool> PrintXpsAsync(string printerName, byte[] xpsData, bool landscape, string paperSizeName)
        {
            string tempXpsPath = Path.Combine(Path.GetTempPath(), $"PrintJob_{Guid.NewGuid()}.xps");

            try
            {
                await File.WriteAllBytesAsync(tempXpsPath, xpsData);

                using (var xpsDoc = new XpsDocument(tempXpsPath, FileAccess.Read))
                {
                    var xpsDocumentWriter = PrintQueue.CreateXpsDocumentWriter(new System.Printing.PrintQueue(
                        new System.Printing.LocalPrintServer(), printerName));

                    if (xpsDocumentWriter != null)
                    {
                        xpsDocumentWriter.Write(xpsDoc.GetFixedDocumentSequence());
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"XPS print error: {ex.Message}");
                return false;
            }
            finally
            {
                // Clean up
                if (File.Exists(tempXpsPath))
                {
                    try { File.Delete(tempXpsPath); } catch { /* Ignore cleanup errors */ }
                }
            }
        }

        /// <summary>
        /// Prints an image
        /// </summary>
        private static bool PrintImage(string printerName, byte[] imageData, bool landscape, string paperSizeName)
        {
            try
            {
                using var memoryStream = new MemoryStream(imageData);
                using var image = Image.FromStream(memoryStream);

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

                pd.PrintPage += (sender, e) =>
                {
                    // Calculate the scaling to fit the image on the page
                    var pageWidth = e.PageBounds.Width - 100; // Margins
                    var pageHeight = e.PageBounds.Height - 100;
                    var imageWidth = image.Width;
                    var imageHeight = image.Height;

                    float ratio = Math.Min((float)pageWidth / imageWidth, (float)pageHeight / imageHeight);
                    int newWidth = (int)(imageWidth * ratio);
                    int newHeight = (int)(imageHeight * ratio);

                    int x = (e.PageBounds.Width - newWidth) / 2;
                    int y = (e.PageBounds.Height - newHeight) / 2;

                    e.Graphics.DrawImage(image, x, y, newWidth, newHeight);
                };

                pd.Print();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Image print error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Prints an Office document using interop or external applications
        /// </summary>
        private static async Task<bool> PrintOfficeDocumentAsync(string printerName, byte[] documentData, bool landscape, string paperSizeName)
        {
            // For simplicity, we'll save to a temp file and use shell printing
            string tempPath = Path.Combine(Path.GetTempPath(), $"PrintJob_{Guid.NewGuid()}.doc");

            try
            {
                await File.WriteAllBytesAsync(tempPath, documentData);

                // Use shell verb for printing
                using var process = new System.Diagnostics.Process();
                process.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "rundll32.exe",
                    Arguments = $"shell32.dll,ShellExec_RunDLL printto \"{tempPath}\" \"{printerName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                process.Start();
                await process.WaitForExitAsync();

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Office document print error: {ex.Message}");
                return false;
            }
            finally
            {
                // Clean up
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* Ignore cleanup errors */ }
                }
            }
        }

        /// <summary>
        /// Sends raw printer data directly to the printer
        /// </summary>
        private static bool SendRawDataToPrinter(string printerName, byte[] rawData)
        {
            IntPtr printerHandle = IntPtr.Zero;

            try
            {
                // Διορθωμένος κώδικας για το OpenPrinter
                var pd = new PRINTER_DEFAULTS
                {
                    pDatatype = IntPtr.Zero,
                    pDevMode = IntPtr.Zero,
                    DesiredAccess = PRINTER_ACCESS_USE
                };

                // Call OpenPrinter with out-handle first, then ref defaults
                if (!OpenPrinter(printerName, out printerHandle, ref pd))
                {
                    Console.WriteLine($"Failed to open printer: {printerName}");
                    return false;
                }

                // Χρήση αναφοράς για το di επίσης
                var di = new DOC_INFO_1
                {
                    pDocName = "RAW Document",
                    pOutputFile = null,
                    pDatatype = "RAW"
                };

                if (!StartDocPrinter(printerHandle, 1, ref di))
                {
                    Console.WriteLine("Failed to start document");
                    return false;
                }

                if (!StartPagePrinter(printerHandle))
                {
                    EndDocPrinter(printerHandle);
                    Console.WriteLine("Failed to start page");
                    return false;
                }

                // Allocate unmanaged memory and copy data
                IntPtr unmanagedData = Marshal.AllocHGlobal(rawData.Length);
                Marshal.Copy(rawData, 0, unmanagedData, rawData.Length);

                // Write the data
                if (!WritePrinter(printerHandle, unmanagedData, rawData.Length, out int bytesWritten))
                {
                    Marshal.FreeHGlobal(unmanagedData);
                    EndPagePrinter(printerHandle);
                    EndDocPrinter(printerHandle);
                    Console.WriteLine("Failed to write to printer");
                    return false;
                }

                Marshal.FreeHGlobal(unmanagedData);

                // End the page and document
                if (!EndPagePrinter(printerHandle) || !EndDocPrinter(printerHandle))
                {
                    Console.WriteLine("Failed to end page or document");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Raw print error: {ex.Message}");
                return false;
            }
            finally
            {
                // Close the printer handle
                if (printerHandle != IntPtr.Zero)
                {
                    ClosePrinter(printerHandle);
                }
            }
        }

        /// <summary>
        /// Checks if the printer supports XPS printing
        /// </summary>
        private static bool HasXpsPrintSupport(string printerName)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_Printer WHERE Name = '{printerName.Replace("'", "\\'")}'");

                foreach (var printer in searcher.Get())
                {
                    var properties = printer.Properties;

                    // Check for XPS support based on printer drivers or capabilities
                    // This is a simplified check that could be expanded based on specific requirements
                    var driverName = properties["DriverName"].Value?.ToString() ?? "";

                    // Most modern Windows printers support XPS to some degree
                    if (driverName.Contains("XPS") || driverName.Contains("Microsoft"))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking XPS support: {ex.Message}");
                return false;
            }
        }
    }
}