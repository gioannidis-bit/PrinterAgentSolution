using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using PrinterAgent.Core;
using PrinterAgent.WebUI.Hubs;

namespace PrinterAgent.WebUI.Controllers
{
    public class PrinterController : Controller
    {
        private readonly IHubContext<PrintHub> _hub;
        public PrinterController(IHubContext<PrintHub> hub) => _hub = hub;


        [HttpGet]
        public IActionResult TestPrint()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> TestPrint(
      string agentId,
      string printerName,
      string documentContent,
      bool? landscape,
      string paperSize)
        {
            // 1) Validate inputs
            if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(printerName))
            {
                ViewBag.Message = "Please select both an agent and a printer.";
                return View();
            }

            // 2) Build the PrintRequest DTO
            var req = new PrintRequest
            {
                AgentId = agentId,
                PrinterName = printerName,
                DocumentContent = documentContent ?? "",
                Landscape = landscape ?? false,
                PaperSize = string.IsNullOrWhiteSpace(paperSize) ? "A4" : paperSize
            };

            // 3) Lookup the connection ID for this agent
            if (!AgentConnectionMap.TryGet(agentId, out var connectionId))
            {
                ViewBag.Message = $"Agent '{agentId}' is not connected.";
                return View();
            }

            // 4) Send the Print message to that agent
            await _hub.Clients.Client(connectionId).SendAsync("Print", req);

            ViewBag.Message = $"Print job dispatched to agent '{agentId}'.";
            return View();
        }
    }
}
