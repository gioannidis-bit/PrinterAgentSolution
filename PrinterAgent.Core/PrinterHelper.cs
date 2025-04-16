using System;
using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Runtime.Versioning;

namespace PrinterAgent.Core
{
    // Marking as Windows-only since System.Drawing.Printing is not cross-platform.
    [SupportedOSPlatform("windows6.1")]
    public static class PrinterHelper
    {
        // Structures used for sending the EM_FORMATRANGE message.
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CHARRANGE
        {
            public int cpMin;    // First character of range (0 for start of doc)
            public int cpMax;    // Last character of range (-1 for end of doc)
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FORMATRANGE
        {
            public IntPtr hdc;           // Actual device context to render on
            public IntPtr hdcTarget;     // Target device context for determining text formatting
            public RECT rc;              // Region of the DC to render to (in twips)
            public RECT rcPage;          // Region of the whole page (in twips)
            public CHARRANGE charrange;  // Range of text to render
        }

        private const int WM_USER = 0x0400;
        private const int EM_FORMATRANGE = WM_USER + 57;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // Returns the list of installed printers.
        public static List<string> GetInstalledPrinters()
        {
            List<string> printers = new List<string>();
            foreach (string printer in PrinterSettings.InstalledPrinters)
            {
                printers.Add(printer);
            }
            return printers;
        }

        /// <summary>
        /// Prints the provided RTF content using a hidden RichTextBox.
        /// Supports rich text (formatted text and images).
        /// </summary>
        /// <param name="printerName">Name of the printer to print to.</param>
        /// <param name="rtfContent">RTF-formatted content to print.</param>
        /// <param name="landscape">True for landscape orientation; false for portrait.</param>
        /// <param name="paperSizeName">Paper size identifier ("A4" or "Letter").</param>
        /// <returns>True if printing was successful; otherwise, false.</returns>
        public static bool PrintRtf(string printerName, string rtfContent, bool landscape, string paperSizeName)
        {
            if (string.IsNullOrWhiteSpace(printerName))
                return false;

            try
            {
                // Create a hidden RichTextBox to load the RTF content.
                using (RichTextBox rtb = new RichTextBox())
                {
                    rtb.Rtf = rtfContent;

                    using (PrintDocument pd = new PrintDocument())
                    {
                        pd.PrinterSettings.PrinterName = printerName;
                        if (!pd.PrinterSettings.IsValid)
                        {
                            Console.WriteLine($"The printer '{printerName}' is not valid on this machine.");
                            return false;
                        }

                        // Set page orientation.
                        pd.DefaultPageSettings.Landscape = landscape;

                        // Map paper size.
                        PaperSize ps;
                        if (paperSizeName.Equals("Letter", StringComparison.OrdinalIgnoreCase))
                        {
                            // Letter size: approx. 8.5 x 11 inches → dimensions in hundredths of an inch.
                            ps = new PaperSize("Letter", 850, 1100);
                        }
                        else
                        {
                            // Default to A4: approx. 8.27 x 11.69 inches.
                            ps = new PaperSize("A4", 827, 1169);
                        }
                        pd.DefaultPageSettings.PaperSize = ps;

                        int startChar = 0; // Starting character index for printing.
                        pd.PrintPage += (sender, e) =>
                        {
                            // Conversion factor: twips per pixel.
                            // 1 inch = 1440 twips, and typically 96 pixels per inch → 1 pixel ≈ 15 twips.
                            float twipsPerPixel = 1440 / e.Graphics.DpiX;

                            // Define the area to print: convert margins (in pixels) to twips.
                            RECT rectToPrint = new RECT
                            {
                                Left = (int)(e.MarginBounds.Left * twipsPerPixel),
                                Top = (int)(e.MarginBounds.Top * twipsPerPixel),
                                Right = (int)(e.MarginBounds.Right * twipsPerPixel),
                                Bottom = (int)(e.MarginBounds.Bottom * twipsPerPixel)
                            };

                            // Define the whole page area in twips.
                            RECT rectPage = new RECT
                            {
                                Left = 0,
                                Top = 0,
                                Right = (int)(e.PageBounds.Width * twipsPerPixel),
                                Bottom = (int)(e.PageBounds.Height * twipsPerPixel)
                            };

                            // Set up the character range that we want to format.
                            CHARRANGE charRange = new CHARRANGE
                            {
                                cpMin = startChar,
                                cpMax = rtb.TextLength
                            };

                            // Fill the FORMATRANGE structure.
                            FORMATRANGE fmtRange = new FORMATRANGE
                            {
                                hdc = e.Graphics.GetHdc(),
                                hdcTarget = e.Graphics.GetHdc(),
                                rc = rectToPrint,
                                rcPage = rectPage,
                                charrange = charRange
                            };

                            // Allocate memory for the structure.
                            IntPtr lParam = Marshal.AllocCoTaskMem(Marshal.SizeOf(fmtRange));
                            Marshal.StructureToPtr(fmtRange, lParam, false);

                            // Send the EM_FORMATRANGE message to format (render) the rich text.
                            IntPtr res = SendMessage(rtb.Handle, EM_FORMATRANGE, new IntPtr(1), lParam);

                            // Free the device context handles.
                            e.Graphics.ReleaseHdc(fmtRange.hdc);
                            e.Graphics.ReleaseHdc(fmtRange.hdcTarget);

                            Marshal.FreeCoTaskMem(lParam);

                            // Update the starting character index for the next page.
                            startChar = res.ToInt32();
                            // If there are more characters, signal that another page is needed.
                            e.HasMorePages = startChar < rtb.TextLength;
                        };

                        pd.Print();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error while printing RTF: " + ex.Message);
                return false;
            }
        }
    }
}
