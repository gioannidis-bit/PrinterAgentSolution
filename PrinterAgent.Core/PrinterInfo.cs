namespace PrinterAgent.Core
{
    public class PrinterInfo
    {
        public string Name { get; set; }
        public string Status { get; set; }
        // Βάζουμε προκαθορισμένες τιμές αντί για null
        public string DriverName { get; set; } = "Unknown";
        public string IPAddress { get; set; } = "Not Available";
        public int ResponseTime { get; set; } = -1; // -1 αντί για null για να δείξουμε ότι δεν είναι διαθέσιμο
    }

}
