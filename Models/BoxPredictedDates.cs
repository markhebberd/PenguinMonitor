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
                if (estHatch.AddDays(3) > DateTime.Now)
                    return "Hatch" + getDateString(estHatch);

                DateTime estPG = DateTime.Parse(estHatchDate);
                if (estPG.AddDays(3) > DateTime.Now)
                    return "PG" + getDateString(estPG);

                DateTime chipStart = DateTime.Parse(chipWindowStart);
                if (chipStart.AddDays(3) > DateTime.Now)
                    return "Chip" + getDateString(chipStart);

                DateTime estFledge = DateTime.Parse(estFledgeDate);
                if (estFledge.AddDays(3) > DateTime.Now)
                    return "Fledge" + getDateString(estFledge);
            }
            catch
            {
                return "BreedingDateError";
            }
            return "";
        }

        private string getDateString(DateTime expectedDate)
        {
            DateTime today = DateTime.Today;
            if (expectedDate.Date.Equals(today))
            {
                return " today";
            }
            if ((expectedDate.Date - today).TotalDays == 1)
            {
                return " tomorrow";
            }
            if ((expectedDate.Date - today).TotalDays == -1)
            {
                return " yesterday";
            }
            if (expectedDate > today)
            {
                return " in " + Math.Ceiling((expectedDate - DateTime.Now).TotalDays) + " days";
            }
            return " " + Math.Ceiling((DateTime.Now - expectedDate).TotalDays) + " days ago";
        }
    }
}