namespace BluePenguinMonitoring.Models
{
    public class BoxPredictedDates
    {
        public int boxNumber { get; set; }
        public string estHatchDate { get; set; }
        public string estPGDate { get; set; }
        public string actPGDate { get; set; }
        public string estFledgeDate { get; set; }
        public string chipWindowStart { get; set; }
        public string chipWindowFinish { get; set; }

        public string boxMiniStatus()
        {
            DateTime estHatch = DateTime.Parse(estHatchDate);
            if (estHatch > DateTime.Now)
                return "estHatch: " + estHatch.ToString("yyMMdd");

            DateTime estPG = DateTime.Parse(estHatchDate);
            if (estPG > DateTime.Now)
                return "estPG: " + estPG.ToString("yyMMdd");

            DateTime estFledge = DateTime.Parse(estFledgeDate);
            if (estFledge > DateTime.Now)
                return "estFledge: " + estFledge.ToString("yyMMdd");

            DateTime chipStart = DateTime.Parse(chipWindowStart);
            if (chipStart > DateTime.Now)
                return "chipStart: " + chipStart.ToString("yyMMdd");

            DateTime chipFinish = DateTime.Parse(chipWindowFinish);
            if (chipFinish > DateTime.Now)
                return "chipFinish: " + chipFinish.ToString("yyMMdd");

            return "";
        }
    }
}