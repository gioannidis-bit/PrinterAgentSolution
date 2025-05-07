using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using PrinterAgent.Core;
using PrinterAgent.WebUI.Hubs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PrinterAgent.WebUI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PrintApiController : ControllerBase
    {
        private readonly IHubContext<PrintHub> _hub;
        
        public PrintApiController(IHubContext<PrintHub> hub)
        {
            _hub = hub;
        }

        /// <summary>
        /// Get a list of available agents with their status
        /// </summary>
        [HttpGet("agents")]
        public IActionResult GetAgents()
        {
            var agents = AgentDataStore.Data.Values
                .Select(a => new
                {
                    a.AgentId,
                    a.MachineName,
                    a.Location,
                    a.IsOnline,
                    LastSeen = a.Timestamp
                })
                .ToList();
                
            return Ok(agents);
        }
        
        /// <summary>
        /// Get a list of available printers for a specific agent
        /// </summary>
        [HttpGet("agents/{agentId}/printers")]
        public IActionResult GetPrinters(string agentId)
        {
            if (!AgentDataStore.Data.TryGetValue(agentId, out var agent))
            {
                return NotFound($"Agent with ID {agentId} not found");
            }
            
            if (!agent.IsOnline)
            {
                return BadRequest($"Agent {agent.MachineName} is currently offline");
            }

            // Διορθωμένος κώδικας - αποφεύγουμε τη χρήση του ?? με List<object>
            var printers = new List<object>();

            if (agent.Printers != null)
            {
                printers = agent.Printers
                    .Select(p => new
                    {
                        Name = p.Name,
                        Status = p.Status,
                        IsOnline = p.Status == "Online",
                        DriverName = p.DriverName,
                        IPAddress = p.IPAddress,
                        ResponseTime = p.ResponseTime
                    })
                    .ToList<object>();
            }

            return Ok(printers);
        }

        /// <summary>
        /// Send a print job to an agent's printer with text content
        /// </summary>
        [HttpPost("text")]
        public async Task<IActionResult> PrintText([FromBody] TextPrintRequest request)
        {
            if (string.IsNullOrEmpty(request.AgentId) || string.IsNullOrEmpty(request.PrinterName))
            {
                return BadRequest("Agent ID and printer name are required");
            }
            
            // Verify agent is online
            if (!AgentDataStore.Data.TryGetValue(request.AgentId, out var agent) || !agent.IsOnline)
            {
                return BadRequest("Agent is offline or not found");
            }
            
            // Verify printer exists
            var printer = agent.Printers?.FirstOrDefault(p => p.Name == request.PrinterName);
            if (printer == null)
            {
                return NotFound($"Printer {request.PrinterName} not found on agent {agent.MachineName}");
            }
            
            // Get connection ID
            if (!AgentConnectionMap.TryGetConnection(request.AgentId, out var connectionId))
            {
                return BadRequest("Agent is not currently connected");
            }
            
            // Create print request
            var printRequest = new PrinterAgent.Core.PrintRequest
            {
                AgentId = request.AgentId,
                MachineName = agent.MachineName,
                PrinterName = request.PrinterName,
                DocumentContent = request.Content,
                Landscape = request.Landscape,
                PaperSize = request.PaperSize ?? "A4",
                Location = agent.Location
            };
            
            // Send print request via SignalR
            await _hub.Clients.Client(connectionId).SendAsync("Print", printRequest);
            
            return Ok(new { Message = "Print request sent", JobId = Guid.NewGuid().ToString() });
        }
        
        /// <summary>
        /// Send a print job to an agent's printer with uploaded file
        /// </summary>
        [HttpPost("file")]
        public async Task<IActionResult> PrintFile()
        {
            // Έλεγχος αν υπάρχουν αρχεία στο αίτημα
            if (Request.Form.Files == null || Request.Form.Files.Count == 0)
            {
                return BadRequest("No file uploaded - the request doesn't contain any files");
            }

            var agentId = Request.Form["agentId"];
            var printerName = Request.Form["printerName"];

            // Περισσότερος έλεγχος και αποσφαλμάτωση
           // _logger.LogInformation($"Received file upload request: Files count={Request.Form.Files.Count}, AgentId={agentId}, PrinterName={printerName}");


            if (string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(printerName))
            {
                return BadRequest("Agent ID and printer name are required");
            }
            
            // Check if file is uploaded
            var file = Request.Form.Files.FirstOrDefault();
            if (file == null)
            {
                return BadRequest("No file uploaded");
            }
            
            // Verify agent is online
            if (!AgentDataStore.Data.TryGetValue(agentId, out var agent) || !agent.IsOnline)
            {
                return BadRequest("Agent is offline or not found");
            }
            
            // Verify printer exists
            var printer = agent.Printers?.FirstOrDefault(p => p.Name == printerName);
            if (printer == null)
            {
                return NotFound($"Printer {printerName} not found on agent {agent.MachineName}");
            }
            
            // Get connection ID
            if (!AgentConnectionMap.TryGetConnection(agentId, out var connectionId))
            {
                return BadRequest("Agent is not currently connected");
            }
            
            // Determine the document format based on file extension
            var documentFormat = GetDocumentFormat(file.FileName);
            bool isLandscape = Request.Form.ContainsKey("landscape") && 
                               Request.Form["landscape"].ToString().ToLower() == "true";
                               
            string paperSize = Request.Form.ContainsKey("paperSize") ? 
                               Request.Form["paperSize"].ToString() : "A4";
            
            // For text files, read the content and send as text
            if (documentFormat == "text" || documentFormat == "rtf")
            {
                string content;
                using (var reader = new StreamReader(file.OpenReadStream()))
                {
                    content = await reader.ReadToEndAsync();
                }
                
                var printRequest = new PrinterAgent.Core.PrintRequest
                {
                    AgentId = agentId,
                    MachineName = agent.MachineName,
                    PrinterName = printerName,
                    DocumentContent = content,
                    Landscape = isLandscape,
                    PaperSize = paperSize,
                    Location = agent.Location
                };
                
                await _hub.Clients.Client(connectionId).SendAsync("Print", printRequest);
            }
            else
            {
                // For binary files, read into memory and send as binary
                using var memoryStream = new MemoryStream();
                await file.CopyToAsync(memoryStream);
                byte[] fileBytes = memoryStream.ToArray();
                
                // Create a DTO for the universal print request
                var printRequest = new UniversalPrintRequest
                {
                    AgentId = agentId,
                    MachineName = agent.MachineName,
                    PrinterName = printerName,
                    DocumentData = fileBytes,
                    DocumentFormat = documentFormat,
                    Landscape = isLandscape,
                    PaperSize = paperSize,
                    Location = agent.Location
                };
                
                // Send as universal print request
                await _hub.Clients.Client(connectionId).SendAsync("UniversalPrint", printRequest);
            }
            
            return Ok(new { Message = "Print request sent", JobId = Guid.NewGuid().ToString() });
        }
        
        /// <summary>
        /// Determine the document format based on file extension
        /// </summary>
        private string GetDocumentFormat(string fileName)
        {
            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            
            return extension switch
            {
                ".txt" => "text",
                ".rtf" => "rtf",
                ".pdf" => "pdf",
                ".xps" => "xps",
                ".oxps" => "xps",
                ".jpg" => "image",
                ".jpeg" => "image",
                ".png" => "image", 
                ".bmp" => "image",
                ".gif" => "image",
                ".tif" => "image",
                ".tiff" => "image",
                ".doc" => "office",
                ".docx" => "office",
                ".xls" => "office",
                ".xlsx" => "office",
                ".ppt" => "office",
                ".pptx" => "office",
                _ => "raw"
            };
        }
    }

    /// <summary>
    /// Request model for text print jobs
    /// </summary>
    public class TextPrintRequest
    {
        public string AgentId { get; set; }
        public string PrinterName { get; set; }
        public string Content { get; set; }
        public bool Landscape { get; set; }
        public string PaperSize { get; set; }
    }

    /// <summary>
    /// Request model for universal print jobs
    /// </summary>
    public class UniversalPrintRequest
    {
        public string AgentId { get; set; }
        public string MachineName { get; set; }
        public string PrinterName { get; set; }
        public byte[] DocumentData { get; set; }
        public string DocumentFormat { get; set; }
        public bool Landscape { get; set; }
        public string PaperSize { get; set; }
        public string Location { get; set; }
    }
}