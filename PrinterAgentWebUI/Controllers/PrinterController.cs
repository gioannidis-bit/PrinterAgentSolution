using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using PrinterAgent.Core;
using PrinterAgent.WebUI.Hubs;
using PrinterAgent.WebUI.Controllers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrinterAgent.WebUI.Controllers
{
    public class PrinterController : Controller
    {
        private readonly IHubContext<PrintHub> _hub;

        public PrinterController(IHubContext<PrintHub> hub) => _hub = hub;

        [HttpGet]
        public IActionResult TestPrint(string agentId = "", string printerName = "")
        {
            // Αν έχουν περαστεί παράμετροι, τις βάζουμε στο ViewBag για προεπιλογή
            ViewBag.SelectedAgentId = agentId;
            ViewBag.SelectedPrinterName = printerName;

            // Περνάμε τη λίστα των agents στο view για την dropdown λίστα
            ViewBag.Agents = AgentDataStore.Data.Values
                .Where(a => a.IsOnline) // Φιλτράρουμε μόνο τους online agents
                .ToList();

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
                ViewBag.MessageType = "danger";

                // Περνάμε τη λίστα των agents στο view για την dropdown λίστα
                ViewBag.Agents = AgentDataStore.Data.Values
                    .Where(a => a.IsOnline)
                    .ToList();

                ViewBag.SelectedAgentId = agentId;
                ViewBag.SelectedPrinterName = printerName;

                return View();
            }

            // 2) Build the PrintRequest DTO
            string machineName = "";
            string location = "";

            // Προσπαθούμε να βρούμε το MachineName και το Location από το AgentDataStore
            if (AgentDataStore.Data.TryGetValue(agentId, out var agentData))
            {
                machineName = agentData.MachineName;
                location = agentData.Location;
            }

            // Εναλλακτικά, προσπαθούμε να βρούμε τα δεδομένα από το AgentConnectionMap
            if (string.IsNullOrEmpty(machineName) && AgentConnectionMap.TryGetMachine(agentId, out var mapMachineName))
            {
                machineName = mapMachineName;
            }

            if (string.IsNullOrEmpty(location) && AgentConnectionMap.TryGetLocation(agentId, out var mapLocation))
            {
                location = mapLocation;
            }

            var req = new PrintRequest
            {
                AgentId = agentId,
                MachineName = machineName,
                PrinterName = printerName,
                DocumentContent = documentContent ?? "",
                Landscape = landscape ?? false,
                PaperSize = string.IsNullOrWhiteSpace(paperSize) ? "A4" : paperSize,
                Location = location
            };

            // 3) Lookup the connection ID for this agent
            if (!AgentConnectionMap.TryGet(agentId, out var connectionId))
            {
                ViewBag.Message = $"Agent '{agentId}' is not connected.";
                ViewBag.MessageType = "danger";

                // Περνάμε τη λίστα των agents στο view για την dropdown λίστα
                ViewBag.Agents = AgentDataStore.Data.Values
                    .Where(a => a.IsOnline)
                    .ToList();

                ViewBag.SelectedAgentId = agentId;
                ViewBag.SelectedPrinterName = printerName;

                return View();
            }

            // 4) Send the Print message to that agent
            await _hub.Clients.Client(connectionId).SendAsync("Print", req);

            ViewBag.Message = $"Print job dispatched to agent '{machineName}'.";
            ViewBag.MessageType = "success";

            // Περνάμε τη λίστα των agents στο view για την dropdown λίστα
            ViewBag.Agents = AgentDataStore.Data.Values
                .Where(a => a.IsOnline)
                .ToList();

            ViewBag.SelectedAgentId = agentId;
            ViewBag.SelectedPrinterName = printerName;

            return View();
        }

        // Νέο action για τη σελίδα διαχείρισης τοποθεσιών των agents
        [HttpGet]
        public IActionResult Locations()
        {
            return View(AgentDataStore.Data.Values.ToList());
        }

        // Ενημέρωση της τοποθεσίας ενός agent
        [HttpPost]
        public async Task<IActionResult> UpdateLocation(string agentId, string location)
        {
            if (string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(location))
            {
                return Json(new { success = false, message = "Agent ID and location are required" });
            }

            // Ενημερώνουμε την τοποθεσία του agent
            if (AgentDataStore.Data.TryGetValue(agentId, out var agent))
            {
                agent.Location = location;
                AgentDataStore.Data[agentId] = agent;

                // Ενημερώνουμε και το AgentConnectionMap
                AgentConnectionMap.SetLocation(agentId, location);

                // Στέλνουμε την ενημέρωση στον agent
                await _hub.Clients.All.SendAsync("UpdateLocation", agentId, location);

                return Json(new { success = true, message = "Location updated successfully" });
            }

            return Json(new { success = false, message = "Agent not found" });
        }
    }
}