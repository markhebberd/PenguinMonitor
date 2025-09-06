namespace BluePenguinMonitoring.Models
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
        public string Sex { get; set; } = "";
        public string VidForScanner { get; set; } = "";
    }
}