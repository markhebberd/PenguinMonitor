using System.Collections.Generic;

namespace BluePenguinMonitoring.Models
{
    public class BoxData
    {
        public List<ScanRecord> ScannedIds { get; set; } = new List<ScanRecord>();
        public int unchippedAdults { get; set; } = 0;
        public int Eggs { get; set; } = 0;
        public int unchippedChicks { get; set; } = 0;
        public string? GateStatus { get; set; } = null; 
        public string Notes { get; set; } = "";
    }
}