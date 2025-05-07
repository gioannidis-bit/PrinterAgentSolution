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


        // Στατικό αντικείμενο κλειδώματος
        private static readonly object _printLock = new object();

        private static bool PrintRtf(string printerName, string rtfContent)
        {
            // Κλείδωμα για να αποτρέψουμε ταυτόχρονες κλήσεις
            lock (_printLock)
            {
                // Create a unique print job name
                string documentName = $"PrintJob_{Guid.NewGuid()}";

                try
                {
                    // Convert RTF string to byte array
                    byte[] rtfBytes = Encoding.Default.GetBytes(rtfContent);

                    // Print RTF as raw data
                    return SendRawDataToPrinter(printerName, rtfBytes);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"RTF direct print error: {ex.Message}");

                    // If direct print fails, try shell printing as a last resort
                    try
                    {
                        string tempRtfPath = Path.Combine(Path.GetTempPath(), $"{documentName}.rtf");
                        File.WriteAllBytes(tempRtfPath, Encoding.Default.GetBytes(rtfContent));

                        // Set up a process to print using the shell
                        using (var process = new System.Diagnostics.Process())
                        {
                            process.StartInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "cmd.exe",
                                Arguments = $"/c print \"{tempRtfPath}\" /d:\"{printerName}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            };

                            process.Start();

                            // Read output and error
                            string output = process.StandardOutput.ReadToEnd();
                            string error = process.StandardError.ReadToEnd();

                            if (!string.IsNullOrEmpty(error))
                            {
                                Console.WriteLine($"CMD print error: {error}");
                            }

                            process.WaitForExit(30000);

                            // Clean up temp file
                            try { File.Delete(tempRtfPath); } catch { }

                            return (process.ExitCode == 0);
                        }
                    }
                    catch (Exception shellEx)
                    {
                        Console.WriteLine($"Shell print fallback error: {shellEx.Message}");
                        return false;
                    }
                }
            }
        }

        private static bool ConvertRtfToDocxUsingWord(string rtfPath, string docxPath)
        {
            dynamic wordApp = null;
            dynamic doc = null;

            try
            {
                // Create a new instance of Word
                Type wordType = Type.GetTypeFromProgID("Word.Application");
                if (wordType == null)
                {
                    Console.WriteLine("Microsoft Word is not installed.");
                    return false;
                }

                wordApp = Activator.CreateInstance(wordType);
                wordApp.Visible = false;

                // Open the RTF document
                doc = wordApp.Documents.Open(rtfPath);

                // Save as DOCX
                object saveFormat = 16; // wdFormatDocumentDefault (DOCX)
                doc.SaveAs2(docxPath, saveFormat);

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
                if (doc != null)
                {
                    doc.Close(false);
                    Marshal.ReleaseComObject(doc);
                }

                if (wordApp != null)
                {
                    wordApp.Quit();
                    Marshal.ReleaseComObject(wordApp);
                }

                // Force garbage collection to release COM objects
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private static bool PrintDocxUsingDocXLibrary(string printerName, string docxPath)
        {
            try
            {
                // Create a separate thread for printing
                var thread = new Thread(() =>
                {
                    try
                    {
                        // Load the DOCX document using the DocX library
                        using (var document = Xceed.Words.NET.DocX.Load(docxPath))
                        {
                            // Create a PrintDocument
                            using (var pd = new PrintDocument())
                            {
                                pd.PrinterSettings.PrinterName = printerName;
                                pd.PrintController = new StandardPrintController();

                                if (!pd.PrinterSettings.IsValid)
                                {
                                    Console.WriteLine($"Printer '{printerName}' is invalid.");
                                    return;
                                }

                                // TODO: Set up a proper print handler for DocX content
                                // This is a simplified approach - we'll use shell printing instead

                                // For now, save and close the document
                                document.Save();
                            }
                        }

                        // Use shell printing as it's more reliable for complex documents
                        using (var process = new System.Diagnostics.Process())
                        {
                            process.StartInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "rundll32.exe",
                                Arguments = $"shell32.dll,ShellExec_RunDLL printto \"{docxPath}\" \"{printerName}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };

                            process.Start();
                            process.WaitForExit(30000); // 30 second timeout
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"DOCX print error: {ex.Message}");
                    }
                });

                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                thread.Join(60000); // 60 second timeout

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DOCX print error: {ex.Message}");
                return false;
            }
        }

        private static bool PrintUsingShell(string printerName, string filePath)
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
                        CreateNoWindow = true
                    };

                    process.Start();
                    if (!process.WaitForExit(30000)) // 30 second timeout
                    {
                        Console.WriteLine("Print process timed out");
                        return false;
                    }

                    return (process.ExitCode == 0);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Shell print error: {ex.Message}");
                return false;
            }
        }

        private static void CleanupTempFiles(string[] filePaths)
        {
            foreach (var filePath in filePaths)
            {
                if (File.Exists(filePath))
                {
                    // Try a few times to delete the file in case it's locked
                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            File.Delete(filePath);
                            break; // File deleted successfully
                        }
                        catch
                        {
                            // Wait a bit before trying again
                            Thread.Sleep(500);
                        }
                    }
                }
            }
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
        // Τμήμα του UniversalPrinter.cs με τη διορθωμένη μέθοδο SendRawDataToPrinter

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

                //printerHandle = pd.pDevMode;

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