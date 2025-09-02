namespace BluePenguinMonitoring.Models
{
    public class BoxStatusRemoteData
    {
        public int boxNumber { get; set; }
        public string eggChickStatusText { get; set; }
        public string breedingLikelyhoodText { get; set; }
        public string PersistentNotes { get; set; }
        public int numEggs()
        {
            return string.IsNullOrWhiteSpace(eggChickStatusText) ? 0:  eggChickStatusText.Trim().Replace("  ", " ").Split(" ")[0].Count(c => c == 'x') - numChicks();
        }
        public int numChicks()
        {
            if(string.IsNullOrWhiteSpace(eggChickStatusText) || !eggChickStatusText.Trim().Contains(" "))
            {
                return 0;
            }
            return eggChickStatusText.Trim().Replace("  ", " ").Split(" ")[1].Count(c => c == 'x');
        }

        public string boxMiniStatus()
        {
            string miniStatus = "";
            for (int i = 0; i < numEggs(); i++)
            {
                miniStatus += "🥚";
            }
            for (int i = 0; i < numChicks(); i++)
            {
                miniStatus += "🐣";
            }
            if (string.IsNullOrWhiteSpace(miniStatus))
            {
                return breedingLikelyhoodText;
            }
            return miniStatus;
        }
    }
}