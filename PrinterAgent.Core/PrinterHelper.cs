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

namespace PrinterAgent.Core
{
    // Windows-only printing utilities.
    [SupportedOSPlatform("windows")]
    public static class PrinterHelper
    {
        // Win32 structures for RTF rendering
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct CHARRANGE { public int cpMin, cpMax; }
        [StructLayout(LayoutKind.Sequential)]
        private struct FORMATRANGE
        {
            public IntPtr hdc;
            public IntPtr hdcTarget;
            public RECT rc;
            public RECT rcPage;
            public CHARRANGE charrange;
        }
        private const int WM_USER = 0x0400;
        private const int EM_FORMATRANGE = WM_USER + 57;
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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
        /// Print RTF content using a hidden RichTextBox for formatting.
        /// </summary>
        public static bool PrintRtf(
            string printerName,
            string rtfContent,
            bool landscape,
            string paperSizeName)
        {
            if (string.IsNullOrWhiteSpace(printerName)) return false;
            try
            {
                using var rtb = new RichTextBox { Rtf = rtfContent };
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

                int startChar = 0;
                pd.PrintPage += (s, e) =>
                {
                    float twips = 1440 / e.Graphics.DpiX;
                    var mb = e.MarginBounds;
                    RECT rectPrint = new()
                    {
                        Left = (int)(mb.Left * twips),
                        Top = (int)(mb.Top * twips),
                        Right = (int)(mb.Right * twips),
                        Bottom = (int)(mb.Bottom * twips)
                    };
                    var pb = e.PageBounds;
                    RECT rectPage = new()
                    {
                        Left = 0,
                        Top = 0,
                        Right = (int)(pb.Width * twips),
                        Bottom = (int)(pb.Height * twips)
                    };
                    CHARRANGE cr = new() { cpMin = startChar, cpMax = rtb.TextLength };
                    FORMATRANGE fr = new()
                    {
                        hdc = e.Graphics.GetHdc(),
                        hdcTarget = e.Graphics.GetHdc(),
                        rc = rectPrint,
                        rcPage = rectPage,
                        charrange = cr
                    };
                    IntPtr lParam = Marshal.AllocCoTaskMem(Marshal.SizeOf(fr));
                    Marshal.StructureToPtr(fr, lParam, false);
                    IntPtr res = SendMessage(rtb.Handle, EM_FORMATRANGE, (IntPtr)1, lParam);
                    e.Graphics.ReleaseHdc(fr.hdc);
                    e.Graphics.ReleaseHdc(fr.hdcTarget);
                    Marshal.FreeCoTaskMem(lParam);
                    startChar = res.ToInt32();
                    e.HasMorePages = startChar < rtb.TextLength;
                };
                pd.Print();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("RTF print error: " + ex.Message);
                return false;
            }
        }
    }
}