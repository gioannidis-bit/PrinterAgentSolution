using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using PrinterAgent.WebUI.Hubs;
using PrinterAgent.WebUI.Controllers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace PrinterAgent.WebUI.Controllers
{
    public class AgentSettingsController : Controller
    {
        private readonly IHubContext<PrintHub> _hubContext;

        public AgentSettingsController(IHubContext<PrintHub> hubContext)
        {
            _hubContext = hubContext;
        }

        public IActionResult Index()
        {
            var agents = AgentDataStore.Data.Values.ToList();
            return View(agents);
        }

        [HttpPost]
        public async Task<IActionResult> UpdateLocation(string agentId, string location)
        {
            if (string.IsNullOrEmpty(agentId))
            {
                return BadRequest("Agent ID is required");
            }

            // Ενημερώνουμε το AgentConnectionMap
            AgentConnectionMap.SetLocation(agentId, location ?? "");

            // Ενημερώνουμε το AgentDataStore
            if (AgentDataStore.Data.TryGetValue(agentId, out var agentData))
            {
                agentData.Location = location ?? "";
                AgentDataStore.Data[agentId] = agentData;
            }

            // Στέλνουμε την ενημερωμένη τοποθεσία στον agent (αν είναι συνδεδεμένος)
            if (AgentConnectionMap.TryGetConnection(agentId, out var connectionId))
            {
                await _hubContext.Clients.Client(connectionId).SendAsync("UpdateLocation", location ?? "");
            }

            return RedirectToAction("Index");
        }

        [HttpGet]
        public IActionResult Edit(string id)
        {
            if (string.IsNullOrEmpty(id) || !AgentDataStore.Data.TryGetValue(id, out var agentData))
            {
                return NotFound();
            }

            return View(agentData);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(string agentId, string location)
        {
            if (string.IsNullOrEmpty(agentId))
            {
                return BadRequest("Agent ID is required");
            }

            // Ενημερώνουμε το AgentConnectionMap
            AgentConnectionMap.SetLocation(agentId, location ?? "");

            // Ενημερώνουμε το AgentDataStore
            if (AgentDataStore.Data.TryGetValue(agentId, out var agentData))
            {
                agentData.Location = location ?? "";
                AgentDataStore.Data[agentId] = agentData;

                // Στέλνουμε την ενημερωμένη τοποθεσία στον agent (αν είναι συνδεδεμένος)
                if (AgentConnectionMap.TryGetConnection(agentId, out var connectionId))
                {
                    await _hubContext.Clients.Client(connectionId).SendAsync("UpdateLocation", location ?? "");
                }

                TempData["Message"] = "Agent location updated successfully";
                return RedirectToAction("Index");
            }

            return NotFound();
        }
    }
}