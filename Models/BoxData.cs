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
        public DateTime whenDataCollectedUtc { get; set; } 
        public string ToString()
        {
            string scannedIDs = "";
            foreach (string id in ScannedIds.Select(s => s.BirdId).ToList())
            {
                scannedIDs += id + ".";
            }
            return $"BoxData: ScannedIds={scannedIDs}, unchippedAdults={unchippedAdults}, Eggs={Eggs}, unchippedChicks={unchippedChicks}, GateStatus={GateStatus}, Notes={Notes}, whenDataCollected={whenDataCollectedUtc}";
        }
    }
}