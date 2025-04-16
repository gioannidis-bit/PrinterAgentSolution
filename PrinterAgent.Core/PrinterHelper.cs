using System.Collections.Generic;
using System.Drawing.Printing;
using System.Runtime.Versioning;


namespace PrinterAgent.Core
{
    [SupportedOSPlatform("windows6.1")]
    public static class PrinterHelper
    {
        // Returns a list of installed printer names.
        public static List<string> GetInstalledPrinters()
        {
            List<string> printers = new List<string>();
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("This API is supported only on Windows.");
            }
            foreach (string printer in PrinterSettings.InstalledPrinters)
            {
                printers.Add(printer);
            }
            return printers;
        }

        // Simple simulation of sending a test print job.
        public static bool SendTestPrint(string printerName, string documentContent)
        {
            // In a real-world scenario, you would generate a print document
            // and send it to the printer. Here we simply simulate a successful job.
            if (string.IsNullOrEmpty(printerName))
                return false;

            // Log the print "job" to simulate.
            System.Diagnostics.Debug.WriteLine($"Sending test print to {printerName}:");
            System.Diagnostics.Debug.WriteLine(documentContent);

            // Simulate success.
            return true;
        }
    }
}
