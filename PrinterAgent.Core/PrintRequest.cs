namespace PrinterAgent.Core
{
    public class PrintRequest
    {
        public string AgentId { get; set; }
        public string MachineName { get; set; }   // Πεδίο για το όνομα του μηχανήματος
        public string PrinterName { get; set; }
        public string DocumentContent { get; set; }
        public bool Landscape { get; set; }
        public string PaperSize { get; set; }
        public string Location { get; set; }      // Νέο πεδίο για την τοποθεσία
    }
}