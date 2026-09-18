using System;

namespace PenguinMonitor.Models
{
    /// <summary>
    /// A penguin biometric observation for a given day, cached locally and queued for sync.
    /// Mirrors a wildwatch penguin_biometric_data row (server primary key biometric_id).
    /// Cached during sync so the detail form opens instantly/offline, and queued on save.
    /// </summary>
    public class BiometricRecord
    {
        public string PengNum { get; set; } = "";
        public string ObservationDate { get; set; } = ""; // yyyy-MM-dd (NZ date)

        public string? Weight { get; set; }
        public string? FlipperLength { get; set; }
        public string? ObservedSex { get; set; }
        public bool ConditionMoulting { get; set; }
        public bool ConditionDead { get; set; }
        public bool DispositionPassive { get; set; }
        public bool DispositionAggressive { get; set; }
        public string? Notes { get; set; }

        /// <summary>Server primary key. Null until first uploaded; used to update instead of duplicate.</summary>
        public int? BiometricId { get; set; }

        /// <summary>True when locally edited and not yet uploaded.</summary>
        public bool IsPendingUpload { get; set; }
        public DateTime? PendingUploadSinceUtc { get; set; }
    }
}
