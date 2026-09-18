using System;
using System.Collections.Generic;
using System.Linq;

namespace PenguinMonitor.Models
{
    /// <summary>The phone's copy of the colony's rows, keyed the way the server keys them.
    ///
    /// The old caches were keyed by box name and held only each box's latest observation, which an
    /// incremental feed can't be applied to: a feed row identifies itself by id, not by whether it
    /// happens to be some box's most recent. Keyed by id, a changed row lands on the row it changed
    /// and a deleted row can actually be removed — the two things the old download could not do.
    ///
    /// See SYNC-INCREMENTAL.md. The views the screens read (TodayBoxes / PreviousBoxes) are derived
    /// from this, not stored alongside it, so there is one copy of the truth.</summary>
    public class LocalDb
    {
        /// <summary>Bump to force a full re-pull — the stores are rebuilt from scratch when the shape
        /// they hold changes. A phone that skipped the release still gets a correct cache.</summary>
        public const int SchemaVersion = 3;   // v3: pit_id lost the reader's "LA" prefix
        public int Version { get; set; }

        /// <summary>Server's max updated_at from the last fully applied payload, sent back as
        /// `since`. Empty means "never synced" — a full pull. Advanced ONLY on a complete apply:
        /// recording it after a partial one skips those rows forever.</summary>
        public string Watermark { get; set; } = "";

        public Dictionary<int, ObsRow> Observations { get; set; } = new();
        public Dictionary<int, ScanRow> Scans { get; set; } = new();
        public Dictionary<int, BioRow> Biometrics { get; set; } = new();
        public Dictionary<string, PengRow> Penguins { get; set; } = new();
        public Dictionary<string, ChipRow> Chips { get; set; } = new();
        public Dictionary<int, LocRow> Locations { get; set; } = new();
        public Dictionary<string, DayNoteRow> DayNotes { get; set; } = new();
        public Dictionary<int, string> Observers { get; set; } = new();

        public class ObsRow
        {
            public int observation_id { get; set; }
            public int location_id { get; set; }
            public string? observation_time_utc { get; set; }
            public int adults { get; set; }
            public int eggs { get; set; }
            public int chicks { get; set; }
            public int no_scan { get; set; }
            public int? failed_eggs { get; set; }
            public int? dead_chicks { get; set; }
            public string? breeding_status { get; set; }
            public string? gate_status { get; set; }
            public string? notes { get; set; }
            public int? observer_id { get; set; }
            public int is_deleted { get; set; }
        }

        public class ScanRow
        {
            public int scan_id { get; set; }
            public int observation_id { get; set; }
            public string? pit_id { get; set; }
            public int scan_deleted { get; set; }
        }

        public class BioRow
        {
            public int biometric_id { get; set; }
            public string? peng_num { get; set; }
            public string? observation_date { get; set; }
            public string? weight { get; set; }
            public string? flipper_length { get; set; }
            public string? observed_sex { get; set; }
            public int? is_moulting { get; set; }
            public int? condition_ticks { get; set; }
            public string? notes { get; set; }
            public int is_deleted { get; set; }
        }

        public class PengRow
        {
            public string peng_num { get; set; } = "";
            public int? chipped_as_adult { get; set; }
            public string? sex { get; set; }
            public int? is_dead { get; set; }
            public string? death_date { get; set; }
            public string? chick_size_code { get; set; }
            public int? alert { get; set; }
            public string? notes { get; set; }
            /// <summary>Weighted field-sex evidence over the bird's whole biometric history — a
            /// "probably" counts 2, a "maybe" 1 — summed server-side, because the phone holds only
            /// the current round's biometrics and could never reach the real score from those.</summary>
            public int? sex_guess_m { get; set; }
            public int? sex_guess_f { get; set; }
        }

        public class ChipRow
        {
            public string pit_id { get; set; } = "";
            public string? peng_num { get; set; }
            public string? chip_date { get; set; }
            public int? is_active { get; set; }
            public string? chip_box { get; set; }
        }

        public class LocRow
        {
            public int location_id { get; set; }
            public string? location_name { get; set; }
            public string? persistent_notes { get; set; }
            public int watched { get; set; }
            // The box tag lives on the location row, so it rides the one feed rather than its own
            // call: pit_id is the tag, the rest is where and when it was read. Decimals arrive as
            // strings from MySQL, kept as strings for the same reason the biometrics are — parsing
            // is the reader's job, and a null must stay tellable from a zero.
            public string? pit_id { get; set; }
            public string? scan_time_utc { get; set; }
            public string? latitude { get; set; }
            public string? longitude { get; set; }
            public string? accuracy { get; set; }
        }

        public class DayNoteRow
        {
            public string note_date { get; set; } = "";
            public string? note { get; set; }
            public int? observer_id { get; set; }
            public int? scribe_id { get; set; }
        }

        // ===== Lookups the derived views need =====

        public string BoxNameFor(int locationId) =>
            Locations.TryGetValue(locationId, out var l) ? (l.location_name ?? "") : "";

        public string? ObserverName(int? id) =>
            id.HasValue && Observers.TryGetValue(id.Value, out var n) ? n : null;

        /// <summary>Live scans of an observation, deleted ones already gone.</summary>
        public List<ScanRow> ScansOf(int observationId) =>
            Scans.Values.Where(s => s.observation_id == observationId).ToList();

        /// <summary>Every live observation for a box, newest first. The full history the phone holds
        /// — not just the latest, which is all the old download could ever see.</summary>
        public List<ObsRow> ObservationsForBox(string boxName)
        {
            var ids = Locations.Values.Where(l => l.location_name == boxName).Select(l => l.location_id).ToHashSet();
            return Observations.Values
                .Where(o => ids.Contains(o.location_id))
                .OrderByDescending(o => o.observation_time_utc ?? "")
                .ToList();
        }

        /// <summary>Drop everything but keep the schema version — used when the shape changes and
        /// the next sync must be a full pull.</summary>
        public void Reset()
        {
            Watermark = "";
            Observations.Clear(); Scans.Clear(); Biometrics.Clear();
            Penguins.Clear(); Chips.Clear(); Locations.Clear();
            DayNotes.Clear(); Observers.Clear();
            Version = SchemaVersion;
        }
    }
}
