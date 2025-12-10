using System;
using System.Collections.Generic;

namespace PenguinMonitor.Models
{
    public class MonitorDetails
    {
        public bool IsDeleted { get; set; } = false;
        public string? DeletionReason { get; set; } = null;
        public DateTime LastSaved { get; set; }
        public string filename { get; set; }
        public Dictionary<string, BoxData> BoxData { get; set; } = new Dictionary<string, BoxData>();
    }
}