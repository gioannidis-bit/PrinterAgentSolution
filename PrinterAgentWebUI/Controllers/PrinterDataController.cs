using Microsoft.AspNetCore.Mvc;
using PrinterAgent.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using PrinterAgent.WebUI.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.Text.Json;

namespace PrinterAgent.WebUI.Controllers
{
    // For simplicity, we use an in-memory store.
    // In production use a persistent database.
    public static class AgentDataStore
    {
        // Key: AgentId, Value: AgentData (last report from agent)
        public static ConcurrentDictionary<string, AgentData> Data = new();
    }

    public class AgentData
    {
        public string AgentId { get; set; }
        public string MachineName { get; set; }
        public string Location { get; set; } // Νέο πεδίο για την τοποθεσία του agent
        public DateTime Timestamp { get; set; }
        public List<PrinterInfo> Printers { get; set; }
        public bool IsOnline { get; set; } // Νέο πεδίο για την κατάσταση του agent
    }

    [ApiController]
    [Route("api/[controller]")]
    public class PrinterDataController : ControllerBase
    {
        private readonly IHubContext<PrintHub> _hubContext;

        // Constructor με dependency injection για το hub context
        public PrinterDataController(IHubContext<PrintHub> hubContext)
        {
            _hubContext = hubContext;
        }

        // Endpoint for the agent to POST its printer data.
        [HttpPost]
        public async Task<IActionResult> Post([FromBody] AgentData data)
        {
            // Καταγράψτε τα εισερχόμενα δεδομένα
            System.Diagnostics.Debug.WriteLine("Received data from agent: " + JsonSerializer.Serialize(data));

            if (data == null || data.Printers == null)
                return BadRequest("Invalid data.");

            // Για κάθε εκτυπωτή, εξασφαλίστε ότι οι νέες ιδιότητες έχουν default τιμές αν είναι null
            foreach (var printer in data.Printers)
            {
                printer.DriverName = printer.DriverName ?? "Unknown";

                // Fix: Properly set a default value for IP Address
                printer.IPAddress = printer.IPAddress ?? "Not Available";

                // Fix: Use conditional check for ResponseTime
                if (printer.ResponseTime <= 0)
                {
                    printer.ResponseTime = -1; // Default value for no response time
                }
            }

            // Ενημερώνουμε την κατάσταση online
            data.IsOnline = true;

            // Αν υπάρχει ήδη location από το AgentConnectionMap, χρησιμοποιούμε αυτό
            if (string.IsNullOrEmpty(data.Location))
            {
                if (AgentConnectionMap.TryGetLocation(data.AgentId, out var location) && !string.IsNullOrEmpty(location))
                {
                    data.Location = location;
                }
                else
                {
                    data.Location = data.MachineName; // Fallback τοποθεσία είναι το όνομα του μηχανήματος
                }
            }
            else
            {
                // Ενημερώνουμε το AgentConnectionMap με τη νέα τοποθεσία
                AgentConnectionMap.UpdateLocation(data.AgentId, data.Location);
            }

            // Store/update the agent data.
            AgentDataStore.Data[data.AgentId] = data;

            // Ενημερώνουμε όλους τους clients για την αλλαγή
            await _hubContext.Clients.All.SendAsync("AgentDataUpdated", AgentDataStore.Data.Values);
            await _hubContext.Clients.All.SendAsync("PrintersUpdated", GetAllPrinters());

            return Ok();
        }

        // Endpoint for testing - to get the stored data.
        [HttpGet]
        public IActionResult Get()
        {
            // Ελέγχουμε για τυχόν agents που έχουν κάνει timeout (60 δευτερόλεπτα)
            CheckAgentTimeouts();
            return Ok(AgentDataStore.Data.Values);
        }

        // Νέο endpoint για ενημέρωση της τοποθεσίας ενός agent
        [HttpPost("location")]
        public async Task<IActionResult> UpdateLocation([FromBody] LocationUpdateModel model)
        {
            if (string.IsNullOrEmpty(model.AgentId) || string.IsNullOrEmpty(model.Location))
            {
                return BadRequest("Agent ID and location are required");
            }

            // Ενημερώνουμε την τοποθεσία στο AgentConnectionMap
            AgentConnectionMap.UpdateLocation(model.AgentId, model.Location);

            // Ενημερώνουμε και το AgentDataStore
            if (AgentDataStore.Data.TryGetValue(model.AgentId, out var data))
            {
                data.Location = model.Location;
                AgentDataStore.Data[model.AgentId] = data;
            }

            // Στέλνουμε εντολή ενημέρωσης στον agent μέσω του hub
            await _hubContext.Clients.All.SendAsync("UpdateLocation", model.AgentId, model.Location);

            // Ενημερώνουμε όλους τους clients για την αλλαγή
            await _hubContext.Clients.All.SendAsync("AgentDataUpdated", AgentDataStore.Data.Values);

            return Ok(new { success = true });
        }

        // Βοηθητική μέθοδος για έλεγχο timeout agents
        private void CheckAgentTimeouts()
        {
            var now = DateTime.UtcNow;
            var timeoutThreshold = now.AddSeconds(-60); // 60 seconds timeout

            foreach (var agentId in AgentDataStore.Data.Keys)
            {
                if (AgentDataStore.Data.TryGetValue(agentId, out var data))
                {
                    if (data.Timestamp < timeoutThreshold)
                    {
                        data.IsOnline = false;
                        AgentDataStore.Data[agentId] = data;
                    }
                }
            }
        }

        // Βοηθητική μέθοδος για τη δημιουργία λίστας με τους εκτυπωτές
        private List<PrinterStatusModel> GetAllPrinters()
        {
            CheckAgentTimeouts();

            var result = new List<PrinterStatusModel>();

            foreach (var agent in AgentDataStore.Data.Values)
            {
                if (agent.Printers == null) continue;

                foreach (var printer in agent.Printers)
                {
                    result.Add(new PrinterStatusModel
                    {
                        AgentId = agent.AgentId,
                        AgentName = agent.MachineName,
                        AgentLocation = agent.Location ?? "",
                        PrinterName = printer.Name,
                        PrinterStatus = printer.Status,
                        AgentIsOnline = agent.IsOnline,
                        DriverName = printer.DriverName,
                        IPAddress = printer.IPAddress,
                        ResponseTime = printer.ResponseTime
                    });
                }
            }

            return result;
        }
    }

    public class LocationUpdateModel
    {
        public string AgentId { get; set; }
        public string Location { get; set; }
    }

    public class PrinterStatusModel
    {
        public string AgentId { get; set; }
        public string AgentName { get; set; }
        public string AgentLocation { get; set; }
        public string PrinterName { get; set; }
        public string PrinterStatus { get; set; }
        public bool AgentIsOnline { get; set; }
        public string DriverName { get; set; } = "Unknown";
        public string IPAddress { get; set; } = "Not Available";
        public int ResponseTime { get; set; } = -1;
    }
}