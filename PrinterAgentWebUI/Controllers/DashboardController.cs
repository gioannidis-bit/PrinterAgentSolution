using Microsoft.AspNetCore.Mvc;
using PrinterAgent.WebUI.Controllers;  // To access AgentDataStore

namespace PrinterAgent.WebUI.Controllers
{
    public class DashboardController : Controller
    {
        // GET: /Dashboard
        public IActionResult Index()
        {
            // Get all agent data from our in-memory store.
            var agentData = AgentDataStore.Data.Values;
            return View("~/Views/Printer/Index.cshtml", agentData);
        }
    }
}
