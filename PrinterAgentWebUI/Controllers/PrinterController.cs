using Microsoft.AspNetCore.Mvc;
using PrinterAgent.Core;

namespace PrinterAgent.WebUI.Controllers
{
    public class PrinterController : Controller
    {
        [HttpGet]
        public IActionResult TestPrint()
        {
            return View();
        }

        [HttpPost]
        public IActionResult TestPrint(string agentId, string printerName, string documentContent, bool? landscape, string paperSize)
        {
            bool isLandscape = landscape ?? false; // false if checkbox not checked
            string selectedPaperSize = string.IsNullOrWhiteSpace(paperSize) ? "A4" : paperSize;


            bool result = PrinterHelper.PrintRtf(printerName, documentContent, isLandscape, selectedPaperSize);
            ViewBag.Message = result
                ? $"Test print command sent to {printerName} from agent {agentId}."
                : "Failed to send test print command.";
            return View();
        }
    }
}
