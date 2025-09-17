namespace BluePenguinMonitoring.Models
{
    public class BoxRemoteData
    {
        public int boxNumber { get; set; }
        public string eggChickStatusText { get; set; }
        public string breedingLikelyhoodText { get; set; }
        public string PersistentNotes { get; set; }

        public string boxMiniStatus(int numEggs, int numChicks)
        {
            string miniStatus = "";
            for (int i = 0; i < numEggs; i++)
            {
                miniStatus += "🥚";
            }
            for (int i = 0; i < numChicks; i++)
            {
                miniStatus += "🐣";
            }
            if (string.IsNullOrWhiteSpace(miniStatus))
            {
                miniStatus = breedingLikelyhoodText;
            }
            return $"({miniStatus}) \n({PersistentNotes})".Replace("()","").Trim();
        }
    }
}