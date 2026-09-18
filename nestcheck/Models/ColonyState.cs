using System;
using System.Collections.Generic;
using System.Linq;

namespace PenguinMonitor.Models
{
    /// <summary>A day's note waiting for a connection, kept whole so it can be written to the day
    /// it belongs to rather than the day it finally uploads on.</summary>
    public class PendingDayNote
    {
        public string NzDate { get; set; } = "";
        public string Note { get; set; } = "";
        public int ObserverId { get; set; }
        public int ScribeId { get; set; }
    }

    public class ColonyState
    {
        public DateTime LastSyncedUtc { get; set; }
        public string DailyLabel { get; set; } = "";
        public string DailyLabelDate { get; set; } = "";
        /// <summary>Who was looking in the boxes today and who was working the phone — users.id
        /// from the server's people list, 0 for "not recorded". Both ride with the daily label to
        /// the colony's day_notes row. Cleared with it at rollover. (The chipper is recorded
        /// per-chip in the chip workflow, not here.)</summary>
        public int DailyObserverId { get; set; }
        public int DailyScribeId { get; set; }

        /// <summary>The label was set but its server save didn't land — offline, or refused. Retried
        /// on every sync until it does. Without this the note stays on the phone: an observation
        /// upload only fills a day that has none, so a corrected label never reaches the day view.</summary>
        public bool DailyLabelPendingUpload { get; set; }

        /// <summary>Day notes from earlier days that still haven't reached the server. Rollover
        /// clears the live label at midnight, and a note set out of signal the day before would
        /// have gone with it — these are held here and retried on every sync instead.</summary>
        public List<PendingDayNote> PendingDayNotes { get; set; } = new();

        /// <summary>
        /// Pending observations not yet uploaded to server.
        /// Can have multiple per box (across different days while offline).
        /// </summary>
        public List<BoxObservation> PendingObservations { get; set; } = new();

        /// <summary>
        /// Previous state from server (last known observation per box).
        /// Shown as read-only orange summary. Not uploaded.
        /// </summary>
        public Dictionary<string, BoxObservation> PreviousBoxes { get; set; } = new();

        /// <summary>
        /// Today's confirmed observations (uploaded to server today, or downloaded as today's data).
        /// One per box. Shown in edit fields when revisiting.
        /// </summary>
        public Dictionary<string, BoxObservation> TodayBoxes { get; set; } = new();

        /// <summary>
        /// Today's penguin biometric records, keyed by full peng_num + date (see BiometricKey).
        /// Downloaded during sync and/or edited locally (IsPendingUpload until uploaded).
        /// </summary>
        public Dictionary<string, BiometricRecord> TodayBiometrics { get; set; } = new();

        public int PendingUploadCount => PendingObservations.Count(o => o.IsPendingUpload);

        public int PendingBiometricCount => TodayBiometrics.Values.Count(b => b.IsPendingUpload);

        /// <summary>
        /// Get all pending observations for a specific box, newest first.
        /// </summary>
        public List<BoxObservation> GetPendingForBox(string boxName)
        {
            return PendingObservations
                .Where(o => o.BoxName == boxName)
                .OrderByDescending(o => o.WhenDataCollectedUtc)
                .ToList();
        }

        /// <summary>
        /// Get or create today's observation for a box.
        /// If one exists in TodayBoxes, return it. Otherwise check pending for today.
        /// </summary>
        /// <summary>
        /// Move yesterday's TodayBoxes into PreviousBoxes. Call on app resume or before display.
        /// </summary>
        public void RolloverDay()
        {
            var nzToday = MainActivity.NzToday;
            var nzTodayStr = nzToday.ToString("yyyy-MM-dd");

            var staleKeys = TodayBoxes
                .Where(kvp => MainActivity.ToNzTime(kvp.Value.WhenDataCollectedUtc).Date < nzToday)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in staleKeys)
            {
                PreviousBoxes[key] = TodayBoxes[key];
                TodayBoxes.Remove(key);
            }

            // Clear daily label (and who was out) if it was set on a previous day. If it never
            // reached the server, park it first — yesterday's note is still yesterday's record,
            // and midnight isn't a reason to throw it away.
            if (!string.IsNullOrEmpty(DailyLabelDate) && DailyLabelDate != nzTodayStr)
            {
                // Park it whether or not anyone typed a label: "Britta observing, Marian scribing"
                // is the day's record just as much as a line of text is, and requiring the text
                // threw the people away at midnight on every day nobody labelled.
                if (DailyLabelPendingUpload
                    && (!string.IsNullOrWhiteSpace(DailyLabel) || DailyObserverId != 0 || DailyScribeId != 0)
                    && !PendingDayNotes.Any(n => n.NzDate == DailyLabelDate))
                    PendingDayNotes.Add(new PendingDayNote {
                        NzDate = DailyLabelDate, Note = DailyLabel,
                        ObserverId = DailyObserverId, ScribeId = DailyScribeId });
                DailyLabelPendingUpload = false;
                DailyLabel = "";
                DailyLabelDate = "";
                DailyObserverId = 0;
                DailyScribeId = 0;
            }

