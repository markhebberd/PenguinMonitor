using System;

namespace PenguinMonitor.Models
{
    public class BoxTag
    {
        public string BoxID { get; set; } = "";
        public string TagNumber { get; set; } = "";
        public DateTime ScanTimeUTC { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public float Accuracy { get; set; } = -1;
    }
}
