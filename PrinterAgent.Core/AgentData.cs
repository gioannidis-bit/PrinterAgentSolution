using PrinterAgent.Core;

public class AgentData
{
    public string AgentId { get; set; }
    public string MachineName { get; set; }   // <-- here
    public DateTime Timestamp { get; set; }
    public List<PrinterInfo> Printers { get; set; }
}
