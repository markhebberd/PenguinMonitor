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
        public string FullPitId { get; set; } = "";
        public string ScannedId { get; set; } = "";
        public string PengNum { get; set; } = "";
        public LifeStage LastKnownLifeStage { get; set; }
        public DateTime ChipDate { get; set; }
        public string Sex { get; set; } = "";
        public string ChipAs { get; set; } = "";
        public string ChickSizeCode { get; set; } = "";
        /// <summary>Weighted field-sex evidence from the server: a "probably" counts 2, a "maybe" 1.
        /// Once one side reaches SexConfirmScore the bird is worth sexing for real.</summary>
        public int SexGuessM { get; set; }
        public int SexGuessF { get; set; }
        /// <summary>Flagged on the website as a bird to shout about when it turns up in a box.
        /// Raises the same alert an unsexed adult does — once per scan, not once per reason.</summary>
        public bool HasAlert { get; set; }
    }
}

namespace PenguinMonitor.Models
{
    /// <summary>peng_num as the server stores and speaks it: colony-prefixed, "PT1039", "NI7".
    ///
    /// The API used to hand each colony its own birds bare ("1039") and put the prefix back on
    /// writes; a write path that forgot to put it back failed a foreign key in production. Now
    /// the wire, the caches and every key on the phone hold the full form.</summary>
    public static class PengNums
    {
        /// <summary>The full form of a number a person typed, or an older build stored. One that
        /// already carries letters is taken as-is; a bare one is the current colony's, since that
        /// is the only colony the phone was ever handed bare numbers for.</summary>
        public static string Full(string? raw, string? colonyPrefix)
        {
            var s = (raw ?? "").Trim().TrimStart('#').Trim().ToUpperInvariant();
            if (s.Length == 0 || char.IsLetter(s[0])) return s;
            return (colonyPrefix ?? "").Trim().ToUpperInvariant() + s;
        }

        /// <summary>The trailing number, for ordering — "PT98" before "PT1039", which a string sort
        /// gets backwards. Anything without one sorts last.</summary>
        public static int Number(string? pengNum)
        {
            var m = System.Text.RegularExpressions.Regex.Match(pengNum ?? "", @"(\d+)$");
            return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : int.MaxValue;
        }
    }
}
