using System;
using System.Collections.Generic;

namespace BluePenguinMonitoring.Models
{
    public class MonitorDetails
    {
        public bool IsDeleted { get; set; } = false;
        public DateTime LastSaved { get; set; }
        public string filename { get; set; } 
        public Dictionary<int, BoxData> BoxData { get; set; } = new Dictionary<int, BoxData>();
    }
}