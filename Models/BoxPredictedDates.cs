namespace BluePenguinMonitoring.Models
{
    public class BoxPredictedDates
    {
        public int boxNumber { get; set; }
        public string estHatchDate { get; set; }
        public string estPGDate { get; set; }
        public string estFledgeDate { get; set; }
        public string chipWindowStart { get; set; }
        public string chipWindowFinish { get; set; }

        public string breedingDateStatus()
        {
            try
            {
                DateTime estHatch = DateTime.Parse(estHatchDate);
                if (estHatch.AddDays(3) >= DateTime.Today)
                    return "Hatch" + getDateString(estHatch);

                DateTime estPG = DateTime.Parse(estPGDate);
                if (estPG.AddDays(3) >= DateTime.Today)
                    return "PG" + getDateString(estPG);

                DateTime chipStart = DateTime.Parse(chipWindowStart);
                if (chipStart.AddDays(3) >= DateTime.Today)
                    return "Chip" + getDateString(chipStart);

                DateTime estFledge = DateTime.Parse(estFledgeDate);
                return "Fledge" + getDateString(estFledge);
            }
            catch { return $"No dates in sheet"; }
        }
        private string getDateString(DateTime expectedDate)
        {
            DateTime today = DateTime.Today;
            if (expectedDate.Date.Equals(today))
            {
                return " today";
            }
            if ((expectedDate.Date - today).TotalDays == 1 && expectedDate > today)
            {
                return " tomorrow";
            }
            if ((today - expectedDate.Date).TotalDays == 1)
            {
                return " yesterday";
            }
            if (expectedDate > today)
            {
                return " in " + Math.Ceiling((expectedDate - today).TotalDays) + " days";
            }
            return " " + Math.Ceiling((today - expectedDate).TotalDays) + " days ago";
        }
    }
}