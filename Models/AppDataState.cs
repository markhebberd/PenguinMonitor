using System;
using System.Collections.Generic;

namespace BluePenguinMonitoring.Models
{
    public class AppDataState
    {
        public int CurrentBox { get; set; } = 1;
        public DateTime LastSaved { get; set; }
        public Dictionary<int, BoxData> BoxData { get; set; } = new Dictionary<int, BoxData>();
    }
}