            // Drop downloaded biometrics from a previous day; keep unsynced edits so they aren't lost
            var staleBio = TodayBiometrics
                .Where(kvp => kvp.Value.ObservationDate != nzTodayStr && !kvp.Value.IsPendingUpload)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in staleBio)
                TodayBiometrics.Remove(key);
        }

        /// <summary>Key for a bird's record on one day. peng_num alone is unique per bird, but a
        /// bird can be measured on more than one day with the earlier day still unsent — after a
        /// spell offline — and keying on the bird alone had the second measurement quietly replace
        /// the first, which was then never uploaded.</summary>
        public static string BiometricKey(string pengNum, string observationDate) => $"{pengNum}|{observationDate}";

        /// <summary>
        /// Store a biometric record for a bird on its observation date, replacing any existing one.
        /// </summary>
        public void SaveBiometric(BiometricRecord record)
        {
            if (string.IsNullOrEmpty(record.PengNum)) return;
            TodayBiometrics[BiometricKey(record.PengNum, record.ObservationDate)] = record;
        }

        /// <summary>The record held for a bird on a given day, if any.</summary>
        public BiometricRecord? GetBiometric(string pengNum, string observationDate) =>
            TodayBiometrics.TryGetValue(BiometricKey(pengNum, observationDate), out var r) ? r : null;

        /// <summary>Re-key records saved by an older build, which used the bare peng_num. Runs on
        /// load, so a queued record written before the change still uploads and still opens.</summary>
        public void MigrateBiometricKeys()
        {
            foreach (var old in TodayBiometrics.Where(kv => !kv.Key.Contains('|')).ToList())
            {
                TodayBiometrics.Remove(old.Key);
                var date = string.IsNullOrEmpty(old.Value.ObservationDate)
                    ? MainActivity.NzToday.ToString("yyyy-MM-dd") : old.Value.ObservationDate;
                if (string.IsNullOrEmpty(old.Value.PengNum)) old.Value.PengNum = old.Key;
                TodayBiometrics[BiometricKey(old.Value.PengNum, date)] = old.Value;
            }
        }

        /// <summary>Tags held on this phone from before the reader's "LA" prefix was dropped —
        /// scans taken but not yet uploaded, and the boxes' last-known state. They are compared
        /// against freshly scanned tags by string, so one spelling of a bird has to win or the
        /// same bird reads as two. Idempotent: a bare tag is already what this leaves behind.</summary>
        public void MigrateTagPrefixes()
        {
            foreach (var obs in PendingObservations.Concat(PreviousBoxes.Values).Concat(TodayBoxes.Values))
                foreach (var scan in obs.ScannedIds)
                    scan.BirdId = BluetoothManager.BareEid(scan.BirdId ?? "");
        }

        /// <summary>Which spelling of peng_num this state holds: 0, the bare numbers the API used to
        /// hand out for the viewing colony ("1039"); PengNumFormatFull, the stored form ("PT1039")
        /// it speaks now. Defaults to 0 so a file from an older build reads as needing migration.</summary>
        public int PengNumFormat { get; set; }
        public const int PengNumFormatFull = 1;

        /// <summary>Re-key biometrics held from before the API spoke full numbers. The unsent ones
        /// are the point: the server now refuses a bare peng_num on a write, so a record queued by
        /// the old build would sit in the queue for good. Needs the colony's prefix — a bare number
        /// only ever meant "this colony's bird" — so with none known yet it waits and runs on a later
        /// load. Idempotent: a number that already has letters is left as it is.</summary>
        public bool MigratePengNumsToFull(string colonyPrefix)
        {
            if (PengNumFormat >= PengNumFormatFull || string.IsNullOrEmpty(colonyPrefix)) return false;
            foreach (var old in TodayBiometrics.ToList())
            {
                var full = PengNums.Full(old.Value.PengNum, colonyPrefix);
                var key = BiometricKey(full, old.Value.ObservationDate);
                if (key == old.Key && full == old.Value.PengNum) continue;
                TodayBiometrics.Remove(old.Key);
                old.Value.PengNum = full;
                // Two spellings of one bird's day: unsent work wins over a downloaded copy.
                if (TodayBiometrics.TryGetValue(key, out var there) && there.IsPendingUpload && !old.Value.IsPendingUpload)
                    continue;
                TodayBiometrics[key] = old.Value;
            }
            PengNumFormat = PengNumFormatFull;
            return true;
        }

        public BoxObservation? GetTodayForBox(string boxName)
        {
            if (TodayBoxes.TryGetValue(boxName, out var today))
                return today;
            // Check pending for today's date
            var nzToday = MainActivity.NzToday;
            return PendingObservations
                .Where(o => o.BoxName == boxName && MainActivity.ToNzTime(o.WhenDataCollectedUtc).Date == nzToday)
                .OrderByDescending(o => o.WhenDataCollectedUtc)
                .FirstOrDefault();
        }

        /// <summary>
        /// Save a box observation. If same box + same NZ day exists in pending, update it.
        /// Otherwise add new.
        /// </summary>
        public void SaveBoxObservation(string boxName, BoxObservation obs)
        {
            obs.BoxName = boxName;
            var nzDate = MainActivity.ToNzTime(obs.WhenDataCollectedUtc).Date;
            var existing = PendingObservations.FirstOrDefault(o =>
                o.BoxName == boxName && MainActivity.ToNzTime(o.WhenDataCollectedUtc).Date == nzDate);
            if (existing != null)
            {
                // Update in place
                var idx = PendingObservations.IndexOf(existing);
                PendingObservations[idx] = obs;
            }
            else
            {
                PendingObservations.Add(obs);
            }
        }

    }
}
