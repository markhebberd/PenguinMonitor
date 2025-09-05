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
                if (boxNumber == 18)
                    ;
                DateTime estHatch = DateTime.Parse(estHatchDate);
                if (estHatch.AddDays(3) > DateTime.Now)
                    return "Hatch " + (estHatch.Equals(DateTime.Today) ?"today": (estHatch - DateTime.Now).TotalDays.ToString("F1") + " days");

                DateTime estPG = DateTime.Parse(estHatchDate);
                if (estPG.AddDays(3) > DateTime.Now)
                    return "PG " + (estPG.Equals(DateTime.Today) ? "today" : (estPG - DateTime.Now).TotalDays.ToString("F1") + " days");

                DateTime estFledge = DateTime.Parse(estFledgeDate);
                if (estFledge.AddDays(3) > DateTime.Now)
                    return "Fledge " + (estFledge.Equals(DateTime.Today) ? "today" : (estFledge - DateTime.Now).TotalDays.ToString("F1") + " days");

                DateTime chipStart = DateTime.Parse(chipWindowStart);
                if (chipStart.AddDays(3) > DateTime.Now)
                    return "ChipStart " + (chipStart.Equals(DateTime.Today) ? "today" : (chipStart - DateTime.Now).TotalDays.ToString("F1") + " days");

                DateTime chipFinish = DateTime.Parse(chipWindowFinish);
                if (chipFinish.AddDays(3) > DateTime.Now)
                    return "ChipFin " + (chipFinish.Equals(DateTime.Today) ? "today" : (chipFinish - DateTime.Now).TotalDays.ToString("F1") + " days");
            }
            catch
            {
                return "BreedingDateError";
            }
            return "";
        }
    }
}