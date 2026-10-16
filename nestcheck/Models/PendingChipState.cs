using System;
using System.Collections.Generic;

namespace PenguinMonitor.Models
{
    /// <summary>
    /// Snapshot of an in-progress chipping/rechipping workflow. Chipping a bird can take
    /// ~15 minutes in the field, and Android may kill the backgrounded app in that time —
    /// this file lets the app reopen the form exactly where it was, PIT included.
    /// </summary>
    public class PendingChipState
    {
        public string FullPitId { get; set; } = "";
        public string BoxName { get; set; } = "";
        public bool IsRechip { get; set; }
        public string RechipPengNum { get; set; } = "";
        public bool IsChick { get; set; }
        public string ChickSizeCode { get; set; } = "";
        public string SexCode { get; set; } = "";
        public string ChipBox { get; set; } = "";
        // Who chipped and who assisted, as users.id (0 = not set). ChipBy is the legacy acronym,
        // kept only as a server-side fallback when ChipperId is 0 (e.g. the user list hadn't synced).
        public int ChipperId { get; set; }
        public int AssistantId { get; set; }
        public string ChipBy { get; set; } = "";
        // A chipper not in the user list, typed by name (used only when ChipperId is 0).
        public string ChipperName { get; set; } = "";
        public string Weight { get; set; } = "";
        public string Flipper { get; set; } = "";
        public string Notes { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        /// <summary>Number predicted on-device when queued offline. The server honours it if
        /// still free, else assigns +100 (renamable later) — so field notes stay meaningful.</summary>
        public string RequestedPengNum { get; set; } = "";
        /// <summary>Every time this chip was scanned while its form was open (UTC), first one first.</summary>
        public List<DateTime> ScanTimesUtc { get; set; } = new();

        // Scan-cleanup intent — so cancelling a restored (post-kill) scan-triggered form
        // still removes the provisional scan from the box, exactly like a normal cancel.
        public bool ScanCleanup { get; set; }
        public string ScanCleanupBox { get; set; } = "";
        public bool ScanCleanupDecrement { get; set; }
    }
}
