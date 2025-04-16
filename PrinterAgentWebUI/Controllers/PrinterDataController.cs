using Microsoft.AspNetCore.Mvc;
using PrinterAgent.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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
        public DateTime Timestamp { get; set; }
        public List<PrinterInfo> Printers { get; set; }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class PrinterDataController : ControllerBase
    {
        // Endpoint for the agent to POST its printer data.
        [HttpPost]
        public IActionResult Post([FromBody] AgentData data)
        {
            if (data == null || data.Printers == null)
                return BadRequest("Invalid data.");

            // Store/update the agent data.
            AgentDataStore.Data[data.AgentId] = data;
            return Ok();
        }

        // Endpoint for testing - to get the stored data.
        [HttpGet]
        public IActionResult Get()
        {
            return Ok(AgentDataStore.Data.Values);
        }
    }
}
