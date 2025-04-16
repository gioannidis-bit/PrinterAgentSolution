using System.Collections.Generic;
using System.Drawing.Printing;

namespace PrinterAgent.Core
{
    public static class PrinterHelper
    {
        // Returns a list of installed printer names.
        public static List<string> GetInstalledPrinters()
        {
            List<string> printers = new List<string>();
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
