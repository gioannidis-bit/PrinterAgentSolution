using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using PrinterAgent.WebUI.Hubs;
using System.Threading.Tasks;

namespace PrinterAgent.WebUI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PrintController : ControllerBase
    {
        private readonly IHubContext<PrintHub> _hub;
        public PrintController(IHubContext<PrintHub> hub) => _hub = hub;

        [HttpGet("agents")]
        public IActionResult GetAgents()
        {
            return Ok(AgentConnectionMap.GetAll());
        }


        [HttpPost]
        public async Task<IActionResult> Post([FromBody] PrintRequest req)
        {
            if (!AgentConnectionMap.TryGetConnection(req.AgentId, out var connId))
                return NotFound("Agent not connected");

            await _hub.Clients.Client(connId).SendAsync("Print", req);
            return Ok("Dispatched");
        }
    }

    public class PrintRequest
    {
        public string AgentId { get; set; }

        public string MachineName { get; set; }

        public string PrinterName { get; set; }

        public string DocumentContent { get; set; }

        public bool Landscape { get; set; }

        public string PaperSize { get; set; }

        public string Location { get; set; }
    }
}
