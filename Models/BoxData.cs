using System.Collections.Generic;

namespace BluePenguinMonitoring.Models
{
    public class BoxData
    {
        public List<ScanRecord> ScannedIds { get; set; } = new List<ScanRecord>();
        public int Adults { get; set; } = 0;
        public int Eggs { get; set; } = 0;
        public int Chicks { get; set; } = 0;
        public string? GateStatus { get; set; } = null; 
        public string Notes { get; set; } = "";
        public DateTime whenDataCollectedUtc { get; set; } 
        public string ToString()
        {
            string scannedIDs = "";
            foreach (string id in ScannedIds.Select(s => s.BirdId).ToList())
            {
                scannedIDs += id + ".";
            }
            return $"BoxData: ScannedIds={scannedIDs}, Adults={Adults}, Eggs={Eggs}, Chicks={Chicks}, GateStatus={GateStatus}, Notes={Notes}, whenDataCollected={whenDataCollectedUtc}";
        }
    }
}