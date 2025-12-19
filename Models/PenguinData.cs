namespace PenguinMonitor.Models
{
    public enum LifeStage
    {
        Adult,
        Chick,
        Returnee,
        Dead
    }
    public class PenguinData
    {
        public string ScannedId { get; set; } = "";
        public LifeStage LastKnownLifeStage { get; set; }
        public DateTime ChipDate { get; set; }
        public string Sex { get; set; } = "";
        public string VidForScanner { get; set; } = "";
        public string ChipAs { get; set; } = "";
    }
}