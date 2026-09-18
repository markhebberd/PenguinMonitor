import React, { Fragment, Suspense, createContext, lazy, useCallback, useContext, useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { updateRecord, createRecord, deleteRecord, fetchHistory, fetchColonies, saveVerification } from './api/boxtags';
import { getStoreVersion, syncDatabase, triggerSync, primeFromCache, queryAllLocations, queryCarryForward, getDcmBoxes, prevNonIgnObs, queryPreviousObservations, getDateStats, computeDateStats, startPolling, stopPolling, getColonyId, setActiveColony, observedSexGuess, observedSexScore, SEX_CONFIRM_SCORE, queryBoxDetailSync, splitDismissed, dismissError, undismissError, computeAllPenguinsRows, computeBoxesSeenByPit, queryChipOnlyBoxes, getDayNote, getDayPeople, getUsers, getUserName, saveDayNote, getObserverName, getCachedFmDates, setCachedFmDates, searchLocal, displayPengNum, fullPengNum, pengNumValue, comparePengNum, activeColonyPrefix } from './api/localdb';
import { useAllPenguins, useBoxInfo, useOverview, useBoxTags, useDateStats, useBoxDetail, useBirdDetail, useDayData, useEggArrival, useFirstEgg, useDistinctAdults, usePeakAdults, useChickReturn, useMissedScans, useMissingNoScans, useDbVersion, useBirdTwoBoxes, useScanBeforeChip, useDeadScanned, useImprobableCounts, useFutureObservations, useRetiredTagScans, useChicksNoScan, useDuplicateObservations, useDuplicateScans, useSameGenderConflicts, useChickSizeMismatch, useMissingChipMeasures } from './api/useLocalDb';
import { getSeasonStart, getSeasonLabel, SEASON_START_MONTH, SEASON_START_DAY } from './config';
import { DAY, BREEDING_OFFSETS, SECOND_EGG_LAG_DAYS, MAX_OFFSPRING_SHOWN, PAIR_WEIGHTS, IMPLIED_SHARE_CONFIDENCE, PRE_BREEDING_SIGHTINGS_CAP } from './breedingConstants';
// The breeding algorithm itself — one implementation, shared with the server. See src/breeding.ts.
import { parseDate, isChickAtObsDate, segmentClutches, postGuardRanges, looksFledged } from './breeding';
import type { Clutch } from './breeding';
import { ColonyMap } from './components/ColonyMap';
import { BoxGrid } from './components/BoxGrid';
import { StatsPanel } from './components/StatsPanel';
// A code-split chunk can 404 after a new deploy (its hashed filename is gone), and nginx's
// SPA fallback then serves index.html (text/html) in its place — so the dynamic import fails
// with a MIME-type error. Reload once to pick up the fresh index + chunk hashes; a sessionStorage
// guard prevents a reload loop if the import genuinely can't be loaded.
function lazyWithReload<T extends React.ComponentType<any>>(factory: () => Promise<{ default: T }>) {
  return lazy(() => factory()
    .then(m => { sessionStorage.removeItem('ww_chunk_reload'); return m; })
    .catch((err: unknown) => {
      if (!sessionStorage.getItem('ww_chunk_reload')) {
        sessionStorage.setItem('ww_chunk_reload', '1');
        window.location.reload();
        return new Promise<{ default: T }>(() => {}); // never resolves; page is reloading
      }
      throw err;
    }));
}
const DiskHistoryChart = lazyWithReload(() => import('./components/DiskHistoryChart'));
const AlgorithmDoc = lazyWithReload(() => import('./components/AlgorithmDoc'));
import type { BoxTag } from './types';
import './App.css';

interface Scan { scan_id?:number; peng_num?:string|null; pit_id:string; sex:string|null; life_stage:string|null; chip_date:string|null; chipped_as_adult:number|null; }

interface Observation {
  observation_id?:number;
  observation_time_utc:string;
  adults:number; eggs:number; chicks:number;
  breeding_status:string|null; gate_status:string|null; notes:string;
  no_scan?:number;
  fledged_unchipped?:number;
  // What the counts can't show: an end of life a monitor saw on this visit. null is "nothing
  // recorded", 0 is "looked, nothing failed" — a distinction detection will need.
  failed_eggs?:number|null;
  dead_chicks?:number|null;
  scans: Scan[];
  edit_count?:string|number;
  observer_id?:number|string|null;
}
interface ChippedHere { peng_num:string; pit_id:string; sex:string|null; life_stage:string|null; chipped_as_adult:number; chip_date:string; chip_by:string|null; chick_size_code?:string|null; }
// The algorithm's tunable numbers live in one module so the admin page's Algorithm
// tab can quote the same values the code runs on. See src/breedingConstants.ts.

function displayStatus(status: string|null, eggs: number, chicks: number, postGuard = false): string|null {
  const s = (status || '').trim();
  // Eggs/chicks in the box mean incubation/guard has started, whatever pre-breeding
  // assessment (NO/UNL/POT/CON/BR/blank) was last recorded. Explicit stage or alert
  // statuses (I, G, PG, MOULT, ABN, DCM) always display as stored.
  if (['BR', 'CON', 'POT', 'UNL', 'NO', ''].includes(s)) {
    if (chicks > 0) return postGuard ? 'PG' : 'G';
    if (eggs > 0) return 'I';
    if (s === 'BR') return 'NO';
  }
  // Guard the box has since left is post-guard, however it was recorded.
  return postGuard && s === 'G' ? 'PG' : status;
}

/** Post-guard ranges for one box, rebuilt only when the local database changes — the box
 *  view asks once per observation card and the day view once per row. */
const pgRangeCache = new Map<string, { version: number; ranges: { from: number; to: number }[] }>();
function boxPostGuardRanges(box: string): { from: number; to: number }[] {
  const version = getStoreVersion();
  const hit = pgRangeCache.get(box);
  if (hit && hit.version === version) return hit.ranges;
  const ranges = postGuardRanges(queryBoxDetailSync(box)?.observations || []);
  pgRangeCache.set(box, { version, ranges });
  return ranges;
}

/** Had this box's chicks been left to themselves by this moment? */
function isPostGuard(box?: string | null, timeUtc?: string | null): boolean {
  if (!box || !timeUtc) return false;
  const t = parseDate(timeUtc).getTime();
  return boxPostGuardRanges(box).some(r => t >= r.from && t <= r.to);
}

/** Status badge for read-only views: an IGN observation shows the box's previous
 *  (pre-IGN) nest status instead, so ignoring a nest doesn't hide its real state.
 *  The editable ObsCard deliberately still shows IGN. `o` may be an observation or a
 *  sighting object (time in observation_time_utc or date). */
function displayStatusOrPrev(o: any, box?: string): string | null {
  if ((o.breeding_status || '').trim() === 'IGN') {
    const prev = box ? prevNonIgnObs(box, o.observation_time_utc || o.date) : null;
    return prev ? displayStatus(prev.breeding_status, prev.eggs || 0, prev.chicks || 0, isPostGuard(box, prev.observation_time_utc)) : null;
  }
  return displayStatus(o.breeding_status, o.eggs, o.chicks, isPostGuard(box, o.observation_time_utc || o.date));
}

const DARK_TEXT_STATUSES = new Set(['NO','UNL','POT','CON','I','']);

// "Only changed" day-view filter fields (compare a day's observation to the box's previous one)
const CHANGED_FIELDS: { key: string; label: string }[] = [
  { key: 'status', label: 'Breeding status' },
  { key: 'adults', label: 'Adults' },
  { key: 'eggs', label: 'Eggs' },
  { key: 'chicks', label: 'Chicks' },
  { key: 'sum', label: 'Eggs+chicks' },
];
/** True if a day's observation differs from the box's previous observation in any selected field.
 *  Breeding status carries forward: a blank current status inherits the previous, so only a
 *  newly-recorded differing status counts. A missing previous is an empty baseline (0 / no status). */
function obsDiffersFromPrev(o: any, prev: any, fields: Set<string>): boolean {
  if (fields.has('status')) {
    const cur = (o.breeding_status || '').trim();
    const prevStatus = (prev?.breeding_status || '').trim();
    if (cur && cur !== prevStatus) return true;
  }
  if (fields.has('adults') && (o.adults || 0) !== (prev?.adults || 0)) return true;
  if (fields.has('eggs') && (o.eggs || 0) !== (prev?.eggs || 0)) return true;
  if (fields.has('chicks') && (o.chicks || 0) !== (prev?.chicks || 0)) return true;
  if (fields.has('sum') && ((o.eggs || 0) + (o.chicks || 0)) !== ((prev?.eggs || 0) + (prev?.chicks || 0))) return true;
  return false;
}

// Color progression: NO → UNL → POT → CON → BR → Guard → PG → Molting. Red = alert only.
const STATUS_COLORS: Record<string,string> = {
  NO:'#E0E0E0',       // gray
  UNL:'#FFF9C4',      // pale yellow
  POT:'#FFF176',      // yellow
  CON:'#FFD54F',      // amber
  BR:'#66BB6A',       // light green - breeding confirmed
  I:'#A5D6A7',        // lightest green - incubation
  G:'#4CAF50',        // mid green - guard
  PG:'#2E7D32',       // darkest green - post guard
  MOULT:'#42A5F5',    // blue - moulting
  ABN:'#F44336',      // red - alert
  DCM:'#BCAAA4',      // light brown
  IGN:'#90A4AE',      // blue-grey - ignored (excused from Full Monitor)
  '':'#F5F5F5',
};

const STATUS_NAMES: Record<string,string> = {
  NO:'No', UNL:'Unlikely', POT:'Potential', CON:'Confident',
  I:'Incubation', G:'Guard', PG:'Post-guard', MOULT:'Moulting',
  DCM:'DCM', IGN:'Ignored',
};

// Observer-settable breeding statuses for the quick radial status picker on a locked
// observation. Order = ring position: CON at top (12 o'clock), then clockwise.
const STATUS_PICK_OPTIONS = ['CON','POT','UNL','NO','ABN','DCM','IGN'];

function SeasonBar({ observations, chickChipTimes, seasonStart, seasonEnd, label, todayCutoff, onHighlight, onScrollTo }: {
  observations: Observation[]; chickChipTimes?: number[]; seasonStart: Date; seasonEnd: Date; label: string; todayCutoff?: Date;
  onHighlight?: (obsDate: string | null) => void;
  onScrollTo?: (obsDate: string) => void;
}) {
  const totalMs = seasonEnd.getTime() - seasonStart.getTime();
  if (totalMs <= 0) return null;

  const sorted = [...observations]
    .filter(o => { const t = parseDate(o.observation_time_utc).getTime(); return t >= seasonStart.getTime() && t <= seasonEnd.getTime(); })
    .sort((a, b) => parseDate(a.observation_time_utc).getTime() - parseDate(b.observation_time_utc).getTime());

  // Also consider obs before this season to get the initial status
  const allSorted = [...observations].sort((a, b) => parseDate(a.observation_time_utc).getTime() - parseDate(b.observation_time_utc).getTime());

  // Build status changes from ALL observations (to carry forward pre-season status)
  // Derive status: I=Incubation (eggs, no chicks), G=Guard (chicks present)
  const changes: { time: number; status: string }[] = [];
  let runningStatus = '';
  for (const obs of allSorted) {
    let s = obs.breeding_status;
    // BR maps to incubation or guard based on egg/chick state
    if (s === 'BR') {
      s = obs.chicks > 0 ? 'G' : obs.eggs > 0 ? 'I' : null;
    }
    // Infer from egg/chick presence even without explicit status
    if (!s && obs.chicks > 0) {
      s = 'G';
    } else if (!s && obs.eggs > 0 && runningStatus !== 'I' && runningStatus !== 'G') {
      s = 'I';
    }
    // End when eggs+chicks drop to 0
    if ((runningStatus === 'G' || runningStatus === 'I') && obs.eggs === 0 && obs.chicks === 0 && !s) {
      runningStatus = '';
      changes.push({ time: parseDate(obs.observation_time_utc).getTime(), status: '' });
    } else if (s && s !== runningStatus) {
      runningStatus = s;
      changes.push({ time: parseDate(obs.observation_time_utc).getTime(), status: s });
    }
  }

  const dataEnd = todayCutoff ? Math.min(todayCutoff.getTime(), seasonEnd.getTime()) : seasonEnd.getTime();

  // Predicted phases, one per breeding attempt, off the same clutch segmentation and laid
  // estimate the rest of the app runs on. Per attempt matters twice over: a season with a
  // relay clutch gets both phases drawn, and a season bar is drawn from ITS OWN clutches
  // instead of whatever the box's latest attempt happened to be dated at.
  const phases = segmentClutches(allSorted).map(c => ({
    from: c.start,
    pg: (c.laid ?? c.start) + BREEDING_OFFSETS.pg * DAY,       // guard ends: laid + 52d
    to: c.windowEnd,                                           // the check that ended it, or the predicted fledge
    clutch: c,                                                 // for looksFledged, which needs the laid estimate
  }));

  // Build segments: observer-set statuses first, then overlay calculated phases
  const segments: { startPct: number; endPct: number; status: string }[] = [];

  // Observer-set status segments
  for (let i = 0; i < changes.length; i++) {
    const segStart = Math.max(changes[i].time, seasonStart.getTime());
    let segEnd = (i + 1 < changes.length) ? Math.min(changes[i + 1].time, dataEnd) : dataEnd;
    // Truncate Guard where its own attempt's guard ends (calculated)
    const pgIn = phases.find(p => p.pg > segStart && p.pg < segEnd)?.pg;
    if (changes[i].status === 'G' && pgIn) segEnd = pgIn;
    if (segEnd <= seasonStart.getTime()) continue;
    if (segStart >= dataEnd) continue;
    if (!changes[i].status) continue; // skip empty status segments
    const startPct = ((segStart - seasonStart.getTime()) / totalMs) * 100;
    const endPct = ((segEnd - seasonStart.getTime()) / totalMs) * 100;
    segments.push({ startPct, endPct, status: changes[i].status });
  }

  // Add the calculated PG phase after each attempt's guard ends. The observer sets BR
  // (displayed as G) from egg appearance, so there's no separate Guard to draw — the
  // observer-set G covers it. Moulting is shown from biometric data only, never calculated.
  const addPhase = (start: number, end: number, status: string) => {
    const s = Math.max(start, seasonStart.getTime());
    const e = Math.min(end, dataEnd);
    if (e <= s) return;
    segments.push({ startPct: ((s - seasonStart.getTime()) / totalMs) * 100, endPct: ((e - seasonStart.getTime()) / totalMs) * 100, status });
  };
  for (const p of phases) if (p.pg < dataEnd) addPhase(p.pg, p.to, 'PG');

  // Future portion (white) after today
  const futurePct = todayCutoff ? ((todayCutoff.getTime() - seasonStart.getTime()) / totalMs) * 100 : null;

  // Month labels for this season
  const months: { label: string; pct: number }[] = [];
  for (let m = 0; m < 13; m++) {
    const d = new Date(seasonStart);
    d.setMonth(d.getMonth() + m);
    d.setDate(1);
    if (d.getTime() >= seasonStart.getTime() && d.getTime() <= seasonEnd.getTime()) {
      months.push({ label: d.toLocaleDateString('en-NZ', { month: 'short' }), pct: ((d.getTime() - seasonStart.getTime()) / totalMs) * 100 });
    }
  }

  // Find first egg and first chick appearance in this season
  let firstEggTime: number | null = null;
  let firstChickTime: number | null = null;
  let prevEggs = 0;
  let prevChicks = 0;
  for (const o of sorted) {
    if (o.eggs > 0 && prevEggs === 0 && firstEggTime === null) {
      firstEggTime = parseDate(o.observation_time_utc).getTime();
    }
    if (o.chicks > 0 && prevChicks === 0 && firstChickTime === null) {
      firstChickTime = parseDate(o.observation_time_utc).getTime();
    }
    prevEggs = o.eggs;
    prevChicks = o.chicks;
  }

  // Milestone markers for egg and chick first appearance
  const milestones: { pct: number; icon: string; label: string }[] = [];
  if (firstEggTime && firstEggTime >= seasonStart.getTime() && firstEggTime <= seasonEnd.getTime()) {
    milestones.push({ pct: ((firstEggTime - seasonStart.getTime()) / totalMs) * 100, icon: '\uD83E\uDD5A', label: 'First egg' });
  }
  if (firstChickTime && firstChickTime >= seasonStart.getTime() && firstChickTime <= seasonEnd.getTime()) {
    milestones.push({ pct: ((firstChickTime - seasonStart.getTime()) / totalMs) * 100, icon: '\uD83D\uDC23', label: 'First chick' });
  }

  // Classify each monitor as routine, significant, or warning
  type MarkerType = 'routine' | 'egg-appear' | 'chick-appear' | 'egg-gone' | 'chick-gone' | 'chick-fledged' | 'no-adult-warn';
  const markers: { pct: number; obs: typeof sorted[0]; type: MarkerType; icon: string; date: string }[] = [];
  for (let i = 0; i < sorted.length; i++) {
    const o = sorted[i];
    const prev = i > 0 ? sorted[i - 1] : null;
    const t = parseDate(o.observation_time_utc).getTime();
    const pct = ((t - seasonStart.getTime()) / totalMs) * 100;

    // Detect significant events
    const eggsAppeared = prev !== null && prev.eggs === 0 && o.eggs > 0;
    const chicksAppeared = prev !== null && prev.chicks === 0 && o.chicks > 0;
    const eggsGone = prev !== null && prev.eggs > 0 && o.eggs === 0 && o.chicks <= prev.chicks;
    const chicksGone = prev !== null && prev.chicks > 0 && o.chicks === 0;
    // An unattended nest is only worth flagging while the parents should still be attending
    // it — dated off this observation's OWN attempt, so the same laid estimate decides both
    // this warning and where post-guard starts. Past that mark an empty nest is the end of
    // guard, not an alarm.
    const attempt = phases.find(p => t >= p.from && t <= p.to);
    const prePgNoAdults = !!attempt && t < attempt.pg && o.eggs + o.chicks > 0 && o.adults === 0;
    // Chicks gone from a nest they were ready to leave have fledged, not vanished — see
    // looksFledged. Eggs are never fledged, so a clutch that lost eggs at the same check is
    // a loss. A monitor's own "presumed fledged" on either side of the disappearance (the
    // last sighting, or the check that found the box empty) settles it outright.
    const prevT = prev ? parseDate(prev.observation_time_utc).getTime() : null;
    const recordedFledged = Number(prev?.fledged_unchipped || 0) > 0 || Number(o.fledged_unchipped || 0) > 0;
    const fledged = chicksGone && !eggsGone && !!attempt && prevT !== null
      && looksFledged(attempt.clutch, prevT, { chickChipTimes, recordedFledged });

    let type: MarkerType = 'routine';
    let icon = '';
    if (prePgNoAdults) { type = 'no-adult-warn'; icon = '⚠'; }
    else if (fledged) { type = 'chick-fledged'; icon = '🐥'; }
    else if (eggsGone || chicksGone) { type = eggsGone ? 'egg-gone' : 'chick-gone'; icon = '✕'; }
    else if (eggsAppeared) { type = 'egg-appear'; icon = '\uD83E\uDD5A'; }
    else if (chicksAppeared) { type = 'chick-appear'; icon = '\uD83D\uDC23'; }

    markers.push({ pct, obs: o, type, icon, date: o.observation_time_utc });
  }

  return (
    <div className="season-bar">
      <div className="season-bar-label">{label}</div>
      <div className="season-bar-content">
        <div className="status-bar-labels">
          {months.map((m, i) => <span key={i} className="month-label" style={{ left: `${m.pct}%` }}>{m.label}</span>)}
        </div>
        <div className="status-bar">
          {segments.map((seg, i) => (
            <div key={i} className="status-segment" style={{ left: `${seg.startPct}%`, width: `${seg.endPct - seg.startPct}%`, backgroundColor: STATUS_COLORS[seg.status] || STATUS_COLORS[''] }}
              title={STATUS_NAMES[seg.status] || 'No status'} />
          ))}
          {markers.map((m, i) => (
            m.type === 'routine' ? (
              <div key={i}
                className="status-marker-tick"
                style={{ left: `${m.pct}%` }}
                onMouseEnter={() => onHighlight?.(m.date)}
                onMouseLeave={() => onHighlight?.(null)}
                onClick={() => onScrollTo?.(m.date)}
                title={`${fmtDateTime(m.obs.observation_time_utc)}\n\uD83D\uDC27${m.obs.adults} \uD83E\uDD5A${m.obs.eggs} \uD83D\uDC23${m.obs.chicks}${m.obs.breeding_status ? ' ' + m.obs.breeding_status : ''}`}
              />
            ) : (
              <div key={i}
                className={`status-marker-event ${m.type}`}
                style={{ left: `${m.pct}%` }}
                onMouseEnter={() => onHighlight?.(m.date)}
                onMouseLeave={() => onHighlight?.(null)}
                onClick={() => onScrollTo?.(m.date)}
                title={`${fmtDateTime(m.obs.observation_time_utc)}\n\uD83D\uDC27${m.obs.adults} \uD83E\uDD5A${m.obs.eggs} \uD83D\uDC23${m.obs.chicks}${m.obs.breeding_status ? ' ' + m.obs.breeding_status : ''}${m.type === 'no-adult-warn' ? '\n⚠ No adults before post-guard!' : m.type === 'chick-fledged' ? '\n🐥 Fledged' : m.type.includes('gone') ? '\n✕ Disappeared' : ''}`}
              >{m.icon}</div>
            )
          ))}
          {/* milestones now shown as event markers */}
          {futurePct !== null && futurePct < 100 && (
            <div className="status-future" style={{ left: `${futurePct}%`, width: `${100 - futurePct}%` }} />
          )}
        </div>
      </div>
    </div>
  );
}

/** Colour key for the breeding status bars (No, Unlikely, Potential, …). */
function StatusLegend() {
  return (
    <div className="status-bar-legend">
      {Object.entries(STATUS_NAMES).map(([k, v]) => (
        <span key={k}><i style={{ background: STATUS_COLORS[k] }} />{v}</span>
      ))}
    </div>
  );
}

function BreedingStatusBar({ observations, box, onHighlight, onScrollTo, hideLegend }: { observations: Observation[]; box?: string; onHighlight?: (date: string | null) => void; onScrollTo?: (date: string) => void; hideLegend?: boolean }) {
  // When chicks were chipped in this nest. A chipping is the strongest thing the record has
  // to say a chick got away (looksFledged), and it is NOT an observation — a chick can be
  // chipped on a visit that logged no nest check at all, as box 9 was in 2025 — so the bars
  // can't find it in `observations` and have to be told.
  const dbVersion = useDbVersion();
  const chickChipTimes = useMemo(() => {
    if (!box) return [];
    return (queryBoxDetailSync(box)?.all_penguins || [])
      .filter((p: any) => p.is_chipped_here && !p.chipped_as_adult && p.chip_date)
      .map((p: any) => parseDate(p.chip_date).getTime());
  }, [box, dbVersion]);
  const now = new Date();
  const currentSeasonStart = getSeasonStart(now);
  void now; // currentSeasonEnd no longer needed - full year bar with todayCutoff

  // Previous season
  const prevSeasonEnd = new Date(currentSeasonStart);
  const prevSeasonStart = new Date(prevSeasonEnd);
  prevSeasonStart.setFullYear(prevSeasonStart.getFullYear() - 1);

  const prevLabel = getSeasonLabel(prevSeasonStart);
  const currentLabel = getSeasonLabel(now);

  // Current season bar runs full year (Apr 1 to Mar 31) but after today is white/empty
  const currentSeasonFullEnd = new Date(currentSeasonStart);
  currentSeasonFullEnd.setFullYear(currentSeasonFullEnd.getFullYear() + 1);

  const hasPrevData = observations.some(o => {
    const t = parseDate(o.observation_time_utc).getTime();
    return t >= prevSeasonStart.getTime() && t < prevSeasonEnd.getTime();
  });

  return (
    <div className="status-bar-wrap">
      <SeasonBar observations={observations} chickChipTimes={chickChipTimes} seasonStart={currentSeasonStart} seasonEnd={currentSeasonFullEnd} label={currentLabel} todayCutoff={now} onHighlight={onHighlight} onScrollTo={onScrollTo} />
      {hasPrevData && (
        <SeasonBar observations={observations} chickChipTimes={chickChipTimes} seasonStart={prevSeasonStart} seasonEnd={prevSeasonEnd} label={prevLabel} onHighlight={onHighlight} onScrollTo={onScrollTo} />
      )}
      {!hideLegend && <StatusLegend />}
    </div>
  );
}



/** One day after chipping — renders a chick-chipped bird in its chick-time context
 *  (pale yellow) without triggering the same-day chipped-here (green) styling. */
function chickContextDate(chipDate: string): string {
  return new Date(new Date(chipDate).getTime() + 86400000).toISOString().slice(0, 10);
}

/** A bird still living its chick life: chipped as a chick, given a size code, and not yet
 *  scanned back as an adult. Its mini shows the size code where an adult shows a sex icon,
 *  so size is what a reader sorts it by — a sexed chick is still a chick. */
function isChickRecord(s: any): boolean {
  return !s?.chipped_as_adult && !!s?.chick_size_code && !s?.hasReturned;
}
/** Biggest chick first — BC, LC, SC — the order they're weighed, written down and talked
 *  about. Anything without a size code (every adult) sorts after the chicks. */
const chickSizeOrder = (s: any) =>
  ({ BC: 0, LC: 1, SC: 2 } as Record<string, number>)[(s?.chick_size_code || '').toUpperCase()] ?? 3;
/** M first, F second, everyone else last — a chick ranks with "everyone else" whether or
 *  not it has been sexed, so siblings stay together and sort by size. */
const sexOrder = (s: any) => {
  if (isChickRecord(s)) return 2;
  const sex = (s?.sex || '').toUpperCase();
  return sex === 'M' ? 0 : sex === 'F' ? 1 : 2;
};
/** THE display order for birds shown together: males, then females, then the rest, with
 *  chicks among themselves biggest first. Every list of minis sorts through here, so a
 *  nest's two chicks read BC-then-LC wherever they meet — breeding window, chipping day,
 *  day row or observation. */
function scanSortMFC(a: any, b: any): number {
  return sexOrder(a) - sexOrder(b) || chickSizeOrder(a) - chickSizeOrder(b);
}

function penguinSexClass(sex: string|null|undefined, chipDate?: string|null, chippedAsAdult?: number|null, observationDate?: string): string {
  if (isChickAtObsDate(chipDate, chippedAsAdult, observationDate)) return 'chick';
  const s = (sex || '').toUpperCase();
  return s === 'F' ? 'f' : s === 'M' ? 'm' : '';
}

function penguinSexIcon(sex: string|null|undefined, chipDate?: string|null, chippedAsAdult?: number|null, observationDate?: string): string {
  if (isChickAtObsDate(chipDate, chippedAsAdult, observationDate)) return '\uD83D\uDC23';
  const s = (sex || '').toUpperCase();
  return s === 'F' ? '\u2640' : s === 'M' ? '\u2642' : '';
}

/** Field-observation sex + confidence, stored on biometrics.observed_sex as PM/MM/U/MF/PF.
 *  `short` = compact form for mini views (cM/mM/U/mF/cF); otherwise full words.
 *  Wording matches nestcheck's picker — the same code must not read as a different degree
 *  of certainty depending on which screen you're looking at.
 *  Legacy M/F values (and anything unrecognised) fall back gracefully. */
const OBSERVED_SEX: Record<string, { short: string; full: string }> = {
  PM: { short: 'cM', full: 'Confident M' },
  MM: { short: 'mM', full: 'Maybe M' },
  U:  { short: 'U',  full: 'Unsure' },
  MF: { short: 'mF', full: 'Maybe F' },
  PF: { short: 'cF', full: 'Confident F' },
  M:  { short: 'M',  full: 'Male' },    // legacy
  F:  { short: 'F',  full: 'Female' },  // legacy
};
function observedSexLabel(code: string|null|undefined, short: boolean): string {
  if (!code) return '';
  const entry = OBSERVED_SEX[code.toUpperCase()];
  if (!entry) return code; // unknown \u2014 show raw
  return short ? entry.short : entry.full;
}

/** Navigate on click, allow ctrl+click to open in new tab */
function navClick(e: React.MouseEvent, action: () => void) {
  if (e.ctrlKey || e.metaKey || e.button === 1) return; // let browser handle new tab
  e.preventDefault();
  e.stopPropagation();
  action();
}

function useDateTooltip() {
  const [tip, setTip] = useState<{ date: string; x: number; y: number } | null>(null);
  const timerRef = useRef<ReturnType<typeof setTimeout>>(undefined);
  const autoHideRef = useRef<ReturnType<typeof setTimeout>>(undefined);
  const show = useCallback((date: string, e: React.MouseEvent) => {
    clearTimeout(timerRef.current);
    clearTimeout(autoHideRef.current);
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    timerRef.current = setTimeout(() => {
      setTip({ date, x: rect.left, y: rect.bottom + 4 });
      // Auto-dismiss after 5s even if the pointer stays on the day.
      autoHideRef.current = setTimeout(() => setTip(null), 5000);
    }, 350);
  }, []);
  const hide = useCallback(() => { clearTimeout(timerRef.current); clearTimeout(autoHideRef.current); setTip(null); }, []);
  return { tip, show, hide };
}

// registeredFmDates: NZ date (YYYY-MM-DD) -> the season + date-number it was registered as
// in the enter-date workflow. Used, alongside a computed full monitor, to flag FM dates green.
const DateTooltipCtx = createContext<{ show: (date: string, e: React.MouseEvent) => void; hide: () => void; statsCache: Map<string, any>; registeredFmDates: Map<string, { season: number; number: number; partial: boolean }> }>({ show: () => {}, hide: () => {}, statsCache: new Map(), registeredFmDates: new Map() });

/**
 * The day's note, editable in place — one free-text line saying what this day's monitor was
 * ("Full monitor with Mark"). One per colony per date, so it is shown once at the top of the day
 * rather than repeated on every box's row, which is what the old per-observation
 * monitor_filename did. Clearing the text deletes the note.
 */
function DayField({ date, token, canEdit, saved, placeholder, addLabel, maxLength, nextFieldRef }: {
  date: string; token?: string; canEdit?: boolean;
  saved: string; placeholder: string; addLabel: string; maxLength: number;
  /** Enter commits and moves here, so the note flows into the people fields like Tab does. */
  nextFieldRef?: React.MutableRefObject<HTMLSpanElement | null>;
}) {
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(saved);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  // A value arriving from a sync (or switching days) replaces the field, but never mid-edit —
  // that would overwrite what the user is typing.
  useEffect(() => { if (!editing) setText(saved); }, [saved, editing]);
  useEffect(() => { if (editing) inputRef.current?.focus(); }, [editing]);

  // Enter commits and then moves focus on, which blurs this input — and blur commits too. Both
  // calls read the same not-yet-updated `saved`, so without this guard the day gets saved twice
  // and the second insert trips the (colony_id, note_date) unique key.
  const committing = useRef(false);
  const commit = async () => {
    if (committing.current) return;
    const next = text.trim();
    setEditing(false);
    if (!token || next === saved.trim()) { setText(saved); return; }
    committing.current = true;
    setSaving(true); setError(null);
    try {
      await saveDayNote(token, date, { note: next });
    } catch (e: any) {
      setError(e.message || 'Failed to save');
      setText(saved);
    } finally {
      committing.current = false;
      setSaving(false);
    }
  };

  if (!canEdit) return saved ? <span className="day-hdr-note">{saved}</span> : null;
  if (!editing) return (
    <span className={`day-hdr-note day-note-editable${saved ? '' : ' day-note-empty'}`}
      title={placeholder} onClick={() => setEditing(true)}>
      {saving ? 'Saving…' : (saved || addLabel)}
      {error && <span style={{color:'#F44336'}}> {error}</span>}
    </span>
  );
  return (
    <input ref={inputRef} className="day-note-input" value={text} maxLength={maxLength}
      placeholder={placeholder}
      onChange={e => setText(e.target.value)}
      onBlur={commit}
      onKeyDown={e => {
        if (e.key === 'Enter') { e.preventDefault(); commit(); nextFieldRef?.current?.focus(); }
        if (e.key === 'Escape') { e.preventDefault(); setText(saved); setEditing(false); }
      }} />
  );
}

/** Pick a person from the user table. Every user is listed, active or not: attribution is
 *  historical, and whoever did the work may since have left. Service accounts are excluded
 *  (getUsers drops them). Typing filters; Enter takes the top match; the blue bar shows which
 *  that is. Used for the day's observer/scribe and for a chip's chipper/assistant. */
function UserPickerField({ userId, label, onSave, addLabel, title, fieldRef, onAfterCommit }: {
  userId: number | null; label?: string; onSave: (id: number | null) => Promise<any>;
  addLabel?: string; title?: string;
  /** Lets the previous field hand focus here. */
  fieldRef?: React.MutableRefObject<HTMLSpanElement | null>;
  /** Called the moment a person is chosen, to move focus on to the next field. */
  onAfterCommit?: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Which option the arrow keys are sitting on. -1 = none highlighted, which is the state with
  // an empty box: Enter must not fire until you have either typed or arrowed to something.
  const [active, setActive] = useState(-1);
  const inputRef = useRef<HTMLInputElement>(null);
  const closedRef = useRef<HTMLSpanElement | null>(null);
  // A commit hands focus back to the closed field so the next Tab moves on. That focus must
  // not spring the picker open again, or selecting someone would trap you in the same field.
  const skipFocusOpen = useRef(false);
  const name = getUserName(userId);

  // Focus follows the open/closed flip, and only after the re-render — at commit time the
  // closed span does not exist yet, so focusing it there would be a no-op.
  const wantRefocus = useRef(false);
  useEffect(() => {
    if (open) { inputRef.current?.focus(); return; }
    if (wantRefocus.current) {
      wantRefocus.current = false;
      skipFocusOpen.current = true;
      closedRef.current?.focus();
      setTimeout(() => { skipFocusOpen.current = false; }, 0);
    }
  }, [open]);

  const commit = async (next: number | null) => {
    setOpen(false); setQuery('');
    // Chain on to the next field if there is one — that is what makes "brit ⏎ mar ⏎" work.
    // Otherwise hand focus back to this field so the next Tab moves along. Done before the
    // save is awaited, so typing the next name never waits on the network.
    if (onAfterCommit) onAfterCommit(); else wantRefocus.current = true;
    if (next === userId) return;
    setSaving(true); setError(null);
    try { await onSave(next); } catch (e: any) { setError(e.message || 'Failed to save'); }
    setSaving(false);
  };

  // tabIndex puts the closed field in the tab order, so Tab out of the day note lands here and
  // opens it ready to type — and Tab again moves on to the next person field.
  if (!open) return (
    <span ref={el => { closedRef.current = el; if (fieldRef) fieldRef.current = el; }} tabIndex={0} role="button"
      className={`day-hdr-note day-note-editable${name ? '' : ' day-note-empty'}`}
      title={title}
      onClick={() => { setQuery(''); setActive(-1); setOpen(true); }}
      onFocus={() => { if (!skipFocusOpen.current) { setQuery(''); setActive(-1); setOpen(true); } }}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setQuery(''); setActive(-1); setOpen(true); } }}>
      {name && label && <span className="day-hdr-people-label">{label}</span>}
      {saving ? 'Saving…' : (name || addLabel || '-')}
      {error && <span style={{color:'#F44336'}}> {error}</span>}
    </span>
  );

  const q = query.trim().toLowerCase();
  const matches = getUsers().filter(u => !q || u.name.toLowerCase().includes(q));
  // One flat list so the arrows walk "— not recorded —" too: clearing is an option like any
  // other. null stands for that row; everything else is a user id.
  const options: (number | null)[] = [...(userId !== null ? [null] : []), ...matches.map(u => u.id)];
  // Typing highlights the top match (so "brit ⏎" still works); an empty box highlights nothing,
  // and a query matching nobody highlights nothing — Enter must not fall through to "— not
  // recorded —" and silently clear the field.
  const topIdx = matches.length ? (userId !== null ? 1 : 0) : -1;
  const shown = active >= 0 ? Math.min(active, options.length - 1) : (q ? topIdx : -1);
  const move = (d: number) => setActive(i => {
    const from = i >= 0 ? i : (shown >= 0 ? shown : -1);
    return Math.max(0, Math.min(from + d, options.length - 1));
  });
  return (
    <span className="day-person-picker">
      {label && <span className="day-hdr-people-label">{label}</span>}
      <input ref={inputRef} className="day-note-input day-person-input" value={query}
        placeholder={name || 'Search people'}
        onChange={e => { setQuery(e.target.value); setActive(-1); }}
        // Blur closes the picker, but a result's onMouseDown fires first so the click lands.
        onBlur={() => setTimeout(() => setOpen(false), 150)}
        onKeyDown={e => {
          if (e.key === 'Escape') { e.preventDefault(); setOpen(false); setQuery(''); setActive(-1); }
          if (e.key === 'ArrowDown') { e.preventDefault(); move(1); }
          if (e.key === 'ArrowUp') { e.preventDefault(); move(-1); }
          // Only fires on something actually highlighted — typed-and-matched, or arrowed to.
          if (e.key === 'Enter') { e.preventDefault(); if (shown >= 0) commit(options[shown]); }
        }} />
      <div className="day-person-results">
        {userId !== null && (
          <div className={`day-person-result muted${shown === 0 ? ' top' : ''}`}
            ref={el => { if (shown === 0) el?.scrollIntoView({ block: 'nearest' }); }}
            onMouseDown={() => commit(null)}>— not recorded —</div>
        )}
        {matches.map((u, i) => {
          const idx = userId !== null ? i + 1 : i;
          return (
            <div key={u.id}
              className={`day-person-result${u.id === userId ? ' selected' : ''}${shown === idx ? ' top' : ''}`}
              // Keep the highlighted row visible: the list is long enough to scroll.
              ref={el => { if (shown === idx) el?.scrollIntoView({ block: 'nearest' }); }}
              onMouseDown={() => commit(u.id)}>
              {u.name}{u.active ? '' : <span className="muted"> (inactive)</span>}
            </div>
          );
        })}
        {matches.length === 0 && <div className="day-person-result muted">No one matches "{query}"</div>}
      </div>
    </span>
  );
}

/** Observer or scribe for the day — the shared picker, wired to this day's note row. */
function DayPersonField({ date, token, canEdit, field, userId, label, fieldRef, onAfterCommit }: {
  date: string; token?: string; canEdit?: boolean;
  field: 'observer_id' | 'scribe_id'; userId: number | null; label: string;
  fieldRef?: React.MutableRefObject<HTMLSpanElement | null>;
  onAfterCommit?: () => void;
}) {
  const name = getUserName(userId);
  if (!canEdit) return name ? <span className="day-hdr-note"><span className="day-hdr-people-label">{label}</span>{name}</span> : null;
  return <UserPickerField userId={userId} label={label} addLabel={`+ ${label}`}
    title={`Who was ${field === 'observer_id' ? 'observing' : 'recording'}`}
    fieldRef={fieldRef} onAfterCommit={onAfterCommit}
    onSave={id => saveDayNote(token || '', date, { [field]: id })} />;
}

/** The day's record beside the stats line: what happened, and who was out. The two people are
 *  users, picked from the user table; the note is free text.
 *
 *  The three are chained for the keyboard: Enter (or Tab) out of the note opens Observer, and
 *  choosing an observer jumps straight to Scribe — so "brit ⏎ mar ⏎" fills both without
 *  touching the mouse. */
function DayNoteEditor({ date, token, canEdit }: { date: string; token?: string; canEdit?: boolean }) {
  const { observer_id, scribe_id } = getDayPeople(date);
  const note = getDayNote(date) || '';
  const observerRef = useRef<HTMLSpanElement | null>(null);
  const scribeRef = useRef<HTMLSpanElement | null>(null);
  const parts: React.ReactNode[] = [];
  if (canEdit || note) parts.push(
    <DayField key="note" date={date} token={token} canEdit={canEdit} saved={note}
      placeholder="Monitor notes" addLabel="+ note" maxLength={255} nextFieldRef={observerRef} />);
  if (canEdit || observer_id) parts.push(
    <DayPersonField key="obs" date={date} token={token} canEdit={canEdit} field="observer_id"
      userId={observer_id} label="Observer" fieldRef={observerRef}
      onAfterCommit={() => scribeRef.current?.focus()} />);
  if (canEdit || scribe_id) parts.push(
    <DayPersonField key="rec" date={date} token={token} canEdit={canEdit} field="scribe_id"
      userId={scribe_id} label="Scribe" fieldRef={scribeRef} />);
  return (<>{parts.map((el, i) => (
    <Fragment key={i}>{i > 0 && <span className="day-hdr-sep"> · </span>}{el}</Fragment>
  ))}</>);
}

function DateStatsLine({ stats, showDate, date, hideLabel }: { stats: any; showDate?: boolean; date?: string; hideLabel?: boolean }) {
  const multiObs = stats.obs > stats.boxes;
  const { registeredFmDates } = useContext(DateTooltipCtx);
  const reg = date ? registeredFmDates.get(date.length > 10 ? toNzDateStr(date) : date) : undefined;
  // For a missed FM date, name the missing boxes when only a few, else just the count.
  const missing: string[] = stats.missingBoxes || [];
  const missedSuffix = missing.length > 0 && missing.length < 4 ? ` — missed "${missing.join(', ')}"` : ` — missed (${missing.length})`;
  return (<>
    {showDate && date && <b className="date-stats-date">{formatDate(date)}</b>}
    {reg?.partial
      ? <span style={{color:'#00796b'}}> <b>Partial Monitor</b> ({stats.boxes}/{stats.totalLocations})</span>
      : stats.isFullMonitor
      ? <span style={{color:'#2e7d32'}}> <b>Full Monitor</b> ({stats.boxes}/{stats.totalLocations})</span>
      : <span> {stats.boxes}/{stats.totalLocations} boxes</span>}
    {reg && (reg.partial
      ? <span style={{color:'#00796b'}}> <b>PM #{reg.number}</b> from {seasonRange(String(reg.season))}</span>
      : <span style={{color: stats.isFullMonitor ? '#2e7d32' : '#e65100'}}> <b>FM #{reg.number}</b> from {seasonRange(String(reg.season))}{stats.isFullMonitor ? '' : missedSuffix}</span>)}
    {/* Who recorded the boxes. Counts only when more than one person was out, where the
        split is the interesting part; a solo round just reads "by AL". */}
    {stats.observers?.length > 0 && <span className="muted"> by {stats.observers
      .map((o: any) => stats.observers.length > 1 ? `${o.name} (${o.count})` : o.name).join(', ')}</span>}
    {multiObs && <span>, {stats.obs} obs</span>}
    {stats.adults > 0 && <span> {'\uD83D\uDC27'}{stats.adults}</span>}
    {stats.eggs > 0 && <span> {'\uD83E\uDD5A'}{stats.eggs}</span>}
    {stats.chicks > 0 && <span> {'\uD83D\uDC23'}{stats.chicks}</span>}
    {stats.penguins > 0 && <span> {stats.penguins} scanned</span>}
    {stats.chipped > 0 && <span> {stats.chipped} chipped</span>}
    {/* The day's note. Hidden where an editable copy sits beside this line (the day page header). */}
    {!hideLabel && stats.label && <span className="muted"> {stats.label}</span>}
  </>);
}

function DateTooltipPortal({ tip, statsCache }: { tip: { date: string; x: number; y: number } | null; statsCache: Map<string, any> }) {
  if (!tip) return null;
  const stats = statsCache.get(tip.date) || computeDateStats(tip.date);
  if (!stats) return null;
  const left = Math.min(tip.x, window.innerWidth - 260);
  const above = tip.y + 120 > window.innerHeight;
  const top = above ? tip.y - 128 : tip.y;
  return (
    <div className="date-tooltip" style={{ left, top }}>
      <div><DateStatsLine stats={stats} showDate date={tip.date} /></div>
    </div>
  );
}

function DateLink({ date, onDayClick }: { date: string; onDayClick?: (day: string) => void }) {
  const day = date.length > 10 ? toNzDateStr(date) : date;
  const { show, hide, statsCache, registeredFmDates } = useContext(DateTooltipCtx);
  const fm = registeredFmDates.get(day);
  const stats = statsCache.get(day);
  const complete = !!stats?.isFullMonitor; // full monitor = complete box set (same test as the calendar)
  // A Partial Monitor (PM) date is green on registration alone — it's a deliberate partial round,
  // so the full box-set check doesn't apply. Otherwise: orange for a registered FM date whose
  // observations are incomplete (data missing), green for a complete full monitor, plain otherwise.
  const cls = fm?.partial ? ' fm-pm' : fm && !complete ? ' fm-partial' : complete ? ' fm-date' : '';
  return <a className={`date-link${cls}`} href={`/day/${day}`} onClick={e => navClick(e, () => onDayClick?.(day))}
    onMouseEnter={e => show(day, e)} onMouseLeave={hide}>{formatDate(date)}{fm ? <span className="fm-tag"> ({fm.partial ? 'PM' : 'FM'} {fm.number})</span> : ''}</a>;
}

/** Peng_num of the bird whose peng panel is currently open (null when none). Lets a
 *  mini click detect "already selected" and toggle the highlight. */
let openPanelPengNum: string | null = null;

/** While a peng panel is open, every mini of its bird gets a subtle lifted (3D) look —
 *  except the panel's own header mini, which is the selection itself, not a reference.
 *  Done with an injected style rule keyed on data-peng rather than per-element classes,
 *  so minis (re)rendered anywhere on the page while the panel is open still pick it up.
 *  Re-clicking a mini of the already-open bird toggles the highlight off/on. */
let selectedPengStyle: HTMLStyleElement | null = null;
let selectedPengKeys: string[] = [];
let selectedPengHidden = false;
function applySelectedPengStyle() {
  if (!selectedPengStyle) selectedPengStyle = document.head.appendChild(document.createElement('style'));
  selectedPengStyle.textContent = selectedPengKeys.length === 0 || selectedPengHidden ? '' :
    selectedPengKeys.map(k => `.scan[data-peng="${CSS.escape(k)}"]:not(.bird-title-peng *)`).join(', ') +
    ' { position: relative; top: -2px; box-shadow: 2px 3px 5px rgba(0,0,0,.5); }';
}
function setSelectedPengMinis(keys: (string | null | undefined)[]) {
  selectedPengKeys = keys.filter(Boolean) as string[];
  selectedPengHidden = false;
  applySelectedPengStyle();
}
function toggleSelectedPengMinis() {
  selectedPengHidden = !selectedPengHidden;
  applySelectedPengStyle();
}

function PenguinMini({ scan, onClick, observationDate, navigateDirectly, currentStatus, title }: { scan: Scan | ChippedHere | any; onClick: () => void; observationDate?: string; navigateDirectly?: boolean; currentStatus?: boolean; title?: string }) {
  const sex = (scan.sex || '').toUpperCase();
  const num = scan.peng_num ? `#${displayPengNum(scan.peng_num)}` : '';
  const chip = scan.pit_id ? scan.pit_id.slice(-8) : '';
  const wasChippedAsChick = !scan.chipped_as_adult;
  // currentStatus (bird-page header): solid yellow only while the bird is actually
  // chick-aged (<90 days since chipping). Beyond that it renders as an adult — sex
  // colour if returned and sexed, grey "unproven" otherwise (including chick-chipped
  // birds never scanned again, e.g. lost at sea) — with the yellow chick-origin inset.
  // Without currentStatus, life-stage is judged as at the given observation date.
  const stillChick = currentStatus
    ? (wasChippedAsChick && !scan.hasReturned && isChickAtObsDate(scan.chip_date, scan.chipped_as_adult))
    : isChickAtObsDate(scan.chip_date, scan.chipped_as_adult, observationDate);
  const cls = currentStatus
    ? (stillChick ? 'chick' : (sex === 'F' ? 'f' : sex === 'M' ? 'm' : ''))
    : penguinSexClass(sex, scan.chip_date, scan.chipped_as_adult, observationDate);
  const icon = currentStatus
    ? (stillChick ? '🐣' : (sex === 'F' ? '♀' : sex === 'M' ? '♂' : ''))
    : penguinSexIcon(sex, scan.chip_date, scan.chipped_as_adult, observationDate);
  const chickOrigin = wasChippedAsChick && !stillChick;
  const chipCls = currentStatus
    ? (chickOrigin && sex ? 'chipped-chick' : '')
    : (wasChippedAsChick ? 'chipped-chick' : '');
  const grayCls = currentStatus
    ? (chickOrigin && !sex ? 'unproven' : '')
    : (wasChippedAsChick && !stillChick && !sex && !observationDate ? 'unproven' : '');
  const obsNzDate = observationDate ? toNzDateStr(observationDate) : '';
  const chippedHereCls = scan.chip_date && obsNzDate && scan.chip_date.substring(0, 10) === obsNzDate ? 'chipped-here' : '';
  // Combined chick size code: LC + M → LCM, LC + no sex but returned → LCU, LC alone → LC
  const sc = scan.chick_size_code || '';
  // The size code carries a sex letter only when it's a GUESS (the U-tokens built below); a
  // CONFIRMED sex is already the pill colour, so "LCM" collapses to "LC" — but a guessed
  // "BC-2UM" keeps its tokens.
  const sizeLabel = sc ? (!sex && scan.hasReturned ? sc + 'U' : sc) : '';
  // Unsexed bird: surface biometric sex guesses (U = unconfirmed). A single guess merges
  // onto the size label with no count/space, sharing one U — "LCU"+M → "LCUM", "LC"+M → "LCUM".
  // Repeated guesses or guesses for both sexes use numbered tokens, most-guessed first,
  // hyphen-joined so the guess data reads as one unit — e.g. "BC-2UM", "1UM-1UF".
  const guess = sex ? { m: 0, f: 0 } : observedSexGuess(scan.peng_num);
  const guessSexes = [{ c: guess.m, s: 'M' }, { c: guess.f, s: 'F' }].filter(g => g.c > 0).sort((a, b) => b.c - a.c);
  let mid: string;
  if (guessSexes.length === 0) {
    mid = sizeLabel;
  } else if (guessSexes.length === 1 && guessSexes[0].c === 1) {
    mid = (sizeLabel.endsWith('U') ? sizeLabel : sizeLabel + 'U') + guessSexes[0].s;
  } else {
    // tokens carry their own U, so drop the size label's redundant returned-unsexed U ("BCU" → "BC")
    const base = sizeLabel.endsWith('U') ? sizeLabel.slice(0, -1) : sizeLabel;
    mid = [base, guessSexes.map(g => `${g.c}U${g.s}`).join('-')].filter(Boolean).join('-');
  }
  const href = scan.peng_num ? `/bird/${scan.peng_num}` : undefined;
  // Hovering a mini tied to a data entry shows that entry's NZ-local time. Use the first
  // timestamped source available — an explicit observationDate, or a timestamp carried on the
  // scan/sighting object — skipping bare YYYY-MM-DD dates; never overrides an explicit title.
  const timeSrc = [observationDate, scan.observation_time_utc, scan.date, scan.last_seen]
    .find((v: any) => typeof v === 'string' && v.length > 10);
  const nzTime = timeSrc
    ? parseDate(timeSrc).toLocaleString('en-NZ', { timeZone: 'Pacific/Auckland', weekday: 'short', day: 'numeric', month: 'short', year: 'numeric', hour: 'numeric', minute: '2-digit' })
    : undefined;
  return (
    <a className={`scan clickable ${cls} ${chipCls} ${grayCls} ${chippedHereCls}`} data-peng={scan.peng_num || chip || undefined} href={href} title={title || nzTime}
      /* Yellow chipped-as-chick strip begins halfway across the gap between the pit_id and the
         size code: in from the right by the right padding (--scan-pad-r, which each mini size
         sets to match its own) plus the code and half a gap. The inter-unit gap is a .scan-sp
         span (0.5ch), so half is 0.25ch. Falls back to a fixed 85% when there's no code. */
      style={mid ? ({ '--chick-stop': `calc(100% - var(--scan-pad-r, 2px) - ${mid.length + 0.25}ch)` } as React.CSSProperties) : undefined}
      onClick={navigateDirectly ? undefined : e => navClick(e, () => {
      onClick();
      // Re-clicking the bird already open in the panel won't remount it — toggle the highlight.
      if (scan.peng_num && scan.peng_num === openPanelPengNum) toggleSelectedPengMinis();
    })}>
      {num}{num && icon ? <span className="scan-sp" /> : null}{!sizeLabel && icon && <span className="sex-icon">{icon}</span>}{(num || icon) && chip ? <span className="scan-sp" /> : null}{chip}{mid ? <><span className="scan-sp" />{mid}</> : null}
    </a>
  );
}

/** One bird at one box on one day — the unit of "we know this bird was here".
 *  A nest observation records box contents AND who was scanned; a chipping records
 *  only the bird. Both are sightings; only the contents differ. Everything that
 *  reasons about nest attendance (pair detection, per-slot visit counts) consumes
 *  this one list, so a bird known only from a chipping is never invisible to it. */
interface BoxSighting {
  key: string;          // last 8 of pit_id — the bird identity used across box views
  t: number;            // when, ms
  time: string;         // the source timestamp ('…Z' obs time, or a bare chip date)
  day: string;          // NZ day
  group: string;        // co-presence group: one observation, or one day's chippings
  source: 'scan' | 'chip';
  bird: any;            // bird record: the scan row, or the all_penguins entry
}

/** Every sighting at one box, chronological. A bird scanned twice in one observation
 *  is one sighting; a bird scanned AND chipped here on the same NZ day is one sighting
 *  (the scan wins — it carries the box contents). */
function boxSightings(observations: Observation[], allPenguinsInBox?: any[]): BoxSighting[] {
  const out: BoxSighting[] = [];
  const scannedDayKey = new Set<string>(); // `<key>|<nzDay>`
  for (const o of observations) {
    const t = parseDate(o.observation_time_utc).getTime();
    const day = toNzDateStr(o.observation_time_utc);
    const group = `o${o.observation_id ?? o.observation_time_utc}`;
    const seen = new Set<string>();
    for (const s of o.scans) {
      const key = s.pit_id.slice(-8);
      if (seen.has(key)) continue; // one visit per observation
      seen.add(key);
      scannedDayKey.add(`${key}|${day}`);
      out.push({ key, t, time: o.observation_time_utc, day, group, source: 'scan', bird: s });
    }
  }
  for (const p of (allPenguinsInBox || [])) {
    if (!p.is_chipped_here || !p.chip_date || !p.pit_id) continue;
    const key = p.pit_id.slice(-8);
    const day = String(p.chip_date).slice(0, 10);
    if (scannedDayKey.has(`${key}|${day}`)) continue;
    out.push({ key, t: parseDate(`${day} 00:00:00`).getTime(), time: day, day,
      group: `c${day}`, source: 'chip', bird: p });
  }
  return out.sort((a, b) => a.t - b.t);
}

/** What the season says for one candidate pair. Each count is multiplied by its weight in
 *  PAIR_WEIGHTS and the results added: strong evidence counts for more, but enough weak
 *  evidence can still carry a pair, which is what lets a thinly-monitored nest be answered
 *  at all. Sightings after guard score nothing — the nest is unattended by then — though
 *  they still make a bird a candidate. */
interface PairEvidence {
  sharedIg: number;   // recorded together during incubation or guard
  sharedPre: number;  // recorded together before laying (capped)
  impliedIg: number;  // one of them beside an unidentified adult, during incubation or guard
  impliedPre: number; // the same, before laying
  ig: number;         // sightings during incubation and guard, either bird
  pre: number;        // sightings before laying this season, either bird (capped per bird)
  bred: number;       // how many of the two bred at this box in an earlier season (0–2)
  near: number;       // combined distance from laying — separates pairs that score alike, so
                      // the answer can't depend on which bird happened to be encountered first
}
const capPre = (n: number) => Math.min(n, PRE_BREEDING_SIGHTINGS_CAP);
const evidenceScore = (e: PairEvidence): number =>
  PAIR_WEIGHTS.sharedIg * (e.sharedIg + IMPLIED_SHARE_CONFIDENCE * e.impliedIg)
  + PAIR_WEIGHTS.ig * e.ig
  + PAIR_WEIGHTS.sharedPre * (e.sharedPre + IMPLIED_SHARE_CONFIDENCE * e.impliedPre)
  + PAIR_WEIGHTS.pre * e.pre
  + PAIR_WEIGHTS.bred * e.bred;
const betterEvidence = (a: PairEvidence, b: PairEvidence): boolean => {
  const sa = evidenceScore(a), sb = evidenceScore(b);
  return sa !== sb ? sa > sb : a.near < b.near;
};

interface PairContext {
  noScanByGroup: Map<string, number>;  // observation → adults present but not identified
  bredBefore: Set<string>;             // pit8s that were a parent here in an earlier season
}

/** Detect the breeding pair for one clutch. Every bird sighted at the box this season is a
 *  candidate; where in the attempt it was seen is weight, not a filter, so when there's no
 *  well-attended pair the search still reaches the birds that were only ever seen at the
 *  edges. A valid pair is one M + one F where at least one sex is confirmed — the other may
 *  come from majority biometric sex guesses (M+F, M+UF, F+UM; never UM+UF). */
function detectClutchPair(c: Clutch, sightings: BoxSighting[], birdMap: Map<string, any>, excluded: (b: any) => boolean, ctx: PairContext): { male: string; female: string } | null {
  const anchor = c.laid ?? c.windowStart;
  const per = new Map<string, { ig: number; pre: number; post: number; near: number }>();
  const groups = new Map<string, string[]>();
  const groupT = new Map<string, number>();   // when each co-presence group happened
  for (const s of sightings) {
    const e = per.get(s.key) ?? { ig: 0, pre: 0, post: 0, near: Infinity };
    if (s.t >= anchor && s.t <= c.guardEnd) e.ig++;      // incubation and guard
    else if (s.t < anchor) e.pre++;                       // before laying, this season
    else e.post++;                                        // after guard: the nest is empty of adults
    e.near = Math.min(e.near, Math.abs(s.t - anchor));
    per.set(s.key, e);
    if (!groups.has(s.group)) groups.set(s.group, []);
    groups.get(s.group)!.push(s.key);
    groupT.set(s.group, s.t);
  }
  // Shared sightings, split by the phase they fell in — the same split as the per-bird counts.
  const coIg = new Map<string, number>(), coPre = new Map<string, number>();
  for (const [g, present] of groups) {
    const gt = groupT.get(g) ?? 0;
    const bucket = gt >= anchor && gt <= c.guardEnd ? coIg : gt < anchor ? coPre : null;
    if (!bucket) continue;   // after guard: worth nothing, though it still keeps them candidates
    for (let i = 0; i < present.length; i++) for (let j = i + 1; j < present.length; j++) {
      const key = [present[i], present[j]].sort().join('|');
      bucket.set(key, (bucket.get(key) || 0) + 1);
    }
  }
  // An unchipped bird can perfectly well be a parent; it just can't be named. Where a monitor
  // recorded an adult they couldn't identify beside one of a pair we already know breeds
  // together, the unnamed bird was most likely the partner — so those observations back that
  // pair too. Only ever a nudge behind a real shared sighting, and only for a pair that has
  // one: with no established partner an unnamed adult points at nobody in particular.
  const impliedFor = (a: string, b: string): { ig: number; pre: number } => {
    const out = { ig: 0, pre: 0 };
    for (const [g, present] of groups) {
      if (!(ctx.noScanByGroup.get(g) || 0)) continue;
      if (present.includes(a) === present.includes(b)) continue;
      const gt = groupT.get(g) ?? 0;
      if (gt >= anchor && gt <= c.guardEnd) out.ig++;
      else if (gt < anchor) out.pre++;
    }
    return out;
  };
  const sexOf = (b: any): { sex: string; confirmed: boolean } | null => {
    const s = (b.sex || '').toUpperCase();
    if (s === 'M' || s === 'F') return { sex: s, confirmed: true };
    const g = observedSexGuess(b.peng_num);
    if (g.m > g.f) return { sex: 'M', confirmed: false };
    if (g.f > g.m) return { sex: 'F', confirmed: false };
    return null;
  };
  const cands = Array.from(per.entries())
    .map(([key, e]) => ({ key, e, bird: birdMap.get(key) }))
    .filter(x => x.bird && !excluded(x.bird));
  let best: { male: string; female: string; ev: PairEvidence } | null = null;
  for (let i = 0; i < cands.length; i++) for (let j = i + 1; j < cands.length; j++) {
    const a = sexOf(cands[i].bird), b = sexOf(cands[j].bird);
    if (!a || !b || a.sex === b.sex || (!a.confirmed && !b.confirmed)) continue;
    const pk = [cands[i].key, cands[j].key].sort().join('|');
    const sharedIg = coIg.get(pk) || 0, sharedPre = coPre.get(pk) || 0;
    // An unnamed adult only points at a partner for a pair already known to breed together.
    const implied = (sharedIg + sharedPre) > 0 ? impliedFor(cands[i].key, cands[j].key) : { ig: 0, pre: 0 };
    const x = cands[i].e, y = cands[j].e;
    const ev: PairEvidence = {
      sharedIg,
      // Courtship is capped, per bird and for the pair: see PRE_BREEDING_SIGHTINGS_CAP.
      sharedPre: capPre(sharedPre),
      impliedIg: implied.ig, impliedPre: Math.min(implied.pre, PRE_BREEDING_SIGHTINGS_CAP),
      ig: x.ig + y.ig,
      pre: capPre(x.pre) + capPre(y.pre),
      bred: (ctx.bredBefore.has(cands[i].key) ? 1 : 0) + (ctx.bredBefore.has(cands[j].key) ? 1 : 0),
      near: x.near + y.near,
    };
    if (!best || betterEvidence(ev, best.ev)) {
      best = { male: a.sex === 'M' ? cands[i].key : cands[j].key, female: a.sex === 'F' ? cands[i].key : cands[j].key, ev };
    }
  }
  if (best) return { male: best.male, female: best.female };
  // No valid pair — fall back to the best-evidenced single bird as a lone parent, ranked the
  // same way (nothing to share a sighting with, so those terms are zero). Sex needn't be known;
  // slot it by whatever signal exists, defaulting to the male slot.
  let solo: { key: string; sex: string; ev: PairEvidence } | null = null;
  for (const cd of cands) {
    const ev: PairEvidence = { sharedIg: 0, sharedPre: 0, impliedIg: 0, impliedPre: 0,
      ig: cd.e.ig, pre: capPre(cd.e.pre), bred: ctx.bredBefore.has(cd.key) ? 1 : 0, near: cd.e.near };
    if (!solo || betterEvidence(ev, solo.ev)) solo = { key: cd.key, sex: sexOf(cd.bird)?.sex || '', ev };
  }
  if (solo) return { male: solo.sex === 'F' ? '' : solo.key, female: solo.sex === 'F' ? solo.key : '' };
  return null;
}

/** Best-known sex: the confirmed value, else the majority biometric guess, else null.
 *  Same rule detectClutchPair scores pairs with (it additionally tracks confirmed-ness). */
function guessedSex(b: any): 'M' | 'F' | null {
  const s = (b?.sex || '').toUpperCase();
  if (s === 'M' || s === 'F') return s;
  const g = observedSexGuess(b?.peng_num);
  return g.m > g.f ? 'M' : g.f > g.m ? 'F' : null;
}

/** Display sort rank by sex: M first, F second, unsexed last. An unsexed bird with a
 *  majority biometric sex guess (rendered as UM/UF) ranks with the confirmed sex; a chick
 *  ranks last whatever its guess, so siblings stay together and sort by size. */
function sexSortOrder(b: any): number {
  if (isChickRecord(b)) return 2;
  const s = guessedSex(b);
  return s === 'M' ? 0 : s === 'F' ? 1 : 2;
}

const ordinal = (n: number) => n === 1 ? '1st' : n === 2 ? '2nd' : n === 3 ? '3rd' : `${n}th`;
/** Season label "2026" → "2026/27" (breeding season spans two calendar years). */
const seasonRange = (label: string) => `${label}/${String((parseInt(label) + 1) % 100).padStart(2, '0')}`;

const fmtMs = (ms: number) => new Date(ms).toLocaleDateString('en-NZ', { day: 'numeric', month: 'short', timeZone: 'Pacific/Auckland' });
/**
 * How a season reads at a glance: the coloured word beside a box's or a bird's season.
 *
 *   none   — nothing was laid
 *   bred   — the season produced a chick that got away
 *   active — an attempt is still running
 *   fail   — eggs were laid and nothing came of them
 *
 * "A chick that got away" is what the record ESTABLISHES, by either of the two ways it can:
 * a chick microchipped in the nest, or unchipped chicks a monitor recorded as presumed
 * fledged. Counting only the chipped ones read a nest whose brood nobody happened to catch
 * as a failure, which is the opposite of what happened.
 *
 * Deliberately NOT counted: the timeline's date-based inference that chicks last seen past
 * the chip window must have fledged (see looksFledged). That is a fair reading of one
 * marker, but a season summary is a stronger claim — it is what breeding success gets
 * counted from — and the record has two plain ways to state the outcome. Inferring success
 * from an offset would inflate the figures with attempts nobody actually saw finish.
 */
function seasonOutcome(clutchCount: number, families: { clutch: Clutch; chicks: any[]; fledgedUnchipped: number }[]): 'none' | 'bred' | 'active' | 'fail' {
  if (clutchCount === 0) return 'none';
  if (families.some(f => f.chicks.length > 0 || f.fledgedUnchipped > 0)) return 'bred';
  return families.some(f => clutchActive(f.clutch)) ? 'active' : 'fail';
}

/** Clutch still running: no terminating observation yet and predicted fledge not passed. */
const clutchActive = (c: { end: number | null; windowEnd: number }) => c.end === null && Date.now() <= c.windowEnd;
/** Breeding-window date range; an active window reads "6 Jul – current". */
const windowRange = (c: { windowStart: number; windowEnd: number; end: number | null }) =>
  `${fmtMs(c.windowStart)} – ${clutchActive(c) ? 'current' : fmtMs(c.windowEnd)}`;

/** What a clutch with no laid estimate can honestly say for itself: the day the nest was
 *  found with something in it. Not a fault to be fixed — a box first checked with eggs
 *  already there has no empty check to date laying back from, and never will — so it states
 *  the one dated fact rather than flagging the absence of the other. It also explains the
 *  window beside it, which starts at the discovery instead of at laying. */
const discoveryNote = (c: { startKind: 'egg' | 'chick'; start: number }) =>
  `${c.startKind === 'chick' ? 'Chicks' : 'Eggs'} found on ${fmtMs(c.start)}`;

/** Active window only: ALL upcoming stage dates predicted from the laid estimate — same
 *  offsets as the nestcheck Next Breeding Dates card. Hatch is shown only while the
 *  clutch is still in the egg phase; the chip window runs to fledge. */
/** Unchipped offspring in a family box. Once the clutch has ENDED these are final
 *  stages — egg that never hatched / chick never chipped — and get the red ✕. While
 *  the clutch is still active they're simply in progress, so no failure mark. */
function OffspringFinal({ kind, active, recorded }: { kind: 'egg' | 'chick'; active: boolean; recorded?: boolean }) {
  const failed = !active || recorded;   // recorded: a monitor found it dead and wrote it down
  const title = recorded ? 'A monitor recorded this chick as dead'
    : kind === 'egg'
      ? (active ? 'Egg in the nest' : 'Egg did not hatch')
      : (active ? 'Unchipped chick in the nest' : 'Chick was not chipped in the nest');
  return (
    <span className={`offspring-final${failed ? ' offspring-failed' : ''}`} title={title}>
      {kind === 'egg' ? '🥚' : '🐣'}{failed && <span className="fail-x">{'✕'}</span>}
    </span>
  );
}

/** Whether ClutchPredictions renders anything. Callers need to know before laying the card
 *  out: when there IS a predictions row, the window dates ride its right-hand end.
 *  Declared (and fingerprinted) alongside the component so the "only while running"
 *  rule the Algorithm tab states can't drift out of the doc check. */
function hasClutchPredictions(c: Clutch) {
  return clutchActive(c) && c.laid !== null;
}

/** `trailing` (the window dates) rides the end of the last line rather than taking a row of
 *  its own — which needs each stage to be a flex item, so the separator trails its stage
 *  instead of leading the next one. A stage that starts a line must not start with ", ". */
function ClutchPredictions({ clutch, trailing }: { clutch: Clutch; trailing?: React.ReactNode }) {
  if (!hasClutchPredictions(clutch)) return null;
  const d = (off: number) => fmtMs(clutch.laid! + off * DAY);
  const t = (off: number) => clutch.laid! + off * DAY;
  // laid estimates the first egg; the second is laid ~2 days later. Little penguins
  // almost always lay 2, so show both unless only a single egg was ever recorded.
  const twoEggs = (clutch.maxEggs || 2) >= 2;
  // Laid date(s): two eggs collapse to "5·7 Jul" (shared month) or "30 Jul·1 Aug"
  // (crossing months) with a mid-height dot; a single egg is just its date.
  const laidText = (() => {
    if (!twoEggs) return d(0);
    const a = d(0), b = d(SECOND_EGG_LAG_DAYS), sp = a.indexOf(' ');
    return b.endsWith(a.slice(sp + 1)) ? `${a.slice(0, sp)}·${b}` : `${a}·${b}`;
  })();
  // The ± is the laid date's own uncertainty (half the gap between the last empty check and
  // the first egg), so it sits with that date rather than trailing the whole sentence, where
  // it read as if it qualified fledging too.
  const unc = clutch.laidUncertainty !== null && clutch.laidUncertainty > 0
    ? ` ± ${clutch.laidUncertainty} day${clutch.laidUncertainty !== 1 ? 's' : ''}` : '';
  const parts = [
    { text: laidText + unc, t: t(0) },
    ...(clutch.maxChicks === 0 ? [{ text: `Hatch ${d(BREEDING_OFFSETS.hatch)}`, t: t(BREEDING_OFFSETS.hatch) }] : []),
    { text: `Guard ends ${d(BREEDING_OFFSETS.pg)}`, t: t(BREEDING_OFFSETS.pg) },
    { text: `Chip ${d(BREEDING_OFFSETS.chip)} – ${d(BREEDING_OFFSETS.fledge)}`, t: t(BREEDING_OFFSETS.chip) },
    { text: `Fledge ${d(BREEDING_OFFSETS.fledge)}`, t: t(BREEDING_OFFSETS.fledge) },
  ];
  const nextIdx = parts.findIndex(p => p.t >= Date.now()); // the stage coming up next
  return (
    <span className={`clutch-predictions${trailing ? ' has-trailing' : ''}`}>
      {parts.map((p, i) => <span key={i}>{(i === nextIdx || i === 0) ? <b>{p.text}</b> : p.text}{i < parts.length - 1 ? ',' : ''}</span>)}
      {trailing}
    </span>
  );
}

/**
 * A clutch card's body: the birds' row (passed in) plus the predictions row, with the window
 * dates dropped into whichever of the two has room left at its end.
 *
 * Preference is the predictions' last line — the card's bottom-right corner — and CSS handles
 * that on its own. What CSS can't do is compare the two rows, so when the dates would be
 * pushed onto a line of their own, this measures whether the birds' row could take them
 * instead. The choice reads only geometry the dates don't affect (the last stage's end, and
 * the birds' row without them), so it can't oscillate between the two slots.
 */
function ClutchBody({ clutch, dates, children }: { clutch: Clutch; dates: React.ReactNode; children: React.ReactNode }) {
  const bodyRef = useRef<HTMLDivElement>(null);
  const [onBirdsRow, setOnBirdsRow] = useState(false);
  const measure = useCallback(() => {
    const body = bodyRef.current;
    const datesEl = body?.querySelector('.clutch-dates') as HTMLElement | null;
    const predEl = body?.querySelector('.clutch-predictions') as HTMLElement | null;
    if (!body || !datesEl || !predEl) return;   // no predictions row — CSS already handles it
    const right = body.getBoundingClientRect().right;
    const needed = datesEl.getBoundingClientRect().width;
    // Where a row's content stops: its last item's last rect, not a union box — an item that
    // wrapped reports where it actually ends rather than its widest line.
    const endOf = (el?: Element) => {
      const rects = el?.getClientRects();
      return rects?.length ? rects[rects.length - 1].right : null;
    };
    // .clutch-birds is display:contents, so it has no box — descend to what it holds.
    const boxed = (el: Element): Element[] =>
      el.getClientRects().length ? [el] : Array.from(el.children).flatMap(boxed);
    const stages = Array.from(predEl.children).filter(el => el !== datesEl);
    const predictionsEnd = endOf(stages[stages.length - 1]);
    const freeOnPredictions = predictionsEnd === null ? Infinity : right - predictionsEnd;
    // The birds' row is everything in the body bar the predictions and the dates, and it's the
    // last of those — on the last line the birds wrapped to — that the dates have to fit after.
    const items = Array.from(body.children).filter(el => el !== datesEl && el !== predEl).flatMap(boxed);
    const birdsEnd = endOf(items[items.length - 1]);
    const freeOnBirds = right - (birdsEnd ?? body.getBoundingClientRect().left);
    setOnBirdsRow(freeOnPredictions < needed + 8 && freeOnBirds >= needed + 14);
  }, []);
  // Re-measure on every render (the stages restate their dates as a clutch ages) and on any
  // resize of the card.
  useLayoutEffect(measure);
  useLayoutEffect(() => {
    const body = bodyRef.current;
    if (!body || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(measure);
    ro.observe(body);
    return () => ro.disconnect();
  }, [measure]);
  const inCorner = hasClutchPredictions(clutch) && !onBirdsRow;
  return (
    <div className="clutch-body" ref={bodyRef}>
      {children}
      {!inCorner && dates}
      <ClutchPredictions clutch={clutch} trailing={inCorner ? dates : undefined} />
    </div>
  );
}


/** Per-box data-quality checks (mirrors the admin-page checks, scoped to one box's
 *  observations). All dates are NZ days. Returns human-readable detail lines so the
 *  season summary can list what's wrong. */
interface DataIssue { day: string; text: string }
function seasonDataIssues(obs: Observation[]) {
  const byDay = new Map<string, Observation[]>();
  for (const o of obs) {
    const day = toNzDateStr(o.observation_time_utc);
    if (!byDay.has(day)) byDay.set(day, []);
    byDay.get(day)!.push(o);
  }
  // Duplicate observations: 2+ non-deleted observations for this box on the same day
  const dupObs: DataIssue[] = [];
  for (const [day, list] of byDay) if (list.length > 1) dupObs.push({ day, text: `${day} — ${list.length} observations` });
  // Duplicate scans: within one observation, the same pit scanned 2+ times, or one
  // penguin scanned via 2+ different chips
  const dupScans: DataIssue[] = [];
  for (const o of obs) {
    const day = toNzDateStr(o.observation_time_utc);
    const pitCounts = new Map<string, number>();
    const pitPeng = new Map<string, string | null>();
    const pengPits = new Map<string, Set<string>>();
    for (const s of o.scans) {
      pitCounts.set(s.pit_id, (pitCounts.get(s.pit_id) || 0) + 1);
      pitPeng.set(s.pit_id, s.peng_num ?? null);
      if (s.peng_num) {
        if (!pengPits.has(s.peng_num)) pengPits.set(s.peng_num, new Set());
        pengPits.get(s.peng_num)!.add(s.pit_id);
      }
    }
    for (const [pit, n] of pitCounts) if (n > 1) {
      const peng = pitPeng.get(pit);
      dupScans.push({ day, text: `${day} — ${peng ? `#${displayPengNum(peng)}` : pit.slice(-8)} ×${n}` });
    }
    for (const [peng, pits] of pengPits) if (pits.size > 1) dupScans.push({ day, text: `${day} — #${displayPengNum(peng)} (${pits.size} chips)` });
  }
  // Same-gender conflicts: 2+ distinct penguins of the same sex on the same day
  const conflicts: DataIssue[] = [];
  for (const [day, list] of byDay) {
    const bySex = new Map<string, Set<string>>();
    for (const o of list) for (const s of o.scans) {
      const sex = (s.sex || '').toUpperCase();
      if ((sex === 'M' || sex === 'F') && s.peng_num) {
        if (!bySex.has(sex)) bySex.set(sex, new Set());
        bySex.get(sex)!.add(s.peng_num);
      }
    }
    for (const [sex, pengs] of bySex) if (pengs.size > 1) {
      conflicts.push({ day, text: `${day} — ${pengs.size} ${sex} (${Array.from(pengs).map(p => `#${displayPengNum(p)}`).join(', ')})` });
    }
  }
  return { dupObs, dupScans, conflicts };
}

interface BoxFamily {
  clutch: Clutch;
  male: string;      // pit8 of the male parent, '' if none detected
  female: string;    // pit8 of the female parent, '' if none detected
  parents: any[];    // parent bird objects (0-2)
  chicks: any[];     // this-season chicks chipped in the nest (bird objects)
  failedEggs: number;   // eggs that never became a chick (final stage), capped at MAX_OFFSPRING_SHOWN
  plainChicks: number;  // unchipped chicks assumed to have died (final stage), capped at MAX_OFFSPRING_SHOWN
  deadChicks: number;   // of those, the ones a monitor found dead and recorded — dead on the record,
                        // not by inference, so they read as failed while the attempt is still running
  fledgedUnchipped: number; // unchipped chicks a monitor recorded as presumed fledged
}
interface BoxSeasonData {
  label: string;
  seasonYear: number;
  seasonStart: Date;
  seasonEnd: Date;
  seasonObs: Observation[];   // chronological
  seasonSightings: BoxSighting[]; // chronological; scans + chippings, deduped per visit
  birds: any[];               // sorted M/F/unsexed, scan-count desc
  birdMap: Map<string, any>;  // pit8 -> bird
  clutches: Clutch[];
  families: BoxFamily[];      // one per clutch; parents empty when no pair detected
  parentKeys: Set<string>;
  chickFamily: Map<string, number>; // chick pit8 -> family index
  isCurrent: boolean;
}

/** THE shared breeding-family detection for one box's observations: group by season,
 *  segment each season into clutches, detect each clutch's pair, assign this-season
 *  chicks, and tally offspring at their final life stage. Both the box breeding
 *  overview and the bird panel's family view consume this, so the detection algorithm
 *  lives in exactly one place — change it here and both views update. */
function computeBoxFamilies(observations: Observation[], allPenguinsInBox?: any[]): BoxSeasonData[] {
  const seasonBirds = new Map<string, Map<string, any>>();
  const seasonObsMap = new Map<string, Observation[]>();
  const seasonSightMap = new Map<string, BoxSighting[]>();
  for (const obs of observations) {
    const label = getSeasonLabel(parseDate(obs.observation_time_utc));
    if (!seasonObsMap.has(label)) seasonObsMap.set(label, []);
    seasonObsMap.get(label)!.push(obs);
  }
  // Every check of this box, ever, in order. Clutch segmentation runs one season at a
  // time but needs the box's real monitoring gaps — the last check before a season
  // started is a real check, and 1 April must not read as "unwatched since forever".
  const allObsTimes = observations
    .map(o => parseDate(o.observation_time_utc).getTime()).sort((a, b) => a - b);
  // Every bird-visit to this box — scans and chippings alike — grouped by season. A
  // bird's per-season count and lastSeen come from ITS OWN season's sightings, so a
  // bird chipped here in one season and scanned here in the next is two separate
  // one-season records, not the box lifetime stamped onto both.
  for (const s of boxSightings(observations, allPenguinsInBox)) {
    const label = getSeasonLabel(new Date(s.t));
    if (!seasonSightMap.has(label)) seasonSightMap.set(label, []);
    seasonSightMap.get(label)!.push(s);
    if (!seasonBirds.has(label)) seasonBirds.set(label, new Map());
    const birdMap = seasonBirds.get(label)!;
    const existing = birdMap.get(s.key);
    if (!existing) {
      birdMap.set(s.key, { ...s.bird, lastSeen: s.time, scanCount: 1 });
    } else {
      existing.scanCount++;
      if (s.time > existing.lastSeen) {
        existing.lastSeen = s.time;
        if (s.source === 'scan') { existing.sex = s.bird.sex; existing.life_stage = s.bird.life_stage; }
      }
      // A scan row carries no chip provenance; keep what the chipping sighting knows.
      if (s.source === 'chip') { existing.is_chipped_here = true; if (!existing.chip_date) existing.chip_date = s.bird.chip_date; }
    }
  }
  // Always surface the current season, even with no sightings yet.
  const currentLabel = getSeasonLabel();
  if (!seasonBirds.has(currentLabel)) seasonBirds.set(currentLabel, new Map());
  // Surface every monitored season (has observations) even if no bird was ever scanned — the
  // overview shows it with "No breeding observed".
  for (const label of seasonObsMap.keys()) if (!seasonBirds.has(label)) seasonBirds.set(label, new Map());

  // Adults a monitor saw but couldn't name, per observation. Pair detection uses them to
  // corroborate a known pair — the group key matches the one boxSightings builds.
  const noScanByGroup = new Map<string, number>();
  for (const o of observations) {
    const n = Number(o.no_scan) || 0;
    if (n) noScanByGroup.set(`o${o.observation_id ?? o.observation_time_utc}`, n);
  }
  // OLDEST season first, so each season's detection can see who already bred here: a bird
  // with a breeding history at this box outranks a stranger on equal evidence. The result is
  // flipped back to newest-first at the end, which is the order every screen displays.
  const seasons = Array.from(seasonBirds.entries()).sort((a, b) => a[0].localeCompare(b[0]));
  const bredBefore = new Set<string>();
  // A chick is this box's offspring only if it was CHIPPED here — a chick chipped in one box
  // but scanned in another the same season is a visitor there, not that box's chick, and must
  // not be pulled into a second family. Chip-box provenance is read straight from the penguin
  // records (their is_chipped_here), which survives the same-day scan/chip sighting dedup that
  // can otherwise leave a scanned-and-chipped chick without chip provenance on its sighting.
  const chippedHereKeys = new Set((allPenguinsInBox || [])
    .filter((p: any) => p.is_chipped_here && p.pit_id)
    .map((p: any) => String(p.pit_id).slice(-8)));
  const result: BoxSeasonData[] = [];
  for (const [label, birdMap] of seasons) {
    const seasonYear = parseInt(label);
    const seasonStart = new Date(seasonYear, 3, 1); // Apr 1
    const seasonEnd = new Date(seasonYear + 1, 3, 1); // next Apr 1
    // A bird counts as this season's chick only if chipped as a chick DURING this
    // season — a returning adult chick-chipped in an earlier season is a visitor.
    const chippedThisSeason = (b: any) => {
      if (b.chipped_as_adult || !b.chip_date) return false;
      const cd = new Date(b.chip_date);
      return cd >= seasonStart && cd < seasonEnd;
    };
    const sObsChrono = (seasonObsMap.get(label) || []).slice()
      .sort((a, b) => parseDate(a.observation_time_utc).getTime() - parseDate(b.observation_time_utc).getTime());
    const firstT = sObsChrono.length ? parseDate(sObsChrono[0].observation_time_utc).getTime() : 0;
    let priorObsT: number | null = null;
    for (const t of allObsTimes) { if (t >= firstT) break; priorObsT = t; }
    const clutches = segmentClutches(sObsChrono, priorObsT);
    // Pair detection runs on SIGHTINGS, not observations: a bird chipped at this nest
    // attended it even when no observation was recorded that day (e.g. an adult chipped
    // while guarding chicks). Same-day scan/chip pairs are already deduped to one visit.
    const sSightings = seasonSightMap.get(label) || [];
    const pairs = clutches.map(c => detectClutchPair(c, sSightings, birdMap, chippedThisSeason,
      { noScanByGroup, bredBefore: new Set(bredBefore) }));
    const parentKeys = new Set<string>();
    for (const pair of pairs) if (pair) { if (pair.male) parentKeys.add(pair.male); if (pair.female) parentKeys.add(pair.female); }

    // Assign each season chick to the clutch whose window holds its chip date; chips
    // land at the end of an attempt, so otherwise default to the last detected family.
    const familyIdxs = pairs.map((p, i) => p ? i : -1).filter(i => i >= 0);
    const chickFamily = new Map<string, number>();
    for (const b of birdMap.values()) {
      const k = b.pit_id.slice(-8);
      if (!chippedThisSeason(b) || !chippedHereKeys.has(k) || parentKeys.has(k)) continue;
      const ct = new Date(b.chip_date!).getTime();
      let fi = clutches.findIndex((c, i) => pairs[i] && ct >= c.windowStart && ct <= c.windowEnd);
      if (fi < 0 && familyIdxs.length > 0) fi = familyIdxs[familyIdxs.length - 1];
      if (fi >= 0) chickFamily.set(k, fi);
    }

    // Sort birds: M, F, unsexed (sex guesses count); chicks biggest first, so a nest's
    // BC leads its LC however often each was scanned; otherwise by scan count descending.
    const birds = Array.from(birdMap.values()).sort((a, b) => {
      const diff = sexSortOrder(a) - sexSortOrder(b);
      if (diff !== 0) return diff;
      const size = chickSizeOrder(a) - chickSizeOrder(b);
      if (size !== 0) return size;
      return b.scanCount - a.scanCount;
    });

    const families: BoxFamily[] = clutches.map((clutch, ci) => {
      const pair = pairs[ci];
      const male = pair?.male || '', female = pair?.female || '';
      const parents = pair ? [birdMap.get(male), birdMap.get(female)].filter(Boolean) : [];
      const chicks = birds.filter(b => chickFamily.get(b.pit_id.slice(-8)) === ci);
      // Offspring at FINAL life stage: egg that never hatched, chick never chipped.
      const failedEggs = Math.min(Math.max(0, clutch.maxEggs - clutch.maxChicks), MAX_OFFSPRING_SHOWN);
      const unchipped = Math.max(0, Math.min(clutch.maxChicks, MAX_OFFSPRING_SHOWN) - chicks.length);
      // Of the never-chipped chicks, those a monitor logged as presumed-fledged (summed
      // over this clutch's observations) render as fledged rather than assumed-died. Cap
      // at the unchipped count so an over-entry can't invent chicks.
      const fledgedUnchipped = Math.min(unchipped, sObsChrono.reduce((s, o) => {
        const t = parseDate(o.observation_time_utc).getTime();
        return (t >= clutch.start && t <= (clutch.end ?? Infinity)) ? s + (Number(o.fledged_unchipped) || 0) : s;
      }, 0));
      const plainChicks = unchipped - fledgedUnchipped;
      const deadChicks = Math.min(plainChicks, sObsChrono.reduce((n, o) => {
        const t = parseDate(o.observation_time_utc).getTime();
        return (t >= clutch.start && t <= (clutch.end ?? Infinity)) ? n + (Number(o.dead_chicks) || 0) : n;
      }, 0));
      return { clutch, male, female, parents, chicks, failedEggs, plainChicks, deadChicks, fledgedUnchipped };
    });

    result.push({ label, seasonYear, seasonStart, seasonEnd, seasonObs: sObsChrono, seasonSightings: sSightings, birds, birdMap, clutches, families, parentKeys, chickFamily, isCurrent: label === currentLabel });
    for (const k of parentKeys) bredBefore.add(k);   // established here; later seasons weigh it
  }
  return result.reverse();   // newest season first, the order every screen renders
}

/**
 * One pass over the whole colony, shared by every report that needs it.
 *
 * A dozen reports each opened with `for (loc of queryAllLocations()) queryBoxDetailSync(loc)`,
 * half of them then running computeBoxFamilies over the result — so opening the reports or
 * admin page rebuilt every box, and re-detected every breeding family, once per chart. They
 * all read the same cache at the same version, so the answer is the same every time: build it
 * once and hand out the same arrays.
 *
 * Cached against the local DB's store version, so a sync (or an edit) invalidates it exactly
 * as each report's own `useMemo(..., [v])` does — the reports need no cache-busting of their own.
 * `families` is built lazily: the reports that only want observations don't pay for detection.
 */
interface ColonyBox { box: string; loc: any; detail: any; readonly families: BoxSeasonData[] }
let _colonyBoxCache: { version: number; boxes: ColonyBox[] } | null = null;
function allColonyBoxes(): ColonyBox[] {
  const version = getStoreVersion();
  if (_colonyBoxCache && _colonyBoxCache.version === version) return _colonyBoxCache.boxes;
  const boxes: ColonyBox[] = [];
  for (const loc of queryAllLocations()) {
    // A verdict can outlive the observation it was anchored to, but the deleted rows
    // themselves aren't needed here — each verification carries its anchor's date and
    // deleted flag, which is all the conflict check reads.
    const detail = queryBoxDetailSync(loc.location_name);
    let fams: BoxSeasonData[] | null = null;
    boxes.push({
      box: String(loc.location_name), loc, detail,
      get families() { return fams ??= computeBoxFamilies(detail.observations, detail.all_penguins); },
    });
  }
  _colonyBoxCache = { version, boxes };
  return boxes;
}

function SeasonBirdsSection({ label, birds, seasonStatus, statusLabel, latestObs,
  onSeasonClick, issueBadges, dayToObsTime, clutches, visitorBirds, visitorRow, aggSlots, noScanFor, renderClutch }: any) {
  return (
    <div className="season-birds">
      <div className="season-year">
        <div className={`season-yr${latestObs ? ' clickable' : ''}`}
          onClick={latestObs ? () => onSeasonClick?.(latestObs) : undefined}>
          {seasonRange(label)}
        </div>
        <div className="season-birdcount">{birds.length} bird{birds.length !== 1 ? 's' : ''}</div>
        <span className={`season-status st-${seasonStatus}`}><span className="ss-dot" />{statusLabel}</span>
      </div>
      <div className="season-content">
        {issueBadges.length > 0 && (
          <div className="season-issues">
            {issueBadges.map((b: any) => (
              <span key={b.key} className="issue-badge">
                {'\u26A0'} {b.detail.length} {b.label}{b.detail.length !== 1 ? 's' : ''}
                <span className="issue-tip">
                  {b.detail.map((d: any, i: number) => {
                    const t = dayToObsTime.get(d.day);
                    return (
                      <a key={i} className={`issue-row${t ? ' clickable' : ''}`} onClick={t ? () => onSeasonClick?.(t) : undefined}>{d.text}</a>
                    );
                  })}
                </span>
              </span>
            ))}
          </div>
        )}
        {clutches.length === 0 ? (
          // No clutches → every observation lands in slot g0, so that's the season's no-scan total.
          visitorRow('Seen in box', [...visitorBirds.map((b: any) => ({ b, n: b.scanCount })), ...noScanFor(['g0'])])
        ) : (() => {
          const nodes: React.ReactNode[] = [];
          const post = visitorRow('Post-breeding', aggSlots([`g${clutches.length}`]));
          if (post) nodes.push(post);
          for (let ci = clutches.length - 1; ci >= 0; ci--) {
            nodes.push(renderClutch(ci));
            if (ci > 0) { const between = visitorRow(`Between ${ordinal(ci)} & ${ordinal(ci + 1)} clutch`, aggSlots([`g${ci}`])); if (between) nodes.push(between); }
          }
          const pre = visitorRow('Pre-breeding', aggSlots(['g0']));
          if (pre) nodes.push(pre);
          return nodes;
        })()}
      </div>
    </div>
  );
}

function AllScannedBirds({ observations, onBirdClick, allPenguinsInBox, onSeasonClick, children, boxName, verifications = [], token, canEdit = false, onDataChange }: { observations: Observation[]; onBirdClick: (tag:string)=>void; allPenguinsInBox?: any[]; onSeasonClick?: (obsTime: string) => void; children?: React.ReactNode; boxName?: string; verifications?: any[]; token?: string; canEdit?: boolean; onDataChange?: () => void }) {
  const seasonData = computeBoxFamilies(observations, allPenguinsInBox);
  const isMobile = typeof window !== 'undefined' && window.innerWidth <= 700;
  const [prevExpanded, setPrevExpanded] = useState(false);
  // Which clutch's verify modal is open, plus the click point to anchor it near the tick.
  const [openVerify, setOpenVerify] = useState<{ key: string; pos: { x: number; y: number } } | null>(null);
  const currentSeasons: React.ReactNode[] = [];
  const previousSeasons: React.ReactNode[] = [];
  // Verifications whose anchor observation no longer starts any detected clutch — the algorithm
  // has re-segmented away from the verified truth, so surface them as a drift warning. A verdict
  // that was rejected outright is dropped: the reviewer said there was no breeding here and the
  // detection has since vanished too, which is agreement, not drift. What's left is an orphaned
  // ACCEPTANCE — a clutch a human confirmed that the algorithm no longer sees — and that is the
  // only case worth a warning. (The admin integrity check drops anything with a rejected half;
  // a half-accepted, half-rejected clutch still strands the acceptance, so it stays here.)
  const allStartIds = new Set(seasonData.flatMap(sd => sd.clutches.map(c => c.startObsId).filter((x): x is number => x != null)));
  const orphanVerifications = verifications
    .filter(v => !allStartIds.has(v.observation_id))
    .map(v => {
      const halves = [
        { name: 'pair', verdict: v.adults_verdict, by: v.adults_reviewed_by_name, note: v.adults_note },
        { name: 'offspring', verdict: v.chicks_verdict, by: v.chicks_reviewed_by_name, note: v.chicks_note },
      ].filter(h => h.verdict);
      return {
        v,
        // "pair + offspring accepted by Marian", or "pair accepted by Marian, offspring rejected by Bev"
        // when the two halves were judged differently.
        what: ['accepted', 'rejected'].map(verdict => {
          const mine = halves.filter(h => h.verdict === verdict);
          if (!mine.length) return null;
          return `${mine.map(h => h.name).join(' + ')} ${verdict}${mine[0].by ? ` by ${mine[0].by}` : ''}`;
        }).filter(Boolean).join(', '),
        // The rejector's reason, which is usually what explains the drift.
        notes: [...new Set(halves.filter(h => h.verdict === 'rejected' && h.note).map(h => h.note))].join(' · '),
        stranded: halves.some(h => h.verdict === 'accepted'),
        season: v.anchor_time ? seasonRange(getSeasonLabel(parseDate(v.anchor_time))) : '',
        when: v.anchor_time ? parseDate(v.anchor_time).toLocaleDateString('en-NZ', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'Pacific/Auckland' }) : '',
        why: v.anchor_deleted ? 'the observation it was recorded against was deleted' : 'no breeding window starts here any more',
      };
    })
    .filter(o => o.stranded)
    .sort((a, b) => String(b.v.anchor_time || '').localeCompare(String(a.v.anchor_time || '')));

  seasonData.forEach(({ label, seasonStart, seasonEnd, seasonObs: sObsChrono, seasonSightings, birds, clutches, families, parentKeys, chickFamily, isCurrent }) => {
        if (birds.length === 0 && sObsChrono.length === 0 && !isCurrent) return;
        const sorted = birds;

        // Every non-family sighting is placed in a slot by WHEN it happened: inside a
        // breeding window (`w<ci>`, shown as a visitor beside that family box) or in a
        // gap between/around windows (`g<gi>`, gi = index of the next window, so g0 is
        // before the first and g<n> is after the last). A bird seen across several
        // slots appears in each, its per-slot counts summing to its season visits.
        const slotCounts = new Map<string, Map<string, number>>();
        const bump = (k: string, slot: string, n: number) => {
          if (!slotCounts.has(k)) slotCounts.set(k, new Map());
          const m = slotCounts.get(k)!;
          m.set(slot, (m.get(slot) || 0) + n);
        };
        // Unscanned birds present ("No scan" on the observation) tracked per slot, so
        // they show as a grouped stand-in in the window/pre/post/seen-in-box rows.
        const noScanBySlot = new Map<string, number>();
        // Family-box birds (parents/chicks) get a count PER clutch window, keyed
        // `<ci>|<key>` — so a parent shared by both clutches shows its actual sightings
        // in each window, not the season total repeated on every row.
        const winCount = new Map<string, number>();
        // Which slot a moment in the season falls in.
        const slotFor = (t: number) => {
          for (let ci = 0; ci < clutches.length; ci++) {
            if (t >= clutches[ci].windowStart && t <= clutches[ci].windowEnd) return { slot: `w${ci}`, wci: ci };
          }
          let gi = 0; while (gi < clutches.length && t >= clutches[gi].windowStart) gi++;
          return { slot: `g${gi}`, wci: -1 };
        };
        // "No scan" is a property of an OBSERVATION (someone looked in the box and saw
        // unchipped/unscanned birds), so it's tallied from observations, not sightings.
        for (const o of sObsChrono) {
          const ns = Number(o.no_scan) || 0;
          if (ns > 0) {
            const { slot } = slotFor(parseDate(o.observation_time_utc).getTime());
            noScanBySlot.set(slot, (noScanBySlot.get(slot) || 0) + ns);
          }
        }
        // Bird counts come from sightings, so a bird known here only from its chipping
        // (no observation that day) still lands in the right slot with a real count —
        // whether it's a parent, a chick, or a visitor.
        for (const s of seasonSightings) {
          const { slot, wci } = slotFor(s.t);
          if (parentKeys.has(s.key) || chickFamily.has(s.key)) {
            // A parent's courtship-period attendance (before the eggs appeared) is the
            // very evidence that made it a parent, so credit it to the family card
            // rather than stranding it in the pre-breeding row.
            const ci = wci >= 0 ? wci
              : parentKeys.has(s.key) ? clutches.findIndex(c => s.t >= c.attendStart && s.t < c.windowStart)
              : -1;
            if (ci >= 0) winCount.set(`${ci}|${s.key}`, (winCount.get(`${ci}|${s.key}`) || 0) + 1);
            else if (parentKeys.has(s.key)) bump(s.key, slot, 1); // parent seen well outside its window → pre/post-breeding
            continue;
          }
          bump(s.key, slot, 1);
        }
        // Season context: a bird chipped as a chick during the listed season renders
        // as a chick (pale yellow) — we're looking at it during its chick time. The
        // context date is the day AFTER chipping so the same-day chipped-here (green)
        // styling never applies here. In later seasons the bird renders as an adult.
        const seasonObsDate = (b: any) => {
          if (!b.chipped_as_adult && b.chip_date) {
            const cd = new Date(b.chip_date);
            if (cd >= seasonStart && cd < seasonEnd) return chickContextDate(b.chip_date);
          }
          return undefined;
        };

        const birdWithCount = (b: any, count?: number, showZero?: boolean) => {
          if (b?.noScan) return (
            <span key="noscan" className="bird-with-count">
              <span className="scan no-scan" title="Unscanned birds present">No scan</span>
              <span className="scan-count">{count}x</span>
            </span>
          );
          const n = count ?? b.scanCount;
          return (
            <span key={b.pit_id.slice(-8)} className="bird-with-count">
              <PenguinMini scan={b} onClick={() => onBirdClick(b.peng_num || b.pit_id)} observationDate={seasonObsDate(b)} />
              {(n > 0 || showZero) && <span className="scan-count">{n}x</span>}
            </span>
          );
        };

        // Newest observation in this season — the scroll/expand target for the matching
        // season section in the observation list lower on the page.
        const latestObs = sObsChrono.reduce((m, o) => o.observation_time_utc > m ? o.observation_time_utc : m, '');
        const issues = seasonDataIssues(sObsChrono);
        const issueBadges = [
          { key: 'dupobs', label: 'duplicate observation', detail: issues.dupObs },
          { key: 'conflict', label: 'same-sex conflict', detail: issues.conflicts },
          { key: 'dupscan', label: 'duplicate scan', detail: issues.dupScans },
        ].filter(b => b.detail.length > 0);
        // NZ day → newest observation that day, so an issue row can scroll to it.
        const dayToObsTime = new Map<string, string>();
        for (const o of sObsChrono) {
          const day = toNzDateStr(o.observation_time_utc);
          const prev = dayToObsTime.get(day);
          if (!prev || o.observation_time_utc > prev) dayToObsTime.set(day, o.observation_time_utc);
        }

        // Season outcome colour: grey = nothing laid, green = a chick got away, blue = still
        // running, red = eggs and nothing came of them. One rule, shared with the bird view.
        const seasonStatus = seasonOutcome(clutches.length, families);
        const statusLabel = seasonStatus === 'none' ? 'None' : seasonStatus === 'bred' ? 'Bred' : seasonStatus === 'active' ? 'Active' : 'Failed';
        // Everything not part of a detected clutch is a visitor, shown once with its season total.
        const visitorBirds = sorted.filter((b: any) => { const k = b.pit_id.slice(-8); return !parentKeys.has(k) && !chickFamily.has(k); });
        // Visitors sit in gap slots by WHEN they were seen: g0 = before the first window (pre),
        // g<ci> = between clutch ci-1 and ci, g<n> = after the last window (post). Birds seen inside
        // a window show in that clutch's card. Rendered reverse-chronological (newest on top).
        // Grouped "No scan" stand-in for the given slots — [] when none, so callers can spread it.
        const noScanFor = (slots: string[]) => {
          const n = slots.reduce((sum, s) => sum + (noScanBySlot.get(s) || 0), 0);
          return n > 0 ? [{ b: { noScan: true }, n }] : [];
        };
        const aggSlots = (slots: string[]) => sorted
          .map((b: any) => { const k = b.pit_id.slice(-8); let n = 0; for (const s of slots) n += slotCounts.get(k)?.get(s) || 0; return { b, n }; })
          .filter((x: any) => x.n > 0)
          .concat(noScanFor(slots));
        const visitorRow = (label: string, list: { b: any; n: number }[]) => list.length > 0 ? (
          <div className="season-visitors" key={label}>
            <span className="visitors-lbl">{label}</span>
            <span className="visitors-list">{list.map(x => birdWithCount(x.b, x.n))}</span>
          </div>
        ) : null;
        const renderClutch = (ci: number) => {
          const fam = families[ci];
          const { clutch, parents: pairBirds, failedEggs, plainChicks, fledgedUnchipped } = fam;
          const famChicks = [...fam.chicks].sort(scanSortMFC);
          const active = clutchActive(clutch);
          // green = a chipped chick; blue = eggs still active; red = eggs, no chipped chick.
          const cardStatus = famChicks.length > 0 ? 'bred' : active ? 'active' : 'fail';
          const inNest = aggSlots([`w${ci}`]); // non-pair birds seen inside this window
          // Human verification for this clutch, matched by its anchor observation.
          const clutchVer = clutch.startObsId != null ? (verifications.find(v => v.observation_id === clutch.startObsId) || null) : null;
          const vState = computeClutchVerify(fam, clutchVer);
          const vKey = `${label}:${ci}`;
          const showTick = canEdit || !!clutchVer;
          const dates = (
            <span className={`clutch-dates${clutch.startObsTime ? ' clickable' : ''}`}
              title="Go to where the egg/chick was first detected"
              onClick={clutch.startObsTime ? () => onSeasonClick?.(clutch.startObsTime) : undefined}>{windowRange(clutch)}</span>
          );
          return (
            <div key={`cl${ci}`} className={`clutch-card ${cardStatus}`}>
              {openVerify?.key === vKey && (
                // A verdict refreshes in place — the modal stays open so both halves can be
                // reviewed in one sitting; only clicking away (backdrop/✕) closes it.
                <BreedingVerifyModal pos={openVerify.pos} fam={fam} state={vState} box={boxName || ''}
                  token={token} canEdit={canEdit} onBirdClick={onBirdClick}
                  onClose={() => setOpenVerify(null)}
                  onChanged={() => onDataChange?.()} />
              )}
              {clutches.length > 1 && (
                <div className={`clutch-label${clutch.startObsTime ? ' clickable' : ''}`}
                  title="Go to where the egg/chick was first detected"
                  onClick={clutch.startObsTime ? () => onSeasonClick?.(clutch.startObsTime) : undefined}>{ordinal(ci + 1)} clutch</div>
              )}
              {clutch.laidFailed && (
                <div className="season-issues">
                  <span className={`issue-badge note${clutch.startObsTime ? ' clickable' : ''}`}
                    title="Go to where the egg/chick was first detected"
                    onClick={clutch.startObsTime ? () => onSeasonClick?.(clutch.startObsTime) : undefined}>{discoveryNote(clutch)}</span>
                </div>
              )}
              <ClutchBody clutch={clutch} dates={dates}>
                <span className="clutch-birds">
                  {pairBirds.map(b => birdWithCount(b, winCount.get(`${ci}|${b.pit_id.slice(-8)}`) || 0, true))}
                  {famChicks.map(b => {
                    const k = b.pit_id.slice(-8);
                    // A chick belongs to exactly one clutch, so credit its full box attendance
                    // to it rather than window-clipping — chicks are chipped/scanned around
                    // fledge, which lands at or past windowEnd and would otherwise show 0.
                    return birdWithCount(b, Math.max(b.scanCount || 0, winCount.get(`${ci}|${k}`) || 0));
                  })}
                  {Array.from({ length: failedEggs }).map((_, i) => <OffspringFinal key={`fe${i}`} kind="egg" active={active} />)}
                  {Array.from({ length: plainChicks }).map((_, i) => <OffspringFinal key={`pc${i}`} kind="chick" active={active} recorded={i < fam.deadChicks} />)}
                  {Array.from({ length: fledgedUnchipped }).map((_, i) => (
                    <span key={`fu${i}`} className="scan chick offspring-fledged" title="Last sighting of unchipped chick, presumed fledged">Unchipped</span>
                  ))}
                </span>
                {showTick && (
                  <BreedingVerifyTick state={vState} canEdit={canEdit}
                    onOpen={(e) => setOpenVerify({ key: vKey, pos: { x: Math.min(e.clientX + 8, window.innerWidth - 340), y: e.clientY + 8 } })} />
                )}
              </ClutchBody>
              {inNest.length > 0 && (
                <div className="clutch-visitors"><span className="visitors-lbl">Also in nest</span><span className="visitors-list">{inNest.map(x => birdWithCount(x.b, x.n))}</span></div>
              )}
            </div>
          );
        };

        const node = (
          <SeasonBirdsSection key={label} label={label} birds={birds}
            seasonStatus={seasonStatus} statusLabel={statusLabel} latestObs={latestObs}
            onSeasonClick={onSeasonClick} issueBadges={issueBadges} dayToObsTime={dayToObsTime}
            clutches={clutches} visitorBirds={visitorBirds} visitorRow={visitorRow}
            aggSlots={aggSlots} noScanFor={noScanFor} renderClutch={renderClutch} />
        );
        if (isCurrent) currentSeasons.push(node);
        else previousSeasons.push(node);
      });

  const prevCount = previousSeasons.length;
  return (
    <div className="all-birds">
      {orphanVerifications.length > 0 && (
        <div className="verify-orphans" title="A verified clutch's anchor observation no longer starts a detected breeding window — the algorithm has re-segmented away from the saved truth.">
          <div className="verify-orphans-head">
            {'✗'} {orphanVerifications.length} verified clutch{orphanVerifications.length !== 1 ? 'es' : ''} no longer detected
          </div>
          {orphanVerifications.map(o => {
            // A deleted anchor has nothing left to scroll to, so only a live one links out.
            const go = o.v.anchor_time && !o.v.anchor_deleted ? () => onSeasonClick?.(o.v.anchor_time) : undefined;
            return (
              <div key={o.v.verification_id} className={`verify-orphan-row${go ? ' clickable' : ''}`}
                title={go ? 'Go to the observation it was verified against' : undefined} onClick={go}>
                {o.season ? `${o.season} · ` : ''}{o.when || 'date unknown'} {'—'} {o.what} {'·'} {o.why}
                {o.notes ? <div className="verify-orphan-note">{'“'}{o.notes}{'”'}</div> : null}
              </div>
            );
          })}
        </div>
      )}
      {currentSeasons}
      {prevCount > 0 && isMobile ? (
        <>
          <div className="season-divider clickable" onClick={() => setPrevExpanded(!prevExpanded)}>
            <hr/><span>Previous seasons ({prevCount}) {prevExpanded ? '\u25B2' : '\u25BC'}</span><hr/>
          </div>
          {prevExpanded && <>{previousSeasons}{children}</>}
        </>
      ) : <>{previousSeasons}{children}</>}
    </div>
  );
}

/** The radial ("flower") breeding-status picker, viewport-fixed via portal so
 *  overflow-clipping ancestors can't crop the ring. Shared by the observation
 *  card and the day-view row badges. */
function StatusPickRing({ pos, current, onPick, onClose }: { pos: { x: number; y: number }; current: string; onPick: (s: string) => void; onClose: () => void }) {
  return createPortal((
    <>
      <div className="status-picker-backdrop" onClick={onClose} />
      <div className="status-picker" style={{ left: pos.x, top: pos.y }} onClick={e => e.stopPropagation()}>
        <button type="button"
          className={`status-pick-item bordered${current ? '' : ' current'}`}
          style={{ transform: 'translate(-50%,-50%)', background: STATUS_COLORS[''], color: '#333' }}
          title="Clear the status"
          onClick={() => onPick('')}>Clear</button>
        {STATUS_PICK_OPTIONS.map((opt, i) => {
          const n = STATUS_PICK_OPTIONS.length;
          const angle = (i / n) * 2 * Math.PI - Math.PI / 2;
          const r = 56;
          const x = Math.cos(angle) * r, y = Math.sin(angle) * r;
          const isCur = opt === current;
          return (
            <button key={opt} type="button"
              className={`status-pick-item${DARK_TEXT_STATUSES.has(opt)?' bordered':''}${isCur?' current':''}`}
              style={{transform:`translate(-50%,-50%) translate(${x}px, ${y}px)`, background:STATUS_COLORS[opt]||'#ccc', color:DARK_TEXT_STATUSES.has(opt)?'#333':'#fff'}}
              title={STATUS_NAMES[opt]||opt} onClick={() => onPick(opt)}>{opt}</button>
          );
        })}
      </div>
    </>
  ), document.body);
}

/** Clamp a badge's centre so the whole picker ring stays inside the viewport. */
function ringPos(e: React.MouseEvent): { x: number; y: number } {
  const r = (e.currentTarget as HTMLElement).getBoundingClientRect();
  const m = 84;
  return {
    x: Math.min(Math.max(r.left + r.width / 2, m), window.innerWidth - m),
    y: Math.min(Math.max(r.top + r.height / 2, m), window.innerHeight - m),
  };
}

function ObsCard({ obs, box, onBirdClick, onDayClick, highlight, scrollTo, token, canEdit, allPenguins, hideDate, onDataChange }: { obs: Observation; box?: string; onBirdClick?: (tag:string)=>void; onDayClick?: (day:string)=>void; highlight?: boolean; scrollTo?: boolean; token?: string; canEdit?: boolean; allPenguins?: any[]; hideDate?: boolean; onDataChange?: ()=>void }) {
  const ref = useRef<HTMLDivElement>(null);
  const [flashing, setFlashing] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const [editCount, setEditCount] = useState(parseInt(String(obs.edit_count || '0')) || 0);
  useEffect(() => {
    if (scrollTo && ref.current) {
      ref.current.scrollIntoView({ behavior: 'smooth', block: 'center' });
      setFlashing(true);
    } else if (!highlight) {
      // Keep the highlight until the user navigates elsewhere — no auto-fade timer.
      setFlashing(false);
    }
  }, [highlight, scrollTo]);
  const obsId = obs.observation_id;
  const localObs = obs;
  const dayNote = getDayNote(toNzDateStr(obs.observation_time_utc));
  const recordedBy = getObserverName(obs.observer_id);
  const [editing, setEditing] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [birdSearch, setBirdSearch] = useState('');
  // Quick radial breeding-status picker on a locked (non-edit) observation — the only
  // field editable without entering edit mode. Writes the single field directly.
  const [statusPicker, setStatusPicker] = useState(false);
  const [pickerPos, setPickerPos] = useState<{x:number;y:number} | null>(null);
  const [statusOverride, setStatusOverride] = useState<string | null>(null);
  const effectiveStatus = statusOverride ?? localObs.breeding_status ?? '';
  useEffect(() => { setStatusOverride(null); }, [obs.breeding_status, obsId]);
  const pickStatus = async (val: string) => {
    setStatusPicker(false);
    // Re-picking the current status clears it.
    const next = val === (effectiveStatus || '') ? '' : val;
    if (next === (localObs.breeding_status || '') && statusOverride === null) return;
    setStatusOverride(next);
    if (obsId && token) {
      await updateRecord(token, 'observations', obsId, { breeding_status: next });
      setEditCount(c => c + 1);
      onDataChange?.();
    }
  };

  // Edit mode is a local DRAFT — nothing is written to the server until "Done"
  // (Cancel discards). This removes the silent last-write-wins where each field saved
  // live, letting a second editor's stale view clobber the first's data.
  type Draft = { adults:number; eggs:number; chicks:number; breeding_status:string; gate_status:string; notes:string; no_scan:number; fledged_unchipped:number; failed_eggs:number|null; dead_chicks:number|null };
  const [draft, setDraft] = useState<Draft|null>(null);
  const [draftScans, setDraftScans] = useState<Scan[]>([]);
  const setField = (f: keyof Draft, v: any) => setDraft(d => d ? { ...d, [f]: v } : d);
  const scanKey = (s: any) => String(s.scan_id ?? s.pit_id);

  const startEdit = () => {
    setDraft({
      adults: Number(obs.adults)||0, eggs: Number(obs.eggs)||0, chicks: Number(obs.chicks)||0,
      breeding_status: obs.breeding_status || '', gate_status: obs.gate_status || '',
      notes: obs.notes || '', no_scan: Number(obs.no_scan)||0, fledged_unchipped: Number(obs.fledged_unchipped)||0,
      failed_eggs: obs.failed_eggs == null ? null : Number(obs.failed_eggs),
      dead_chicks: obs.dead_chicks == null ? null : Number(obs.dead_chicks),
    });
    setDraftScans([...obs.scans]);
    setEditing(true);
  };
  const cancelEdit = () => { setEditing(false); setDraft(null); setDraftScans([]); setBirdSearch(''); };

  // The peng# you typed comes first, then chip numbers containing it — "12" is #12, not the four
  // PIT IDs with 12 somewhere in them. The typed number resolves to this colony's bird first; a
  // visitor with the same number follows it, since the prefix is easy to leave off.
  const filteredAdd = (() => {
    const q = birdSearch.trim().toUpperCase();
    if (!q || !allPenguins) return [];
    const full = fullPengNum(q);
    const bare = (n: any) => String(n ?? '').toUpperCase().replace(/^[A-Z]+/, '');
    const free = (p: any) => !draftScans.some(s => s.pit_id === p.pit_id);
    const isNum = (p: any) => p.peng_num && (p.peng_num === full || bare(p.peng_num) === bare(q));
    const byNum = allPenguins.filter((p: any) => isNum(p) && free(p))
      .sort((a: any, b: any) => Number(b.peng_num === full) - Number(a.peng_num === full));
    const byPit = allPenguins.filter((p: any) => !isNum(p) && p.pit_id && p.pit_id.toUpperCase().includes(q) && free(p));
    return [...byNum, ...byPit].slice(0, 8);
  })();

  // Adding/removing a penguin bumps the matching count in the draft (chick if chipped as
  // a chick < 3 months before this obs, else adult); it's all committed together on Done.
  const countField = (s: any): keyof Draft => isChickAtObsDate(s.chip_date, s.chipped_as_adult, obs.observation_time_utc) ? 'chicks' : 'adults';
  const draftAddScan = (p: any) => {
    if (!draft || draftScans.some(s => s.pit_id === p.pit_id)) return;
    const f = countField(p);
    setDraftScans([...draftScans, { peng_num: p.peng_num, pit_id: p.pit_id, sex: p.sex, life_stage: p.life_stage, chip_date: p.chip_date, chipped_as_adult: p.chipped_as_adult }]);
    setField(f, (Number(draft[f]) || 0) + 1);
    setBirdSearch('');
  };
  const draftRemoveScan = (scan: any) => {
    if (!draft) return;
    const f = countField(scan);
    setDraftScans(draftScans.filter(s => scanKey(s) !== scanKey(scan)));
    setField(f, Math.max(0, (Number(draft[f]) || 0) - 1));
  };
  // A "no scan" is an adult that was present but couldn't be scanned, so it counts
  // toward the adult total — add/remove it in step with the adult count.
  const draftAddNoScan = () => { if (draft) { setField('no_scan', (draft.no_scan || 0) + 1); setField('adults', (draft.adults || 0) + 1); } };
  const draftRemoveNoScan = () => { if (draft && (draft.no_scan || 0) > 0) { setField('no_scan', (draft.no_scan || 0) - 1); setField('adults', Math.max(0, (draft.adults || 0) - 1)); } };

  // Commit the whole draft on Done: one observations update for changed fields, plus
  // create/delete for added/removed scans. Nothing was written before this point.
  // withNote: ask for a reason and record it against the change. Plain save writes no note.
  const commit = async (withNote = false) => {
    if (!obsId || !token || !draft) { cancelEdit(); return; }
    const fields: Record<string, any> = {};
    for (const f of ['adults','eggs','chicks','no_scan','fledged_unchipped'] as (keyof Draft)[]) if (Number((obs as any)[f]||0) !== Number(draft[f]||0)) fields[f] = Number(draft[f]||0);
    // Nullable: an emptied field clears the record rather than claiming a zero.
    for (const f of ['failed_eggs','dead_chicks'] as (keyof Draft)[]) {
      const was = (obs as any)[f] == null ? null : Number((obs as any)[f]);
      const now = draft[f] == null ? null : Number(draft[f]);
      if (was !== now) fields[f] = now;
    }
    for (const f of ['breeding_status','gate_status','notes'] as (keyof Draft)[]) if (((obs as any)[f]||'') !== (draft[f]||'')) fields[f] = draft[f] || null;
    const draftKeys = new Set(draftScans.map(scanKey));
    const toAdd = draftScans.filter(s => !s.scan_id);
    const toRemove = obs.scans.filter((s: any) => s.scan_id && !draftKeys.has(scanKey(s)));
    const changed = Object.keys(fields).length + toAdd.length + toRemove.length;
    if (changed === 0) { cancelEdit(); return; }
    let reason: string | null = null;
    if (withNote) {
      reason = prompt(`Save ${changed} change${changed===1?'':'s'} to this observation.\n\nReason for the change:`);
      if (reason === null) return; // cancelled — stay in edit mode
    }
    setEditing(false);
    try {
      const why = reason || undefined;
      if (Object.keys(fields).length > 0) await updateRecord(token, 'observations', obsId, fields, why);
      for (const p of toAdd) await createRecord(token, 'penguin_scans', { observation_id: obsId, pit_id: p.pit_id, scan_time_utc: obs.observation_time_utc }, why);
      for (const s of toRemove) await deleteRecord(token, 'penguin_scans', s.scan_id!, why);
      setEditCount(c => c + 1);
    } finally {
      setDraft(null); setDraftScans([]); setBirdSearch('');
      onDataChange?.();
    }
  };

  return (
    <div ref={ref} className={`obs-card ${flashing ? 'highlighted' : ''}${highlight ? ' obs-pinned' : ''}`} style={deleting ? {opacity: 0.4, pointerEvents: 'none'} : undefined}>
      <div className="obs-top">
        {/* The day's note (what the day's monitor was) sits beside the date, the way the
            old per-observation monitor_filename did — one shared value, shown per card. */}
        {!hideDate && <span><b><DateLink date={obs.observation_time_utc} onDayClick={onDayClick} /></b>
          {dayNote && <span className="obs-day-note"> · {dayNote}</span>}</span>}
        <span className="obs-top-right">
          {/* Who recorded it — observations.observer_id, named from the cached observers list. */}
          {recordedBy && <span className="obs-by">{recordedBy}</span>}
          {canEdit && editCount > 0 && obsId && <span className="edit-badge clickable" onClick={() => setShowHistory(!showHistory)}>{editCount === 1 ? 'edited' : `${editCount} edits`}</span>}
          {canEdit && obsId && !editing && <button className="edit-btn" onClick={startEdit}>Edit</button>}
          {editing && <>
            <button className="edit-btn" onClick={cancelEdit}>Cancel</button>
            <button className="edit-btn done-btn" onClick={() => commit(false)}>Save</button>
            <button className="edit-btn done-btn" onClick={() => commit(true)}>Save with note</button>
            <button className="edit-btn" style={{background:'#F44336', color:'#fff'}} onClick={async () => {
              const reason = prompt(`Delete observation from ${formatDate(obs.observation_time_utc)}?\n\nReason for deletion (optional):`);
              if (reason === null) return;
              setEditing(false);
              setDeleting(true);
              await deleteRecord(token || '', 'observations', obsId!, reason || undefined);
              onDataChange?.();
            }}>Delete</button>
          </>}
        </span>
      </div>
      {!editing ? (
        <>
          <div className="obs-nums">
            {(() => {
              const ds = displayStatus(effectiveStatus, localObs.eggs, localObs.chicks, isPostGuard(box, localObs.observation_time_utc));
              const clickable = canEdit && !!obsId && !!token;
              return (
                <span className="status-anchor">
                  <span
                    className={`badge ${ds && DARK_TEXT_STATUSES.has(ds)?'bordered':''}${clickable?' clickable':''}`}
                    style={{background:STATUS_COLORS[ds||'']||'#ccc',color:ds && DARK_TEXT_STATUSES.has(ds)?'#333':'#fff'}}
                    onClick={clickable ? (e) => {
                      setPickerPos(ringPos(e));
                      setStatusPicker(v => !v);
                    } : undefined}
                    title={clickable ? 'Change breeding status' : (STATUS_NAMES[ds||'']||undefined)}
                  >{ds || '\u2014'}</span>
                  {statusPicker && pickerPos && (
                    <StatusPickRing pos={pickerPos} current={effectiveStatus || ''} onPick={pickStatus} onClose={() => setStatusPicker(false)} />
                  )}
                </span>
              );
            })()}
            {localObs.adults === 0 && localObs.eggs === 0 && localObs.chicks === 0 && <span className="muted">Empty</span>}
            {localObs.adults > 0 && <span>{'\uD83D\uDC27'.repeat(Math.min(localObs.adults, 6))}</span>}
            {localObs.eggs > 0 && <span>{'\uD83E\uDD5A'.repeat(Math.min(localObs.eggs, 6))}</span>}
            {localObs.chicks > 0 && <span>{'\uD83D\uDC23'.repeat(Math.min(localObs.chicks, 6))}</span>}
            {/* Ends of life the monitor entered by hand, one crossed icon each — they read as
                the live counts do, rather than as a badge with a number in it. An explicit zero
                is a statement that they checked and nothing died, so it draws nothing. */}
            {Array.from({ length: Math.min(Number(localObs.failed_eggs) || 0, 6) }).map((_, i) => (
              <span key={`fe${i}`} title="Egg recorded as failed on this visit"><OffspringFinal kind="egg" active={false} /></span>
            ))}
            {Array.from({ length: Math.min(Number(localObs.dead_chicks) || 0, 6) }).map((_, i) => (
              <span key={`dc${i}`} title="Chick recorded as dead on this visit"><OffspringFinal kind="chick" active={false} /></span>
            ))}
            {localObs.gate_status && <span className="gate">{localObs.gate_status}</span>}
            {[...obs.scans].sort(scanSortMFC).map((s,j) => (
              <PenguinMini key={j} scan={s} onClick={() => onBirdClick?.(s.peng_num || s.pit_id)} observationDate={obs.observation_time_utc} />
            ))}
            {Array.from({ length: Number(obs.no_scan) || 0 }).map((_, k) => (
              <span key={`ns${k}`} className="scan no-scan">No scan</span>
            ))}
          </div>
          {localObs.notes && <div className="obs-notes">{localObs.notes}</div>}
        </>
      ) : (
        <>
        <div className="obs-edit-fields">
        <div className="obs-edit-birds">
          {[...draftScans].sort(scanSortMFC).map(s => (
            <span key={scanKey(s)} className="scan-removable">
              <PenguinMini scan={s} onClick={() => onBirdClick?.(s.peng_num || s.pit_id)} observationDate={obs.observation_time_utc} />
              <button className="remove-scan" onClick={() => draftRemoveScan(s)}>&times;</button>
            </span>
          ))}
          {Array.from({ length: draft?.no_scan || 0 }).map((_, k) => (
            <span key={`ns${k}`} className="scan-removable">
              <span className="scan no-scan">No scan</span>
              <button className="remove-scan" onClick={draftRemoveNoScan}>&times;</button>
            </span>
          ))}
          <div className="add-scan-search">
            <input className="ef-input" placeholder="Add penguin #..." value={birdSearch} onChange={e => setBirdSearch(e.target.value)} />
            {filteredAdd.length > 0 && (
              <div className="add-scan-results">
                {filteredAdd.map((p: any) => (
                  <div key={p.pit_id} className="add-scan-option" onClick={() => draftAddScan(p)}>
                    <PenguinMini scan={p} onClick={() => draftAddScan(p)} />
                  </div>
                ))}
              </div>
            )}
          </div>
          <button type="button" className="add-noscan-btn" onClick={draftAddNoScan}>Add no scan</button>
        </div>
        <div className="obs-edit-row">
          <label>{'\uD83D\uDC27'}</label><EditableField value={draft?.adults ?? 0} type="number" onSave={async v => { setField('adults', v == null ? 0 : v); }} canEdit={true} inline narrow min={0} />
          <label>{'\uD83E\uDD5A'}</label><EditableField value={draft?.eggs ?? 0} type="number" onSave={async v => { setField('eggs', v == null ? 0 : v); }} canEdit={true} inline narrow min={0} />
          <label>{'\uD83D\uDC23'}</label><EditableField value={draft?.chicks ?? 0} type="number" onSave={async v => { setField('chicks', v == null ? 0 : v); }} canEdit={true} inline narrow min={0} />
          <label title="Unchipped chicks presumed fledged">{'\uD83D\uDD4A'}</label><EditableField value={draft?.fledged_unchipped ?? 0} type="number" onSave={async v => { setField('fledged_unchipped', v == null ? 0 : v); }} canEdit={true} inline narrow min={0} />
          <label title="Eggs seen failed on this visit — for a failure the counts can't show, e.g. one replaced the same day. Blank = not recorded, 0 = checked, none failed"><OffspringFinal kind="egg" active={false} /></label><EditableField value={draft?.failed_eggs ?? ''} type="number" onSave={async v => { setField('failed_eggs', v); }} canEdit={true} inline narrow min={0} />
          <label title="Chicks seen dead on this visit. Blank = not recorded, 0 = checked, none died"><OffspringFinal kind="chick" active={false} /></label><EditableField value={draft?.dead_chicks ?? ''} type="number" onSave={async v => { setField('dead_chicks', v); }} canEdit={true} inline narrow min={0} />
          <EditableField value={draft?.breeding_status ?? ''} type="select" options={['','CON','POT','UNL','NO','DCM','ABN','IGN']} onSave={async v => { setField('breeding_status', v || ''); }} canEdit={true} placeholder="Nest status" />
          <EditableField value={draft?.gate_status ?? ''} type="select" options={['','Gate up','Regate']} onSave={async v => { setField('gate_status', v || ''); }} canEdit={true} placeholder="Gate status" />
          <EditableField value={draft?.notes ?? ''} onSave={async v => { setField('notes', v || ''); }} placeholder="notes" canEdit={true} inline multiline />
        </div>
        </div>
        </>
      )}
      {showHistory && token && obsId && <HistoryPanel token={token} table="observations" id={obsId} onClose={() => setShowHistory(false)} />}
    </div>
  );
}

function EditableField({ value, type, options, onSave, placeholder, canEdit, inline, narrow, min, multiline }: {
  value: any; type?: 'text'|'number'|'select'|'date'; options?: string[];
  onSave: (val: any) => Promise<any>; placeholder?: string; canEdit?: boolean; inline?: boolean; narrow?: boolean; min?: number; multiline?: boolean;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(String(value ?? ''));
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const ref = useRef<HTMLInputElement|HTMLSelectElement>(null);
  const focused = useRef(false);
  // The last value we committed via onSave. Guards against a single edit firing onSave
  // more than once — native date pickers emit several blur events, and each blur would
  // otherwise re-run onSave (and its reason prompt). Kept in sync with the prop.
  const lastSaved = useRef(String(value ?? ''));

  useEffect(() => { if (editing && ref.current) ref.current.focus(); }, [editing]);
  // Inline fields write through to the parent draft on every change (below), which
  // bumps `value`. Don't resync (and clobber the caret / an in-progress entry) while
  // the field is focused — only when the value changes from outside.
  useEffect(() => { if (!focused.current) { setDraft(String(value ?? '')); lastSaved.current = String(value ?? ''); } }, [value]);

  const display = value !== null && value !== undefined && value !== '' ? String(value) : null;
  const flash = () => { setSaved(true); setTimeout(() => setSaved(false), 2000); };

  // Selects always render as a live dropdown, so a blank value is obviously
  // settable (shows the placeholder, e.g. "Nest status") rather than a "-".
  if (type === 'select') {
    if (!canEdit) return <span className="ef-value">{display ?? <span className="muted">{placeholder || '-'}</span>}</span>;
    const opts = options || [];
    const allOpts = draft && !opts.includes(draft) ? [draft, ...opts] : opts; // keep current value (e.g. BR) visible
    return (
      <select className={`ef-input${draft === '' ? ' ef-placeholder' : ''}`} value={draft} disabled={saving}
        onChange={async e => { const v = e.target.value; setDraft(v); setSaving(true); await onSave(v || null); setSaving(false); flash(); }}>
        {allOpts.map(o => <option key={o} value={o}>{o || (placeholder || '(none)')}</option>)}
      </select>
    );
  }

  if (!canEdit) return <span className="ef-value">{display ?? <span className="muted">{placeholder || '-'}</span>}</span>;

  const save = async () => {
    if (saving) return;
    let val = type === 'number' ? (draft === '' ? null : parseFloat(draft)) : (draft || null);
    if (type === 'number' && val !== null && min !== undefined && (val as number) < min) val = min;
    const valStr = String(val ?? '');
    // Nothing actually changed since the last commit — close without re-saving (and
    // without re-prompting for a reason). This is what collapses a date field's repeat
    // blur events into a single save.
    if (valStr === lastSaved.current) { setEditing(false); return; }
    lastSaved.current = valStr; // set before awaiting so a concurrent blur is a no-op
    setSaving(true);
    await onSave(val);
    setDraft(valStr); // resync display to the (possibly clamped) saved value
    setSaving(false);
    setEditing(false);
    flash();
  };

  const cancel = () => { setDraft(String(value ?? '')); setEditing(false); };

  // Inline: a plain input shown directly (no click-to-reveal span / pencil icon),
  // used in the observation edit row where the card is already in edit mode.
  if (inline) {
    if (multiline) {
      return (
        <textarea ref={ref as any} className="ef-input ef-notes" value={draft} disabled={saving} rows={1}
          placeholder={placeholder}
          onFocus={() => { focused.current = true; }}
          onChange={e => { setDraft(e.target.value); onSave(e.target.value || null); }}
          onBlur={() => { focused.current = false; }}
          onKeyDown={e => { if (e.key === 'Escape') cancel(); }} />
      );
    }
    return (
      <input ref={ref as any} className={`ef-input${narrow ? ' ef-narrow' : ''}`} type={type || 'text'} value={draft} disabled={saving}
        placeholder={placeholder} min={min}
        onFocus={() => { focused.current = true; }}
        onChange={e => {
          const raw = e.target.value;
          setDraft(raw);
          let val: any = type === 'number' ? (raw === '' ? null : parseFloat(raw)) : (raw || null);
          if (type === 'number' && val !== null && min !== undefined && (val as number) < min) val = min;
          onSave(val);
        }}
        onBlur={() => { focused.current = false; setDraft(String(value ?? '')); }}
        onKeyDown={e => { if (e.key === 'Enter') (e.currentTarget as HTMLInputElement).blur(); if (e.key === 'Escape') cancel(); }} />
    );
  }

  if (!editing) {
    return (
      <span className="ef-value clickable" onClick={() => setEditing(true)}>
        {display ?? <span className="muted">{placeholder || '-'}</span>}
        {saved && <span className="ef-saved">&#10003;</span>}
        <span className="ef-pencil">&#9998;</span>
      </span>
    );
  }

  return (
    <input ref={ref as any} className="ef-input" type={type || 'text'} value={draft} disabled={saving}
      placeholder={placeholder}
      onChange={e => setDraft(e.target.value)}
      onBlur={save}
      onKeyDown={e => { if (e.key === 'Enter') save(); if (e.key === 'Escape') cancel(); }} />
  );
}

function HistoryPanel({ token, table, id, onClose }: { token: string; table: string; id: number; onClose: () => void }) {
  const [entries, setEntries] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    fetchHistory(token, table, id).then(d => { setEntries(Array.isArray(d) ? d : []); setLoading(false); });
  }, [token, table, id]);

  return (
    <div className="history-panel">
      <div className="history-header">
        <b>Change history</b>
        <button className="page-back" onClick={onClose}>&times;</button>
      </div>
      {loading ? <p className="muted">Loading...</p> : entries.length === 0 ? <p className="muted">No changes recorded</p> : (
        <div className="history-entries">
          {entries.map((e: any, i: number) => {
            const fields = typeof e.changed_fields === 'string' ? JSON.parse(e.changed_fields) : e.changed_fields;
            // Field values recorded with the entry. INSERTs from nestcheck sync carry the
            // full original observation — show it, so an edited observation's initial
            // values stay visible in the history.
            const fieldList = fields && Object.keys(fields).length > 0 && (
              <div className="history-fields">
                {Object.entries(fields).map(([k, v]: [string, any]) => (
                  <div key={k} className="history-field">
                    <span className="muted">{k}:</span> {v && typeof v === 'object' && 'old' in v
                      ? <><s>{String(v.old ?? '')}</s> &rarr; {String(v.new ?? '')}</>
                      : <>{String(v ?? '')}</>}
                  </div>
                ))}
              </div>
            );
            return (
              <div key={i} className="history-entry">
                <div className="history-meta">
                  <span className={`history-action ${e.action.toLowerCase()}`}>{e.action}</span>
                  <span className="muted">{e.observer_name}</span>
                  <span className="muted">{parseDate(e.change_timestamp).toLocaleString('en-NZ', {timeZone:'Pacific/Auckland'})}</span>
                  {e.change_reason && <span className="muted" style={{fontStyle:'italic'}}>"{e.change_reason}"</span>}
                </div>
                {e.action === 'UPDATE' && fieldList}
                {e.action === 'INSERT' && (e.table_name === 'penguin_scans' && e.penguin_info ? (
                  <div className="history-fields">
                    <PenguinMini scan={e.penguin_info} onClick={() => {}} /> added
                  </div>
                ) : fieldList || <div className="history-fields muted">Record created</div>)}
                {e.action === 'DELETE' && (e.table_name === 'penguin_scans' && e.penguin_info ? (
                  <div className="history-fields">
                    <PenguinMini scan={e.penguin_info} onClick={() => {}} /> removed
                  </div>
                ) : <div className="history-fields muted">Record deleted</div>)}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

/** A chipping event shown as a sighting card: green left border (vs blue for
 *  observations), date + bird mini + "Chipped by X" — no adult/egg/chick counts,
 *  since a chipping isn't an observation and those values are unknown. Multiple birds
 *  chipped in the same box on the same day share one card (one row per bird). */
function ChipCard({ date, birds, chipBy, scan, box, onBoxClick, onBirdClick, onDayClick }: {
  date: string; birds?: any[]; chipBy?: string | null; scan?: any; box?: string;
  onBoxClick?: (box: string) => void; onBirdClick: (num: string) => void; onDayClick?: (day: string) => void;
}) {
  // A nest chipped in one go is read biggest chick first, not in the order the chips
  // happened to be entered.
  const list = (birds && birds.length) ? [...birds].sort(scanSortMFC) : [{ ...scan, chip_by: chipBy }];
  return (
    <div className="obs-card chip-card">
      <div className="obs-top">
        <span><b><DateLink date={date} onDayClick={onDayClick} /></b></span>
        {box && onBoxClick && <a className="bird-chip clickable" href={`/box/${box}`} onClick={e => navClick(e, () => onBoxClick(box))}>Box {box}</a>}
      </div>
      {/* Pass the chip day itself so each mini gets the green chipped-here styling —
          this card IS the chipping event. */}
      {list.map((b: any) => (
        <div className="obs-nums" key={b.pit_id}>
          <PenguinMini scan={b} onClick={() => onBirdClick(b.peng_num)} observationDate={date} />
          <span className="muted">{b.is_rechip ? `Rechipped by ${b.chip_by || '?'}` : `Chipped by ${b.chip_by || '?'}`}</span>
        </div>
      ))}
    </div>
  );
}

/** Collapse adjacent same-day chipping events into a single card carrying every bird
 *  chipped that day (each render site here is scoped to one box, so same chip_date ⇒
 *  same box). Input must already be time-sorted; chip events sit at `<day> 00:00:00`
 *  so same-day ones are contiguous. */
function mergeSameDayChips(items: any[]): any[] {
  const out: any[] = [];
  for (let i = 0; i < items.length; i++) {
    const it = items[i];
    if (!it._chip) { out.push(it); continue; }
    const chipBirds = [it];
    while (i + 1 < items.length && items[i + 1]._chip && items[i + 1].chip_date === it.chip_date) {
      chipBirds.push(items[++i]);
    }
    out.push({ ...it, _chipBirds: chipBirds });
  }
  return out;
}

/**
 * Box detail body: "Breeding history" + "Observations". Extracted from the inline box view so
 * the full app and the embed (?embed=1 -> nestcheck box modal) render it from one place and
 * can't drift. Edit affordances (add-penguin, deleted toggle) are gated on canEdit / callbacks.
 */
function BoxPanel({ data, boxName, allPenguins, onBirdClick, onDayClick, highlightObs, scrollToObs, onScrollToObs, token, canEdit, onDataChange, showDeleted, deletedObs, onToggleDeleted, onAddPenguin }: {
  data: any; boxName: string; allPenguins: any[];
  onBirdClick: (tag: string) => void; onDayClick: (day: string) => void;
  highlightObs: string | null; scrollToObs: string | null; onScrollToObs: (date: string) => void;
  token?: string; canEdit?: boolean; onDataChange?: () => void;
  showDeleted?: boolean; deletedObs?: any[]; onToggleDeleted?: () => void; onAddPenguin?: (box: string) => void;
}) {
  const [curSeasonOpen, setCurSeasonOpen] = useState(true);
  // Re-open the current season if a link targets one of its observations.
  useEffect(() => {
    const target = scrollToObs || highlightObs;
    if (target && target >= getSeasonStart().toISOString()) setCurSeasonOpen(true);
  }, [scrollToObs, highlightObs]);
  return (
    <div className="detail-obs">
      <div className="obs-columns">
        <div className="obs-col obs-col-overview">
          <h3 className="obs-section-head">Breeding history</h3>
          <AllScannedBirds observations={data.observations} onBirdClick={onBirdClick} allPenguinsInBox={data.all_penguins}
            onSeasonClick={(t: string) => onScrollToObs(t)} boxName={boxName}
            verifications={data.verifications}
            token={token} canEdit={canEdit} onDataChange={onDataChange}>
            {(() => {
              // Chronological, and a nest chipped in one visit reads biggest chick first.
              const chipped = (data.all_penguins || []).filter((p: any) => p.is_chipped_here)
                .sort((a: any, b: any) => (a.chip_date || '').localeCompare(b.chip_date || '') || scanSortMFC(a, b));
              if (chipped.length === 0 && !canEdit) return null;
              return (
                <div className="chipped-here">
                  <div className="muted">Chipped in this box: {chipped.length}</div>
                  <div className="bird-row">
                    {chipped.map((c: any) => {
                      const cur = allPenguins?.find((p: any) => p.peng_num === c.peng_num);
                      return (
                      <span key={c.pit_id} className="bird-with-count">
                        <PenguinMini scan={cur ? {...c, hasReturned: cur.hasReturned} : c} onClick={() => onBirdClick(c.peng_num)} observationDate={c.chip_date ? chickContextDate(c.chip_date) : undefined} />
                        <span className="scan-count">{c.chip_date ? getSeasonLabel(parseDate(c.chip_date)) : ''}{c.chip_by ? ` ${c.chip_by}` : ''}</span>
                      </span>
                      );
                    })}
                    {canEdit && onAddPenguin && <button className="add-penguin-btn" title="Add a penguin chipped in this box" onClick={() => onAddPenguin(boxName)}>+ 🐧</button>}
                  </div>
                </div>
              );
            })()}
          </AllScannedBirds>
        </div>
        <div className="obs-col obs-col-observations">
          <h3 className="obs-section-head">Observations</h3>
          {(() => {
            const thisSeasonStart = getSeasonStart().toISOString();
            const thisLabel = getSeasonLabel();
            // Chip and rechip events with no matching scan of the bird on the chip day
            // become their own sighting card (a same-day scan already shows the bird —
            // by the bird, not the pit, so a rechip-day scan on either chip suppresses it).
            const scannedPengsByDay = new Map<string, Set<string>>();
            for (const o of data.observations) {
              const day = toNzDateStr(o.observation_time_utc);
              if (!scannedPengsByDay.has(day)) scannedPengsByDay.set(day, new Set());
              for (const s of (o.scans || [])) if (s.peng_num) scannedPengsByDay.get(day)!.add(s.peng_num);
            }
            const chipEvents = (data.chip_events || [])
              .filter((p: any) => !scannedPengsByDay.get(p.chip_date)?.has(p.peng_num))
              .map((p: any) => ({ ...p, _chip: true, observation_time_utc: `${p.chip_date} 00:00:00` }));
            const byTimeDesc = (a: any, b: any) => b.observation_time_utc.localeCompare(a.observation_time_utc);
            const thisSeason = [...data.observations, ...chipEvents].filter((o: any) => o.observation_time_utc >= thisSeasonStart).sort(byTimeDesc);
            const prevObs = [...data.observations, ...chipEvents].filter((o: any) => o.observation_time_utc < thisSeasonStart).sort(byTimeDesc);
            const prevSeasons = new Map<string, Observation[]>();
            for (const obs of prevObs) {
              const label = getSeasonLabel(parseDate(obs.observation_time_utc));
              if (!prevSeasons.has(label)) prevSeasons.set(label, []);
              prevSeasons.get(label)!.push(obs);
            }
            const sortedPrev = Array.from(prevSeasons.entries()).sort((a, b) => b[0].localeCompare(a[0]));
            const deletedCount = (data as any)?.deleted_count || 0;
            const mergedObs = showDeleted && (deletedObs?.length || 0) > 0
              ? [...thisSeason.map((o: any) => ({...o, _deleted: false})), ...(deletedObs || []).map((o: any) => ({...o, _deleted: true}))]
                .sort((a, b) => b.observation_time_utc.localeCompare(a.observation_time_utc))
              : thisSeason;
            return (<>
              <div className="season-divider clickable" onClick={() => setCurSeasonOpen(o => !o)}><hr/><span>{seasonRange(thisLabel)} ({thisSeason.length}) {curSeasonOpen ? '▲' : '▼'}
                {curSeasonOpen && deletedCount > 0 && onToggleDeleted && <span className="deleted-indicator" onClick={(e) => { e.stopPropagation(); onToggleDeleted(); }}> · {showDeleted ? 'hide' : 'show'} {deletedCount} deleted</span>}
              </span><hr/></div>
              {curSeasonOpen && <>
              {mergedObs.length === 0 && <p className="muted">No observations this season</p>}
              {mergeSameDayChips(mergedObs).map((obs: any, i: number) => obs._deleted ? (
                <div key={`del${obs.observation_id}`} className="obs-card deleted-obs">
                  <div className="obs-top">
                    <span><s><DateLink date={obs.observation_time_utc} onDayClick={onDayClick} /></s></span>
                    <span className="muted">deleted {obs.deleted_at ? formatDate(obs.deleted_at) : ''} by {obs.deleted_by_name || '?'}{obs.delete_reason ? ` — ${obs.delete_reason}` : ''}</span>
                  </div>
                  <div className="obs-nums">
                    {obs.adults === 0 && obs.eggs === 0 && obs.chicks === 0 && <span className="muted">Empty</span>}
                    {obs.adults > 0 && <span>{'🐧'.repeat(Math.min(obs.adults, 6))}</span>}
                    {obs.eggs > 0 && <span>{'🥚'.repeat(Math.min(obs.eggs, 6))}</span>}
                    {obs.chicks > 0 && <span>{'🐣'.repeat(Math.min(obs.chicks, 6))}</span>}
                    {obs.breeding_status && <span className="badge bordered" style={{background:'#E0E0E0', color:'#333'}}>{obs.breeding_status}</span>}
                  </div>
                  {obs.notes && <div className="obs-notes"><s>{obs.notes}</s></div>}
                </div>
              ) : obs._chip ? (
                <ChipCard key={`chip${obs.pit_id}`} date={obs.chip_date} birds={obs._chipBirds} onBirdClick={onBirdClick} onDayClick={onDayClick} />
              ) : (
                <ObsCard key={obs.observation_id || `t${i}`} obs={obs} box={boxName} onBirdClick={onBirdClick} onDayClick={onDayClick} highlight={highlightObs !== null && obs.observation_time_utc === highlightObs} scrollTo={scrollToObs !== null && obs.observation_time_utc === scrollToObs} token={token} canEdit={canEdit} allPenguins={allPenguins} onDataChange={onDataChange} />
              ))}
              </>}
              {sortedPrev.map(([label, obs]) => (
                <CollapsibleSeason key={label} label={label} observations={obs} box={boxName} onBirdClick={onBirdClick} onDayClick={onDayClick} highlightObs={highlightObs} scrollToObs={scrollToObs} token={token} canEdit={canEdit} allPenguins={allPenguins} onDataChange={onDataChange} />
              ))}
            </>);
          })()}
        </div>
      </div>
    </div>
  );
}

// One biometric input in the edit card. Holds its own draft and commits on blur / Enter, and
// only when the value actually changed — a keystroke-by-keystroke save would fire an update per
// digit. The unit sits inside the box as a suffix, not as a placeholder.
function BioInput({ value, onCommit, unit, type = 'number', multiline }: {
  value: any; onCommit: (v: string) => void; unit?: string; type?: 'number' | 'text' | 'date'; multiline?: boolean;
}) {
  const shown = value === null || value === undefined ? '' : (type === 'number' && value !== '' ? String(Math.round(parseFloat(value))) : String(value));
  const [draft, setDraft] = useState(shown);
  const focused = useRef(false);
  useEffect(() => { if (!focused.current) setDraft(shown); }, [shown]);
  const commit = () => { focused.current = false; if (draft.trim() !== shown) onCommit(draft.trim()); };
  const common = {
    value: draft, onFocus: () => { focused.current = true; }, onBlur: commit,
    onChange: (e: any) => setDraft(e.target.value),
  };
  if (multiline) return <textarea className="bio-in bio-note" rows={2} placeholder="Note" {...common} />;
  return (
    <span className="bio-in-wrap">
      <input className="bio-in" type={type} inputMode={type === 'number' ? 'decimal' : undefined} {...common}
        onKeyDown={e => { if (e.key === 'Enter') (e.currentTarget as HTMLInputElement).blur(); if (e.key === 'Escape') { setDraft(shown); } }} />
      {unit && <span className="bio-unit">{unit}</span>}
    </span>
  );
}

// Biometrics for a bird: summary line that expands to per-record view/edit, an add form,
// and a "removed" section (soft-deleted records) with restore. Renders table rows for bird-table.
function BiometricsEditor({ pengNum, biometrics, deleted, token, canEdit, editing }: {
  pengNum: string; biometrics: any[]; deleted: any[]; token?: string; canEdit: boolean; editing: boolean;
}) {
  const [showBio, setShowBio] = useState(false);
  // Editing the bird means working on these, so they open with it — scrolling past a section
  // costs nothing, hunting for a collapsed one costs a click every time.
  useEffect(() => { if (editing) setShowBio(true); }, [editing]);
  const [showRemoved, setShowRemoved] = useState(false);
  const [adding, setAdding] = useState(false);
  // Every biometric field the DB carries, so nothing is hidden.
  const MEASURES: [string, string, string][] = [['weight', 'Weight', 'g'], ['flipper_length', 'Flipper', 'mm'], ['body_length', 'Body', 'mm'], ['beak_length', 'Beak', 'mm']];
  const FLAGS: [string, string][] = [['is_moulting', 'Moulting'], ['condition_ticks', 'Ticks'], ['condition_healthy', 'Healthy'], ['disposition_aggressive', 'Aggressive'], ['disposition_passive', 'Passive']];
  const emptyForm: any = { observation_date: toNzDateStr(new Date().toISOString()), observed_sex: '', sex: '', notes: '' };
  MEASURES.forEach(([k]) => emptyForm[k] = '');
  FLAGS.forEach(([k]) => emptyForm[k] = false);
  const [form, setForm] = useState<any>(emptyForm);
  const [busy, setBusy] = useState(false);
  const SEX_OPTS = ['', 'PM', 'MM', 'U', 'MF', 'PF'];
  const setF = (k: string, v: any) => setForm((f: any) => ({ ...f, [k]: v }));

  if (biometrics.length === 0 && deleted.length === 0 && !editing) return null;

  const saveField = (id: number, field: string) => async (val: any) => {
    if (token) return updateRecord(token, 'penguin_biometric_data', id, { [field]: val === '' ? null : val });
  };
  const toggle = async (b: any, field: string, val: boolean) => { if (token) await updateRecord(token, 'penguin_biometric_data', b.biometric_id, { [field]: val ? 1 : 0 }); };
  const remove = async (b: any) => { if (token && confirm('Remove this biometric record?')) await deleteRecord(token, 'penguin_biometric_data', b.biometric_id); };
  const restore = async (b: any) => { if (token) await updateRecord(token, 'penguin_biometric_data', b.biometric_id, { is_deleted: 0 }); };
  const submitAdd = async () => {
    if (!token || busy) return;
    if (!form.observation_date) { alert('Date is required'); return; }
    setBusy(true);
    const rec: any = { peng_num: pengNum, observation_date: form.observation_date, observed_sex: form.observed_sex || null, sex: form.sex || null, notes: form.notes.trim() || null };
    MEASURES.forEach(([k]) => { if (String(form[k]).trim() !== '') rec[k] = parseFloat(form[k]); });
    FLAGS.forEach(([k]) => { rec[k] = form[k] ? 1 : 0; });
    await createRecord(token, 'penguin_biometric_data', rec);
    setBusy(false); setAdding(false); setForm(emptyForm);
  };

  const sexCounts: Record<string, number> = {};
  const lastComment = biometrics.find((b: any) => b.notes)?.notes;
  const weights = biometrics.filter((b: any) => b.weight).map((b: any) => parseFloat(b.weight));
  const flippers = biometrics.filter((b: any) => b.flipper_length).map((b: any) => parseFloat(b.flipper_length));
  biometrics.forEach((b: any) => { if (b.observed_sex) sexCounts[b.observed_sex] = (sexCounts[b.observed_sex] || 0) + 1; });
  const sexSummary = Object.entries(sexCounts).map(([s, n]) => `sexed ${observedSexLabel(s, true)} ${n}x`).join(', ');
  const range = (vals: number[], unit: string) => { if (!vals.length) return ''; const lo = Math.round(Math.min(...vals)), hi = Math.round(Math.max(...vals)); return `${lo === hi ? lo : `${lo}-${hi}`}${unit}${vals.length > 1 ? ` (${vals.length}x)` : ''}`; };
  const summary = [sexSummary, range(weights, 'g'), range(flippers, 'mm'), lastComment ? `"${lastComment.slice(0, 40)}"` : ''].filter(Boolean).join(' · ');

  // Edit layout: one card per record — date and actions across the top, measurements in a
  // grid of labelled boxes, flags as toggle chips, note underneath.
  const editCard = (key: string, v: any, set: (k: string, val: any) => void, head: any, foot?: any) => (
    <tr key={key} className="bio-record-head"><td colSpan={2}>
      <div className="bio-card">
        <div className="bio-card-head">
          <BioInput type="date" value={v.observation_date} onCommit={val => set('observation_date', val)} />
          {head}
        </div>
        <div className="bio-grid">
          <label className="bio-field"><span>Sex</span>
            <select className="bio-in" value={v.observed_sex || ''} onChange={e => set('observed_sex', e.target.value)}>
              {SEX_OPTS.map(s => <option key={s} value={s}>{s ? observedSexLabel(s, false) : '-'}</option>)}
            </select>
          </label>
          {MEASURES.map(([k, label, unit]) => (
            <label key={k} className="bio-field"><span>{label}</span><BioInput value={v[k]} unit={unit} onCommit={val => set(k, val)} /></label>
          ))}
          {v.sex && <label className="bio-field"><span>Sex (legacy)</span><BioInput type="text" value={v.sex} onCommit={val => set('sex', val)} /></label>}
        </div>
        <div className="bio-flags">
          {FLAGS.map(([k, label]) => (
            <label key={k} className={`bio-flag${v[k] && v[k] !== '0' ? ' on' : ''}`}>
              <input type="checkbox" checked={!!v[k] && v[k] !== '0'} onChange={e => set(k, e.target.checked)} />{label}
            </label>
          ))}
        </div>
        <BioInput multiline type="text" value={v.notes} onCommit={val => set('notes', val)} />
        {foot}
      </div>
    </td></tr>
  );

  const record = (b: any, i: number, removed: boolean) => {
    const flags = FLAGS.filter(([k]) => b[k]).map(([, label]) => label);
    if (editing && !removed) return editCard(`bio${b.biometric_id ?? i}`, b,
      (k, val) => {
        if (FLAGS.some(([f]) => f === k)) toggle(b, k, val);
        else if (MEASURES.some(([m]) => m === k)) saveField(b.biometric_id, k)(val === '' ? '' : parseFloat(val));
        else saveField(b.biometric_id, k)(val);
      },
      <button className="edit-btn" onClick={() => remove(b)}>Remove</button>);
    return (<Fragment key={`${removed ? 'del' : 'bio'}${b.biometric_id ?? i}`}>
      <tr className="bio-record-head"><td className="muted" colSpan={2} style={{ fontWeight: 600, fontSize: 11 }}>
        {b.observation_date || ''}
        {removed && <span className="bird-badge" style={{ background: '#FFCDD2', marginLeft: 6 }}>removed</span>}
        {removed && canEdit && <button className="edit-btn" style={{ marginLeft: 8 }} onClick={() => restore(b)}>Restore</button>}
      </td></tr>
      <tr><td className="muted">Sex</td><td>{observedSexLabel(b.observed_sex, false) || <span className="muted">-</span>}</td></tr>
      {b.sex && <tr><td className="muted">Sex (legacy)</td><td>{b.sex}</td></tr>}
      {MEASURES.map(([k, label, unit]) => b[k] ? <tr key={k}><td className="muted">{label}</td><td>{`${parseFloat(b[k]).toFixed(0)}${unit}`}</td></tr> : null)}
      {flags.length > 0 && <tr><td className="muted">Flags</td><td>{flags.join(', ')}</td></tr>}
      <tr><td className="muted">Note</td><td style={{ fontSize: 11 }}>{b.notes || <span className="muted">-</span>}</td></tr>
    </Fragment>);
  };

  return (<>
    <tr><td className="muted">Biometrics</td><td className="clickable" onClick={() => setShowBio(!showBio)}>{summary || <span className="muted">-</span>} <span className="muted small">{biometrics.length} records {showBio ? '▲' : '▼'}</span></td></tr>
    {showBio && <>
      {editing && !adding && <tr><td></td><td><button className="edit-btn" onClick={() => setAdding(true)}>+ Add biometric</button></td></tr>}
      {editing && adding && editCard('bio-new', form, setF,
        <span className="bio-card-title">New biometric</span>,
        <div className="bio-card-foot"><button className="edit-btn done-btn" disabled={busy} onClick={submitAdd}>{busy ? 'Saving…' : 'Save'}</button> <button className="edit-btn" onClick={() => { setAdding(false); setForm(emptyForm); }}>Cancel</button></div>)}
      {biometrics.map((b, i) => record(b, i, false))}
      {deleted.length > 0 && <tr><td></td><td className="clickable muted small" onClick={() => setShowRemoved(!showRemoved)}>{deleted.length} removed {showRemoved ? '▲' : '▼'}</td></tr>}
      {showRemoved && deleted.map((b, i) => record(b, i, true))}
    </>}
  </>);
}

function BirdPage({ data, onBirdClick, onBoxClick, onSightingClick, onDayClick, token, canEdit, onClose }: { data: any; onBirdClick: (tag:string)=>void; onBoxClick: (box:string)=>void; onSightingClick: (box:string, date:string)=>void; onDayClick?: (day:string)=>void; token?: string; canEdit?: boolean; onClose?: () => void }) {
  const p = data.penguin;
  // Step to the next penguin by number. The colony prefix is part of the key, so ordering is
  // on the numeric tail within this bird's own prefix — PT7 steps to PT8, never to NI8 — and it
  // lands on the next bird that EXISTS rather than peng_num + 1: the numbering has gaps wherever
  // a bird was never created or has since gone.
  const allPenguins = useAllPenguins();
  const [prevPeng, nextPeng] = useMemo(() => {
    const prefixOf = (n: any) => String(n ?? '').match(/^[A-Z]*/)![0];
    const mine = prefixOf(p.peng_num);
    const here = pengNumValue(p.peng_num);
    if (isNaN(here)) return [null, null];
    let below: string | null = null, belowNum = -Infinity;
    let above: string | null = null, aboveNum = Infinity;
    for (const row of allPenguins) {
      if (prefixOf(row.peng_num) !== mine) continue;
      const v = pengNumValue(row.peng_num);
      if (v < here && v > belowNum) { belowNum = v; below = row.peng_num; }
      if (v > here && v < aboveNum) { aboveNum = v; above = row.peng_num; }
    }
    return [below, above];
  }, [allPenguins, p.peng_num]);
  // Left/right step down/up the numbering; . and , step the container the bird sits in (the
  // box, or the day when the panel is docked in the day overlay). Splitting them that way is
  // what keeps the two window listeners out of each other's way — sharing a key moved the bird
  // and the day on a single press. Same guard as the app's other shortcuts: never while typing.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.metaKey || e.ctrlKey || e.altKey || e.defaultPrevented) return;
      const t = e.target as HTMLElement | null;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable)) return;
      const step = e.key === 'ArrowLeft' ? prevPeng : e.key === 'ArrowRight' ? nextPeng : null;
      if (!step) return;
      e.preventDefault();
      onBirdClick(step);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [prevPeng, nextPeng, onBirdClick]);
  const sightings: any[] = data.sightings || [];
  const biometrics: any[] = data.biometrics || [];
  const partners: any[] = data.partners || [];

  const chips: any[] = p.chips || [];
  const activeChip = chips.find((c: any) => c.is_active == 1) || chips[0];

  // Boxes ordered by the bird's most recent visit to each, newest first.
  const boxLastSeen = new Map<string, number>();
  for (const s of sightings) {
    const t = parseDate(s.date).getTime();
    if (!boxLastSeen.has(s.box) || t > boxLastSeen.get(s.box)!) boxLastSeen.set(s.box, t);
  }
  const boxes = Array.from(boxLastSeen.keys()).sort((a, b) => boxLastSeen.get(b)! - boxLastSeen.get(a)!);

  // Shared sightings ordered by the most recent time this bird was seen with each of them,
  // so whoever it is currently keeping company with sits at the top.
  const lastSeenWith = (pt: any) => Math.max(...(pt.sightings || []).map((s: any) => parseDate(s.date).getTime()), 0);
  const partnersByRecent = [...partners].sort((a, b) => lastSeenWith(b) - lastSeenWith(a));

  // When the panel opens or switches bird, subtly lift every mini of this bird on the
  // page so the user sees at a glance where it's referenced, for as long as it's open.
  useEffect(() => {
    openPanelPengNum = p.peng_num || null;
    setSelectedPengMinis([p.peng_num, ...chips.map((c: any) => (c.pit_id || '').slice(-8))]);
    return () => { openPanelPengNum = null; setSelectedPengMinis([]); };
  }, [p.peng_num]);

  // Peng-centric breeding family: run the SAME nest family detection (computeBoxFamilies)
  // over every box this bird was seen/chipped in, then keep the clutches where this bird
  // was a parent (→ partner + offspring) or a chick (→ parents + siblings). Recomputed
  // from `data`, which changes identity whenever the cache updates.
  const { pengFamilies, boxWindows, breedingSeasons } = useMemo(() => {
    const myPits = new Set<string>(chips.map((c: any) => (c.pit_id || '').slice(-8)).filter(Boolean));
    const boxNames = new Set<string>();
    for (const s of sightings) if (s.box) boxNames.add(s.box);
    for (const c of chips) if (c.chip_box) boxNames.add(c.chip_box);
    const isMine = (b: any) => myPits.has((b.pit_id || '').slice(-8));
    type Entry = { season: string; seasonYear: number; box: string; role: 'parent' | 'chick'; fam: BoxFamily; partner?: any; parents: any[]; siblings: any[]; clutchIndex: number; clutchCount: number };
    const entries: Entry[] = [];
    // Per box: every clutch's breeding window, so a shared sighting can be flagged as
    // falling inside a breeding window (and get the black-box treatment).
    const boxWindows = new Map<string, { windowStart: number; windowEnd: number; startObsTime: string; fam: BoxFamily }[]>();
    for (const box of boxNames) {
      const bd = queryBoxDetailSync(box);
      if (!bd?.observations?.length) continue;
      const wins: { windowStart: number; windowEnd: number; startObsTime: string; fam: BoxFamily }[] = [];
      for (const sd of computeBoxFamilies(bd.observations, bd.all_penguins)) {
        const clutchCount = sd.families.length;
        sd.families.forEach((fam, ci) => {
          const c = fam.clutch;
          wins.push({ windowStart: c.windowStart, windowEnd: c.windowEnd, startObsTime: c.startObsTime, fam });
          const asParent = (fam.male && myPits.has(fam.male)) || (fam.female && myPits.has(fam.female));
          const asChick = fam.chicks.some(isMine);
          if (asParent) {
            entries.push({ season: sd.label, seasonYear: sd.seasonYear, box, role: 'parent', fam,
              partner: fam.parents.find(b => !isMine(b)), parents: fam.parents, siblings: [], clutchIndex: ci, clutchCount });
          } else if (asChick) {
            entries.push({ season: sd.label, seasonYear: sd.seasonYear, box, role: 'chick', fam,
              parents: fam.parents, siblings: fam.chicks.filter(b => !isMine(b)), clutchIndex: ci, clutchCount });
          }
        });
      }
      boxWindows.set(box, wins);
    }
    entries.sort((a, b) => b.seasonYear - a.seasonYear || a.box.localeCompare(b.box));
    // Season timeline (newest first): every year between the bird's first and last breeding
    // entry, so seasons it wasn't part of a pair render an explicit "None" row.
    const bySeasonYear = new Map<number, Entry[]>();
    for (const e of entries) { if (!bySeasonYear.has(e.seasonYear)) bySeasonYear.set(e.seasonYear, []); bySeasonYear.get(e.seasonYear)!.push(e); }
    const breedingSeasons: { seasonYear: number; entries: Entry[] }[] = [];
    if (entries.length > 0) {
      const yrs = entries.map(e => e.seasonYear);
      for (let y = Math.max(...yrs); y >= Math.min(...yrs); y--) breedingSeasons.push({ seasonYear: y, entries: bySeasonYear.get(y) || [] });
    }
    return { pengFamilies: entries, boxWindows, breedingSeasons };
  }, [data]);
  // The breeding window (if any) containing a shared sighting, plus its NZ date range.
  const windowFor = (box: string, dateStr: string) => {
    const t = parseDate(dateStr).getTime();
    return (boxWindows.get(box) || []).find(w => t >= w.windowStart && t <= w.windowEnd) || null;
  };
  const [showHistory, setShowHistory] = useState<{table:string;id:number}|null>(null);
  const [hasHistory, setHasHistory] = useState(false);
  const [editing, setEditing] = useState(false);
  // The ▸ arrow opens the full record read-only; Edit shows the same rows, live.
  const [showDetails, setShowDetails] = useState(false);
  const fullView = editing || showDetails;
  const [copiedPit, setCopiedPit] = useState(false);
  const copyPit = (v: string) => { navigator.clipboard?.writeText(v); setCopiedPit(true); setTimeout(() => setCopiedPit(false), 1500); };
  const [expandedSections, setExpandedSections] = useState<Record<string, boolean>>({});
  const toggleSection = (key: string) => setExpandedSections(s => ({...s, [key]: !s[key]}));
  useEffect(() => {
    if (token && p.peng_num) {
      fetchHistory(token, 'penguins', p.peng_num).then(d => setHasHistory(Array.isArray(d) && d.length > 0));
    }
  }, [token, p.peng_num]);
  const savePenguin = (field: string) => async (val: any) => {
    const oldVal = p[field] ?? '';
    if (String(oldVal) === String(val ?? '')) return;
    const reason = prompt(`Change ${field} on penguin #${displayPengNum(p.peng_num)} from "${oldVal}" to "${val ?? ''}"?\n\nReason (optional):`);
    if (reason === null) return;
    return updateRecord(token || '', 'penguins', p.peng_num, {[field]: val}, reason || undefined);
  };
  // 0/1 in the column, Yes/No in the panel — savePenguin's prompt would read "alert from 0 to
  // 1", so it asks in the same words the row uses.
  const saveAlert = async (val: any) => {
    const on = val === 'Yes' ? 1 : 0;
    if ((p.alert ? 1 : 0) === on) return;
    // No reason prompt: a flag you switch on to catch a bird next time, and off once you have,
    // is routine housekeeping. audit_log still records who changed it and when.
    return updateRecord(token || '', 'penguins', p.peng_num, { alert: on });
  };
  // Stored inverted (chipped_as_adult), so this can't go through savePenguin without the
  // audit prompt reading "chipped_as_adult from 0 to 1". Ask the question the panel asks.
  const saveChippedAsChick = async (val: any) => {
    const asAdult = val === 'Yes' ? 0 : 1;
    if ((p.chipped_as_adult ? 1 : 0) === asAdult) return;
    const reason = prompt(`Change chipped as chick on penguin #${displayPengNum(p.peng_num)} from "${p.chipped_as_adult ? 'No' : 'Yes'}" to "${val}"?\n\nReason (optional):`);
    if (reason === null) return;
    return updateRecord(token || '', 'penguins', p.peng_num, { chipped_as_adult: asAdult }, reason || undefined);
  };
  const saveChip = (pitId: string, field: string) => async (val: any) => {
    const reason = prompt(`Change ${field} on chip ${pitId.slice(-8)}?\n\nReason (optional):`);
    if (reason === null) return;
    return updateRecord(token || '', 'penguin_chips', pitId, {[field]: val}, reason || undefined);
  };
  // chipper_id / assistant_id are user references, so they save as ids rather than through
  // saveChip's text path. No reason prompt: picking a name from a list is unambiguous, and
  // audit_log records the change either way.
  const saveChipPerson = (pitId: string, field: 'chipper_id' | 'assistant_id', id: number | null) =>
    updateRecord(token || '', 'penguin_chips', pitId, { [field]: id });
  // Weight and flipper as measured on the chipping day: the biometric row dated the same NZ
  // day as the chip. Editing writes through to that row — these are not fields of the chip.
  // Residency: chipping to the bird's most recent sighting — how long it has been on the
  // books here. Blank (and the row hidden) until it has been seen since being chipped.
  const residency = activeChip?.chip_date && sightings.length > 0
    ? durationBetween(parseDate(activeChip.chip_date), parseDate(sightings[0].date)) : '';
  const chipDay = (c: any) => (c.chip_date ? String(c.chip_date).slice(0, 10) : null);
  const chipBio = (c: any) => {
    const day = chipDay(c);
    return day ? biometrics.find((b: any) => String(b.observation_date || '').slice(0, 10) === day) || null : null;
  };
  const saveChipBio = (c: any, field: string) => async (val: any) => {
    if (!token) return;
    const num = val === '' || val === null || val === undefined ? null : parseFloat(val);
    const bio = chipBio(c);
    if (bio) return updateRecord(token, 'penguin_biometric_data', bio.biometric_id, { [field]: num });
    // No biometric on the chipping day yet — the first measurement entered creates one.
    const day = chipDay(c);
    if (!day || num === null) return;
    return createRecord(token, 'penguin_biometric_data', { peng_num: p.peng_num, observation_date: day, [field]: num });
  };


  return (
    <div className="bird-detail">
      <div className="bird-title-row">
        <span className="bird-title-peng">
          <PenguinMini scan={{peng_num: p.peng_num, pit_id: activeChip?.pit_id, sex: p.sex, chip_date: activeChip?.chip_date, chipped_as_adult: p.chipped_as_adult, chick_size_code: p.chick_size_code, hasReturned: p.hasReturned}} onClick={() => activeChip?.pit_id && copyPit(activeChip.pit_id.slice(-8))} title={activeChip?.pit_id ? (copiedPit ? 'Copied chip ID' : `Copy chip ID (${activeChip.pit_id.slice(-8)})`) : undefined} currentStatus />
        </span>
        <span className="bird-title-actions">
          <span className="bird-action-stack">
            {canEdit && !editing && <button className="edit-btn" onClick={() => setEditing(true)}>Edit</button>}
            {editing && <span className="edit-btns"><button className="edit-btn" onClick={() => setEditing(false)}>Cancel</button><button className="edit-btn done-btn" onClick={() => setEditing(false)}>Done</button></span>}
            {canEdit && hasHistory && <button className="history-btn" onClick={() => setShowHistory({table:'penguins', id:p.peng_num})}>History</button>}
          </span>
          <button className="peng-step" onClick={() => prevPeng && onBirdClick(prevPeng)} disabled={!prevPeng}
            title={prevPeng ? `Previous penguin (#${displayPengNum(prevPeng)}) — ←` : 'No lower peng#'} aria-label="Previous penguin">‹</button>
          <button className="peng-step" onClick={() => nextPeng && onBirdClick(nextPeng)} disabled={!nextPeng}
            title={nextPeng ? `Next penguin (#${displayPengNum(nextPeng)}) — →` : 'No higher peng#'} aria-label="Next penguin">›</button>
          {onClose && <button className="day-bird-close" onClick={onClose} title="Close" aria-label="Close">×</button>}
        </span>
      </div>

      {showHistory && token && <HistoryPanel token={token} table={showHistory.table} id={showHistory.id} onClose={() => setShowHistory(null)} />}

      {/* All penguin data. Collapsed it is three or four lines — last seen, the active
          chip's date and box, and notes if there are any. The ▸ on the right opens the
          full field set in exactly the edit view's layout, read-only (EditableField
          renders as plain text when canEdit is false); Edit makes that same view live. */}
      <div className="bird-section">
        <div className="bird-table-wrap">
          <table className="bird-table">
            <tbody>
              {sightings.length > 0 && <tr><td className="muted">Last seen</td><td>
                <DateLink date={sightings[0].date} onDayClick={onDayClick} />
                <span className="muted"> at </span>
                <a className="bird-chip clickable" href={`/box/${sightings[0].box}`} onClick={e => navClick(e, () => onBoxClick(sightings[0].box))}>Box {sightings[0].box}</a>
              </td></tr>}

              {!fullView && <>
                <tr><td className="muted">Chip date</td><td>
                  {activeChip?.chip_date
                    ? <><DateLink date={activeChip.chip_date} onDayClick={onDayClick} /> <span className="muted">{agoSince(activeChip.chip_date)}</span></>
                    : <span className="muted">-</span>}
                </td></tr>
                {residency && <tr><td className="muted">Residency</td><td>{residency}</td></tr>}
                {activeChip?.chip_box && <tr><td className="muted">Chip box</td><td>
                  <a className="bird-chip clickable" href={`/box/${activeChip.chip_box}`} onClick={e => navClick(e, () => onBoxClick(activeChip.chip_box))}>Box {activeChip.chip_box}</a>
                  {(getUserName(activeChip.chipper_id) || activeChip.chip_by) &&
                    <span className="chip-by"><span className="muted">by:</span> {getUserName(activeChip.chipper_id) || activeChip.chip_by}</span>}
                </td></tr>}
                {/* A death is the headline fact about a bird — it shouldn't need the full view. */}
                {!!p.death_date && <tr><td className="muted">Died</td><td>{String(p.death_date).slice(0, 10)}</td></tr>}
                {!!p.notes && <tr><td className="muted">Notes</td><td>{p.notes}</td></tr>}
              </>}

              {fullView && <>
                <tr><td className="muted">Sex</td><td><EditableField value={p.sex} type="select" options={['','M','F']} onSave={savePenguin('sex')} canEdit={editing} /></td></tr>
                <tr><td className="muted">Chipped as Chick</td><td>
                  <EditableField value={p.chipped_as_adult ? 'No' : 'Yes'} type="select" options={['Yes', 'No']}
                    onSave={saveChippedAsChick} canEdit={editing} /></td></tr>
                {/* Field alert: scanning this bird raises one, the way an unsexed adult does. */}
                <tr><td className="muted">Alert on scan</td><td>
                  <EditableField value={p.alert ? 'Yes' : 'No'} type="select" options={['No', 'Yes']}
                    onSave={saveAlert} canEdit={editing} /></td></tr>
                <tr><td className="muted">Chick Size Code</td><td>
                  <EditableField value={p.chick_size_code} type="select" options={['', 'BC', 'LC', 'SC']}
                    onSave={savePenguin('chick_size_code')} placeholder="-" canEdit={editing} /></td></tr>
                {chips.map((c: any, i: number) => {
                  const re = 'Re'.repeat(i);
                  const prefix = i === 0 ? '' : re.toLowerCase();
                  return (<Fragment key={`chip${i}`}>
                    <tr><td className="muted">{prefix ? `${re}chip ` : ''}PIT ID</td><td>{c.pit_id}{!c.is_active && <span className="bird-badge" style={{background:'#FFCDD2', marginLeft:4}}>Retired</span>}</td></tr>
                    <tr><td className="muted">{prefix ? `${re}chip ` : 'Chip '}Date</td><td>
                      <EditableField value={c.chip_date} type="date" onSave={saveChip(c.pit_id, 'chip_date')} placeholder="date" canEdit={editing} />
                      {!editing && c.chip_date && <span className="muted"> {agoSince(c.chip_date)}</span>}
                    </td></tr>
                    {/* Whole-bird figure, so it hangs off the original chip only, not each rechip. */}
                    {i === 0 && !!residency && <tr><td className="muted">Residency</td><td>{residency}</td></tr>}
                    <tr><td className="muted">{prefix ? `${re}chip ` : 'Chip '}Box</td><td><EditableField value={c.chip_box} onSave={saveChip(c.pit_id, 'chip_box')} placeholder="box" canEdit={editing} /></td></tr>
                    <tr><td className="muted">{prefix ? `${re}chipped ` : 'Chipped '}By</td><td>
                      {editing
                        ? <UserPickerField userId={c.chipper_id ?? null} addLabel="+ chipper" title="Who fitted the transponder"
                            onSave={id => saveChipPerson(c.pit_id, 'chipper_id', id)} />
                        : (getUserName(c.chipper_id) || c.chip_by || <span className="muted">-</span>)}
                    </td></tr>
                    {(editing || c.assistant_id) && <tr><td className="muted">Assistant</td><td>
                      {editing
                        ? <UserPickerField userId={c.assistant_id ?? null} addLabel="+ assistant" title="Who assisted with the chipping"
                            onSave={id => saveChipPerson(c.pit_id, 'assistant_id', id)} />
                        : (getUserName(c.assistant_id) || <span className="muted">-</span>)}
                    </td></tr>}
                    {(() => { const bio = chipBio(c); return (<>
                      <tr><td className="muted">{prefix ? `${re}chip ` : 'Chip '}Weight</td><td><span className="chip-measure">
                        <EditableField value={bio?.weight ? parseFloat(bio.weight).toFixed(0) : ''} type="number" min={0}
                          onSave={saveChipBio(c, 'weight')} placeholder="-" canEdit={editing} />
                        {bio?.weight && <span className="muted">g</span>}
                      </span></td></tr>
                      <tr><td className="muted">{prefix ? `${re}chip ` : 'Chip '}Flipper</td><td><span className="chip-measure">
                        <EditableField value={bio?.flipper_length ? parseFloat(bio.flipper_length).toFixed(0) : ''} type="number" min={0}
                          onSave={saveChipBio(c, 'flipper_length')} placeholder="-" canEdit={editing} />
                        {bio?.flipper_length && <span className="muted">mm</span>}
                      </span></td></tr>
                    </>); })()}
                  </Fragment>);
                })}
                {(editing || !!p.death_date) && <tr><td className="muted">Date of death</td><td>{!editing
                  ? (p.death_date ? p.death_date.slice(0, 10) : <span className="muted">-</span>)
                  // A death is stamped at 2pm NZ (02:00 UTC) on the chosen date; clearing the field marks the bird alive.
                  : <EditableField value={p.death_date ? p.death_date.slice(0, 10) : ''} type="date"
                      onSave={(v: any) => savePenguin('death_date')(v ? `${v} 02:00:00` : null)} placeholder="-" canEdit={true} />}</td></tr>}
                <tr><td className="muted">Notes</td><td><EditableField value={p.notes} onSave={savePenguin('notes')} placeholder="-" canEdit={editing} /></td></tr>
                <BiometricsEditor pengNum={p.peng_num} biometrics={biometrics} deleted={data.biometrics_deleted || []} token={token} canEdit={!!canEdit} editing={editing} />
              </>}
            </tbody>
          </table>
          {/* Hidden while editing — Edit already shows every field, and Cancel/Done is the way out. */}
          {!editing && <button className="detail-toggle" onClick={() => setShowDetails(v => !v)}
            title={showDetails ? 'Hide full record' : 'Show full record'}
            aria-label={showDetails ? 'Hide full record' : 'Show full record'}>{showDetails ? '▾' : '▸'} Details</button>}
        </div>
      </div>

      {/* Sightings loading indicator */}
      {sightings.length === 0 && <p className="muted">Loading sighting history...</p>}

      {/* Breeding family — this bird's role in each detected nest family, from the same
          detection (computeBoxFamilies) the box breeding overview uses, rendered in the
          same year-spine + outcome-card layout as the box view. */}
      {pengFamilies.length > 0 && (
        <div className="bird-section">
          <h3 className="collapsible" onClick={() => toggleSection('breeding')}>{expandedSections.breeding ? '▾' : '▸'} Breeding history ({pengFamilies.length})</h3>
          {expandedSections.breeding && <div className="all-birds">
            {breedingSeasons.map(season => {
              const st = seasonOutcome(season.entries.length, season.entries.map(e => e.fam));
              // In the bird's own hatch season it IS the chick — "Bred" would read as it breeding.
              const wasChick = season.entries.some(e => e.role === 'chick');
              const stLabel = st === 'none' ? 'None' : st === 'bred' ? (wasChick ? 'Chick' : 'Bred') : st === 'active' ? 'Active' : 'Failed';
              const stClass = wasChick && st === 'bred' ? 'chick' : st;
              return (
                <div key={season.seasonYear} className="season-birds">
                  <div className="season-year">
                    <div className="season-yr">{seasonRange(String(season.seasonYear))}</div>
                    <span className={`season-status st-${stClass}`}><span className="ss-dot" />{stLabel}</span>
                  </div>
                  <div className="season-content">
                    {season.entries.map((e) => {
                      const offspringDate = (b: any) => b.chip_date ? chickContextDate(b.chip_date) : undefined;
                      const c = e.fam.clutch;
                      const active = clutchActive(c);
                      const cardStatus = e.fam.chicks.length > 0 ? 'bred' : active ? 'active' : 'fail';
                      const dates = (
                        <span className={`clutch-dates${c.startObsTime ? ' clickable' : ''}`}
                          title="Go to where the eggs/chicks first appeared"
                          onClick={c.startObsTime ? () => onSightingClick(e.box, c.startObsTime) : undefined}>{windowRange(c)}</span>
                      );
                      return (
                        <div key={`${e.seasonYear}-${e.box}-${e.clutchIndex}`} className={`clutch-card ${cardStatus}`}>
                          <div className="clutch-box-row">
                            {e.role === 'parent' ? (<>
                              <span className="muted">with</span>
                              {e.partner
                                ? <PenguinMini scan={e.partner} onClick={() => onBirdClick(e.partner.peng_num || e.partner.pit_id)} />
                                : <span className="muted">partner not identified</span>}
                            </>) : (<>
                              <span className="muted">Parents</span>
                              {e.parents.length > 0
                                ? [...e.parents].sort(scanSortMFC).map((pt: any) => <PenguinMini key={pt.pit_id} scan={pt} onClick={() => onBirdClick(pt.peng_num || pt.pit_id)} />)
                                : <span className="muted">not identified</span>}
                            </>)}
                            <span className="muted">in</span>
                            <a className="bird-chip clickable" href={`/box/${e.box}`} onClick={ev => navClick(ev, () => onBoxClick(e.box))}>Box {e.box}</a>
                            {e.clutchCount > 1 && <span className="clutch-label">{ordinal(e.clutchIndex + 1)} clutch</span>}
                          </div>
                          {c.laidFailed && (
                            <div className="season-issues">
                              <span className={`issue-badge note${c.startObsTime ? ' clickable' : ''}`}
                                title="Go to where the egg/chick was first detected"
                                onClick={c.startObsTime ? () => onSightingClick(e.box, c.startObsTime) : undefined}>{discoveryNote(c)}</span>
                            </div>
                          )}
                          <ClutchBody clutch={c} dates={dates}>
                            <span className="clutch-birds">
                              {e.role === 'parent' ? (<>
                                {[...e.fam.chicks].sort(scanSortMFC).map((ck: any) => (
                                  <PenguinMini key={ck.pit_id} scan={ck} onClick={() => onBirdClick(ck.peng_num || ck.pit_id)} observationDate={offspringDate(ck)} />
                                ))}
                                {Array.from({ length: e.fam.failedEggs }).map((_, j) => (
                                  <OffspringFinal key={`fe${j}`} kind="egg" active={active} />
                                ))}
                                {Array.from({ length: e.fam.plainChicks }).map((_, j) => (
                                  <OffspringFinal key={`pc${j}`} kind="chick" active={active} />
                                ))}
                                {Array.from({ length: e.fam.fledgedUnchipped }).map((_, j) => (
                                  <span key={`fu${j}`} className="scan chick offspring-fledged" title="Last sighting of unchipped chick, presumed fledged">Unchipped</span>
                                ))}
                              </>) : e.siblings.length > 0 && <>
                                <span className="muted">Sibling</span>
                                {[...e.siblings].sort(scanSortMFC).map((sb: any) => (
                                  <PenguinMini key={sb.pit_id} scan={sb} onClick={() => onBirdClick(sb.peng_num || sb.pit_id)} observationDate={offspringDate(sb)} />
                                ))}
                              </>}
                            </span>
                          </ClutchBody>
                        </div>
                      );
                    })}
                  </div>
                </div>
              );
            })}
          </div>}
        </div>
      )}

      {/* Locations with sightings */}
      {sightings.length > 0 && <div className="bird-section">
        <h3 className="collapsible" onClick={() => toggleSection('boxes')}>{expandedSections.boxes ? '▾' : '▸'} Seen in {boxes.length} box{boxes.length !== 1 ? 'es' : ''}</h3>
        {expandedSections.boxes && boxes.map((b: string) => {
          const boxSightings = sightings.filter((s: any) => s.box === b);
          // Who this bird shared the box with, tallied over every visit — the collapsed
          // header's "with <mini> 2x, <mini> 1x". Most-shared first; each mini is stamped
          // with the latest visit they were on, so it shows their stage as last seen.
          const companionMap = new Map<string, { scan: any; count: number; date: string }>();
          let noScan = 0;
          for (const sg of boxSightings) {
            noScan += sg.no_scan || 0;
            for (const sw of sg.seen_with || []) {
              const c = companionMap.get(sw.peng_num);
              if (c) c.count++;
              else companionMap.set(sw.peng_num, { scan: sw, count: 1, date: sg.date });
            }
          }
          const companions = Array.from(companionMap.values())
            .sort((x, y) => y.count - x.count || comparePengNum(x.scan.peng_num, y.scan.peng_num));
          const boxKey = `box-${b}`;
          const boxOpen = !!expandedSections[boxKey];
          return (
            <div key={b} className="obs-card" style={{marginBottom:6}}>
              <div className="box-head collapsible" onClick={() => toggleSection(boxKey)}>
                <span className="partner-toggle">{boxOpen ? '▾' : '▸'}</span>
                {/* Each element sits over its own count: the box over its visit total, each
                    companion over how many of those visits they shared. */}
                <span className="box-count-stack">
                  <a className="bird-chip clickable" href={`/box/${b}`} onClick={e => { e.stopPropagation(); navClick(e, () => onBoxClick(b)); }}>Box {b}</a>
                  <span className="scan-count">{boxSightings.length}x</span>
                </span>
                {(companions.length > 0 || noScan > 0) && <span className="muted box-head-with">with</span>}
                {companions.map((c, ci) => (
                  // stopPropagation so opening a companion's panel doesn't also toggle the box.
                  <span key={c.scan.peng_num} className="box-companion" onClick={e => e.stopPropagation()}>
                    <span className="bc-top">
                      <PenguinMini scan={c.scan} onClick={() => onBirdClick(c.scan.peng_num)} observationDate={c.date} />
                      {(ci < companions.length - 1 || noScan > 0) && <span className="muted">,</span>}
                    </span>
                    <span className="scan-count">{c.count}x</span>
                  </span>
                ))}
                {noScan > 0 && (
                  <span className="box-companion">
                    <span className="bc-top"><span className="scan no-scan">No scan</span></span>
                    <span className="scan-count">{noScan}x</span>
                  </span>
                )}
              </div>
              {boxOpen && boxSightings.map((sg: any, i: number) => (
                <div key={i} style={{marginBottom:3}}>
                  <div className="obs-nums" style={{fontSize:11}}>
                    <DateLink date={sg.date} onDayClick={() => onSightingClick(b, sg.date)} />
                    {((sg.seen_with || []).length > 0 || (sg.no_scan || 0) > 0) && <span className="muted">with</span>}
                    {[...(sg.seen_with || [])].sort(scanSortMFC).map((sw: any) => (
                      <PenguinMini key={sw.peng_num} scan={sw} onClick={() => onBirdClick(sw.peng_num)} observationDate={sg.date} />
                    ))}
                    {Array.from({ length: sg.no_scan || 0 }).map((_, k) => (
                      <span key={`ns${k}`} className="scan no-scan">No scan</span>
                    ))}
                    {(() => { const ds = displayStatusOrPrev(sg, sg.box); return ds && ds !== 'NO' && <span className={`badge ${DARK_TEXT_STATUSES.has(ds)?'bordered':''}`} style={{background:STATUS_COLORS[ds]||'#ccc',color:DARK_TEXT_STATUSES.has(ds)?'#333':'#fff'}}>{ds}</span>; })()}
                  </div>
                  {sg.notes && <div className="obs-notes">{sg.notes}</div>}
                </div>
              ))}
            </div>
          );
        })}
      </div>}

      {/* Shared sightings — split by season; sightings inside a breeding window sit in a
          black box tagged with the window dates. "No scan" birds group as one stand-in. */}
      {partners.length > 0 && (
        <div className="bird-section">
          <h3 className="collapsible" onClick={() => toggleSection('partners')}>{expandedSections.partners ? '▾' : '▸'} Shared sightings ({partners.length})</h3>
          {expandedSections.partners && <p className="muted">Birds seen in the same box at the same time &middot; "No scan" = unscanned birds present</p>}
          {expandedSections.partners && partnersByRecent.map((pt: any, pi: number) => {
            const partnerRow = (s: any, i: number) => (
              <a key={i} className="partner-row clickable" href={`/box/${s.box}`} onClick={e => navClick(e, () => onSightingClick(s.box, s.date))}>
                <DateLink date={s.date} onDayClick={onDayClick} />
                <span className="bird-chip">Box {s.box}</span>
                {s.eggs > 0 && <span>{'🥚'.repeat(Math.min(s.eggs, 4))}</span>}
                {s.chicks > 0 && <span>{'🐣'.repeat(Math.min(s.chicks, 4))}</span>}
                {(() => { const ds = displayStatusOrPrev(s, s.box); return ds && ds !== 'NO' && <span className={`badge ${DARK_TEXT_STATUSES.has(ds)?'bordered':''}`} style={{background:STATUS_COLORS[ds]||'#ccc',color:DARK_TEXT_STATUSES.has(ds)?'#333':'#fff'}}>{ds}</span>; })()}
                {[...(s.also_seen || [])].sort(scanSortMFC).map((sw: any) => (
                  <PenguinMini key={sw.peng_num} scan={sw} onClick={() => onBirdClick(sw.peng_num)} observationDate={s.date} />
                ))}
              </a>
            );
            const bySeason = new Map<string, any[]>();
            for (const s of pt.sightings) {
              const label = getSeasonLabel(parseDate(s.date));
              if (!bySeason.has(label)) bySeason.set(label, []);
              bySeason.get(label)!.push(s);
            }
            const seasonList = Array.from(bySeason.entries()).sort((a, b) => b[0].localeCompare(a[0]));
            const partnerKey = `partner-${pt.is_no_scan ? 'noscan' : pt.peng_num}`;
            const partnerOpen = !!expandedSections[partnerKey];
            return (
              <div key={pi} className="partner-card">
                <div className="partner-head collapsible" onClick={() => toggleSection(partnerKey)}>
                  <span className="partner-toggle">{partnerOpen ? '▾' : '▸'}</span>
                  <span className="muted">{pt.sightings.length} shared sighting{pt.sightings.length !== 1 ? 's' : ''} with</span>
                  {pt.is_no_scan
                    ? <span className="scan no-scan">No scan</span>
                    : <PenguinMini scan={pt} onClick={() => onBirdClick(pt.peng_num)} observationDate={pt.sightings[0]?.date} />}
                </div>
                {partnerOpen && seasonList.map(([label, seasonSightings]) => {
                  const windowGroups = new Map<string, { win: any; rows: any[] }>();
                  const loose: any[] = [];
                  for (const s of seasonSightings) {
                    const win = windowFor(s.box, s.date);
                    if (win) {
                      const gkey = `${s.box}|${win.windowStart}`;
                      if (!windowGroups.has(gkey)) windowGroups.set(gkey, { win, rows: [] });
                      windowGroups.get(gkey)!.rows.push(s);
                    } else loose.push(s);
                  }
                  const groups = Array.from(windowGroups.values()).sort((a, b) => b.win.windowStart - a.win.windowStart);
                  return (
                    <div key={label} className="partner-season">
                      <div className="partner-season-label">{seasonRange(label)}</div>
                      {groups.map((g, gi) => {
                        const fam = g.win.fam;
                        return (
                        <div key={`w${gi}`} className="partner-window-box">
                          <div className="partner-window-head">
                            {/* Offspring at their final life stage: chipped chick →
                                PenguinMini, chick never chipped → red-✕ 🐣, egg that
                                never hatched → red-✕ egg. */}
                            <span className="partner-window-offspring">
                              {[...fam.chicks].sort(scanSortMFC).map((ck: any) => (
                                <PenguinMini key={ck.pit_id} scan={ck} onClick={() => onBirdClick(ck.peng_num || ck.pit_id)} observationDate={ck.chip_date ? chickContextDate(ck.chip_date) : undefined} />
                              ))}
                              {Array.from({ length: fam.plainChicks }).map((_, j) => (
                                <OffspringFinal key={`pc${j}`} kind="chick" active={clutchActive(fam.clutch)} />
                              ))}
                              {Array.from({ length: fam.failedEggs }).map((_, j) => (
                                <OffspringFinal key={`fe${j}`} kind="egg" active={clutchActive(fam.clutch)} />
                              ))}
                            </span>
                            <a className="partner-window-dates clickable" href={`/box/${g.rows[0].box}`}
                              title="Go to the nest at the start of the breeding window"
                              onClick={ev => navClick(ev, () => onSightingClick(g.rows[0].box, g.win.startObsTime))}>
                              {windowRange(fam.clutch)}
                            </a>
                          </div>
                          <ClutchPredictions clutch={fam.clutch} />
                          <div className="partner-sightings">{g.rows.map(partnerRow)}</div>
                        </div>
                        );
                      })}
                      {loose.length > 0 && <div className="partner-sightings">{loose.map(partnerRow)}</div>}
                    </div>
                  );
                })}
              </div>
            );
          })}
        </div>
      )}


      {/* Sighting history */}
      {sightings.length > 0 && <div className="bird-section">
        <h3 className="collapsible" onClick={() => toggleSection('sightings')}>{expandedSections.sightings ? '▾' : '▸'} Sighting history ({sightings.length})</h3>
        {expandedSections.sightings && (() => {
          // Sightings arrive newest-first; grouping preserves that order within and across
          // seasons, so each season header introduces the block of visits below it.
          const bySeason = new Map<string, any[]>();
          for (const s of sightings) {
            const label = getSeasonLabel(parseDate(s.date));
            if (!bySeason.has(label)) bySeason.set(label, []);
            bySeason.get(label)!.push(s);
          }
          return Array.from(bySeason.entries()).sort((a, b) => b[0].localeCompare(a[0])).map(([label, rows]) => (
            <div key={label} className="sight-season">
              <div className="sight-season-label">{seasonRange(label)} <span className="muted">{rows.length} sighting{rows.length !== 1 ? 's' : ''}</span></div>
              {rows.map((s: any, i: number) => s.source === 'chip' ? (
                <ChipCard key={i} date={s.date} box={s.box} onBoxClick={onBoxClick} onDayClick={onDayClick} onBirdClick={onBirdClick}
                  chipBy={s.chip_by}
                  scan={{ peng_num: p.peng_num, pit_id: s.pit_id || activeChip?.pit_id, sex: p.sex, chip_date: s.date, chipped_as_adult: p.chipped_as_adult, chick_size_code: p.chick_size_code, chip_by: s.chip_by, is_rechip: s.is_rechip }} />
              ) : (
                <div key={i} className="obs-card">
                  <div className="obs-top">
                    <b><DateLink date={s.date} onDayClick={() => onSightingClick(s.box, s.date)} /></b>
                    <a className="bird-chip clickable" href={`/box/${s.box}`} onClick={e => navClick(e, () => onSightingClick(s.box, s.date))}>Box {s.box}</a>
                  </div>
                  <div className="obs-nums">
                    {s.adults === 0 && s.eggs === 0 && s.chicks === 0 && <span className="muted">Empty</span>}
                    {s.adults > 0 && <span>{'\uD83D\uDC27'.repeat(Math.min(s.adults, 6))}</span>}
                    {s.eggs > 0 && <span>{'\uD83E\uDD5A'.repeat(Math.min(s.eggs, 6))}</span>}
                    {s.chicks > 0 && <span>{'\uD83D\uDC23'.repeat(Math.min(s.chicks, 6))}</span>}
                    {(() => { const ds = displayStatusOrPrev(s, s.box); return ds && <span className={`badge ${DARK_TEXT_STATUSES.has(ds)?'bordered':''}`} style={{background:STATUS_COLORS[ds]||'#ccc',color:DARK_TEXT_STATUSES.has(ds)?'#333':'#fff'}}>{ds}</span>; })()}
                    {((s.seen_with || []).length > 0 || (s.no_scan || 0) > 0) && <span className="muted">with</span>}
                    {[...(s.seen_with || [])].sort(scanSortMFC).map((sw: any) => (
                      <PenguinMini key={sw.peng_num} scan={sw} onClick={() => onBirdClick(sw.peng_num)} observationDate={s.date} />
                    ))}
                    {Array.from({ length: s.no_scan || 0 }).map((_, k) => (
                      <span key={`ns${k}`} className="scan no-scan">No scan</span>
                    ))}
                  </div>
                  {s.notes && <div className="obs-notes">{s.notes}</div>}
                </div>
              ))}
            </div>
          ));
        })()}
      </div>}
    </div>
  );
}

function PenguinSearch({ penguins, search, onSearchChange, onBirdClick }: {
  penguins: any[]; search: string; onSearchChange: (s:string)=>void; onBirdClick: (tag:string)=>void;
}) {
  const [open, setOpen] = useState(false);
  const filtered = useMemo(() => {
    if (search.length === 0) return { exact: [] as any[], pit: [] as any[] };
    const s = search.toUpperCase();
    // A bare number is the one on screen — this colony's bird — so match it in full form.
    const full = fullPengNum(s);
    const exact = penguins.filter(p => p.peng_num && p.peng_num === full);
    const pit = penguins.filter(p => p.pit_id && p.pit_id.toUpperCase().includes(s) && !(p.peng_num && p.peng_num === full));
    return { exact, pit };
  }, [penguins, search]);

  const handleKey = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') {
      const first = filtered.exact[0] || filtered.pit[0];
      if (first) { onBirdClick(first.peng_num || first.pit_id); onSearchChange(''); setOpen(false); }
    } else if (e.key === 'Escape') { setOpen(false); }
  };

  return (
    <div className="penguin-search">
      <input
        type="text"
        placeholder="Penguin"
        value={search}
        onChange={e => { onSearchChange(e.target.value.replace(/[^0-9A-Za-z]/g, '')); setOpen(true); }}
        onFocus={() => setOpen(true)}
        onBlur={() => setTimeout(() => setOpen(false), 200)}
        onKeyDown={handleKey}
        className="penguin-search-input"
      />
      {open && (filtered.exact.length > 0 || filtered.pit.length > 0) && (
        <div className="penguin-results">
          {filtered.exact.map((p: any) => (
            <div key={p.peng_num} className={`penguin-result clickable ${penguinSexClass(p.sex, p.chip_date, p.chipped_as_adult)}`} onClick={() => { onBirdClick(p.peng_num); onSearchChange(''); }}>
              <span className="pr-tag"><PenguinMini scan={p} onClick={() => { onBirdClick(p.peng_num); onSearchChange(''); }} /></span>
              <span className="pr-meta">
                                                <span className="pr-stat">{p.total_scans} scan{p.total_scans>1?'s':''}</span>
              </span>
            </div>
          ))}
          {filtered.exact.length > 0 && filtered.pit.length > 0 && <div className="muted small" style={{padding:'2px 8px', borderTop:'1px solid #eee'}}>PIT ID matches:</div>}
          {filtered.pit.slice(0, 20).map((p: any) => (
            <div key={p.pit_id} className={`penguin-result clickable ${penguinSexClass(p.sex, p.chip_date, p.chipped_as_adult)}`} onClick={() => { onBirdClick(p.peng_num || p.pit_id); onSearchChange(''); }}>
              <span className="pr-tag"><PenguinMini scan={p} onClick={() => { onBirdClick(p.peng_num || p.pit_id); onSearchChange(''); }} /></span>
              <span className="pr-meta">
                                                <span className="pr-stat">{p.total_scans} scan{p.total_scans>1?'s':''}</span>
              </span>
            </div>
          ))}
          {filtered.pit.length > 20 && <div className="muted" style={{padding:'4px 8px'}}>+{filtered.pit.length - 20} more</div>}
        </div>
      )}
      {open && search.length > 0 && filtered.exact.length === 0 && filtered.pit.length === 0 && (
        <div className="penguin-results"><div className="muted" style={{padding:'8px'}}>No penguins match "{search}"</div></div>
      )}
    </div>
  );
}

/** Tick beside the box heading: green = watched box, grey = not. Editors click to toggle. */
function WatchedTick({ location, token, canEdit }: { location: any; token?: string; canEdit: boolean }) {
  const [watched, setWatched] = useState<boolean>(!!Number(location.watched));
  useEffect(() => { setWatched(!!Number(location.watched)); }, [location.location_id, location.watched]);
  const toggle = async () => {
    if (!canEdit || !token) return;
    const next = !watched;
    setWatched(next);
    try {
      await updateRecord(token, 'observation_locations', location.location_id, { watched: next ? 1 : 0 });
    } catch (e: any) {
      setWatched(!next);
      alert('Failed to update watched flag: ' + (e?.message || e));
    }
  };
  return (
    <span className={`watched-tick${watched ? ' on' : ''}${canEdit ? ' clickable' : ''}`}
      title={watched ? 'Watched box' : 'Not watched'} onClick={toggle}>✓</span>
  );
}

// ============ Breeding verification (human ground truth) ============

type VerifyTick = 'grey' | 'green' | 'red';
type Verdict = 'accepted' | 'rejected' | null;

/** The pair the algorithm currently assigns to this family, as peng_nums (null per empty slot). */
function detectedPair(fam: BoxFamily): { male: string | null; female: string | null } {
  const nm = (k: string) => k ? (fam.parents.find((p: any) => p.pit_id.slice(-8) === k)?.peng_num ?? null) : null;
  return { male: nm(fam.male), female: nm(fam.female) };
}
const pengSetEq = (a: Set<string>, b: Set<string>) => a.size === b.size && [...a].every(x => b.has(x));

interface ClutchVerify {
  verification: any | null;
  adultsVerdict: Verdict; adultsMatch: boolean;
  chicksVerdict: Verdict; chicksMatch: boolean;
  offspringVerifiable: boolean;   // window detected closed
  tick: VerifyTick;
}

/** Aggregate tick state for one clutch. Each half is accepted / rejected / unreviewed.
 *  grey = a verifiable half is still unreviewed; green = every verifiable half accepted AND still
 *  matching the algorithm; red = any half rejected, or an accepted half no longer matching (drift).
 *  Adults match = detected pair equals the accepted pair on both slots; offspring match = detected
 *  chipped-chick set AND all three counts equal the accepted snapshot. */
function computeClutchVerify(fam: BoxFamily, verification: any | null): ClutchVerify {
  const offspringVerifiable = !clutchActive(fam.clutch);
  const det = detectedPair(fam);
  const v = verification;
  const adultsVerdict: Verdict = v?.adults_verdict ?? null;
  const adultsMatch = adultsVerdict === 'accepted'
    && (v.male_peng_num ?? null) === det.male
    && (v.female_peng_num ?? null) === det.female;
  const chicksVerdict: Verdict = v?.chicks_verdict ?? null;
  const detChicks = new Set<string>(fam.chicks.map((c: any) => c.peng_num).filter(Boolean));
  const verChicks = new Set<string>(v?.chicks || []);
  const chicksMatch = chicksVerdict === 'accepted'
    && pengSetEq(detChicks, verChicks)
    && Number(v.dead_eggs) === fam.failedEggs
    && Number(v.dead_chicks) === fam.plainChicks
    && Number(v.fledged_unchipped) === fam.fledgedUnchipped;
  const red = adultsVerdict === 'rejected' || chicksVerdict === 'rejected'
    || (adultsVerdict === 'accepted' && !adultsMatch)
    || (chicksVerdict === 'accepted' && !chicksMatch);
  const green = adultsVerdict === 'accepted' && adultsMatch
    && (!offspringVerifiable || (chicksVerdict === 'accepted' && chicksMatch));
  const tick: VerifyTick = red ? 'red' : green ? 'green' : 'grey';
  return { verification: v, adultsVerdict, adultsMatch, chicksVerdict, chicksMatch, offspringVerifiable, tick };
}

/**
 * Clutches where a human verdict and the current detection disagree, split by what the
 * disagreement means. A rejection is a standing statement that the detection is wrong there.
 * Drift is the opposite problem: an acceptance recorded against a detection the algorithm has
 * since moved away from, so the stored truth describes a window that no longer exists in that
 * shape — including the case where no window starts at that observation any more.
 *
 * Recomputed from the cache the same way the box view does it, so a row here is exactly a red
 * tick on that box's breeding history.
 */
function computeVerifyConflicts(): { rejected: any[]; drifted: any[] } {
  const rejected: any[] = [], drifted: any[] = [];
  const named = (v: any) => v.adults_reviewed_by_name || v.chicks_reviewed_by_name || '';
  for (const { box, detail, families } of allColonyBoxes()) {
    const vers: any[] = detail?.verifications || [];
    if (!vers.length) continue;
    const anchored = new Set<number>();
    for (const season of families) {
      for (const fam of season.families as any[]) {
        const anchor = fam.clutch.startObsId;
        const v = anchor != null ? vers.find((x: any) => x.observation_id === anchor) : null;
        if (!v) continue;
        anchored.add(anchor);
        const state = computeClutchVerify(fam, v);
        const time = fam.clutch.startObsTime;
        const base = {
          box, season: seasonRange(String(season.label)),
          obs_date: time ? toNzDateStr(time) : '',
          by: named(v),
          _href: time ? `/?box=${encodeURIComponent(box)}&obs=${encodeURIComponent(time)}` : undefined,
        };
        const refused = [
          state.adultsVerdict === 'rejected' ? 'pair' : null,
          state.chicksVerdict === 'rejected' ? 'offspring' : null,
        ].filter(Boolean);
        if (refused.length) {
          rejected.push({ ...base, what: refused.join(' + '),
            note: [...new Set([v.adults_note, v.chicks_note].filter(Boolean))].join(' · ') });
          continue;
        }
        const moved = [
          state.adultsVerdict === 'accepted' && !state.adultsMatch ? 'pair' : null,
          state.chicksVerdict === 'accepted' && !state.chicksMatch ? 'offspring' : null,
        ].filter(Boolean);
        if (moved.length) drifted.push({ ...base, what: moved.join(' + '), why: 'detection changed since it was accepted' });
      }
    }
    // Verdicts with no window left to attach to. Only acceptances are a problem: a rejection
    // whose window has since disappeared is settled — the reviewer said the detection was
    // wrong and the algorithm now agrees there is nothing there.
    for (const v of vers) {
      if (anchored.has(v.observation_id)) continue;
      if (v.adults_verdict === 'rejected' || v.chicks_verdict === 'rejected') continue;
      if (v.adults_verdict !== 'accepted' && v.chicks_verdict !== 'accepted') continue;
      const time = v.anchor_time;
      const row = {
        box,
        season: time ? seasonRange(getSeasonLabel(parseDate(time))) : '',
        obs_date: time ? toNzDateStr(time) : '',
        by: named(v),
        _href: time ? `/?box=${encodeURIComponent(box)}&obs=${encodeURIComponent(time)}` : undefined,
      };
      drifted.push({ ...row, what: 'whole window',
        why: v.anchor_deleted
          ? 'the observation it was recorded against was deleted'
          : 'no breeding window starts here any more' });
    }
  }
  const bySeason = (a: any, b: any) => String(b.obs_date).localeCompare(String(a.obs_date));
  return { rejected: rejected.sort(bySeason), drifted: drifted.sort(bySeason) };
}

/** Tick beside a breeding-window card. Editors always click (to review); viewers click only when
 *  there's a recorded verdict to view. */
function BreedingVerifyTick({ state, canEdit, onOpen }: { state: ClutchVerify; canEdit: boolean; onOpen: (e: React.MouseEvent) => void }) {
  const clickable = canEdit || !!state.verification;
  const title = state.tick === 'green' ? 'Verified — algorithm still agrees'
    : state.tick === 'red' ? 'Rejected or no longer detected — click to review'
    : 'Click to review this breeding window';
  return (
    <span className={`verify-tick vt-${state.tick}${clickable ? ' clickable' : ''}`} title={title}
      onClick={clickable ? onOpen : undefined}>{state.tick === 'red' ? '✗' : '✓'}</span>
  );
}

/** Accept / reject each half of a clutch's detection, anchored near the clicked tick. Accept
 *  snapshots the detected data as truth; reject records a note. Birds render as PenguinMinis. */
function BreedingVerifyModal({ pos, fam, state, box, token, canEdit, onBirdClick, onClose, onChanged }: {
  pos: { x: number; y: number }; fam: BoxFamily; state: ClutchVerify; box: string;
  token?: string; canEdit: boolean; onBirdClick: (tag: string) => void; onClose: () => void; onChanged: () => void;
}) {
  const obsId = fam.clutch.startObsId;
  const v = state.verification;
  const [rejecting, setRejecting] = useState<'adults' | 'chicks' | null>(null);
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');
  if (obsId == null) return null;

  const male = fam.parents.find((p: any) => p.pit_id.slice(-8) === fam.male);
  const female = fam.parents.find((p: any) => p.pit_id.slice(-8) === fam.female);

  const run = (body: Record<string, any>) => async () => {
    setBusy(true); setErr('');
    try {
      const r = await saveVerification(token!, { observation_id: obsId, ...body });
      if (r && r.error) { setErr(r.error); return; }
      setRejecting(null); setNote(''); onChanged();
    } catch (e: any) { setErr(e?.message || String(e)); }
    finally { setBusy(false); }
  };
  const acceptAdults = run({ half: 'adults', verdict: 'accepted', male_peng_num: detectedPair(fam).male, female_peng_num: detectedPair(fam).female });
  const acceptChicks = run({ half: 'chicks', verdict: 'accepted', chicks: fam.chicks.map((c: any) => c.peng_num).filter(Boolean),
    dead_eggs: fam.failedEggs, dead_chicks: fam.plainChicks, fledged_unchipped: fam.fledgedUnchipped });
  const doReject = (half: 'adults' | 'chicks') => run({ half, verdict: 'rejected', note });
  const doClear = (half: 'adults' | 'chicks') => run({ half, verdict: 'clear' });

  const fmtD = (s?: string) => s ? parseDate(s).toLocaleDateString('en-NZ', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'Pacific/Auckland' }) : '';
  const mini = (b: any) => b ? <PenguinMini key={b.pit_id} scan={b} onClick={() => onBirdClick(b.peng_num || b.pit_id)} /> : null;

  const half = (name: 'adults' | 'chicks', verdict: Verdict, match: boolean, reviewedBy?: string, reviewedAt?: string, noteText?: string, accept?: () => void) => (<>
    {rejecting === name ? (
      <div className="verify-reject">
        <input autoFocus placeholder="Why is this wrong?" value={note} onChange={e => setNote(e.target.value)} />
        <div className="verify-actions">
          <button className="verify-danger" disabled={busy || !note.trim()} onClick={doReject(name)}>Confirm reject</button>
          <button disabled={busy} onClick={() => { setRejecting(null); setNote(''); }}>Cancel</button>
        </div>
      </div>
    ) : (<>
      {verdict && (
        <div className={`verify-by ${verdict === 'accepted' && match ? 'vt-green' : 'vt-red'}`}>
          {verdict === 'accepted' ? (match ? '✓ accepted' : '✗ accepted, no longer detected') : '✗ rejected'}
          {reviewedBy ? ` by ${reviewedBy}` : ''}{reviewedAt ? ` · ${fmtD(reviewedAt)}` : ''}
          {verdict === 'rejected' && noteText ? <div className="verify-note">{'“'}{noteText}{'”'}</div> : null}
        </div>
      )}
      {canEdit && (
        <div className="verify-actions">
          {verdict !== 'accepted' && accept && <button disabled={busy} onClick={accept}>Accept</button>}
          {verdict !== 'rejected' && <button className="verify-danger" disabled={busy} onClick={() => { setRejecting(name); setNote(''); }}>Reject</button>}
          {verdict && <button className="verify-clear" disabled={busy} onClick={doClear(name)}>Clear</button>}
        </div>
      )}
    </>)}
  </>);

  return createPortal((
    <>
      <div className="verify-backdrop" onClick={onClose} />
      <div className="verify-modal" style={{ left: pos.x, top: pos.y }} onClick={e => e.stopPropagation()}>
        <div className="verify-head"><b>Verify breeding {'—'} Box {box}</b><span className="verify-close clickable" onClick={onClose}>{'✕'}</span></div>
        {err && <div className="verify-err">{err}</div>}

        <div className="verify-section">
          <div className="verify-lbl">Adults</div>
          <div className="verify-birds">{male || female ? <>{mini(male)}{mini(female)}</> : <span className="muted">no pair detected</span>}</div>
          {half('adults', state.adultsVerdict, state.adultsMatch, v?.adults_reviewed_by_name, v?.adults_reviewed_at, v?.adults_note, acceptAdults)}
        </div>

        {state.offspringVerifiable ? (
          <div className="verify-section">
            <div className="verify-lbl">Offspring</div>
            <div className="verify-birds">{fam.chicks.length > 0 ? [...fam.chicks].sort(scanSortMFC).map(mini) : <span className="muted">no chipped chicks</span>}</div>
            <div className="verify-counts-view muted">{fam.failedEggs} dead egg{fam.failedEggs !== 1 ? 's' : ''} {'·'} {fam.plainChicks} dead chick{fam.plainChicks !== 1 ? 's' : ''} {'·'} {fam.fledgedUnchipped} fledged unchipped</div>
            {half('chicks', state.chicksVerdict, state.chicksMatch, v?.chicks_reviewed_by_name, v?.chicks_reviewed_at, v?.chicks_note, acceptChicks)}
          </div>
        ) : (
          <div className="verify-section muted">Offspring can be reviewed once the breeding window has closed.</div>
        )}
      </div>
    </>
  ), document.body);
}

/** Parse flexible date input into YYYY-MM-DD. Accepts d/m/yy, dd/mm/yyyy, d-m-yy, d m yy, yyyy-mm-dd, yy-m-d etc. */
function parseDateInput(input: string): string | null {
  const s = input.trim();
  if (!s) return null;
  const MONTHS: Record<string, number> = { jan:1, feb:2, mar:3, apr:4, may:5, jun:6, jul:7, aug:8, sep:9, oct:10, nov:11, dec:12 };
  const parseMonth = (p: string): number => {
    const n = parseInt(p);
    if (!isNaN(n)) return n;
    const lc = p.toLowerCase();
    for (const [name, num] of Object.entries(MONTHS)) { if (name.startsWith(lc) || lc.startsWith(name)) return num; }
    return NaN;
  };

  // Split on /, -, space, or .
  const parts = s.split(/[\/\-\.\s]+/);
  if (parts.length !== 3) return null;

  let day: number, month: number, year: number;

  // Detect format: if first part is 4 digits, it's yyyy-mm-dd
  if (parts[0].length === 4 && !isNaN(parseInt(parts[0]))) {
    year = parseInt(parts[0]); month = parseMonth(parts[1]); day = parseInt(parts[2]);
  } else if (parts[2].length === 4 && !isNaN(parseInt(parts[2]))) {
    // d/m/yyyy or d/mon/yyyy
    day = parseInt(parts[0]); month = parseMonth(parts[1]); year = parseInt(parts[2]);
  } else {
    // Ambiguous short year: assume d/m/yy or d/mon/yy
    day = parseInt(parts[0]); month = parseMonth(parts[1]); year = parseInt(parts[2]);
    if (!isNaN(year) && year < 100) year += 2000;
  }

  if (isNaN(day) || isNaN(month) || isNaN(year)) return null;
  if (month < 1 || month > 12 || day < 1 || day > 31) return null;

  return `${year}-${String(month).padStart(2,'0')}-${String(day).padStart(2,'0')}`;
}

/** Dates matching a human date query ("28 dec", "5/9", "FM 3 24"), newest first. Shared by the
 *  date search and the unified search so both read the same input the same way. */
function matchDateQuery(dates: string[], search: string, registeredFmDates: Map<string, { season: number; number: number; partial: boolean }>): string[] {
    if (!search.trim()) return [];

    // FM query against the book lookup tables: "FM3" / "FM 3" lists every season's Full
    // Monitor #3; adding a season year ("FM 3 24", "FM 24 3", "FM 2024 3", "FM 3 2024")
    // narrows to that single monitor. A token ≥ 20 reads as a year, the other as the number.
    const fmQ = search.trim().match(/^fm[\s\/\-\.]*(\d{1,4})(?:[\s\/\-\.]+(\d{1,4}))?$/i);
    if (fmQ) {
      const a = parseInt(fmQ[1]);
      const b = fmQ[2] !== undefined ? parseInt(fmQ[2]) : null;
      const toYear = (n: number) => n >= 2000 ? n : (n >= 20 && n < 100 ? n + 2000 : null);
      const hits: string[] = [];
      for (const [day, fm] of registeredFmDates) {
        const ok = b === null
          ? fm.number === a
          : (toYear(a) === fm.season && fm.number === b) || (toYear(b) === fm.season && fm.number === a);
        if (ok) hits.push(day);
      }
      return hits.sort((x, y) => y.localeCompare(x)).slice(0, 12);
    }

    const parsed = parseDateInput(search);
    const MONTHS: Record<string, number> = { jan:1, feb:2, mar:3, apr:4, may:5, jun:6, jul:7, aug:8, sep:9, oct:10, nov:11, dec:12 };
    const matchMonths = (s: string): number[] => {
      if (!s || s.length < 1) return [];
      const lc = s.toLowerCase();
      return Object.entries(MONTHS).filter(([name]) => name.startsWith(lc) || lc.startsWith(name)).map(([, num]) => num);
    };

    return dates.filter(d => {
      const [yr, mo, dy] = d.split('-').map(Number);

      // Exact full date match
      if (parsed && d === parsed) return true;

      // Match against formatted display (e.g. "5 Sep 2025") using word boundary
      const display = formatDate(d).toLowerCase();
      const terms = search.toLowerCase().trim();
      // Use regex word boundary so "2 may" doesn't match "12 may"
      try { if (new RegExp('\\b' + terms.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\b', 'i').test(display)) return true; } catch {}
      if (display === terms) return true;

      // Split input into parts
      const parts = search.split(/[\/\-\.\s]+/).filter(Boolean);

      if (parts.length === 1) {
        const p = parts[0].toLowerCase();
        const n = parseInt(p);
        // Single number: match day or month
        if (!isNaN(n)) {
          if (n === dy || n === mo) return true;
          // 2-digit year
          if (n >= 20 && n < 100 && n + 2000 === yr) return true;
          // 4-digit year
          if (n === yr) return true;
        }
        // Month name (partial: "j" matches jan/jun/jul)
        if (matchMonths(p).includes(mo)) return true;
      }

      if (parts.length === 2) {
        const [a, b] = parts.map(p => p.toLowerCase());
        const na = parseInt(a), nb = parseInt(b);

        // Resolve month names (partial match, may return multiple)
        const ma = matchMonths(a);
        const mb = matchMonths(b);

        // year + month: "2025 12", "25 12", "25 dec"
        if (!isNaN(na) && (na >= 2000 || (na >= 20 && na < 100))) {
          const year = na >= 2000 ? na : na + 2000;
          if (year === yr) {
            if (!isNaN(nb) && nb === mo) return true;
            if (mb.includes(mo)) return true;
          }
        }
        // month + year: "12 2025", "dec 25"
        if (!isNaN(nb) && (nb >= 2000 || (nb >= 20 && nb < 100))) {
          const year = nb >= 2000 ? nb : nb + 2000;
          if (year === yr) {
            if (!isNaN(na) && na === mo) return true;
            if (ma.includes(mo)) return true;
          }
        }
        // d/m: "5/9", "28/12"
        if (!isNaN(na) && !isNaN(nb) && na <= 31 && nb <= 12) {
          if (na === dy && nb === mo) return true;
        }
        // month + day: "dec 28"
        if (ma.length > 0 && !isNaN(nb) && ma.includes(mo) && nb === dy) return true;
        // day + month: "28 dec", "13 j"
        if (mb.length > 0 && !isNaN(na) && mb.includes(mo) && na === dy) return true;
      }

      if (parts.length === 3) {
        if (parsed) return d.startsWith(parsed);
        const [p0, p1, p2] = parts.map(p => p.toLowerCase());
        const n0 = parseInt(p0), n1 = parseInt(p1), n2 = parseInt(p2);
        const m1 = matchMonths(p1);

        // day month year: "20 f 2024", "20 feb 24"
        if (!isNaN(n0) && m1.length > 0) {
          const yearVal = !isNaN(n2) ? (n2 < 100 ? n2 + 2000 : n2) : null;
          if (yearVal && n0 === dy && m1.includes(mo) && yearVal === yr) return true;
        }
        // day month year: "20 2 2024" (numeric month)
        if (!isNaN(n0) && !isNaN(n1) && !isNaN(n2) && n0 <= 31 && n1 <= 12) {
          const yearVal = n2 < 100 ? n2 + 2000 : n2;
          if (n0 === dy && n1 === mo && yearVal === yr) return true;
        }
        // year month day: "2024 feb 20"
        if (!isNaN(n0) && m1.length > 0 && !isNaN(n2)) {
          const yearVal = n0 < 100 ? n0 + 2000 : n0;
          if (yearVal === yr && m1.includes(mo) && n2 === dy) return true;
        }
      }

      return false;
    }).sort((a, b) => {
      // Exact parse match first, then most recent
      if (parsed) {
        if (a === parsed) return -1;
        if (b === parsed) return 1;
      }
      return b.localeCompare(a);
    }).slice(0, 12);
}

/** Bring the page back to the top so a focused field (and its results) is on screen. Walks the
 *  scrollable ancestors too — the day view is a fixed overlay that scrolls itself, and the
 *  window scroll never reaches it. */
function scrollToTop(el: HTMLElement | null) {
  window.scrollTo({ top: 0, behavior: 'smooth' });
  for (let n = el?.parentElement; n; n = n.parentElement) {
    if (n.scrollTop > 0 && n.scrollHeight > n.clientHeight) n.scrollTo({ top: 0, behavior: 'smooth' });
  }
}

/**
 * One box searching everything the cache holds, in precedence order:
 * box > peng# > date > PIT ID > penguin notes > observation notes > day notes.
 *
 * Each kind renders as the thing it will take you to — a box pill, a PenguinMini, a date link,
 * a whole ObsCard — rather than as a line of text describing it. Query syntax comes from
 * parseSearchTerms: `a|b` matches either, and "quotes" keep spaces and commas ("BS, MV").
 */
function UnifiedSearch({ dates, onBoxClick, onBirdClick, onDayClick, onObsClick, onFocusChange }: {
  dates: string[];
  onBoxClick: (box: string) => void;
  onBirdClick: (tag: string) => void;
  onDayClick: (day: string) => void;
  onObsClick: (box: string, time: string) => void;
  onFocusChange?: (focused: boolean, centerDate: string, id: string) => void;
}) {
  const [search, setSearch] = useState('');
  const [open, setOpen] = useState(false);
  const { registeredFmDates } = useContext(DateTooltipCtx);
  const dbVersion = useDbVersion();
  const local = useMemo(() => searchLocal(search), [search, dbVersion]);
  const dateHits = useMemo(() => matchDateQuery(dates, search, registeredFmDates).slice(0, 8), [dates, search, registeredFmDates]);

  // Every result clears the box on the way out, so the dropdown never sits over the thing it
  // just navigated to.
  const go = (fn: () => void) => () => { fn(); setSearch(''); setOpen(false); };

  // Every result as one flat list in the order they're rendered, so the arrow keys can walk
  // them without caring which group a result came from. Keys tie a list entry to its element.
  const flat = useMemo(() => {
    const out: { key: string; run: () => void }[] = [];
    for (const b of local.boxes) out.push({ key: `bx:${b}`, run: () => onBoxClick(b) });
    for (const p of local.pengs) out.push({ key: `pg:${p.peng_num}`, run: () => onBirdClick(p.peng_num) });
    for (const d of dateHits) out.push({ key: `dt:${d}`, run: () => onDayClick(d) });
    for (const p of local.pits) out.push({ key: `pt:${p.peng_num}`, run: () => onBirdClick(p.peng_num || p.pit_id) });
    for (const { peng } of local.pengNotes) out.push({ key: `pn:${peng.peng_num}`, run: () => onBirdClick(peng.peng_num) });
    for (const o of local.obsNotes) out.push({ key: `ob:${o.observation_id}`, run: () => onObsClick(o.box, o.observation_time_utc) });
    for (const { date } of local.dateNotes) out.push({ key: `dn:${date}`, run: () => onDayClick(date) });
    return out;
  }, [local, dateHits, onBoxClick, onBirdClick, onDayClick, onObsClick]);

  // -1 is "nothing stepped to yet", where Enter still takes the top result.
  const [cursor, setCursor] = useState(-1);
  useEffect(() => { setCursor(-1); }, [search]);
  const focusKey = cursor >= 0 ? flat[cursor]?.key : null;
  const cls = (key: string, base: string) => `${base}${focusKey === key ? ' uni-focused' : ''}`;
  const listRef = useRef<HTMLDivElement>(null);
  // Keep the stepped-to result on screen — the list is taller than the dropdown.
  useEffect(() => {
    if (!focusKey) return;
    listRef.current?.querySelector(`[data-uni="${focusKey}"]`)?.scrollIntoView({ block: 'nearest' });
  }, [focusKey]);

  const count = flat.length;
  const boxRow = (list: string[], prefix: string) => (
    <div className="uni-row uni-chips">
      {list.map(b => (
        <span key={b} data-uni={`${prefix}${b}`} className={cls(`${prefix}${b}`, 'uni-item')}>
          <a className="bird-chip clickable" href={`/box/${b}`}
            onClick={e => { e.preventDefault(); go(() => onBoxClick(b))(); }}>Box {b}</a>
        </span>
      ))}
    </div>
  );

  // The calendar tracks this field the way it tracks the date search: opening on focus, and
  // centred on the best date match once there is one. The id matters because several searches
  // are mounted at once (a toolbar's and the mobile menu's) — without it, a hidden one
  // reporting "not focused" would close the calendar the visible one just opened.
  const uid = useId();
  const sorted = useMemo(() => [...dates].sort(), [dates]);
  const centerDate = dateHits[0] || sorted[sorted.length - 1] || '';
  useEffect(() => { onFocusChange?.(open, centerDate, uid); }, [open, centerDate]);
  useEffect(() => () => { onFocusChange?.(false, '', uid); }, []);

  const handleKey = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') { setSearch(''); setOpen(false); return; }
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      if (count === 0) return;
      e.preventDefault();                    // don't drag the caret through the query
      setOpen(true);
      setCursor(c => {
        const next = e.key === 'ArrowDown' ? c + 1 : c - 1;
        return Math.max(-1, Math.min(count - 1, next));
      });
      return;
    }
    // Enter takes the stepped-to result, or the top one when nothing has been stepped to.
    if (e.key === 'Enter') go(() => flat[cursor >= 0 ? cursor : 0]?.run())();
  };

  const label = (text: string) => <div className="uni-label">{text}</div>;

  return (
    <div className="uni-search">
      <input
        type="text"
        placeholder="Search"
        value={search}
        onChange={e => { setSearch(e.target.value.replace(/^\/+/, '')); setOpen(true); }}
        onFocus={e => { setOpen(true); scrollToTop(e.currentTarget); }}
        onBlur={() => setTimeout(() => setOpen(false), 300)}
        onKeyDown={handleKey}
        className="uni-search-input"
      />
      {open && count > 0 && (
        <div className="uni-results" ref={listRef}>
          {local.boxes.length > 0 && (<>
            {label('Box')}
            {boxRow(local.boxes, 'bx:')}
          </>)}
          {local.pengs.length > 0 && (<>
            {label('Penguin')}
            <div className="uni-row uni-chips">
              {local.pengs.map(p => (
                <span key={p.peng_num} data-uni={`pg:${p.peng_num}`} className={cls(`pg:${p.peng_num}`, 'uni-item')}>
                  <PenguinMini scan={p} onClick={go(() => onBirdClick(p.peng_num))} />
                </span>
              ))}
            </div>
          </>)}
          {dateHits.length > 0 && (<>
            {label('Date')}
            <div className="uni-row uni-chips">
              {dateHits.map(d => (
                <span key={d} data-uni={`dt:${d}`} className={cls(`dt:${d}`, 'uni-item')}>
                  <DateLink date={d} onDayClick={go(() => onDayClick(d))} />
                </span>
              ))}
            </div>
          </>)}
          {local.pits.length > 0 && (<>
            {label('PIT ID')}
            <div className="uni-row uni-chips">
              {local.pits.map(p => (
                <span key={p.peng_num} data-uni={`pt:${p.peng_num}`} className={cls(`pt:${p.peng_num}`, 'uni-item')}>
                  <PenguinMini scan={p} onClick={go(() => onBirdClick(p.peng_num || p.pit_id))} />
                </span>
              ))}
            </div>
          </>)}
          {local.pengNotes.length > 0 && (<>
            {label('Penguin notes')}
            {local.pengNotes.map(({ peng, note, from }) => (
              <div key={peng.peng_num} data-uni={`pn:${peng.peng_num}`} className={cls(`pn:${peng.peng_num}`, 'uni-row uni-noted')}>
                <PenguinMini scan={peng} onClick={go(() => onBirdClick(peng.peng_num))} />
                <span className="uni-note">{note}</span>
                {from && <span className="uni-note-src">{from}</span>}
              </div>
            ))}
          </>)}
          {local.obsNotes.length > 0 && (<>
            {label('Observation notes')}
            <div className="uni-obs-list">
            {local.obsNotes.map((o: any) => (
              <div key={o.observation_id} data-uni={`ob:${o.observation_id}`} className={cls(`ob:${o.observation_id}`, 'uni-obs')}>
                <a className="bird-chip clickable" href={`/box/${o.box}`}
                  onClick={e => { e.preventDefault(); go(() => onObsClick(o.box, o.observation_time_utc))(); }}>Box {o.box}</a>
                <ObsCard obs={o} box={o.box}
                  onBirdClick={(tag: string) => go(() => onBirdClick(tag))()}
                  onDayClick={(day: string) => go(() => onDayClick(day))()} />
              </div>
            ))}
            </div>
          </>)}
          {local.dateNotes.length > 0 && (<>
            {label('Day notes')}
            {local.dateNotes.map(({ date, note }) => (
              <div key={date} data-uni={`dn:${date}`} className={cls(`dn:${date}`, 'uni-row uni-noted')}>
                <DateLink date={date} onDayClick={go(() => onDayClick(date))} />
                <span className="uni-note">{note}</span>
              </div>
            ))}
          </>)}
        </div>
      )}
    </div>
  );
}

function DateSearch({ dates, onDayClick, onFocusChange }: { dates: string[]; onDayClick: (day: string) => void; onFocusChange?: (focused: boolean, centerDate: string, id: string) => void }) {
  const uid = useId();
  const [search, setSearch] = useState('');
  const [open, setOpen] = useState(false);
  const { registeredFmDates } = useContext(DateTooltipCtx);
  const filtered = useMemo(() => matchDateQuery(dates, search, registeredFmDates), [dates, search, registeredFmDates]);

  const go = (day: string) => {
    onDayClick(day);
    setSearch('');
    setOpen(false);
  };

  const handleKey = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') {
      if (filtered.length > 0) {
        go(filtered[0]);
      } else {
        const parsed = parseDateInput(search);
        if (parsed) go(parsed);
      }
    } else if (e.key === 'Escape') {
      setSearch('');
      setOpen(false);
    }
  };

  const sorted = useMemo(() => [...dates].sort(), [dates]);
  const centerDate = filtered.length > 0 ? filtered[0] : sorted[sorted.length - 1] || '';

  useEffect(() => { onFocusChange?.(open, centerDate, uid); }, [open, centerDate]);
  useEffect(() => () => { onFocusChange?.(false, '', uid); }, []);

  return (
    <div className="date-search">
      <input
        type="text"
        placeholder="Date"
        value={search}
        onChange={e => { setSearch(e.target.value); setOpen(true); }}
        onFocus={() => setOpen(true)}
        onBlur={() => setTimeout(() => setOpen(false), 300)}
        onKeyDown={handleKey}
        className="date-search-input"
      />
      {open && filtered.length > 0 && (
        <div className="date-results">
          {filtered.map((d, i) => {
            const fm = registeredFmDates.get(d);
            return (
              <div key={d} className={`date-result clickable${i === 0 ? ' focused' : ''}`} onClick={() => go(d)}>
                {formatDate(d)}{fm ? <span className="fm-tag"> (FM {fm.number})</span> : null}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

function LoginScreen({ onLogin }: { onLogin: (token: string, name: string, observerId?: number | string, role?: string) => void }) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [name, setName] = useState('');
  const [error, setError] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [isRegister, setIsRegister] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [isForgot, setIsForgot] = useState(false);
  const [forgotMsg, setForgotMsg] = useState('');

  const handleForgot = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(''); setForgotMsg('');
    setSubmitting(true);
    try {
      const r = await fetch('/api/crud.php?action=request_password_reset', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email })
      });
      const d = await r.json();
      setForgotMsg(d.message || 'If that email has an account, a reset link has been sent.');
    } catch (e: any) {
      setError('Connection failed: ' + (e.message || ''));
    }
    setSubmitting(false);
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setSubmitting(true);
    try {
      if (isRegister) {
        const r = await fetch('/api/crud.php?action=register', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ name, email, password })
        });
        const data = await r.json();
        if (data.success) {
          // Auto-login after register
          setIsRegister(false);
          setError('');
          const r2 = await fetch('/api/crud.php?action=login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ email, password })
          });
          const d2 = await r2.json();
          if (d2.token) { if (d2.email) localStorage.setItem('ww_email', d2.email); onLogin(d2.token, d2.name, d2.observer_id, d2.role); }
          else setError('Registered but login failed');
        } else {
          setError(data.error || 'Registration failed');
        }
      } else {
        const r = await fetch('/api/crud.php?action=login', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ email, password })
        });
        const text = await r.text();
        try {
          const data = JSON.parse(text);
          if (data.token) { if (data.email) localStorage.setItem('ww_email', data.email); onLogin(data.token, data.name, data.observer_id, data.role); }
          else setError(data.error || 'Login failed');
        } catch {
          setError('Server returned unexpected response: ' + text.substring(0, 100));
        }
      }
    } catch (e: any) {
      setError('Connection failed: ' + (e.message || ''));
    }
    setSubmitting(false);
  };

  return (
    <div className="login-page login-bg">
      <div className="login-card">
        <h1>Wildwatch</h1>
        <p className="login-sub">Penguin Colony Monitoring</p>
        {isForgot ? (
          // Once the link is away the form is replaced outright rather than left armed. A second
          // send is the confusing case -- it used to invalidate the link already in their inbox --
          // so the surest fix is to leave nothing to click.
          forgotMsg ? (
            <div>
              <div className="login-info">{forgotMsg}</div>
              <p className="login-alt">The link is valid for 1 hour. If it hasn't arrived in a few minutes, check your spam folder.</p>
              <p className="login-alt"><a className="clickable" onClick={() => { setIsForgot(false); setForgotMsg(''); setError(''); }}>Back to log in</a></p>
            </div>
          ) : (
          <form onSubmit={handleForgot}>
            <input type="email" placeholder="Email" value={email} onChange={e => setEmail(e.target.value)} required autoFocus />
            {error && <div className="login-error">{error}</div>}
            <button type="submit" disabled={submitting}>{submitting ? 'Please wait...' : 'Email me a reset link'}</button>
            <p className="login-alt"><a className="clickable" onClick={() => { setIsForgot(false); setForgotMsg(''); setError(''); }}>Back to log in</a></p>
          </form>
          )
        ) : (
        <form onSubmit={handleSubmit}>
          {isRegister && <input type="text" placeholder="Name" value={name} onChange={e => setName(e.target.value)} required />}
          <input type="email" placeholder="Email" value={email} onChange={e => setEmail(e.target.value)} required />
          <div className="password-field">
            <input type={showPassword ? 'text' : 'password'} placeholder="Password" value={password} onChange={e => setPassword(e.target.value)} required minLength={6} />
            <button type="button" className="toggle-pw" onClick={() => setShowPassword(!showPassword)}>{showPassword ? '\u{1F441}' : '\u{1F441}'}</button>
          </div>
          {error && <div className="login-error">{error}</div>}
          <button type="submit" disabled={submitting}>{submitting ? 'Please wait...' : isRegister ? 'Register' : 'Log in'}</button>
          <p className="login-alt"><a className="clickable" onClick={() => { setIsForgot(true); setError(''); }}>Forgot password?</a></p>
        </form>
        )}
      </div>
      <p className="login-credit">Photo: Marty Melville</p>
    </div>
  );
}

/** Weak-password stems as leet-aware regexes, built once. Each letter also matches the digits
 *  and symbols people swap in for it, so a stem catches "Wildwatch1", "P@ssw0rd", "W1ldw4tch".
 *  Mirrors the $weak / $swap logic in config.php — keep the two lists in step. */
const WEAK_STEMS: RegExp[] = (() => {
  const stems = ['password','123456','1234567','12345678','qwerty','azerty','iloveyou',
    'letmein','motdepasse','changeme','welcome','monkey','dragon','sunshine',
    'wildwatch','nestcheck','penguin','manchot','korora'];
  const swap: Record<string,string> = {a:'a4@',b:'b8',c:'c(',e:'e3',g:'g9',i:'i1!|',l:'l1|',
    o:'o0',s:'s5$',t:'t7+',z:'z2'};
  const escClass = (s: string) => s.replace(/[\]\\^-]/g, '\\$&');
  return stems.map(w => new RegExp([...w].map(ch => swap[ch] ? '[' + escClass(swap[ch]) + ']' : ch.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join(''), 'i'));
})();

/** Mirror of wwPasswordProblem() in config.php — the two MUST stay in step, since the server
 *  is the authority and a client that says "fine" then gets rejected is a worse experience than
 *  no check at all. Returns a short problem sentence, or null when the password is acceptable.
 *  `identity` is the strings a password must not contain: the person's name parts and email. */
function passwordProblem(pw: string, identity: string[] = []): string | null {
  if (pw.length < 8) return 'Use at least 8 characters.';
  // At least 4 distinct chars, before the length exemption — else "000000000000" passes.
  if (new Set(pw).size < 4) return 'Use at least 4 different characters.';
  if (pw.length < 12) {
    const classes = (/[a-z]/.test(pw) ? 1 : 0) + (/[A-Z]/.test(pw) ? 1 : 0)
                  + (/[0-9]/.test(pw) ? 1 : 0) + (/[^a-zA-Z0-9]/.test(pw) ? 1 : 0);
    if (classes < 2) return 'Mix at least two of: lower case, upper case, numbers, symbols — or make it 12+ characters.';
  }
  const lp = pw.toLowerCase();
  const nameTokens = new Set<string>(), emailTokens = new Set<string>();
  for (let raw of identity) {
    raw = (raw || '').toLowerCase().trim();
    if (!raw) continue;
    const isEmail = raw.includes('@');
    if (isEmail) raw = raw.slice(0, raw.indexOf('@'));
    // 4+ so a short whole name ("Amy", "Flo") doesn't ban ordinary words containing it.
    for (const tok of raw.split(/[^\p{L}\p{N}]+/u)) if (tok.length >= 4) (isEmail ? emailTokens : nameTokens).add(tok);
  }
  for (const tok of nameTokens) if (lp.includes(tok)) return 'Do not put your name in your password.';
  for (const tok of emailTokens) if (lp.includes(tok)) return 'Do not put your email address in your password.';
  // Base stems, matched as a substring AND through common character swaps, so "Wildwatch1",
  // "password2026", "P@ssw0rd", "W1ldw4tch" are all caught. Mirrors config.php.
  if (WEAK_STEMS.some(re => re.test(pw))) return 'That password is too easy to guess — choose something less common.';
  return null;
}

/** Set-password screen for emailed links (/?setpw=TOKEN) — covers both new-user
 *  invites and forgot-password resets. On success the API returns a session, so the
 *  user lands straight in the app. */
function SetPasswordScreen({ setpwToken, onLogin }: { setpwToken: string; onLogin: (token: string, name: string, observerId?: number | string, role?: string) => void }) {
  const [checking, setChecking] = useState(true);
  const [who, setWho] = useState<{ observer_name: string; purpose: string } | null>(null);
  const [error, setError] = useState('');
  const [pw, setPw] = useState('');
  const [pw2, setPw2] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    fetch('/api/crud.php?action=check_reset_token', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token: setpwToken })
    }).then(r => r.json()).then(d => {
      if (d.valid) setWho(d); else setError(d.error || 'This link is invalid or has expired.');
      setChecking(false);
    }).catch(() => { setError('Connection failed'); setChecking(false); });
  }, [setpwToken]);

  // Live checks: the same rule the server enforces, plus the two fields matching. Shown red
  // while the field is non-empty and wrong, and the button stays disabled until both pass.
  const pwProblem = pw ? passwordProblem(pw, who ? [who.observer_name] : []) : null;
  const mismatch = pw2.length > 0 && pw !== pw2;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    if (pwProblem) { setError(pwProblem); return; }
    if (pw !== pw2) { setError('Passwords do not match'); return; }
    setSubmitting(true);
    try {
      const r = await fetch('/api/crud.php?action=reset_password', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ token: setpwToken, password: pw })
      });
      const d = await r.json();
      if (d.token) {
        if (d.email) localStorage.setItem('ww_email', d.email);
        window.history.replaceState({}, '', '/'); // drop the one-time token from the URL
        onLogin(d.token, d.name, d.observer_id, d.role);
      } else setError(d.error || 'Failed to set password');
    } catch (e: any) { setError('Connection failed: ' + (e.message || '')); }
    setSubmitting(false);
  };

  return (
    <div className="login-page login-bg">
      <div className="login-card">
        <h1>Wildwatch</h1>
        {checking ? <p className="login-sub">Checking link…</p> : who ? (
          <>
            <p className="login-sub">{who.purpose === 'invite' ? `Welcome, ${who.observer_name} — choose a password for your account.` : `Hi ${who.observer_name} — set a new password.`}</p>
            <form onSubmit={submit}>
              <div className="password-field">
                <input className={pwProblem ? 'pw-invalid' : ''} type={showPassword ? 'text' : 'password'} placeholder="New password" value={pw} onChange={e => setPw(e.target.value)} required minLength={8} autoFocus />
                <button type="button" className="toggle-pw" onClick={() => setShowPassword(!showPassword)}>{'\u{1F441}'}</button>
              </div>
              {pwProblem && <div className="pw-hint pw-invalid-text">{pwProblem}</div>}
              <input className={mismatch ? 'pw-invalid' : ''} type={showPassword ? 'text' : 'password'} placeholder="Repeat password" value={pw2} onChange={e => setPw2(e.target.value)} required minLength={8} />
              {mismatch && <div className="pw-hint pw-invalid-text">Passwords do not match.</div>}
              {error && <div className="login-error">{error}</div>}
              <button type="submit" disabled={submitting || !!pwProblem || pw.length === 0 || pw !== pw2}>{submitting ? 'Please wait...' : 'Set password & log in'}</button>
            </form>
          </>
        ) : (
          <>
            <p className="login-sub">Set password</p>
            <div className="login-error">{error}</div>
            <p className="login-alt"><a className="clickable" onClick={() => { window.location.href = '/'; }}>Go to log in</a></p>
          </>
        )}
      </div>
      <p className="login-credit">Photo: Marty Melville</p>
    </div>
  );
}

function parseDateFlex(input: string): string | null {
  // Parse dates in day-first formats. Year always required.
  // "11/2/25", "11-2-2025", "11 2 25", "26/7/25"
  // NEVER American format. Day is always first.
  const parts = input.trim().split(/[\s\/\-]+/);
  if (parts.length !== 3) return null;
  const day = parseInt(parts[0]);
  const month = parseInt(parts[1]);
  let year = parseInt(parts[2]);
  if (isNaN(day) || isNaN(month) || isNaN(year)) return null;
  if (month < 1 || month > 12 || day < 1 || day > 31) return null;
  if (year < 100) year += 2000;
  return `${year}-${String(month).padStart(2,'0')}-${String(day).padStart(2,'0')}`;
}

function parseSeasonDate(input: string, _seasonYear: number): string | null {
  return parseDateFlex(input);
}

function DataEntryPage({ token, allPenguins, onBack, fmColony }: { token: string; allPenguins: any[]; onBack: () => void; fmColony: boolean }) {
  // Remember the selected season across nav-away/back (page unmounts when leaving /enter)
  const [season, setSeason] = useState(() => {
    const saved = parseInt(sessionStorage.getItem('ww_entry_season') || '', 10);
    return Number.isFinite(saved) ? saved : getSeasonStart().getFullYear();
  });
  useEffect(() => { sessionStorage.setItem('ww_entry_season', String(season)); }, [season]);
  const [box, setBox] = useState('');
  // The text field edits boxInput only; box (which drives all data loads) commits on Enter
  // or via the steppers — so typing "100" never loads box 1 and 10 along the way.
  const [boxInput, setBoxInput] = useState('');
  const [dateInput, setDateInput] = useState('');
  const [parsedDate, setParsedDate] = useState<string|null>(null);
  const [adults, setAdults] = useState(0);
  const [eggs, setEggs] = useState(0);
  const [chicks, setChicks] = useState(0);
  const [noScan, setNoScan] = useState(0);
  const [gateStatus, setGateStatus] = useState('');
  const [breedingStatus, setBreedingStatus] = useState('');
  const [notes, setNotes] = useState('');
  const [birdSearch, setBirdSearch] = useState('');
  const [dateMappings, setDateMappings] = useState<{date_number:number; actual_date:string; partial_monitor?:number}[]>([]);
  const [prevSeasonMappings, setPrevSeasonMappings] = useState<{date_number:number; actual_date:string; partial_monitor?:number}[]>([]);
  const [nextSeasonMappings, setNextSeasonMappings] = useState<{date_number:number; actual_date:string; partial_monitor?:number}[]>([]);
  const [showDateEditor, setShowDateEditor] = useState(false);
  const [dateEditorText, setDateEditorText] = useState('');
  const [scannedBirds, setScannedBirds] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState('');
  const [lastSavedObsId, setLastSavedObsId] = useState<number|null>(null);
  // Observations entered from this page in this session. The season list below offers a delete
  // only for these — it used to spot them by a "web-entry" monitor_filename, which no longer
  // exists, and the point was always "undo what you just typed", not "delete any observation".
  const [enteredObsIds, setEnteredObsIds] = useState<Set<number>>(new Set());
  // Set when a save was blocked by an existing observation on the same date — renders a link to it
  const [dupObs, setDupObs] = useState<{box:string; time:string}|null>(null);
  // Right-side full-height bird dock — opened by clicking a PenguinMini in the existing rows
  const [sideBird, setSideBird] = useState<string|null>(null);
  const sideBirdData = useBirdDetail(sideBird);

  // Date mappings for this season, the one before and the one after. The previous season's
  // date table often runs on into this season's calendar range (observers keep numbering past
  // 1 Apr), and the next season's book can start before it — so all three land in the widened
  // window this page shows.
  //
  // One request for every season, not three for these three: all_fm_dates is what the app
  // already fetches and caches for the FM tags, so the cached copy paints the tables straight
  // away and the fetch behind it only revalidates.
  useEffect(() => {
    if (season < 2020) return;
    let live = true, revalidated = false;
    const apply = (rows: any) => {
      if (!Array.isArray(rows)) return;
      const forSeason = (y: number) => rows
        .filter((r: any) => Number(r.season_year) === y && r.actual_date)
        .map((r: any) => ({ date_number: Number(r.date_number), actual_date: r.actual_date, partial_monitor: Number(r.partial_monitor) || 0 }))
        .sort((a, b) => a.date_number - b.date_number);
      setDateMappings(forSeason(season));
      setPrevSeasonMappings(forSeason(season - 1));
      setNextSeasonMappings(forSeason(season + 1));
    };
    // `revalidated` guards the order, not the speed: a slow IndexedDB read must never land on
    // top of a server response that already arrived.
    getCachedFmDates().then(rows => { if (live && !revalidated && rows) apply(rows); });
    fetch(`/api/crud.php?action=all_fm_dates&colony_id=${getColonyId()}`, { headers: { 'Authorization': `Bearer ${token}` } })
      .then(r => r.json())
      .then(rows => {
        if (!live) return;
        revalidated = true;
        apply(rows);
        if (Array.isArray(rows)) setCachedFmDates(rows);
      })
      .catch(() => {});
    return () => { live = false; };
  }, [season, token]);

  useEffect(() => {
    // Try date number lookup first, then "d m" format
    const trimmed = dateInput.trim();
    const num = parseInt(trimmed);
    if (!isNaN(num) && trimmed === String(num)) {
      const mapping = dateMappings.find(m => m.date_number === num);
      if (mapping) { setParsedDate(mapping.actual_date); return; }
    }
    setParsedDate(parseSeasonDate(trimmed, season));
  }, [dateInput, season, dateMappings]);

  const addBird = (tag: string) => {
    const short = tag.slice(-8);
    if (scannedBirds.includes(short)) return;

    // Reject box tags. Tags are stored as bare digits now, but a pasted or historic value may
    // still carry the reader's letter prefix, so test what's left once it's off.
    const bare = tag.toUpperCase().replace(/^[A-Z]+/, '');
    if (bare.startsWith('900025') || bare.startsWith('9130') || short.startsWith('9130')) {
      setMessage('Box tag - not a penguin');
      return;
    }

    // Must be a known penguin
    const birdInfo = allPenguins.find((p: any) => p.pit_id.slice(-8) === short || p.pit_id === tag);
    if (!birdInfo) {
      setMessage(`Unknown penguin ${short} - not in database`);
      return;
    }
    if (parsedDate) {
      if (birdInfo.chip_date && parsedDate < birdInfo.chip_date) {
        if (!confirm(`WARNING: Observation date ${parsedDate} is before this penguin's chip date ${birdInfo.chip_date}. Continue?`)) return;
      }
      if (birdInfo.is_dead) {
        if (!confirm(`WARNING: ${short} is recorded as dead. Continue?`)) return;
      }
    }

    // Check for alerts
    const seenBirds = new Set<string>();
    for (const o of existingObs) {
      for (const s of (o.scans || [])) seenBirds.add(s.pit_id.slice(-8));
    }
    scannedBirds.forEach(b => seenBirds.add(b));
    const isNew = !seenBirds.has(short);

    // Red alert: only if the date being entered is AFTER eggs first appeared
    if (isNew && parsedDate) {
      const firstEggDate = existingObs
        .filter((o: any) => o.eggs > 0)
        .map((o: any) => o.observation_time_utc.slice(0, 10))
        .sort()[0];
      if (firstEggDate && parsedDate >= firstEggDate) {
        if (!confirm(`RED ALERT: ${short} has not been seen in this box before and eggs appeared on ${firstEggDate}. This observation is dated ${parsedDate}. Are you sure?`)) return;
      } else if (scannedBirds.length >= 2) {
        if (!confirm(`WARNING: ${short} is a 3rd+ penguin in this observation (${scannedBirds.length} already added). Are you sure?`)) return;
      }
    } else if (isNew && scannedBirds.length >= 2) {
      if (!confirm(`WARNING: ${short} is a 3rd+ penguin in this observation (${scannedBirds.length} already added). Are you sure?`)) return;
    }

    setScannedBirds([...scannedBirds, short]);
    setBirdSearch('');

    // Auto-increment adult or chick count: chick if chipped as chick and <3 months since chip
    if (parsedDate && birdInfo.chip_date && !birdInfo.chipped_as_adult) {
      const chipTime = new Date(birdInfo.chip_date).getTime();
      const obsTime = new Date(parsedDate).getTime();
      if ((obsTime - chipTime) < 90 * 86400000) setChicks(c => c + 1);
      else setAdults(a => a + 1);
    } else {
      setAdults(a => a + 1);
    }
  };

  const removeBird = (tag: string) => {
    const bird = allPenguins.find((p: any) => p.pit_id.slice(-8) === tag || p.pit_id === tag);
    if (bird && parsedDate && bird.chip_date && !bird.chipped_as_adult) {
      const chipTime = new Date(bird.chip_date).getTime();
      const obsTime = new Date(parsedDate).getTime();
      if ((obsTime - chipTime) < 90 * 86400000) setChicks(c => Math.max(0, c - 1));
      else setAdults(a => Math.max(0, a - 1));
    } else {
      setAdults(a => Math.max(0, a - 1));
    }
    setScannedBirds(scannedBirds.filter(b => b !== tag));
  };

  const handleSave = async (gateOverride?: string) => {
    if (!box || !parsedDate) { setMessage('Box and valid date required'); return; }
    // Never save a duplicate — one observation per box per date
    const dup = allBoxObs.find((o: any) => toNzDateStr(o.observation_time_utc) === parsedDate);
    if (dup) { setDupObs({ box, time: dup.observation_time_utc }); return; }
    setSaving(true); setMessage(''); setDupObs(null);

    try {
      // Find location_id for this box — in the ACTIVE colony (so we never write to the wrong one)
      const dashRes = await fetch(`/api/dashboard.php?view=box&name=${encodeURIComponent(box)}&colony_id=${getColonyId()}&_=${Date.now()}`, { headers: { 'Authorization': `Bearer ${token}` } });
      const dashData = await dashRes.json();
      const locationId = dashData.location?.location_id;

      if (!locationId) { setMessage(`Box "${box}" not found in database (no location_id)`); setSaving(false); return; }

      const observerId = parseInt(localStorage.getItem('ww_observer_id') || '3');

      // Create observation
      const obsRes = await fetch('/api/crud.php?action=create&table=observations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
        body: JSON.stringify({
          location_id: locationId,
          observer_id: observerId,
          observation_time_utc: parsedDate + ' 02:00:00',
          adults, eggs, chicks, no_scan: noScan,
          breeding_status: breedingStatus || null,
          gate_status: (gateOverride ?? gateStatus) || null,
          notes,
        })
      });
      const obsData = await obsRes.json();

      if (!obsData.success) { setMessage('Failed: ' + (obsData.error || 'unknown')); setSaving(false); return; }

      // 3. Create penguin scans
      for (const birdId of scannedBirds) {
        const knownBird = allPenguins.find((p: any) => p.pit_id.slice(-8) === birdId || p.pit_id === birdId);
        if (!knownBird) {
          setMessage(`Unknown penguin ${birdId} - not in database`);
          continue;
        }
        await fetch('/api/crud.php?action=create&table=penguin_scans', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
          body: JSON.stringify({
            observation_id: obsData.id,
            pit_id: knownBird.pit_id,
            scan_time_utc: parsedDate + ' 02:00:00'
          })
        });
      }

      setMessage(`Saved: Box ${box}, ${formatDate(parsedDate)}, ${scannedBirds.length} birds`);
      setLastSavedObsId(obsData.id);
      setEnteredObsIds(prev => new Set(prev).add(obsData.id));
      // Reset form (keep the date so the next observation can reuse it)
      setAdults(0); setEggs(0); setChicks(0); setNoScan(0); setGateStatus(''); setBreedingStatus('');
      setNotes(''); setScannedBirds([]);
    } catch (e: any) {
      setMessage('Error: ' + e.message);
    }
    setSaving(false);
  };

  // All observations for this box (for status bar + season list), from the cache the rest of
  // the app reads. This was a dashboard.php?view=box fetch keyed on [box, saving] — so it ran
  // twice per save (once each way `saving` flipped) on top of the sync the write already
  // triggers. Reading the cache means a saved observation appears the moment that sync lands,
  // with no request of its own and no out-of-order response to guard against.
  const boxData = useBoxDetail(box || null);
  const allBoxObs: Observation[] = boxData?.observations || [];
  const boxPenguins: any[] = boxData?.all_penguins || [];

  const wwStart = `${season}-04-01`;
  const wwEnd = `${season + 1}-03-31`;
  // The "book" season is this season's FM date table; observers' old books can start it well
  // before 1 Apr (e.g. Dec of the prior year). Show the union of the book span and the wildwatch
  // Apr–Mar season: earliest of (book start, ww start) … latest of (book finish, ww finish).
  const bookDates = dateMappings.map(m => m.actual_date).filter(Boolean).sort();
  const bookStart = bookDates[0];
  const bookEnd = bookDates[bookDates.length - 1];
  const seasonStart = bookStart && bookStart < wwStart ? bookStart : wwStart;
  const seasonEnd = bookEnd && bookEnd > wwEnd ? bookEnd : wwEnd;
  // Dates from the neighbouring book seasons (prev/next) that fall inside this widened window and
  // aren't part of this book's own table — surfaced as pale-yellow "before/after" dates.
  const thisDateSet = new Set(dateMappings.map(m => m.actual_date));
  const crossSeasonDates = [
    ...prevSeasonMappings.map(m => ({ ...m, _season: season - 1 })),
    ...nextSeasonMappings.map(m => ({ ...m, _season: season + 1 })),
  ].filter(m => m.actual_date >= seasonStart && m.actual_date <= seasonEnd && !thisDateSet.has(m.actual_date))
   .sort((a, b) => a.actual_date.localeCompare(b.actual_date));
  const crossDateSet = new Set(crossSeasonDates.map(m => m.actual_date));
  // Earlier (prev-season) dates sit above the table, later (next-season) dates below it.
  const crossBefore = crossSeasonDates.filter(m => m._season < season);
  const crossAfter = crossSeasonDates.filter(m => m._season > season);
  const toDmy = (d: string) => `${parseInt(d.slice(8, 10))}/${parseInt(d.slice(5, 7))}/${d.slice(2, 4)}`;
  // Left/right date arrows: all registered FM dates in this book's window (this season plus
  // the neighbouring-season dates already surfaced), sorted, so the picker can step to the
  // previous/next FM date. Setting a this-season date uses its number; cross-season uses d/m/y.
  const fmStepDates = [
    ...dateMappings.map(m => ({ date: m.actual_date, num: m.date_number, thisSeason: true })),
    ...crossSeasonDates.map(m => ({ date: m.actual_date, num: m.date_number, thisSeason: false })),
  ].filter(x => x.date).sort((a, b) => a.date.localeCompare(b.date));
  const stepFm = (dir: number) => {
    if (!fmStepDates.length) return;
    const cur = parsedDate || '';
    const idx = fmStepDates.findIndex(x => x.date === cur);
    const target = idx !== -1
      ? fmStepDates[Math.min(fmStepDates.length - 1, Math.max(0, idx + dir))]
      : dir > 0 ? (fmStepDates.find(x => x.date > cur) ?? fmStepDates[fmStepDates.length - 1])
                : ([...fmStepDates].reverse().find(x => x.date < cur) ?? fmStepDates[0]);
    if (target) setDateInput(target.thisSeason ? String(target.num) : toDmy(target.date));
  };
  const existingObs = allBoxObs.filter(o =>
    o.observation_time_utc >= seasonStart && o.observation_time_utc <= seasonEnd + ' 23:59:59'
  );

  // Chippings in this box+season, unless the bird is already visible as a scan in
  // one of the box's observations on the chip day (same rule as the box view).
  const entryScannedByDay = new Map<string, Set<string>>();
  for (const o of allBoxObs) {
    const day = toNzDateStr(o.observation_time_utc);
    if (!entryScannedByDay.has(day)) entryScannedByDay.set(day, new Set());
    for (const s of ((o as any).scans || [])) if (s.pit_id) entryScannedByDay.get(day)!.add(s.pit_id);
  }
  const entryChips = boxPenguins
    .filter((p: any) => p.is_chipped_here && p.chip_date && p.chip_date >= seasonStart && p.chip_date <= seasonEnd)
    .filter((p: any) => !entryScannedByDay.get(p.chip_date)?.has(p.pit_id))
    .map((p: any) => ({ ...p, _chip: true, observation_time_utc: `${p.chip_date} 00:00:00` }));
  const entryRows = [...existingObs.map((o: any) => o), ...entryChips]
    .sort((a: any, b: any) => b.observation_time_utc.localeCompare(a.observation_time_utc));
  const todayNz = toNzDateStr(new Date().toISOString()); // highlight an observation dated today (NZ)

  return (
    <div className={`entry-page${sideBird && sideBirdData?.penguin ? ' entry-page-docked' : ''}`}>
      <div className="entry-header">
        <button className="back-btn" onClick={onBack}>&larr; Back</button>
        <h2>Enter observation data</h2>
      </div>

      {/* Persistent context: season + box */}
      <div className="entry-context">
        <div className="entry-row-group">
          <div className="entry-field">
            <label>Season</label>
            <select autoFocus value={season} onChange={e => setSeason(parseInt(e.target.value))} style={{width:'80px'}}>
              {Array.from({length: getSeasonStart().getFullYear() - 2000 - 20}, (_, i) => 21 + i).map(y => <option key={y} value={2000+y}>{y}</option>)}
            </select>
          </div>
          <div className="entry-field" style={{flex:'0 0 auto'}}>
            <label style={{textAlign:'center'}}>Box</label>
            <div style={{display:'flex', gap:4, alignItems:'center'}}>
              {(() => {
                // mem.locations has no defined order (no ORDER BY + incremental sync appends),
                // so natural-sort for sane ‹ › stepping (1, 2, … 99, 100, 103)
                const cmp = (a: string, b: string) => a.localeCompare(b, undefined, { numeric: true });
                const boxNames = [...new Set(queryAllLocations().map((l: any) => String(l.location_name).trim()))].sort(cmp);
                const commitBox = (name: string) => { setBoxInput(name); setBox(name); };
                const stepBox = (dir: number) => {
                  if (!boxNames.length) return;
                  const cur = box.trim();
                  let i = boxNames.indexOf(cur);
                  if (i < 0) {
                    // Current box not in the local list (empty/stale cache) — step from where it would sort
                    const at = boxNames.findIndex(n => cmp(cur, n) < 0);
                    i = (at < 0 ? boxNames.length : at) - (dir > 0 ? 1 : 0);
                  }
                  commitBox(boxNames[Math.min(boxNames.length - 1, Math.max(0, i + dir))]);
                };
                return <>
                  <button className="entry-box-nav" title="Previous box" onClick={() => stepBox(-1)}>‹</button>
                  <input type="text" value={boxInput} onChange={e => setBoxInput(e.target.value)}
                    onKeyDown={e => { if (e.key === 'Enter') commitBox(boxInput.trim()); }}
                    onBlur={() => commitBox(boxInput.trim())}
                    style={{width:'56px'}} />
                  <button className="entry-box-nav" title="Next box" onClick={() => stepBox(1)}>›</button>
                </>;
              })()}
            </div>
          </div>
        </div>
      </div>

      {/* Breeding status bar - always visible */}
      {box && allBoxObs.length > 0 && (
        <div className="entry-context">
          <BreedingStatusBar observations={allBoxObs} box={box} />
        </div>
      )}

      <div className="entry-split">
      {/* LEFT: date table + existing data */}
      <div className="entry-left">
      {/* Date mappings — an FM-book (colony PT) concept, hidden elsewhere */}
      {fmColony && <div className="entry-context">
        <div style={{display:'flex', justifyContent:'space-between', alignItems:'center', marginBottom:'6px'}}>
          <span style={{fontSize:'13px', fontWeight:600, color:'#1a5276'}}>Date table (season {String(season).slice(-2)})</span>
          <button type="button" style={{padding:'4px 12px', background:'#1a5276', color:'#fff', border:'none', borderRadius:'4px', fontSize:'12px', cursor:'pointer'}} onClick={() => { setDateEditorText(dateMappings.map(m =>
                // Seed as dd/mm/yyyy — the format the editor parses back, so lines
                // round-trip. A display format (month name) reads back as invalid.
                // A trailing " PM" marks a Partial Monitor date and round-trips too.
                `${m.date_number} ${m.actual_date.slice(8, 10)}/${m.actual_date.slice(5, 7)}/${m.actual_date.slice(0, 4)}${m.partial_monitor ? ' PM' : ''}`
              ).join('\n')); setShowDateEditor(true); }}>
            {dateMappings.length > 0 ? 'Edit dates' : 'Set up dates'}
          </button>
        </div>
        {crossBefore.length > 0 && (
          <div style={{marginBottom:'6px', paddingBottom:'6px', borderBottom:'1px dashed #ffb74d'}}>
            <div style={{fontSize:'11px', color:'#a15c00', marginBottom:'3px'}}>Earlier, from the previous season's table:</div>
            <div style={{display:'flex', flexWrap:'wrap', gap:'3px'}}>
              {crossBefore.map(m => (
                <span key={`x${m._season}-${m.date_number}`} title={`Season ${String(m._season).slice(-2)} #${m.date_number}`} style={{background:'#fff3e0', border:'1px solid #ffcc80', padding:'3px 8px', borderRadius:'4px', fontSize:'12px', cursor:'pointer'}} onClick={() => setDateInput(toDmy(m.actual_date))}>
                  <b>S{String(m._season).slice(-2)}·{m.date_number}</b> = {formatDate(m.actual_date)}
                </span>
              ))}
            </div>
          </div>
        )}
        {dateMappings.length > 0 ? (
          <div style={{display:'flex', flexWrap:'wrap', gap:'3px'}}>
            {dateMappings.map(m => (
              <span key={m.date_number} style={{background:'#e8ecef', padding:'3px 8px', borderRadius:'4px', fontSize:'12px', cursor:'pointer'}} onClick={() => setDateInput(String(m.date_number))}>
                <b>{m.date_number}</b> = {formatDate(m.actual_date)}
              </span>
            ))}
          </div>
        ) : (
          <p style={{fontSize:'12px', color:'#888', margin:0}}>No date mappings. Click "Edit dates" to define: 1 = 26/7/25, 2 = 3/8/25...</p>
        )}
        {crossAfter.length > 0 && (
          <div style={{marginTop:'6px', paddingTop:'6px', borderTop:'1px dashed #ffb74d'}}>
            <div style={{fontSize:'11px', color:'#a15c00', marginBottom:'3px'}}>Later, from the next season's table:</div>
            <div style={{display:'flex', flexWrap:'wrap', gap:'3px'}}>
              {crossAfter.map(m => (
                <span key={`x${m._season}-${m.date_number}`} title={`Season ${String(m._season).slice(-2)} #${m.date_number}`} style={{background:'#fff3e0', border:'1px solid #ffcc80', padding:'3px 8px', borderRadius:'4px', fontSize:'12px', cursor:'pointer'}} onClick={() => setDateInput(toDmy(m.actual_date))}>
                  <b>S{String(m._season).slice(-2)}·{m.date_number}</b> = {formatDate(m.actual_date)}
                </span>
              ))}
            </div>
          </div>
        )}
        {showDateEditor && (
          <div style={{marginTop:'8px', padding:'8px', background:'#f8f9fa', borderRadius:'6px', border:'1px solid #ddd'}}>
            <p style={{fontSize:'11px',color:'#888',margin:'0 0 4px'}}>One per line: number d/m/yy (e.g. "1 26/7/25"). Add " PM" for a Partial Monitor date (green, no full box-set check).</p>
            <textarea value={dateEditorText} onChange={e => setDateEditorText(e.target.value)} rows={10} style={{width:'100%',fontFamily:'monospace',fontSize:'13px',padding:'6px',border:'1px solid #ddd',borderRadius:'4px'}} />
            <div style={{fontSize:'11px',color:'#888',margin:'4px 0'}}>
              {dateEditorText.trim().split('\n').filter(l => l.trim()).map((l, i) => {
                const first = l.trim().split(/[\s]+/)[0];
                let rest = l.trim().slice(first.length).trim();
                const partial = /\bPM\b\s*$/i.test(rest);
                if (partial) rest = rest.replace(/\s*PM\s*$/i, '').trim();
                const parsed = parseDateFlex(rest);
                const dd = parsed ? `${parsed.slice(8, 10)}/${parsed.slice(5, 7)}/${parsed.slice(0, 4)}` : null;
                return <div key={i} style={{color: parsed ? '#4CAF50' : '#F44336'}}>{first} → {dd || 'invalid'}{partial && parsed ? ' · Partial Monitor' : ''}</div>;
              })}
            </div>
            <div style={{display:'flex', gap:'6px'}}>
              <button style={{flex:1,padding:'6px',background:'#1a5276',color:'#fff',border:'none',borderRadius:'4px',cursor:'pointer',fontSize:'12px'}} onClick={async () => {
                const lines = dateEditorText.trim().split('\n').filter(l => l.trim());
                const mappings = lines.map(l => {
                  const first = l.trim().split(/[\s]+/)[0];
                  let rest = l.trim().slice(first.length).trim();
                  const partial = /\bPM\b\s*$/i.test(rest);
                  if (partial) rest = rest.replace(/\s*PM\s*$/i, '').trim();
                  const parsed = parseDateFlex(rest);
                  return { n: parseInt(first), date: parsed, partial };
                }).filter(m => !isNaN(m.n) && m.date) as {n:number; date:string; partial:boolean}[];
                await fetch(`/api/crud.php?action=season_fm_dates&season=${season}&colony_id=${getColonyId()}`, {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
                  body: JSON.stringify(mappings)
                });
                setDateMappings(mappings.map(m => ({ date_number: m.n, actual_date: m.date, partial_monitor: m.partial ? 1 : 0 })));
                setShowDateEditor(false);
              }}>Save</button>
              <button style={{flex:1,padding:'6px',background:'#fff',color:'#666',border:'1px solid #ddd',borderRadius:'4px',cursor:'pointer',fontSize:'12px'}} onClick={() => setShowDateEditor(false)}>Cancel</button>
            </div>
          </div>
        )}
      </div>}

      {/* Existing observations + chippings for this box+season */}
      {box && entryRows.length > 0 && (
        <div className="entry-existing">
          <h3>{existingObs.length} existing observation{existingObs.length !== 1 ? 's' : ''}{entryChips.length > 0 ? ` + ${entryChips.length} chipping${entryChips.length !== 1 ? 's' : ''}` : ''} for <a className="day-box-link" href={`/box/${box}`}> Box {box}</a> ({season})</h3>
          {entryRows.map((o: any, i: number) => o._chip ? (
            <div key={`chip${o.pit_id}`} className="entry-existing-row entry-chip-row" style={crossDateSet.has(String(o.chip_date).slice(0, 10)) ? {background:'#FEFCE8', borderRadius:4} : undefined}>
              <DateLink date={o.chip_date} onDayClick={(d) => { window.location.href = `/?day=${encodeURIComponent(d)}&box=${encodeURIComponent(box)}`; }} />
              <PenguinMini scan={o} onClick={() => o.peng_num && setSideBird(o.peng_num)} observationDate={o.chip_date} />
              <span className="muted">Chipped by {o.chip_by || '?'}</span>
            </div>
          ) : (
            <div key={i} className="entry-existing-row" style={
              toNzDateStr(o.observation_time_utc) === todayNz ? {background:'#FFF9C4', boxShadow:'inset 0 0 0 2px #FDD835', borderRadius:4}
              : crossDateSet.has(toNzDateStr(o.observation_time_utc)) ? {background:'#FEFCE8', borderRadius:4}
              : undefined}>
              <DateLink date={o.observation_time_utc} onDayClick={(d) => { window.location.href = `/?day=${encodeURIComponent(d)}&box=${encodeURIComponent(box)}`; }} />
              <span>{'\uD83D\uDC27'.repeat(o.adults)}{'\uD83E\uDD5A'.repeat(o.eggs)}{'\uD83D\uDC23'.repeat(o.chicks)}</span>
              {(() => { const ds = displayStatusOrPrev(o, box); return ds && <span className={`badge ${DARK_TEXT_STATUSES.has(ds)?'bordered':''}`} style={{background:STATUS_COLORS[ds]||'#ccc',color:DARK_TEXT_STATUSES.has(ds)?'#333':'#fff'}}>{ds}</span>; })()}
              {o.gate_status && <span className="gate">{o.gate_status}</span>}
              {[...(o.scans || [])].sort(scanSortMFC).map((s: any, j: number) => (
                <PenguinMini key={j} scan={s} onClick={() => s.peng_num && setSideBird(s.peng_num)} observationDate={o.observation_time_utc} />
              ))}
              {Array.from({ length: Number(o.no_scan) || 0 }).map((_, k) => (
                <span key={`ns${k}`} className="scan no-scan">No scan</span>
              ))}
              {o.notes && <span className="muted" style={{fontStyle:'italic', fontSize:12}}>"{o.notes}"</span>}
              <span style={{marginLeft:'auto', display:'flex', alignItems:'center', gap:6}}>
                {o.observation_id && enteredObsIds.has(o.observation_id) && (
                  <button className="remove-scan" onClick={async () => {
                    const reason = prompt(`Delete observation from ${formatDate(o.observation_time_utc)}?\n\nReason (optional):`);
                    if (reason === null) return;
                    // deleteRecord syncs the cache, and the list reads it — the row goes on its own.
                    await deleteRecord(token, 'observations', o.observation_id, reason || undefined);
                  }}>&times;</button>
                )}
                <a className="day-box-link" style={{whiteSpace:'nowrap'}} href={`/?box=${encodeURIComponent(box)}&obs=${encodeURIComponent(o.observation_time_utc)}`}>to observation →</a>
              </span>
            </div>
          ))}
        </div>
      )}

      </div>
      {/* RIGHT: New observation form */}
      <div className="entry-right">
      <div className="entry-form">
        <h3>New observation</h3>
        <div className="entry-row">
          <label>{fmColony ? 'Date (# or d/m/yy)' : 'Date (d/m/yy)'}</label>
          <div style={{display:'flex', alignItems:'center', gap:4}}>
            {fmColony && <button type="button" className="entry-box-nav" title="Previous FM date" disabled={fmStepDates.length === 0} onClick={() => stepFm(-1)}>‹</button>}
            <input type="text" value={dateInput} onChange={e => setDateInput(e.target.value)} placeholder={dateMappings.length > 0 ? `1-${dateMappings.length} or d/m/yy` : 'e.g. 11/2/26'} style={{flex:1, minWidth:0}} />
            {fmColony && <button type="button" className="entry-box-nav" title="Next FM date" disabled={fmStepDates.length === 0} onClick={() => stepFm(1)}>›</button>}
          </div>
          {parsedDate && <span className="date-preview"><DateLink date={parsedDate} onDayClick={(d) => { window.location.href = `/day/${d}`; }} />{dateMappings.find(m => m.actual_date === parsedDate) ? ` (#${dateMappings.find(m => m.actual_date === parsedDate)!.date_number})` : ''}</span>}
          {dateInput && !parsedDate && <span className="date-preview date-invalid">Invalid{dateMappings.length > 0 ? ` (dates 1-${dateMappings.length} available)` : fmColony ? ' - no date table' : ''}</span>}
          {parsedDate && box && (() => {
            const dup = allBoxObs.find((o: any) => toNzDateStr(o.observation_time_utc) === parsedDate);
            return dup ? (
              <span className="date-preview date-dup">⚠ Box {box} already has data on this date — <a className="day-box-link" href={`/?box=${encodeURIComponent(box)}&obs=${encodeURIComponent(dup.observation_time_utc)}`} target="_blank" rel="noopener">edit →</a></span>
            ) : null;
          })()}
        </div>

        {/* Previously seen in this box - sorted M by count, F by count */}
        {box && existingObs.length > 0 && (() => {
          const seenBirds = new Map<string, any & { count: number }>();
          for (const o of existingObs) {
            for (const s of (o.scans || [])) {
              const tag = s.pit_id.slice(-8);
              if (seenBirds.has(tag)) { seenBirds.get(tag)!.count++; }
              else seenBirds.set(tag, { ...s, count: 1 });
            }
          }
          const sorted = Array.from(seenBirds.entries()).sort(([,a], [,b]) => {
            const diff = sexSortOrder(a) - sexSortOrder(b);
            if (diff !== 0) return diff;
            const size = chickSizeOrder(a) - chickSizeOrder(b);
            return size !== 0 ? size : b.count - a.count;
          });
          return sorted.length > 0 ? (
            <div className="entry-row">
              <label>Previously seen</label>
              <div className="bird-row">
                {sorted.map(([tag, scan]) => {
                  const already = scannedBirds.includes(tag);
                  return <span key={tag} className={`bird-with-count ${already ? 'added' : ''}`} style={{opacity: already ? 0.4 : 1}}>
                    <PenguinMini scan={scan} onClick={() => { if (!already) addBird(tag); }} />
                    <span className="scan-count">{scan.count}x</span>
                  </span>;
                })}
              </div>
            </div>
          ) : null;
        })()}

        <div className="entry-row">
          <label>Search by ID</label>
          <div style={{display:'flex', gap:'8px', alignItems:'center'}}>
            <div style={{flex:1, minWidth:0}}>
              <PenguinSearch penguins={allPenguins} search={birdSearch} onSearchChange={setBirdSearch} onBirdClick={(num) => {
                const bird = allPenguins.find((p: any) => p.peng_num === num || p.pit_id === num);
                if (bird) addBird(bird.pit_id.slice(-8));
                setBirdSearch('');
              }} />
            </div>
            <button type="button" className="add-noscan-btn" onClick={() => { setNoScan(n => n + 1); setAdults(a => a + 1); }}>+ No scan</button>
          </div>
        </div>

        <div className="entry-row">
          <label>Observed</label>
          <div className="entry-birds">
            {scannedBirds.map(b => {
              const bird = allPenguins.find((p: any) => p.pit_id.slice(-8) === b || p.pit_id === b);
              return <span key={b} className="scan-removable">
                {bird ? <PenguinMini scan={bird} onClick={() => removeBird(b)} /> : <span className="scan" onClick={() => removeBird(b)}>{b}</span>}
                <button className="remove-scan" onClick={() => removeBird(b)}>&times;</button>
              </span>;
            })}
            {scannedBirds.length === 0 && noScan === 0 && <span className="muted">Click birds above or search to add</span>}
            {Array.from({ length: noScan }).map((_, k) => (
              <span key={`ns${k}`} className="scan-removable">
                <span className="scan no-scan">No scan</span>
                <button className="remove-scan" onClick={() => { setNoScan(n => n - 1); setAdults(a => Math.max(0, a - 1)); }}>&times;</button>
              </span>
            ))}
          </div>
        </div>

        <div className="entry-row-group">
          <div className="entry-field">
            <label>Adults</label>
            <input type="number" min="0" value={adults} onChange={e => setAdults(parseInt(e.target.value)||0)} />
          </div>
          <div className="entry-field">
            <label>Eggs</label>
            <input type="number" min="0" value={eggs} onChange={e => setEggs(parseInt(e.target.value)||0)} />
          </div>
          <div className="entry-field">
            <label>Chicks</label>
            <input type="number" min="0" value={chicks} onChange={e => setChicks(parseInt(e.target.value)||0)} />
          </div>
        </div>

        <div className="entry-row-group">
          <div className="entry-field">
            <label>Gate</label>
            {/* Like the app: picking a gate status completes the box — auto-save if valid */}
            <select value={gateStatus} onChange={e => {
              const v = e.target.value;
              setGateStatus(v);
              if ((v === 'Gate up' || v === 'Regate') && box && parsedDate && !saving) handleSave(v);
            }}>
              <option value="">-</option>
              <option value="Gate up">Gate up</option>
              <option value="Regate">Regate</option>
            </select>
          </div>
          <div className="entry-field">
            <label>Status</label>
            <select value={breedingStatus} onChange={e => setBreedingStatus(e.target.value)}>
              <option value="">-</option>
              <option value="NO">No</option>
              <option value="UNL">Unlikely</option>
              <option value="POT">Potential</option>
              <option value="CON">Confident</option>
              <option value="ABN">Abandoned</option>
              <option value="DCM">DCM</option>
              <option value="IGN">Ignored</option>
            </select>
          </div>
        </div>

        <div className="entry-row">
          <label>Notes</label>
          <textarea value={notes} onChange={e => setNotes(e.target.value)} rows={2} />
        </div>

        {dupObs && createPortal(
          <div className="dup-modal-backdrop" onClick={() => setDupObs(null)}>
            <div className="dup-modal" onClick={e => e.stopPropagation()}>
              <h3>Data not saved</h3>
              <p>Duplicate found — Box {dupObs.box} already has an observation on {formatDate(dupObs.time)}.</p>
              <a className="day-box-link" href={`/?box=${encodeURIComponent(dupObs.box)}&obs=${encodeURIComponent(dupObs.time)}`}>view existing observation →</a>
              <button className="entry-save" style={{marginTop:12}} onClick={() => setDupObs(null)}>OK</button>
            </div>
          </div>, document.body)}
        {message && <div className={message.startsWith('Error') || message.startsWith('Failed') ? 'login-error' : 'entry-success'}>
          {message}
          {lastSavedObsId && !message.startsWith('Error') && !message.startsWith('Failed') && (
            <button style={{marginLeft:8, padding:'2px 10px', fontSize:'12px', background:'#F44336', color:'#fff', border:'none', borderRadius:'4px', cursor:'pointer'}} onClick={async () => {
              await deleteRecord(token, 'observations', lastSavedObsId, 'Undo - entry made in error');
              setMessage('Undone');
              setLastSavedObsId(null);
            }}>Undo</button>
          )}
        </div>}

        <button className="entry-save" onClick={() => handleSave()} disabled={saving || !box || !parsedDate}>
          {saving ? 'Saving...' : 'Save observation'}
        </button>
      </div>
      </div>
      </div>

      {sideBird && sideBirdData?.penguin && (
        <div className="day-bird-dock entry-bird-dock">
          <BirdPage data={sideBirdData} onBirdClick={(num: string) => setSideBird(num)}
            onBoxClick={(b: string) => { setBoxInput(b); setBox(b); }}
            onSightingClick={(b: string) => { setBoxInput(b); setBox(b); }}
            onDayClick={(d: string) => { window.location.href = `/?day=${encodeURIComponent(d)}${box ? `&box=${encodeURIComponent(box)}` : ''}`; }}
            onClose={() => setSideBird(null)}
            token={token} canEdit={false} />
        </div>
      )}

      {/* Date editor is now inline above */}
    </div>
  );
}

const SEASON_COLORS = ['#2196F3', '#4CAF50', '#FF9800', '#9C27B0', '#F44336', '#00BCD4', '#795548', '#607D8B'];

/** Unsexed penguins ranked by how many biometric sex guesses they have — surfaces birds
 *  worth confirming. Tie-break by female-leaning count then peng_num. */
// ===== Penguin groups by box use =====
// Bipartite penguin↔box graph built from every scan. Three grouping methods:
//   strict    — connected components of the raw graph (a single shared sighting joins groups)
//   threshold — drop boxes that are a small share of a bird's sightings, then components
//   louvain   — modularity communities on the penguin co-occurrence projection

function unionFind(n: number) {
  const parent = Array.from({ length: n }, (_, i) => i);
  const find = (x: number): number => parent[x] === x ? x : (parent[x] = find(parent[x]));
  const union = (a: number, b: number) => { const ra = find(a), rb = find(b); if (ra !== rb) parent[ra] = rb; };
  return { find, union };
}

// Louvain modularity clustering on a weighted undirected graph (adjacency map).
// Returns node -> community label.
function louvainCommunities(adj: Map<string, Map<string, number>>): Map<string, string> {
  let mapping = new Map<string, string>([...adj.keys()].map(k => [k, k]));
  let graph = adj;
  for (let level = 0; level < 8; level++) {
    const nodes = [...graph.keys()];
    const k = new Map(nodes.map(n => [n, [...(graph.get(n) || new Map()).values()].reduce((a, b) => a + b, 0)]));
    const m2 = nodes.reduce((a, n) => a + (k.get(n) || 0), 0); // 2m
    if (m2 === 0) break;
    const comm = new Map(nodes.map(n => [n, n]));
    const commTot = new Map(nodes.map(n => [n, k.get(n) || 0]));
    let movedAny = false;
    for (let pass = 0; pass < 20; pass++) {
      let moved = false;
      for (const n of nodes) {
        const cur = comm.get(n)!;
        const ki = k.get(n) || 0;
        commTot.set(cur, (commTot.get(cur) || 0) - ki);
        const links = new Map<string, number>();
        for (const [nb, w] of graph.get(n) || []) {
          if (nb === n) continue;
          const c = comm.get(nb)!;
          links.set(c, (links.get(c) || 0) + w);
        }
        let best = cur;
        let bestGain = (links.get(cur) || 0) - ((commTot.get(cur) || 0) * ki) / m2;
        for (const [c, w] of links) {
          if (c === cur) continue;
          const gain = w - ((commTot.get(c) || 0) * ki) / m2;
          if (gain > bestGain + 1e-12) { bestGain = gain; best = c; }
        }
        comm.set(n, best);
        commTot.set(best, (commTot.get(best) || 0) + ki);
        if (best !== cur) moved = true;
      }
      if (!moved) break;
      movedAny = true;
    }
    if (!movedAny) break;
    mapping = new Map([...mapping].map(([orig, sn]) => [orig, comm.get(sn)!]));
    const agg = new Map<string, Map<string, number>>();
    for (const [a, nbs] of graph) {
      const ca = comm.get(a)!;
      let row = agg.get(ca);
      if (!row) { row = new Map(); agg.set(ca, row); }
      for (const [b, w] of nbs) {
        const cb = comm.get(b)!;
        row.set(cb, (row.get(cb) || 0) + w);
      }
    }
    if (agg.size === graph.size) break;
    graph = agg;
  }
  return mapping;
}

/** Report tables show a short head plus a "Show all (N)" toggle, so one report's long tail
 *  never buries the reports below it on the page. Returns the visible slice and the toggle
 *  button (null when everything already fits). Call it before any early return — it's a hook. */
/** Run something at the browser's convenience. Falls back to a short timer where
 *  requestIdleCallback isn't implemented. Returns its canceller. */
function whenIdle(fn: () => void): () => void {
  const ric = (window as any).requestIdleCallback;
  if (typeof ric === 'function') {
    const id = ric(fn, { timeout: 2000 });
    return () => (window as any).cancelIdleCallback?.(id);
  }
  const id = window.setTimeout(fn, 200);
  return () => window.clearTimeout(id);
}

/**
 * Which of a tabbed page's panels have been built yet.
 *
 * Reports and Admin both render every tab into the DOM and hide the inactive ones, so opening
 * either used to mount every chart at once — a dozen full-colony passes to show you the one tab
 * you asked for. This builds the open tab alone, then brings the rest in one at a time while the
 * browser is idle, so switching stays instant once the page has settled. A tab you reach before
 * its turn comes up is built on the spot.
 *
 * `order` sets which tabs get built ahead and in what order; the active tab is always built
 * whether or not it appears there (which is how a tab too expensive — or too stateful — to
 * pre-build stays out of it).
 */
function useDeferredTabs<T extends string>(active: T, order: readonly T[]): (tab: T) => boolean {
  const [built, setBuilt] = useState<ReadonlySet<T>>(() => new Set([active]));
  useEffect(() => {
    const next = order.find(t => !built.has(t));
    if (next === undefined) return;
    return whenIdle(() => setBuilt(b => b.has(next) ? b : new Set(b).add(next)));
  }, [built, order]);
  // The open tab counts as built without waiting to be recorded as one — which is also what
  // keeps a tab left out of `order` behaving as it always did: shown while open, gone after.
  return (tab: T) => tab === active || built.has(tab);
}

function useTopRows<T>(rows: T[], n = 3, collapsedLabel?: string): [T[], React.ReactNode] {
  const [showAll, setShowAll] = useState(false);
  const button = rows.length > n ? (
    <button className="edit-btn" style={{ marginTop: 6 }} onClick={() => setShowAll(s => !s)}>
      {showAll ? (collapsedLabel || `Show top ${n}`) : `Show all (${rows.length})`}
    </button>
  ) : null;
  return [showAll ? rows : rows.slice(0, n), button];
}

function PenguinGroupsReport({ onOpenBird }: { onOpenBird: (num: string) => void }) {
  const v = useDbVersion();
  const [method, setMethod] = useState<'strict'|'threshold'|'louvain'>('threshold');
  const [minShare, setMinShare] = useState(10);

  // peng_num -> box -> sighting count, plus a representative scan per bird for PenguinMini.
  const base = useMemo(() => {
    const counts = new Map<string, Map<string, number>>();
    const birdInfo = new Map<string, any>();
    for (const { box: rawBox, detail: bd } of allColonyBoxes()) {
      const box = rawBox.trim();
      for (const o of bd?.observations || []) {
        for (const s of o.scans || []) {
          if (!s.peng_num) continue;
          let m = counts.get(s.peng_num);
          if (!m) { m = new Map(); counts.set(s.peng_num, m); }
          m.set(box, (m.get(box) || 0) + 1);
          if (!birdInfo.has(s.peng_num)) birdInfo.set(s.peng_num, s);
        }
      }
    }
    return { counts, birdInfo };
  }, [v]);

  const result = useMemo(() => {
    const { counts } = base;
    const birds = [...counts.keys()];
    let groupsBirds: string[][];

    if (method === 'louvain') {
      // Penguin projection: w_ab = Σ_box (ca·cb)/d_box — co-occurrence discounted by busy boxes.
      const boxBirds = new Map<string, [string, number][]>();
      for (const [num, m] of counts) for (const [box, c] of m) {
        let l = boxBirds.get(box);
        if (!l) { l = []; boxBirds.set(box, l); }
        l.push([num, c]);
      }
      const adj = new Map<string, Map<string, number>>(birds.map(b => [b, new Map()]));
      for (const [, list] of boxBirds) {
        const db = list.reduce((a, [, c]) => a + c, 0);
        for (let i = 0; i < list.length; i++) for (let j = i + 1; j < list.length; j++) {
          const w = (list[i][1] * list[j][1]) / db;
          const a = list[i][0], b = list[j][0];
          adj.get(a)!.set(b, (adj.get(a)!.get(b) || 0) + w);
          adj.get(b)!.set(a, (adj.get(b)!.get(a) || 0) + w);
        }
      }
      const comm = louvainCommunities(adj);
      const byComm = new Map<string, string[]>();
      for (const b of birds) {
        const c = comm.get(b) || b;
        let l = byComm.get(c);
        if (!l) { l = []; byComm.set(c, l); }
        l.push(b);
      }
      groupsBirds = [...byComm.values()];
    } else {
      // strict / threshold: union-find across birds + boxes on kept edges.
      const birdIdx = new Map(birds.map((b, i) => [b, i]));
      const boxIdx = new Map<string, number>();
      for (const m of counts.values()) for (const box of m.keys())
        if (!boxIdx.has(box)) boxIdx.set(box, birds.length + boxIdx.size);
      const uf = unionFind(birds.length + boxIdx.size);
      for (const [num, m] of counts) {
        const total = [...m.values()].reduce((a, b) => a + b, 0);
        for (const [box, c] of m) {
          if (method === 'threshold' && !(c >= 2 && c / total >= minShare / 100)) continue;
          uf.union(birdIdx.get(num)!, boxIdx.get(box)!);
        }
      }
      const byRoot = new Map<number, string[]>();
      for (const b of birds) {
        const r = uf.find(birdIdx.get(b)!);
        let l = byRoot.get(r);
        if (!l) { l = []; byRoot.set(r, l); }
        l.push(b);
      }
      groupsBirds = [...byRoot.values()];
    }

    // Shared post-processing: each box is "owned" by the group with the most sightings in
    // it; a group's exclusivity = share of its birds' sightings that fall in its own boxes.
    const groupOf = new Map<string, number>();
    groupsBirds.forEach((ms, i) => ms.forEach(b => groupOf.set(b, i)));
    const boxGroup = new Map<string, Map<number, number>>();
    for (const [num, m] of counts) {
      const g = groupOf.get(num)!;
      for (const [box, c] of m) {
        let bg = boxGroup.get(box);
        if (!bg) { bg = new Map(); boxGroup.set(box, bg); }
        bg.set(g, (bg.get(g) || 0) + c);
      }
    }
    const owner = new Map<string, number>();
    for (const [box, bg] of boxGroup) {
      let bestG = -1, bestC = -1;
      for (const [g, c] of bg) if (c > bestC) { bestC = c; bestG = g; }
      owner.set(box, bestG);
    }
    const cmp = (a: string, b: string) => a.localeCompare(b, undefined, { numeric: true });
    const rows = groupsBirds.map((members, i) => {
      const boxes: { box: string; c: number }[] = [];
      for (const [box, o] of owner) if (o === i) boxes.push({ box, c: boxGroup.get(box)!.get(i) || 0 });
      boxes.sort((a, b) => b.c - a.c || cmp(a.box, b.box));
      let total = 0, inOwn = 0;
      for (const b of members) for (const [box, c] of counts.get(b)!) {
        total += c;
        if (owner.get(box) === i) inOwn += c;
      }
      return { members: [...members].sort(cmp), boxes, purity: total ? inOwn / total : 0 };
    }).filter(r => r.members.length >= 2)
      .sort((a, b) => b.members.length - a.members.length);
    const singles = birds.length - rows.reduce((a, r) => a + r.members.length, 0);
    return { rows, singles, totalBirds: birds.length };
  }, [base, method, minShare]);

  const [shownGroups, showAllGroupsBtn] = useTopRows(result.rows);

  const methodBlurb = method === 'strict'
    ? 'Connected components of the raw penguin↔box graph — a single shared sighting links two groups, so expect large merged clusters.'
    : method === 'threshold'
    ? `Boxes making up less than ${minShare}% of a bird's sightings (or seen under twice) are ignored, then connected components — groups split where links are only casual visits.`
    : 'Louvain modularity communities on penguin co-occurrence (shared-box sightings, discounted in busy boxes) — finds mostly-exclusive groups even when box use overlaps.';

  return (
    <div className="report-card">
      <h3>Penguin groups by box use</h3>
      <p className="muted">Mutually exclusive groups of penguins based on which boxes they are usually seen in ({result.totalBirds} birds with scans)</p>
      <div className="group-method-row">
        <button className={method === 'strict' ? 'active' : ''} onClick={() => setMethod('strict')}>Strict components</button>
        <button className={method === 'threshold' ? 'active' : ''} onClick={() => setMethod('threshold')}>Usual boxes</button>
        <button className={method === 'louvain' ? 'active' : ''} onClick={() => setMethod('louvain')}>Communities</button>
        {method === 'threshold' && (
          <label className="group-share-slider">
            min share
            <input type="range" min={0} max={50} step={5} value={minShare} onChange={e => setMinShare(parseInt(e.target.value, 10))} />
            {minShare}%
          </label>
        )}
      </div>
      <p className="muted">{methodBlurb}</p>
      {result.rows.length === 0 ? <p className="muted">No groups found</p> : (
        <table className="guess-rank-table rank-table">
          <thead><tr><th>#</th><th>Penguins</th><th>Boxes</th><th>Excl.</th></tr></thead>
          <tbody>
            {shownGroups.map((r, i) => (
              <tr key={i}>
                <td>{r.members.length}</td>
                <td>
                  <div className="group-members">
                    {r.members.map(num => (
                      <PenguinMini key={num} scan={base.birdInfo.get(num)} onClick={() => onOpenBird(num)} />
                    ))}
                  </div>
                </td>
                <td>
                  {r.boxes.slice(0, 15).map((b, j) => (
                    <Fragment key={b.box}>
                      {j > 0 && ', '}
                      <a className="clickable" href={`/box/${b.box}`}><strong>{b.box}</strong></a>
                    </Fragment>
                  ))}
                  {r.boxes.length > 15 && <span className="muted"> +{r.boxes.length - 15} more</span>}
                </td>
                <td>{Math.round(r.purity * 100)}%</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {showAllGroupsBtn}
      {result.singles > 0 && <p className="muted">{result.singles} bird{result.singles === 1 ? '' : 's'} in single-bird groups not shown</p>}
    </div>
  );
}

function MissedScansReport() {
  const boxes = useMissedScans();
  const [shown, showAllBtn] = useTopRows(boxes);

  return (
    <div className="report-card">
      <h3>Possible unchipped penguins — this season</h3>
      <p className="muted">Boxes where adults were recorded present but fewer were scanned, ranked by how often it happened ({boxes.length} boxes). Chipped = birds chipped in that box in the same season.</p>
      {boxes.length === 0 ? <p className="muted">No missed scans this season</p> : (
        <table className="guess-rank-table">
          <thead><tr><th>Box</th><th>Missed</th><th>Chipped</th><th>Days</th></tr></thead>
          <tbody>
            {shown.map((b: any) => (
              <tr key={b.box}>
                <td><a className="clickable" href={`/box/${b.box}`}><strong>{b.box}</strong></a></td>
                <td>{b.missed.length} of {b.observedDays} visit{b.observedDays === 1 ? '' : 's'}</td>
                <td>{b.chipped || ''}</td>
                <td>
                  {b.missed.map((m: any, i: number) => (
                    <Fragment key={m.date}>
                      {i > 0 && ', '}
                      <a className="clickable" href={`/day/${m.date}`}>{m.date.slice(5)}</a>
                      <span className="muted"> ({m.scanned}/{m.adults})</span>
                    </Fragment>
                  ))}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {showAllBtn}
    </div>
  );
}

/**
 * Import a nestcheck day file — the same JSON the phone posts when it syncs
 * ({daily_label, daily_observer_id, daily_scribe_id, observations[]}), read from a file instead
 * of the wire. It goes to the identical endpoint, so a file import and a phone sync produce the
 * same rows, the same conflicts and the same audit trail ("nestcheck_sync").
 *
 * Two passes, as the phone has: `upload` writes what it can and reports any box/day that already
 * has an observation; `confirm` re-sends and force-replaces those in place.
 */
function NestcheckJsonImport({ token, colonyId }: { token: string; colonyId: number }) {
  const [file, setFile] = useState<string>('');
  const [body, setBody] = useState<any>(null);
  const [result, setResult] = useState<any>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const read = async (f: File | undefined) => {
    setResult(null); setError(''); setBody(null); setFile(f?.name || '');
    if (!f) return;
    try {
      const parsed = JSON.parse(await f.text());
      if (!parsed || !Array.isArray(parsed.observations)) throw new Error('No "observations" array — is this a nestcheck day file?');
      setBody(parsed);
    } catch (e: any) { setError(e?.message || 'Could not read that file'); }
  };

  const send = async (action: 'upload' | 'confirm') => {
    if (!body) return;
    setBusy(true); setError('');
    try {
      const r = await fetch(`/api/sync.php?action=${action}&colony_id=${colonyId}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
        body: JSON.stringify(body),
      });
      const json = await r.json();
      if (json.error) { setError(json.error); return; }
      const extraResult = await sendExtras();
      setResult({ ...json, ...extraResult });
      triggerSync();
    } catch (e: any) { setError(e?.message || 'Import failed'); }
    finally { setBusy(false); }
  };

  const list = (k: string) => (Array.isArray(body?.[k]) ? body[k] : []);
  const obsCount = Array.isArray(body?.observations) ? body.observations.length : 0;
  // Only what the phone says hasn't reached wildwatch — the file carries everything it holds,
  // and re-importing the rest would duplicate records the server already has.
  const unsentBio = list('biometrics').filter((b: any) => b.unsent !== false);
  const extras = [
    [unsentBio.length, 'bird detail'], [list('queued_birds').length, 'new bird'],
    [list('day_notes').length, 'day note'], [list('box_notes').length, 'box note'],
  ].filter(([n]) => (n as number) > 0).map(([n, label]) => `${n} ${label}${n === 1 ? '' : 's'}`).join(', ');

  /**
   * The sections sync.php's upload doesn't know about, applied through the same audited crud
   * endpoints the equivalent screens use — a queued bird takes the add-penguin path (penguin,
   * chip, chip-day biometric), a day note takes the day-note endpoint, and so on. One at a
   * time, collecting failures rather than stopping: a bad row shouldn't strand the rest, and
   * what the phone is holding is the least recoverable data there is.
   */
  const sendExtras = async () => {
    const done: string[] = [], failed: string[] = [];
    const count = (n: number, what: string) => { if (n) done.push(`${n} ${what}${n === 1 ? '' : 's'}`); };

    let birds = 0;
    for (const q of list('queued_birds')) {
      try {
        // The phone's file carries numbers as the phone shows them (bare at PT); writes take the
        // full form, and a bare number there means this colony's bird.
        let pengNum = q.rechip_peng_num ? fullPengNum(q.rechip_peng_num) : null;
        if (!pengNum) {
          const r = await createRecord(token, 'penguins', {
            chipped_as_adult: Number(q.chipped_as_adult) ? 1 : 0,
            chick_size_code: q.chick_size_code || null,
          }, 'Imported from a nestcheck file');
          if (!r?.success) throw new Error(r?.error || 'penguin create failed');
          pengNum = r.peng_num;
        }
        const loc = queryAllLocations().find((l: any) => String(l.location_name) === String(q.chip_box));
        const chip = await createRecord(token, 'penguin_chips', {
          pit_id: q.pit_id, peng_num: pengNum, chip_date: q.chip_date, chip_box: q.chip_box,
          location_id: loc?.location_id ?? null, chip_by: q.chipper || null,
          chipper_id: q.chipper_id || null, assistant_id: q.assistant_id || null, is_active: 1,
        }, 'Imported from a nestcheck file');
        if (!chip?.success) throw new Error(chip?.error || 'chip create failed');
        if (q.weight || q.flipper_length || q.observed_sex || q.notes) {
          await createRecord(token, 'penguin_biometric_data', {
            peng_num: pengNum, observation_date: q.chip_date,
            weight: q.weight ?? null, flipper_length: q.flipper_length ?? null,
            observed_sex: q.observed_sex || null, notes: q.notes || null,
          });
        }
        birds++;
      } catch (e: any) { failed.push(`bird ${q.pit_id}: ${e?.message || e}`); }
    }
    count(birds, 'new bird');

    let bios = 0;
    for (const b of unsentBio) {
      try {
        await createRecord(token, 'penguin_biometric_data', {
          peng_num: fullPengNum(b.peng_num), observation_date: b.observation_date,
          weight: b.weight ?? null, flipper_length: b.flipper_length ?? null,
          observed_sex: b.observed_sex || null, notes: b.notes || null,
          is_moulting: b.is_moulting ? 1 : 0, condition_ticks: b.condition_ticks ? 1 : 0,
        });
        bios++;
      } catch (e: any) { failed.push(`biometric ${displayPengNum(fullPengNum(b.peng_num))}: ${e?.message || e}`); }
    }
    count(bios, 'bird detail');

    let notes = 0, keptNotes = 0;
    for (const n of list('day_notes')) {
      try {
        // A note already in wildwatch may have been corrected since; the phone's copy fills a
        // blank day rather than overwriting, the same rule its own sync follows.
        if (n.note && getDayNote(n.nz_date)) { keptNotes++; continue; }
        await saveDayNote(token, n.nz_date, {
          note: n.note || '', observer_id: n.observer_id || null, scribe_id: n.scribe_id || null,
        });
        notes++;
      } catch (e: any) { failed.push(`day note ${n.nz_date}: ${e?.message || e}`); }
    }
    count(notes, 'day note');
    if (keptNotes) done.push(`${keptNotes} day note${keptNotes === 1 ? '' : 's'} left as they are`);

    let boxes = 0;
    for (const bn of list('box_notes')) {
      try {
        const loc = queryAllLocations().find((l: any) =>
          (bn.location_id && l.location_id === bn.location_id) || String(l.location_name) === String(bn.box_name));
        if (!loc) throw new Error('unknown box');
        const fields: Record<string, any> = {};
        if (bn.persistent_notes) fields.persistent_notes = bn.persistent_notes;
        if (bn.watched_unsent) fields.watched = bn.watched ? 1 : 0;
        if (Object.keys(fields).length === 0) continue;
        await updateRecord(token, 'observation_locations', loc.location_id, fields, 'Imported from a nestcheck file');
        boxes++;
      } catch (e: any) { failed.push(`box ${bn.box_name}: ${e?.message || e}`); }
    }
    count(boxes, 'box note');

    triggerSync();
    return { done, failed };
  };
  return (
    <div className="admin-section">
      <h3>Import nestcheck day file</h3>
      <p className="muted" style={{ fontSize: 12, marginTop: 0 }}>
        The JSON a phone sends when it syncs — <code>daily_label</code>, <code>daily_observer_id</code>,
        {' '}<code>daily_scribe_id</code> and an <code>observations</code> array. Same endpoint the phone uses, so
        the result is identical to that phone having synced: unknown boxes and unreadable rows are reported,
        a box already holding an observation for that day comes back as a conflict, and nothing is
        overwritten until you choose to replace it. The day label fills an empty day note; it never
        overwrites one.
      </p>
      <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap', marginBottom: 8 }}>
        <input type="file" accept=".json,application/json" onChange={e => read(e.target.files?.[0])} />
        {body && <span className="muted" style={{ fontSize: 12 }}>
          {file}: {obsCount} observation{obsCount === 1 ? '' : 's'}{extras ? `, ${extras}` : ''}
          {body.daily_label ? ` · "${body.daily_label}"` : ''}
          {body.app_version ? ` · nestcheck ${body.app_version}` : ''}
        </span>}
        {body && <button className="edit-btn" disabled={busy} onClick={() => send('upload')}>{busy ? 'Importing…' : 'Import'}</button>}
      </div>
      {error && <p style={{ color: '#F44336', fontSize: 13 }}>{error}</p>}
      {result && (
        <div style={{ fontSize: 13 }}>
          <p style={{ color: '#4CAF50', margin: '4px 0' }}>
            {(result.created || []).length} observation(s) written{(result.done || []).length > 0 ? `, plus ${result.done.join(', ')}` : ''}.
          </p>
          {(result.failed || []).length > 0 && (
            <details open><summary style={{ color: '#F44336' }}>{result.failed.length} could not be applied</summary>
              <ul className="muted">{result.failed.map((x: string, i: number) => <li key={i}>{x}</li>)}</ul>
            </details>
          )}
          {(result.errors || []).length > 0 && (
            <details open><summary style={{ color: '#e65100' }}>{result.errors.length} skipped</summary>
              <ul className="muted">{result.errors.map((x: any, i: number) => <li key={i}>{typeof x === 'string' ? x : JSON.stringify(x)}</li>)}</ul>
            </details>
          )}
          {(result.conflicts || []).length > 0 && (
            <div style={{ marginTop: 6 }}>
              <p style={{ color: '#e65100', margin: '4px 0' }}>
                {result.conflicts.length} box(es) already have an observation for that day.
              </p>
              <ul className="muted">{result.conflicts.map((cf: any, i: number) => (
                <li key={i}>Box {cf.box_name || cf.box || '?'}{cf.date ? ` · ${cf.date}` : ''}</li>
              ))}</ul>
              <button className="edit-btn" disabled={busy} onClick={() => send('confirm')}
                title="Re-send, replacing the existing observation for each conflicting box/day">
                {busy ? 'Replacing…' : 'Replace existing'}
              </button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * How old the mirror's last report may be before the admin tabs start nagging about it. A day:
 * the mirror runs nightly, so 24 hours means a night was missed (or the NAS was off), which is
 * exactly when an admin wants telling. One constant, so change it here.
 */
const MIRROR_STALE_SECONDS = 24 * 60 * 60;

/**
 * Is there something wrong with the offsite copy? Returns the reason, or null when there isn't
 * one. Answered wherever the app runs: on production by asking the mirror through
 * mirror-remote.php, on the mirror itself by reading its own inventory.
 *
 * Three things count, because all three mean the same thing to an admin — the offsite copy is
 * not known to be good right now:
 *   - the report has aged past MIRROR_STALE_SECONDS;
 *   - the last run's restore did not verify (a fresh report saying RESTORE FAILED is not a
 *     backup, it is a file — only a restored, verified one counts);
 *   - the mirror is configured but cannot be asked, so its age is unknown.
 * A server with no mirror configured at all has nothing to nag about, so it never badges.
 *
 * Only a real answer can raise it. "The server told me it can't reach the mirror" is one —
 * mirror-remote.php returns that as an ordinary 200 with reachable:false, and it badges. "My
 * browser couldn't reach the server" is not: a dev build with no /api behind it, a dropped
 * connection or a login page instead of JSON all say nothing about the offsite copy, so they
 * leave the badge alone rather than claiming the mirror is broken.
 */
function useMirrorAlert(enabled: boolean): string | null {
  const [reason, setReason] = useState<string | null>(null);
  useEffect(() => {
    if (!enabled) { setReason(null); return; }
    const onMirror = localStorage.getItem('ww_is_mirror') === '1';
    const url = onMirror ? '/api/mirror-backups.php' : '/api/mirror-remote.php';
    // The hover text names the threshold, in whatever unit reads naturally for it — an admin
    // shouldn't have to divide "1440 minutes" to know what the badge is complaining about.
    const limit = MIRROR_STALE_SECONDS >= 3600
      ? `${Math.round(MIRROR_STALE_SECONDS / 3600)} hours`
      : `${Math.round(MIRROR_STALE_SECONDS / 60)} minutes`;
    let dead = false;
    const check = async () => {
      try {
        const r = await fetch(url, { headers: { Authorization: `Bearer ${localStorage.getItem('ww_token') || ''}` } });
        const d = await r.json();
        if (dead) return;
        if (!onMirror && d?.state === 'not_configured') { setReason(null); return; }
        // Not an answer about the mirror: no /api behind this build, or a session that has
        // expired into an error payload. Nothing is known either way, so nothing is claimed.
        if (!onMirror && typeof d?.reachable !== 'boolean') { setReason(null); return; }
        if (onMirror && !r.ok) { setReason(null); return; }
        if (!onMirror && !d.reachable) { setReason('The mirror cannot be asked how old the offsite copy is'); return; }
        const age = Number(d?.inventory_age_seconds);
        if (!Number.isFinite(age)) { setReason('The mirror did not say how old its report is'); return; }
        if (age > MIRROR_STALE_SECONDS) { setReason(`The mirror's last report is more than ${limit} old`); return; }
        if (d?.restore !== 'verified') { setReason('The mirror’s last run did not restore and verify'); return; }
        setReason(null);
      } catch { if (!dead) setReason(null); }
    };
    check();
    const t = window.setInterval(check, 120000);
    return () => { dead = true; clearInterval(t); };
  }, [enabled]);
  return reason;
}

/** The red "1" an unhealthy mirror puts on the Admin nav item and the Mirror tab. */
function MirrorAlertBadge({ reason }: { reason: string }) {
  return <span className="stale-badge" title={reason}>1</span>;
}

/* Formatters shared by the two offsite cards below (Wildwatch's mirror, tantrixlab's copies).
 * Backup timestamps are UTC; a backup is judged against the day the site had, so they read in
 * NZ time. The age is what actually gets looked at — "is the offsite copy from today?" — so it
 * comes with each one, in whatever unit is still meaningful at that distance. */
const nz = (iso: string) => {
  const d = new Date(iso);
  return isNaN(d.getTime()) ? String(iso || '') : d.toLocaleString('en-NZ',
    { timeZone: 'Pacific/Auckland', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false });
};
const unit = (n: number, u: string) => `${n} ${u}${n === 1 ? '' : 's'} ago`;
const ago = (secs: number) => {
  const s = Math.max(0, Math.round(secs));
  if (s < 60) return unit(s, 'second');
  if (s < 3600) return unit(Math.round(s / 60), 'minute');
  if (s < 172800) return unit(Math.round(s / 3600), 'hour');
  return unit(Math.round(s / 86400), 'day');
};
const since = (iso: string) => {
  const d = new Date(iso);
  return isNaN(d.getTime()) ? '' : ago((Date.now() - d.getTime()) / 1000);
};
/* The dumps on the offsite box span ~1 MB (Wildwatch) to ~600 MB (tantrix), so all three
 * units have to be legible — a tantrix dump in KB is a number nobody can read. */
const kb = (n: number) => n >= 1073741824 ? `${(n / 1073741824).toFixed(2)} GB`
  : n >= 1048576 ? `${(n / 1048576).toFixed(1)} MB` : `${Math.round(n / 1024)} KB`;

/**
 * Admin → Mirror: the offsite box carries tantrixlab.com as well as Wildwatch, so this tab
 * has to answer "what is offsite for tantrixlab, right now?" the same way the card above
 * answers it for Wildwatch — by listing what is actually on the box on every load, not by
 * describing what a nightly script is supposed to do. The listing is taken server-side over
 * an ssh key restricted to a forced find-listing of the backup directory, so a cron that
 * died shows up here as backups that stopped arriving, rather than as a status file that
 * goes on saying "success" forever.
 */
function OffsiteTantrixlabCard({ token }: { token: string }) {
  const [data, setData] = useState<any | null>(null);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);

  const load = () => {
    setBusy(true); setErr('');
    fetch('/api/admin.php?action=backups', { headers: { Authorization: `Bearer ${token}` } })
      .then(r => r.json())
      .then(d => d.error ? setErr(d.error) : setData(d))
      .catch(e => setErr(String(e?.message || e)))
      .finally(() => setBusy(false));
  };
  useEffect(load, [token]);

  // The listing arrives as flat filenames — <db>_<YYYYMMDD>.sql.gz for a daily,
  // <db>_<YYYYMM>.sql.gz for a monthly — so the databases and their two retention classes
  // are separated here rather than trusted from the path.
  const remote = data?.remote;
  const held = useMemo(() => {
    const out: Record<string, { daily: any[]; monthly: any[]; bytes: number }> = {};
    for (const f of (remote?.files || [])) {
      const m = String(f.name || '').match(/^(.+?)_(\d{6,8})\.sql\.gz$/);
      if (!m) continue;
      const [, db, stamp] = m;
      const row = { ...f, taken: new Date(String(f.mtime || '').replace(' ', 'T') + 'Z').toISOString(), stamp };
      (out[db] ||= { daily: [], monthly: [], bytes: 0 });
      out[db][stamp.length === 8 ? 'daily' : 'monthly'].push(row);
      out[db].bytes += Number(f.bytes) || 0;
    }
    for (const db of Object.keys(out)) {
      out[db].daily.sort((a, b) => b.stamp.localeCompare(a.stamp));
      out[db].monthly.sort((a, b) => b.stamp.localeCompare(a.stamp));
    }
    return out;
  }, [data]);

  const tantrix = held['tantrix_online'];
  const wildwatch = held['wildwatch_nestcheck'];
  const newest = tantrix?.daily[0];
  const newestAgeH = newest ? (Date.now() - new Date(newest.taken).getTime()) / 3600000 : Infinity;
  // The run is nightly, so anything under a day and a half old is this arrangement working.
  // Past that, a night has been missed and the number of copies is quietly shrinking.
  const fresh = newestAgeH <= 36;
  const status = data?.status;                       // the run's own status.json, from the VPS side
  const mediaSize = status?.offsite?.media && status.offsite.media !== '?' ? status.offsite.media : null;

  const fileRows = (rows: any[], kind: string) => rows.map((f: any) => (
    <tr key={f.name}>
      <td>{f.name}</td>
      <td>{kind}</td>
      <td style={{ whiteSpace: 'nowrap' }}>{nz(f.taken)}<span className="muted"> · {since(f.taken)}</span></td>
      <td style={{ whiteSpace: 'nowrap' }}>{kb(Number(f.bytes) || 0)}</td>
    </tr>
  ));

  return (
    <div className="admin-section">
      <h3>Offsite copy: tantrixlab.com</h3>
      <p className="muted">
        tantrixlab.com shares the VPS with Wildwatch, and shares its offsite box too: one run each
        night at 03:40 UTC pushes both sites over SSH to <code>devian</code>. What that box is
        holding for tantrixlab is listed below — read off the box itself each time this tab opens.
      </p>
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', margin: '8px 0' }}>
        <button className="edit-btn" disabled={busy} onClick={load}>{busy ? 'Asking…' : 'Refresh'}</button>
      </div>

      {err && <p style={{ color: '#a4362d' }}>{err}</p>}
      {!data && !err ? <p className="muted">Listing the offsite box…</p> : null}

      {data && !remote?.ok && (
        <div style={{ border: '1px solid #f3d6d3', background: '#fbeceb', borderRadius: 8, padding: '10px 14px' }}>
          <p style={{ margin: 0, color: '#a4362d', fontWeight: 600 }}>Could not list the offsite box</p>
          <p className="muted" style={{ margin: '4px 0 0', fontSize: 12 }}>{remote?.error || 'No answer over ssh.'}</p>
          <p className="muted" style={{ margin: '6px 0 0', fontSize: 12 }}>
            This says nothing about whether the backups are there — only that they could not be counted from here.
          </p>
        </div>
      )}

      {data && remote?.ok && !tantrix && (
        <p style={{ color: '#a4362d', fontWeight: 600 }}>
          The box answered, but holds no <code>tantrix_online</code> dump at all.
        </p>
      )}

      {data && remote?.ok && tantrix && (<>
        <p style={{ margin: '4px 0', fontWeight: 600, color: fresh ? '#1f6b41' : '#a4362d' }}>
          {fresh ? 'OFFSITE COPY CURRENT' : 'OFFSITE COPY STALE'}
          <span className="muted" style={{ fontWeight: 400 }}>
            {' '}· newest {newest.name}, taken {nz(newest.taken)} ({since(newest.taken)}), {kb(Number(newest.bytes) || 0)}
          </span>
        </p>
        <p className="muted" style={{ fontSize: 12, marginTop: 0 }}>
          {tantrix.daily.length} dail{tantrix.daily.length === 1 ? 'y' : 'ies'} + {tantrix.monthly.length} monthl
          {tantrix.monthly.length === 1 ? 'y' : 'ies'} held, {kb(tantrix.bytes)} in total · listed live from the box
          {remote.checked_at ? ` ${since(String(remote.checked_at).replace(' ', 'T') + 'Z')}` : ''}
          {mediaSize ? ` · media ${mediaSize}` : ''}
        </p>

        <div className="table-scroll">
          <table className="guess-rank-table zebra">
            <thead><tr><th>Backup</th><th>Kept as</th><th>Taken</th><th>Size</th></tr></thead>
            <tbody>
              {fileRows(tantrix.daily, 'daily')}
              {fileRows(tantrix.monthly, 'monthly')}
            </tbody>
          </table>
        </div>

        <p className="muted" style={{ fontSize: 12 }}>
          Retention is <strong>a fortnight of dailies + 12 monthlies</strong>: dailies are pruned once
          they pass 14 days, and the first daily of each month is copied aside as that month&rsquo;s
          keeper, so a year of month-ends outlives the fortnight window. The absence of anything older
          than a fortnight above is the policy working, not a gap.
        </p>
      </>)}

      {data && remote?.ok && (
        <ul className="muted" style={{ fontSize: 12 }}>
          <li>
            <strong>Media</strong> — the site&rsquo;s <code>uploads/</code> folder is mirrored
            latest-only{mediaSize ? ` (${mediaSize} at the last run)` : ''}: it tracks what is live and
            keeps no dated history, so a file deleted on the site goes from the copy on the next run.
            It is the one part not in the listing above — the restricted key can only read the database
            directory, so its size comes from the run&rsquo;s own check of the box
            {status?.last_success_at ? `, ${since(status.last_success_at)}` : ''}.
          </li>
          <li>
            <strong>Wildwatch too</strong> — the same run leaves {wildwatch ? `${wildwatch.daily.length} daily and ${wildwatch.monthly.length} monthly ` : ''}
            <code>wildwatch_nestcheck</code> dumps on this box. That is a second, independent copy: the
            NAS mirror above pulls its own dump from production and restore-tests it.
          </li>
          <li>
            <strong>Not restore-tested</strong> — the nightly restore-and-verify covers Wildwatch only.
            tantrixlab&rsquo;s dumps are checked for gzip integrity and a plausible size, then shipped;
            never loaded back into a database. A tantrixlab restore is an untested path until someone tries it.
          </li>
          <li>
            The night is all-or-nothing: any failed step aborts the run, and the VPS&rsquo;s package
            upgrades are chained behind it, so the box never upgrades itself without a fresh backup.
          </li>
        </ul>
      )}
    </div>
  );
}

/**
 * Production asking the mirror what it holds. The mirror is only reachable through its
 * Cloudflare tunnel, so "no answer" is an ordinary outcome here and gets said plainly — an
 * admin needs to know the difference between a mirror with no backups and a mirror it cannot
 * see. Every state below is a real answer to "is the offsite copy good?".
 */
function RemoteMirrorCard() {
  const [data, setData] = useState<any>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState('');
  const [waiting, setWaiting] = useState(false);
  const poll = useRef<number | null>(null);
  const stampBefore = useRef<string>('');

  const load = async (quiet = false) => {
    if (!quiet) setBusy(true);
    try {
      const r = await fetch('/api/mirror-remote.php', { headers: { Authorization: `Bearer ${localStorage.getItem('ww_token') || ''}` } });
      const d = await r.json();
      setData(d);
      return d;
    } catch (e: any) {
      const d = { reachable: false, state: 'unreachable', detail: e?.message || 'Request failed' };
      setData(d);
      return d;
    } finally { if (!quiet) setBusy(false); }
  };
  const stopPoll = () => { if (poll.current) { clearInterval(poll.current); poll.current = null; } setWaiting(false); };
  useEffect(() => { load(); return stopPoll; }, []);

  /**
   * Queue a run, then watch for its result. The run takes minutes, so refreshing once would only
   * re-show the report the mirror had already published — the card waits for a NEW one instead,
   * recognised by its generated timestamp changing, and says so while it waits.
   */
  const requestRun = async () => {
    setBusy(true); setMsg('');
    stampBefore.current = String(data?.generated_utc || '');
    try {
      const r = await fetch('/api/mirror-remote.php?run=1', {
        method: 'POST', headers: { Authorization: `Bearer ${localStorage.getItem('ww_token') || ''}` },
      });
      const d = await r.json();
      const already = d.running === true || d.http === 409;
      if (d.state === 'queued' || already) {
        setMsg(already
          ? `A run is already going${d.started_seconds_ago ? ` (started ${ago(Number(d.started_seconds_ago))})` : ''} — watching for its result.`
          : (d.message || 'Queued on the mirror — it takes a few minutes. This card updates itself.'));
        stopPoll();
        setWaiting(true);
        let ticks = 0;
        poll.current = window.setInterval(async () => {
          const fresh = await load(true);
          if (fresh?.generated_utc && fresh.generated_utc !== stampBefore.current) {
            stopPoll();
            setMsg(fresh.restore === 'verified'
              ? 'Run finished — the new backup restored and verified.'
              : 'Run finished, but the restore check did not pass. See the mirror\u2019s own status page.');
          } else if (++ticks >= 40) {                      // ~10 min
            stopPoll();
            setMsg('Still no new report after 10 minutes — check the mirror\u2019s own status page.');
          }
        }, 15000);
      } else {
        setMsg(d.detail || d.error || 'The mirror did not accept the request.');
        load(true);
      }
    } catch (e: any) { setMsg(e?.message || 'Request failed'); }
    finally { setBusy(false); }
  };

  const files: any[] = Array.isArray(data?.files) ? data.files : [];
  const calendar = useMemo(() => {
    const have = new Set(files.map((f: any) => String(f.name || '').replace('.sql.gz', '')));
    const dates = [...have].sort();
    if (dates.length === 0) return [];
    const MONTHS = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
    const [fy, fm] = dates[0].split('-').map(Number);
    const [ly, lm] = dates[dates.length - 1].split('-').map(Number);
    const now = new Date();
    const rows: { label: string; days: { date: string; on: boolean }[]; count: number }[] = [];
    for (let y = fy, m = fm; y * 12 + m <= ly * 12 + lm; m === 12 ? (m = 1, y++) : m++) {
      const inMonth = new Date(y, m, 0).getDate();
      const last = (y === now.getFullYear() && m === now.getMonth() + 1) ? now.getDate() : inMonth;
      const days = Array.from({ length: last }, (_, i) => {
        const date = `${y}-${String(m).padStart(2, '0')}-${String(i + 1).padStart(2, '0')}`;
        return { date, on: have.has(date) };
      });
      rows.push({ label: `${MONTHS[m - 1]} ${y}`, days, count: days.filter(d => d.on).length });
    }
    return rows;
  }, [data]);

  return (
    <div className="admin-section">
      <h3>Backup mirror</h3>
      <p className="muted">
        The offsite copy: it pulls a current dump from this server, restores it into an empty
        database and checks the result. This card is what the mirror reports back.
      </p>
      {data?.mirror_url && (
        <p style={{ margin: '4px 0 8px', fontSize: 13 }}>
          <a className="clickable" href={data.mirror_url} target="_blank" rel="noreferrer">{data.mirror_url}</a>
          <span className="muted"> · </span>
          <a className="clickable" href={`${data.mirror_url}/status/`} target="_blank" rel="noreferrer">status page</a>
        </p>
      )}
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', margin: '8px 0' }}>
        <button className="edit-btn" disabled={busy} onClick={() => load()}>{busy ? 'Asking…' : 'Refresh'}</button>
        <button className="edit-btn" disabled={busy || waiting || data?.running || !data?.reachable} onClick={requestRun}>
          {waiting || data?.running ? 'Running on the mirror…' : 'Ask the mirror to run now'}
        </button>
        {data?.running && (
          <span className="muted" style={{ fontSize: 12, alignSelf: 'center' }}>
            a run is going{data.running_seconds ? ` — started ${ago(Number(data.running_seconds))}` : ''}
          </span>
        )}
      </div>
      {msg && <p className="muted" style={{ color: '#1a6b8f' }}>{msg}</p>}

      {!data ? <p className="muted">Asking the mirror…</p> : !data.reachable ? (
        <div style={{ border: '1px solid #f3d6d3', background: '#fbeceb', borderRadius: 8, padding: '10px 14px' }}>
          <p style={{ margin: 0, color: '#a4362d', fontWeight: 600 }}>
            {data.state === 'not_configured' ? 'No mirror configured'
              : data.state === 'blocked_by_access' ? 'The mirror is up, but this server is not allowed through'
              : 'Cannot reach the mirror'}
          </p>
          <p className="muted" style={{ margin: '4px 0 0', fontSize: 12 }}>{data.detail || data.error}</p>
          <p className="muted" style={{ margin: '6px 0 0', fontSize: 12 }}>
            This says nothing about whether the mirror is healthy — only that it can&rsquo;t be asked from here.
            Its own status page is the fallback.
          </p>
        </div>
      ) : data.state === 'ok' || files.length > 0 ? (<>
        <p style={{ margin: '4px 0', fontWeight: 600, color: data.restore === 'verified' ? '#1f6b41' : '#a4362d' }}>
          {data.restore === 'verified' ? 'RESTORE VERIFIED' : 'RESTORE FAILED'}
          <span className="muted" style={{ fontWeight: 400 }}>
            {' '}· tested {data.tested_dump}{data.tested_dump_taken ? `, taken ${data.tested_dump_taken}` : ''}
            {data.tables ? ` · ${data.tables} tables, ${Number(data.rows_total).toLocaleString()} rows` : ''}
          </span>
        </p>
        <p className="muted" style={{ fontSize: 12, marginTop: 0 }}>
          Reported {ago(Number(data.inventory_age_seconds) || 0)} · {files.length} backup{files.length === 1 ? '' : 's'} held
        </p>
        {calendar.length > 0 && (
          <div className="mirror-cal">
            {calendar.map(row => (
              <div className="mrow" key={row.label}>
                <span className="ml">{row.label}</span>
                <span className="days">
                  {row.days.map(d => <i key={d.date} className={d.on ? 'on' : 'off'} title={d.date} />)}
                </span>
                <span className="mc">{row.count}</span>
              </div>
            ))}
          </div>
        )}
        <div className="table-scroll">
          <table className="guess-rank-table zebra">
            <thead><tr><th>Backup</th><th>Taken</th><th>Size</th></tr></thead>
            <tbody>{[...files].reverse().map((f: any) => (
              <tr key={f.name}>
                <td>{f.name}{f.name === data.tested_dump && <span className="muted"> · live</span>}</td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  {nz(f.taken_utc)}<span className="muted"> · {since(f.taken_utc)}</span>
                </td>
                <td>{kb(Number(f.bytes) || 0)}</td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      </>) : (
        <p className="muted">{data.detail || data.error || 'The mirror has not published an inventory yet.'}</p>
      )}
    </div>
  );
}

const MISSING_NO_SCANS_TITLE = 'Missing no scans';

function MissingNoScansReport({ hrefFor, token }: { hrefFor: (box: string, time: string) => string; token: string }) {
  const TITLE = MISSING_NO_SCANS_TITLE;
  const navigate = useContext(CheckNavContext);
  const { total, rows } = useMissingNoScans();
  const [mode, setMode] = useState<'top' | 'day' | 'all'>('top');
  const [busyObs, setBusyObs] = useState<number | null>(null);
  const [busyPit, setBusyPit] = useState<string | null>(null);
  const recentDay = rows[0]?.date;
  const shown = mode === 'all' ? rows
    : mode === 'day' ? rows.filter((r: any) => r.date === recentDay)
    : rows.slice(0, 3);

  // One click = one no-scan marker. A row short by more than one needs a click per bird;
  // that's deliberate, since each marker stands for a specific unscanned adult.
  const addNoScan = async (r: any) => {
    setBusyObs(r.obsId);
    try {
      await updateRecord(token, 'observations', r.obsId, { no_scan: r.noScan + 1 },
        `Added no-scan from the "${TITLE}" report`);
    } catch (e: any) {
      alert(e?.message || 'Could not add a no-scan');
    } finally {
      setBusyObs(null);
    }
  };
  // A bird chipped in this box on this day, put on the observation it belongs to. The scan is
  // the same record a reader would have written, so it resolves the short count outright when
  // the bird is an adult.
  const addScan = async (r: any, bird: any) => {
    setBusyPit(bird.pit_id);
    try {
      const res = await createRecord(token, 'penguin_scans', { observation_id: r.obsId, pit_id: bird.pit_id },
        `Added chipping from the "${TITLE}" report`);
      if (res && res.success === false) alert(res.error || 'Could not add the scan');
    } catch (e: any) {
      alert(e?.message || 'Could not add the scan');
    } finally {
      setBusyPit(null);
    }
  };
  return (
    <div className="report-card wide" id={checkSlug(TITLE)} style={{ scrollMarginTop: 70 }}>
      <PinnableTitle title={TITLE} count={total} />
      <p className="muted">Observations where the recorded adult count doesn't match scanned adults + "no scan" markers. Newest first.</p>
      {rows.length === 0 ? <p className="muted">No mismatches found</p> : (<>
        <div style={{ display: 'flex', gap: 6, marginBottom: 8, flexWrap: 'wrap' }}>
          {(([['top', 'Show 3'], ['day', `Most recent day${recentDay ? ` (${recentDay})` : ''}`], ['all', `Show all (${total})`]]) as const).map(([m, label]) => (
            <button key={m} className="edit-btn" style={{ opacity: mode === m ? 1 : 0.55 }} onClick={() => setMode(m)}>{label}</button>
          ))}
        </div>
        <div className="table-scroll">
        <table className="guess-rank-table zebra">
          <thead><tr><th>Date</th><th>Box</th><th>Adults</th><th>In box</th><th>Chipped here that day</th><th className="notes-cell">Notes</th><th></th></tr></thead>
          <tbody>
            {shown.map((r: any, i: number) => {
              const href = hrefFor(r.box, r.time);
              // Anchor per cell (not per row) — a <tr> can't contain an <a>, and this keeps
              // middle-click / ctrl-click / "open in new tab" working on every cell.
              const link = (content: React.ReactNode) => (
                <a href={href} className="cell-link" title="Go to this observation"
                  onClick={navigate ? e => navClick(e, () => navigate(href)) : undefined}>{content}</a>
              );
              return (
                // Whole row navigates; minis and the + No scan button are anchors/buttons and
                // handle themselves (closest() also swallows their bubbled clicks).
                <tr key={i} className="clickable" onClick={e => {
                  if ((e.target as HTMLElement).closest('a, button')) return;
                  if (navigate) navigate(href); else window.location.href = href;
                }}>
                  <td>{link(r.date)}</td>
                  <td>{link(<strong>{r.box}</strong>)}</td>
                  {/* Uncapped, unlike the summary rows elsewhere — here the icons ARE the count. */}
                  <td title={`${r.adults} adult${r.adults === 1 ? '' : 's'}`}>{link(r.adults > 0 ? '🐧'.repeat(r.adults) : '—')}</td>
                  {/* Not wrapped in link() — PenguinMini renders its own anchor. */}
                  <td className="minis-cell">
                    <span className="scans">
                      {[...r.scans].sort(scanSortMFC).map((s: any) => (
                        <PenguinMini key={s.pit_id} scan={s} observationDate={r.time}
                          onClick={() => _adminOpenBird?.(String(s.peng_num))} />
                      ))}
                      {Array.from({ length: r.noScan }).map((_, k) => (
                        <span key={`ns${k}`} className="scan no-scan">No scan</span>
                      ))}
                      {r.scans.length === 0 && r.noScan === 0 && <span className="muted">none</span>}
                    </span>
                  </td>
                  {/* Chipped in this box on this day but not on the observation — one click puts
                      each on it. Not wrapped in link(): the minis and buttons handle their own clicks. */}
                  <td className="minis-cell">
                    {r.newChips.length === 0 ? <span className="muted">—</span> : (
                      <span className="scans">
                        {[...r.newChips].sort(scanSortMFC).map((b: any) => (
                          <span key={b.pit_id} className="new-chip-add">
                            <PenguinMini scan={b} observationDate={r.time}
                              onClick={() => _adminOpenBird?.(String(b.peng_num))} />
                            <button className="edit-btn" disabled={busyPit === b.pit_id}
                              onClick={() => addScan(r, b)}
                              title={`Add #${displayPengNum(b.peng_num)} to this observation`}>
                              {busyPit === b.pit_id ? '…' : '+'}
                            </button>
                          </span>
                        ))}
                      </span>
                    )}
                  </td>
                  <td className="muted notes-cell" title={r.notes || undefined}>{r.notes}</td>
                  <td>
                    {r.missing > 0 && (
                      <button className="edit-btn" disabled={busyObs === r.obsId} onClick={() => addNoScan(r)}
                        title={`Record an unscanned adult on this observation (short by ${r.missing})`}>
                        {busyObs === r.obsId ? 'Adding…' : '+ No scan'}
                      </button>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
        </div>
        <p className="muted" style={{ fontSize: 12, marginTop: 4 }}>Showing {shown.length} of {total}</p>
      </>)}
    </div>
  );
}

function TopChickParentsReport({ onOpenBird }: { onOpenBird: (num: string) => void }) {
  const v = useDbVersion();
  const rows = useMemo(() => {
    // Per detected parent, breeding outcomes summed over every clutch where they were a
    // parent (same nest-family detection the box overview / bird panel use). Eggs/chicks
    // are per-clutch peaks summed; chipped and returned chicks are DISTINCT birds.
    // Presumed fledged = chipped chicks (chipping happens at fledge) + unchipped chicks a
    // monitor logged as fledged.
    const byParent = new Map<string, { bird: any; eggs: number; chicksProduced: number; fledgedUnchipped: number; chicks: Set<string>; returned: Set<string> }>();
    for (const { families } of allColonyBoxes()) {
      for (const sd of families) {
        for (const fam of sd.families) {
          for (const parent of fam.parents) {
            if (!parent.peng_num) continue;
            let e = byParent.get(parent.peng_num);
            if (!e) { e = { bird: parent, eggs: 0, chicksProduced: 0, fledgedUnchipped: 0, chicks: new Set(), returned: new Set() }; byParent.set(parent.peng_num, e); }
            e.eggs += fam.clutch.maxEggs;
            e.chicksProduced += fam.clutch.maxChicks;
            e.fledgedUnchipped += fam.fledgedUnchipped;
            for (const ck of fam.chicks) if (ck.peng_num) {
              e.chicks.add(ck.peng_num);
              if (ck.hasReturned) e.returned.add(ck.peng_num);
            }
          }
        }
      }
    }
    // All returned descendants: walk chipped chicks who returned, then THEIR returned
    // chicks (grandchicks and beyond), distinct birds. BFS with a visited set so bad
    // data can't loop forever.
    const allReturnedDescendants = (root: string): number => {
      const seen = new Set<string>();
      const queue = [root];
      while (queue.length) {
        for (const kid of byParent.get(queue.pop()!)?.returned || []) {
          if (seen.has(kid) || kid === root) continue;
          seen.add(kid); queue.push(kid);
        }
      }
      return seen.size;
    };
    return Array.from(byParent.entries()).map(([num, e]) => ({
      bird: e.bird, eggs: e.eggs, chicksProduced: e.chicksProduced,
      chipped: e.chicks.size, fledged: e.chicks.size + e.fledgedUnchipped, returned: e.returned.size,
      descendants: allReturnedDescendants(num),
    }));
  }, [v]);

  const COLS: { key: string; label: string; value: (r: any) => number }[] = [
    { key: 'eggs', label: 'Eggs', value: r => r.eggs },
    { key: 'chicksProduced', label: 'Chicks', value: r => r.chicksProduced },
    { key: 'chipped', label: 'Chipped chicks', value: r => r.chipped },
    { key: 'fledged', label: 'Presumed fledged', value: r => r.fledged },
    { key: 'returned', label: 'Returned chicks', value: r => r.returned },
    { key: 'descendants', label: 'All returned descendants', value: r => r.descendants },
  ];
  // One-way sorting: clicking a column ranks by it, highest first.
  const [sortKey, setSortKey] = useState('chipped');
  const sorted = useMemo(() => {
    const col = COLS.find(c => c.key === sortKey) || COLS[2];
    const byPeng = (a: any, b: any) => comparePengNum(a.bird.peng_num, b.bird.peng_num);
    return [...rows].sort((a, b) => (col.value(b) - col.value(a)) || byPeng(a, b));
  }, [rows, sortKey]);
  const arrow = (key: string) => sortKey === key ? ' ▼' : '';
  // Just the podium by default — the full ranking is a long tail nobody reads at a glance.
  const [shown, showAllBtn] = useTopRows(sorted);

  return (
    <div className="report-card">
      <h3>Most successful parents</h3>
      <p className="muted">Breeding outcomes summed over every clutch where the bird was a detected parent. Eggs and chicks are each clutch's peak count; presumed fledged is chipped chicks plus unchipped chicks logged as fledged. Click a column to sort.</p>
      {sorted.length === 0 ? <p className="muted">No data available</p> : (
        <div className="table-scroll">
          <table className="guess-rank-table rank-table">
            <thead><tr>
              <th>#</th>
              <th>Penguin</th>
              {COLS.map(c => (
                <th key={c.key} className="clickable" style={{ cursor: 'pointer', whiteSpace: 'nowrap' }} onClick={() => setSortKey(c.key)}>{c.label}{arrow(c.key)}</th>
              ))}
            </tr></thead>
            <tbody>
              {shown.map((r: any, i: number) => (
                <tr key={r.bird.peng_num}>
                  <td>{i + 1}</td>
                  <td><PenguinMini scan={r.bird} onClick={() => onOpenBird(r.bird.peng_num)} /></td>
                  <td>{r.eggs}</td>
                  <td>{r.chicksProduced}</td>
                  <td><strong>{r.chipped}</strong></td>
                  <td>{r.fledged}</td>
                  <td>{r.returned}</td>
                  <td>{r.descendants}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {showAllBtn}
        </div>
      )}
    </div>
  );
}

function UnproductiveParentsReport({ onOpenBird }: { onOpenBird: (num: string) => void }) {
  const v = useDbVersion();
  const rows = useMemo(() => {
    // Same nest-family detection as Top chick parents, but counting breeding windows:
    // clutches where the bird was a detected parent AND at least one egg appeared.
    const byParent = new Map<string, { bird: any; windows: number; chicks: Set<string> }>();
    for (const { families } of allColonyBoxes()) {
      for (const sd of families) {
        for (const fam of sd.families) {
          if (fam.parents.length === 0 || fam.clutch.maxEggs < 1) continue;
          for (const parent of fam.parents) {
            if (!parent.peng_num) continue;
            let e = byParent.get(parent.peng_num);
            if (!e) { e = { bird: parent, windows: 0, chicks: new Set() }; byParent.set(parent.peng_num, e); }
            e.windows++;
            for (const ck of fam.chicks) if (ck.peng_num) e.chicks.add(ck.peng_num);
          }
        }
      }
    }
    return Array.from(byParent.values())
      .map(e => ({ bird: e.bird, windows: e.windows, chipped: e.chicks.size }))
      .filter(r => r.windows >= 2)
      .sort((a, b) => a.chipped - b.chipped || b.windows - a.windows || comparePengNum(a.bird.peng_num, b.bird.peng_num))
      .slice(0, 25);
  }, [v]);

  // Worst 3 by default; the rest of the top 25 is behind the toggle.
  const [shown, showAllBtn] = useTopRows(rows, 3, 'Show worst 3');

  return (
    <div className="report-card">
      <h3>Chronically unproductive parents</h3>
      <p className="muted">Birds detected as part of a breeding pair in windows where at least one egg appeared, ranked by fewest chipped chicks then most windows (min 2 windows, top 25)</p>
      {rows.length === 0 ? <p className="muted">No data available</p> : (
        <table className="guess-rank-table count-cols">
          <thead><tr><th>Penguin</th><th>Egg windows</th><th>Chipped chicks</th></tr></thead>
          <tbody>
            {shown.map((r: any) => (
              <tr key={r.bird.peng_num}>
                <td><PenguinMini scan={r.bird} onClick={() => onOpenBird(r.bird.peng_num)} /></td>
                <td>{r.windows}</td>
                <td><strong>{r.chipped}</strong></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {showAllBtn}
    </div>
  );
}

function UnsexedByGuessesReport() {
  const allPenguins = useAllPenguins();
  const rows = useMemo(() => (allPenguins || [])
    .filter((p: any) => !(p.sex || '').trim())
    .map((p: any) => { const g = observedSexGuess(p.peng_num); return { p, m: g.m, f: g.f, total: g.m + g.f }; })
    .filter((r: any) => r.total > 0)
    .sort((a: any, b: any) => b.total - a.total || b.f - a.f || comparePengNum(a.p.peng_num, b.p.peng_num)),
  [allPenguins]);
  const [shown, showAllBtn] = useTopRows(rows);

  return (
    <div className="report-card">
      <h3>Unsexed penguins by sex guesses</h3>
      <p className="muted">Birds with no assigned sex, ordered by number of biometric sex guesses ({rows.length})</p>
      {rows.length === 0 ? <p className="muted">No data available</p> : (
        <table className="guess-rank-table count-cols">
          <thead><tr><th>Penguin</th><th>Guesses</th><th>{'♂'}</th><th>{'♀'}</th></tr></thead>
          <tbody>
            {shown.map((r: any) => (
              <tr key={r.p.peng_num}>
                <td><PenguinMini scan={r.p} onClick={() => {}} navigateDirectly /></td>
                <td>{r.total}</td>
                <td>{r.m || ''}</td>
                <td>{r.f || ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {showAllBtn}
    </div>
  );
}

function DistinctAdultsChart() {
  const data = useDistinctAdults();

  if (data.length === 0) return <div className="report-card"><p className="muted">No scan data available</p></div>;

  const W = 800, H = 400, PAD = { top: 30, right: 30, bottom: 60, left: 55 };
  const plotW = W - PAD.left - PAD.right;
  const plotH = H - PAD.top - PAD.bottom;

  const maxCount = Math.max(...data.map((d: any) => d.count));
  const barW = Math.min(60, plotW / data.length - 4);
  const xScale = (i: number) => PAD.left + (i + 0.5) * (plotW / data.length);
  const yScale = (v: number) => PAD.top + plotH - (v / maxCount) * plotH;

  return (
    <div className="report-card">
      <h3>Distinct adults scanned per season</h3>
      <p className="muted">Number of unique adult penguins scanned each breeding season (Apr–Mar)</p>
      <svg viewBox={`0 0 ${W} ${H}`} className="report-chart">
        {[0.25, 0.5, 0.75, 1].map(frac => (
          <line key={frac} x1={PAD.left} x2={PAD.left + plotW} y1={yScale(maxCount * frac)} y2={yScale(maxCount * frac)} stroke="#e8ecef" strokeWidth="1" />
        ))}
        {[0, 0.25, 0.5, 0.75, 1].map(frac => (
          <text key={frac} x={PAD.left - 8} y={yScale(maxCount * frac) + 4} textAnchor="end" fontSize="11" fill="#888">{Math.round(maxCount * frac)}</text>
        ))}
        {data.map((d: any, i: number) => (
          <Fragment key={d.season}>
            <rect x={xScale(i) - barW / 2} y={yScale(d.count)} width={barW} height={PAD.top + plotH - yScale(d.count)} fill="#2196F3" opacity="0.8" rx="3" />
            <text x={xScale(i)} y={yScale(d.count) - 6} textAnchor="middle" fontSize="11" fill="#333" fontWeight="600">{d.count}</text>
            <text x={xScale(i)} y={PAD.top + plotH + 16} textAnchor="middle" fontSize="10" fill="#666" transform={`rotate(-35, ${xScale(i)}, ${PAD.top + plotH + 16})`}>{d.season}</text>
          </Fragment>
        ))}
        <line x1={PAD.left} x2={PAD.left} y1={PAD.top} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
        <line x1={PAD.left} x2={PAD.left + plotW} y1={PAD.top + plotH} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
        <text x={14} y={PAD.top + plotH / 2} textAnchor="middle" fontSize="12" fill="#666" transform={`rotate(-90, 14, ${PAD.top + plotH / 2})`}>Distinct adults</text>
      </svg>
    </div>
  );
}

function PeakAdultsChart({ onDayClick }: { onDayClick?: (day: string) => void }) {
  const data = usePeakAdults();

  if (data.length === 0) return <div className="report-card"><p className="muted">No observation data available</p></div>;

  const W = 800, H = 400, PAD = { top: 30, right: 30, bottom: 70, left: 55 };
  const plotW = W - PAD.left - PAD.right;
  const plotH = H - PAD.top - PAD.bottom;

  const maxCount = Math.max(...data.map((d: any) => d.adults));
  const barW = Math.min(60, plotW / data.length - 4);
  const xScale = (i: number) => PAD.left + (i + 0.5) * (plotW / data.length);
  const yScale = (v: number) => PAD.top + plotH - (v / maxCount) * plotH;

  // YYYY-MM-DD → "12 Nov"
  const shortDate = (iso: string) => {
    const [, m, d] = iso.split('-');
    const mon = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'][parseInt(m, 10) - 1] || '';
    return `${parseInt(d, 10)} ${mon}`;
  };

  return (
    <div className="report-card">
      <h3>Most adults on a single day per season</h3>
      <p className="muted">Highest total adults present across the colony on any one day each breeding season (Apr–Mar)</p>
      <svg viewBox={`0 0 ${W} ${H}`} className="report-chart">
        {[0.25, 0.5, 0.75, 1].map(frac => (
          <line key={frac} x1={PAD.left} x2={PAD.left + plotW} y1={yScale(maxCount * frac)} y2={yScale(maxCount * frac)} stroke="#e8ecef" strokeWidth="1" />
        ))}
        {[0, 0.25, 0.5, 0.75, 1].map(frac => (
          <text key={frac} x={PAD.left - 8} y={yScale(maxCount * frac) + 4} textAnchor="end" fontSize="11" fill="#888">{Math.round(maxCount * frac)}</text>
        ))}
        {data.map((d: any, i: number) => (
          <Fragment key={d.season}>
            <rect x={xScale(i) - barW / 2} y={yScale(d.adults)} width={barW} height={PAD.top + plotH - yScale(d.adults)} fill="#FF9800" opacity="0.85" rx="3">
              <title>{`${d.season}: ${d.adults} adults on ${d.date}`}</title>
            </rect>
            <text x={xScale(i)} y={yScale(d.adults) - 6} textAnchor="middle" fontSize="11" fill="#333" fontWeight="600">{d.adults}</text>
            <text x={xScale(i)} y={PAD.top + plotH + 16} textAnchor="middle" fontSize="10" fill="#666">{d.season}</text>
            <text x={xScale(i)} y={PAD.top + plotH + 30} textAnchor="middle" fontSize="9"
              fill={onDayClick ? '#1565c0' : '#999'}
              style={onDayClick ? { cursor: 'pointer', textDecoration: 'underline' } : undefined}
              onClick={onDayClick ? () => onDayClick(d.date) : undefined}>{shortDate(d.date)}</text>
          </Fragment>
        ))}
        <line x1={PAD.left} x2={PAD.left} y1={PAD.top} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
        <line x1={PAD.left} x2={PAD.left + plotW} y1={PAD.top + plotH} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
        <text x={14} y={PAD.top + plotH / 2} textAnchor="middle" fontSize="12" fill="#666" transform={`rotate(-90, 14, ${PAD.top + plotH / 2})`}>Peak adults</text>
      </svg>
    </div>
  );
}

/** First egg recorded in the colony each breeding season, with the box it appeared in. */
function FirstEggReport({ onDayClick }: { onDayClick?: (day: string) => void }) {
  const rows = useFirstEgg();
  const [shown, showAllBtn] = useTopRows(rows); // before the early return — hooks must run every render
  if (rows.length === 0) return <div className="report-card"><p className="muted">No egg data available</p></div>;
  const fmt = (iso: string) => new Date(iso + 'T00:00:00').toLocaleDateString('en-NZ', { day: 'numeric', month: 'short', year: 'numeric' });
  return (
    <div className="report-card">
      <h3>First egg each season</h3>
      <p className="muted">The earliest egg recorded anywhere in the colony each breeding season (Apr–Mar), newest first</p>
      <table className="guess-rank-table mini-list-table">
        <thead><tr><th>Season</th><th>First egg</th><th>Boxes</th></tr></thead>
        <tbody>
          {shown.map((r: any) => (
            <tr key={r.season}>
              <td style={{ fontWeight: 600 }}>{r.season}</td>
              <td>{onDayClick
                ? <span className="clickable" style={{ color: '#1565c0', textDecoration: 'underline' }} onClick={() => onDayClick(r.date)}>{fmt(r.date)}</span>
                : fmt(r.date)}</td>
              <td>{r.boxes.map((b: any, i: number) => (
                <Fragment key={b.box}>{i > 0 ? ', ' : ''}<a className="day-box-link" href={`/?box=${encodeURIComponent(b.box)}&obs=${encodeURIComponent(b.obs_time)}`}>Box {b.box}</a></Fragment>
              ))}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {showAllBtn}
    </div>
  );
}

function EggArrivalChart() {
  const data = useEggArrival();

  if (data.length === 0) return <div className="report-card"><p className="muted">No egg data available</p></div>;

  // Chart dimensions
  const W = 800, H = 400, PAD = { top: 30, right: 120, bottom: 50, left: 50 };
  const plotW = W - PAD.left - PAD.right;
  const plotH = H - PAD.top - PAD.bottom;

  // Find axis ranges
  const allDays = data.flatMap(s => s.data.map((d: any) => d.day));
  const minDay = Math.min(...allDays);
  const maxDay = Math.max(...allDays);
  const maxEggs = Math.max(...data.map(s => s.max_eggs));

  // X-axis: months relative to Apr 1
  const monthTicks = [
    { day: 61, label: 'Jun' }, { day: 92, label: 'Jul' }, { day: 122, label: 'Aug' },
    { day: 153, label: 'Sep' }, { day: 183, label: 'Oct' }, { day: 214, label: 'Nov' },
    { day: 245, label: 'Dec' }, { day: 276, label: 'Jan' }, { day: 306, label: 'Feb' },
  ].filter(t => t.day >= minDay - 10 && t.day <= maxDay + 10);

  const xRange = maxDay - minDay + 20;
  const xScale = (day: number) => PAD.left + ((day - minDay + 10) / xRange) * plotW;
  const yScale = (eggs: number) => PAD.top + plotH - (eggs / maxEggs) * plotH;

  return (
    <div className="report-card">
      <h3>Eggs in colony</h3>
      <p className="muted">Total eggs across all boxes over each breeding season — shows laying, hatching, and loss</p>
      <svg viewBox={`0 0 ${W} ${H}`} className="report-chart">
        {/* Grid lines */}
        {[0.25, 0.5, 0.75, 1].map(frac => (
          <line key={frac} x1={PAD.left} x2={PAD.left + plotW} y1={yScale(maxEggs * frac)} y2={yScale(maxEggs * frac)} stroke="#e8ecef" strokeWidth="1" />
        ))}
        {/* Y axis labels */}
        {[0, 0.25, 0.5, 0.75, 1].map(frac => (
          <text key={frac} x={PAD.left - 8} y={yScale(maxEggs * frac) + 4} textAnchor="end" fontSize="11" fill="#888">{Math.round(maxEggs * frac)}</text>
        ))}
        {/* X axis month labels */}
        {monthTicks.map(t => (
          <Fragment key={t.day}>
            <line x1={xScale(t.day)} x2={xScale(t.day)} y1={PAD.top} y2={PAD.top + plotH} stroke="#f0f0f0" strokeWidth="1" />
            <text x={xScale(t.day)} y={PAD.top + plotH + 18} textAnchor="middle" fontSize="11" fill="#888">{t.label}</text>
          </Fragment>
        ))}
        {/* Lines per season */}
        {data.map((season, i) => {
          const color = SEASON_COLORS[i % SEASON_COLORS.length];
          const points = season.data.map((d: any) => `${xScale(d.day)},${yScale(d.eggs)}`).join(' ');
          // Find peak point for label
          const peak = season.data.reduce((best: any, d: any) => d.eggs > best.eggs ? d : best, season.data[0]);
          return (
            <Fragment key={season.season}>
              <polyline points={points} fill="none" stroke={color} strokeWidth="2" strokeLinejoin="round" opacity="0.85" />
              {peak && (
                <text x={xScale(peak.day)} y={yScale(peak.eggs) - 8} textAnchor="middle" fontSize="10" fill={color} fontWeight="600">{season.season}</text>
              )}
            </Fragment>
          );
        })}
        {/* Axes */}
        <line x1={PAD.left} x2={PAD.left} y1={PAD.top} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
        <line x1={PAD.left} x2={PAD.left + plotW} y1={PAD.top + plotH} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
        <text x={PAD.left + plotW / 2} y={H - 5} textAnchor="middle" fontSize="12" fill="#666">Month</text>
        <text x={14} y={PAD.top + plotH / 2} textAnchor="middle" fontSize="12" fill="#666" transform={`rotate(-90, 14, ${PAD.top + plotH / 2})`}>Total eggs</text>
      </svg>
    </div>
  );
}

function ChickSexChart() {
  const allPenguins = useAllPenguins();

  const data = useMemo(() => {
    if (!allPenguins || allPenguins.length === 0) return null;
    const groups: Record<string, { M: number; F: number; U: number; total: number; returned: number }> = {
      LC: { M: 0, F: 0, U: 0, total: 0, returned: 0 },
      BC: { M: 0, F: 0, U: 0, total: 0, returned: 0 },
      SC: { M: 0, F: 0, U: 0, total: 0, returned: 0 },
    };
    for (const p of allPenguins) {
      if (p.chipped_as_adult) continue;
      const size = p.chick_size_code as string;
      if (!size || !(size in groups)) continue;
      const g = groups[size as keyof typeof groups];
      const sex = (p.sex || '').toUpperCase();
      const s = (sex === 'M' || sex === 'F' ? sex : 'U') as 'M' | 'F' | 'U';
      g[s]++;
      g.total++;
      if (p.hasReturned) g.returned++;
    }
    return groups;
  }, [allPenguins]);

  if (!data) return <div className="report-card"><p className="muted">No data available</p></div>;

  const sizes = ['BC', 'LC', 'SC'] as const;
  const sizeLabels: Record<string, string> = { LC: 'Little Chick', BC: 'Big Chick', SC: 'Single Chick' };
  const sexColors = { M: '#2196F3', F: '#E91E63' };
  const sexLabels: Record<string, string> = { M: 'Male', F: 'Female' };

  const W = 600, H = 320, PAD = { top: 30, right: 20, bottom: 60, left: 50 };
  const plotW = W - PAD.left - PAD.right;
  const plotH = H - PAD.top - PAD.bottom;
  const barGroupW = plotW / sizes.length;
  const barW = barGroupW * 0.3;

  const maxKnown = Math.max(...sizes.map(s => { const g = data[s]; return g ? g.M + g.F : 0; }));
  const maxTotal = maxKnown;
  const yScale = (v: number) => PAD.top + plotH - (maxTotal > 0 ? (v / maxTotal) * plotH : 0);

  return (
    <div className="report-card">
      <h3>Chick size vs sex</h3>
      <p className="muted">Sex distribution of penguins chipped as LC, BC, or SC chicks</p>
      <svg viewBox={`0 0 ${W} ${H}`} className="report-chart">
        {/* Grid lines */}
        {[0.25, 0.5, 0.75, 1].map(frac => (
          <line key={frac} x1={PAD.left} x2={PAD.left + plotW} y1={yScale(maxTotal * frac)} y2={yScale(maxTotal * frac)} stroke="#e8ecef" strokeWidth="1" />
        ))}
        {/* Y axis labels */}
        {[0, 0.25, 0.5, 0.75, 1].map(frac => (
          <text key={frac} x={PAD.left - 8} y={yScale(maxTotal * frac) + 4} textAnchor="end" fontSize="11" fill="#888">{Math.round(maxTotal * frac)}</text>
        ))}
        {/* Bars */}
        {sizes.map((size, gi) => {
          const g = data[size];
          if (!g) return null;
          const cx = PAD.left + barGroupW * gi + barGroupW / 2;
          const sexKeys = ['M', 'F'] as const;
          return (
            <Fragment key={size}>
              {sexKeys.map((sex, si) => {
                const count = g[sex] || 0;
                if (count === 0) return null;
                const x = cx - barW + si * barW;
                const barH = maxTotal > 0 ? (count / maxTotal) * plotH : 0;
                return (
                  <Fragment key={sex}>
                    <rect x={x} y={yScale(count)} width={barW - 2} height={barH} fill={sexColors[sex]} opacity="0.85" rx="2" />
                    <text x={x + (barW - 2) / 2} y={yScale(count) - 4} textAnchor="middle" fontSize="10" fill={sexColors[sex]} fontWeight="600">{count}</text>
                  </Fragment>
                );
              })}
              <text x={cx} y={PAD.top + plotH + 16} textAnchor="middle" fontSize="12" fill="#666" fontWeight="600">{sizeLabels[size]}</text>
              <text x={cx} y={PAD.top + plotH + 30} textAnchor="middle" fontSize="10" fill="#888">n={g.M + g.F}</text>
            </Fragment>
          );
        })}
        {/* Axes */}
        <line x1={PAD.left} x2={PAD.left} y1={PAD.top} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
        <line x1={PAD.left} x2={PAD.left + plotW} y1={PAD.top + plotH} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
      </svg>
      {/* Legend */}
      <div style={{display:'flex', gap:'1.5em', justifyContent:'center', marginTop:'0.5em'}}>
        {(['M','F'] as const).map(sex => (
          <span key={sex} style={{display:'flex', alignItems:'center', gap:'0.3em', fontSize:'0.85em'}}>
            <span style={{width:12,height:12,borderRadius:2,background:sexColors[sex],display:'inline-block'}} />
            {sexLabels[sex]}
          </span>
        ))}
      </div>
      {/* Percentage table */}
      <table style={{margin:'1em auto', borderCollapse:'collapse', fontSize:'0.85em'}}>
        <thead>
          <tr style={{borderBottom:'1px solid #ddd'}}>
            <th style={{padding:'0.3em 1em', textAlign:'left'}}>Size</th>
            <th style={{padding:'0.3em 1em'}}>Total</th>
            <th style={{padding:'0.3em 1em'}}>Male</th>
            <th style={{padding:'0.3em 1em'}}>Female</th>
            <th style={{padding:'0.3em 1em'}}>% Male</th>
            <th style={{padding:'0.3em 1em'}}>% Female</th>
          </tr>
        </thead>
        <tbody>
          {sizes.map(size => {
            const g = data[size];
            if (!g) return null;
            const known = g.M + g.F;
            return (
              <tr key={size} style={{borderBottom:'1px solid #eee'}}>
                <td style={{padding:'0.3em 1em', fontWeight:600}}>{sizeLabels[size]}</td>
                <td style={{padding:'0.3em 1em', textAlign:'center'}}>{known}</td>
                <td style={{padding:'0.3em 1em', textAlign:'center', color:sexColors.M}}>{g.M}</td>
                <td style={{padding:'0.3em 1em', textAlign:'center', color:sexColors.F}}>{g.F}</td>
                <td style={{padding:'0.3em 1em', textAlign:'center'}}>{known > 0 ? (g.M / known * 100).toFixed(1) + '%' : '—'}</td>
                <td style={{padding:'0.3em 1em', textAlign:'center'}}>{known > 0 ? (g.F / known * 100).toFixed(1) + '%' : '—'}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function ChickSexBothReturnedChart() {
  const allPenguins = useAllPenguins();

  const result = useMemo(() => {
    if (!allPenguins || allPenguins.length === 0) return null;
    // Get all chicks with BC/LC size codes
    const chicks = allPenguins.filter((p: any) => !p.chipped_as_adult && (p.chick_size_code === 'BC' || p.chick_size_code === 'LC') && p.chip_box && p.chip_date);

    // Group by nest (chip_box + chip_season)
    const nests = new Map<string, any[]>();
    for (const c of chicks) {
      const d = new Date(c.chip_date);
      const seasonYear = d.getMonth() >= 3 ? d.getFullYear() : d.getFullYear() - 1;
      const key = `${c.chip_box}|${seasonYear}`;
      if (!nests.has(key)) nests.set(key, []);
      nests.get(key)!.push(c);
    }

    const groups = { LC: { M: 0, F: 0, U: 0, total: 0 }, BC: { M: 0, F: 0, U: 0, total: 0 } };
    let pairs = 0;
    let bothReturnedTotal = 0;

    for (const nest of nests.values()) {
      const bc = nest.find((c: any) => c.chick_size_code === 'BC');
      const lc = nest.find((c: any) => c.chick_size_code === 'LC');
      if (!bc || !lc) continue;
      if (!bc.hasReturned || !lc.hasReturned) continue;

      bothReturnedTotal++;

      const bcSex = (bc.sex || '').toUpperCase();
      const lcSex = (lc.sex || '').toUpperCase();
      if (!((bcSex === 'M' && lcSex === 'F') || (bcSex === 'F' && lcSex === 'M'))) continue;

      pairs++;
      for (const c of [bc, lc]) {
        const size = c.chick_size_code as 'BC' | 'LC';
        const sex = (c.sex || '').toUpperCase();
        groups[size][sex as 'M' | 'F']++;
        groups[size].total++;
      }
    }

    return { groups, pairs, bothReturnedTotal };
  }, [allPenguins]);

  if (!result) return <div className="report-card"><h3>Chick size vs sex — one male, one female returned</h3><p className="muted">No data available</p></div>;

  const { groups, pairs, bothReturnedTotal } = result;

  if (!pairs || pairs === 0) return (
    <div className="report-card">
      <h3>Chick size vs sex — one male, one female returned</h3>
      <p className="muted">Waiting for the first pair of male/female chicks to both return to the colony. No nests yet where both the BC and LC returned and one was confirmed male, one female.</p>
      {bothReturnedTotal > 0 && <p className="muted">{bothReturnedTotal} nest{bothReturnedTotal !== 1 ? 's' : ''} where both chicks returned (any sex combination).</p>}
    </div>
  );
  const sizes = ['BC', 'LC'] as const;
  const sizeLabels: Record<string, string> = { LC: 'Little Chick', BC: 'Big Chick' };
  const sexColors = { M: '#2196F3', F: '#E91E63' };
  const sexLabels: Record<string, string> = { M: 'Male', F: 'Female' };

  const W = 500, H = 320, PAD = { top: 30, right: 20, bottom: 60, left: 50 };
  const plotW = W - PAD.left - PAD.right;
  const plotH = H - PAD.top - PAD.bottom;
  const barGroupW = plotW / sizes.length;
  const barW = barGroupW * 0.3;

  const maxVal = Math.max(...sizes.map(s => { const g = groups[s]; return g ? Math.max(g.M, g.F) : 0; }));
  const yScale = (v: number) => PAD.top + plotH - (maxVal > 0 ? (v / maxVal) * plotH : 0);

  return (
    <div className="report-card">
      <h3>Chick size vs sex — one male, one female returned</h3>
      <p className="muted">From nests where both BC and LC returned and one was male, one female ({pairs} pairs)</p>
      <svg viewBox={`0 0 ${W} ${H}`} className="report-chart">
        {[0.25, 0.5, 0.75, 1].map(frac => (
          <line key={frac} x1={PAD.left} x2={PAD.left + plotW} y1={yScale(maxVal * frac)} y2={yScale(maxVal * frac)} stroke="#e8ecef" strokeWidth="1" />
        ))}
        {[0, 0.25, 0.5, 0.75, 1].map(frac => (
          <text key={frac} x={PAD.left - 8} y={yScale(maxVal * frac) + 4} textAnchor="end" fontSize="11" fill="#888">{Math.round(maxVal * frac)}</text>
        ))}
        {sizes.map((size, gi) => {
          const g = groups[size];
          if (!g) return null;
          const cx = PAD.left + barGroupW * gi + barGroupW / 2;
          const sexKeys = ['M', 'F'] as const;
          return (
            <Fragment key={size}>
              {sexKeys.map((sex, si) => {
                const count = g[sex] || 0;
                if (count === 0) return null;
                const x = cx - barW + si * barW;
                const barH = maxVal > 0 ? (count / maxVal) * plotH : 0;
                return (
                  <Fragment key={sex}>
                    <rect x={x} y={yScale(count)} width={barW - 2} height={barH} fill={sexColors[sex]} opacity="0.85" rx="2" />
                    <text x={x + (barW - 2) / 2} y={yScale(count) - 4} textAnchor="middle" fontSize="10" fill={sexColors[sex]} fontWeight="600">{count}</text>
                  </Fragment>
                );
              })}
              <text x={cx} y={PAD.top + plotH + 16} textAnchor="middle" fontSize="12" fill="#666" fontWeight="600">{sizeLabels[size]}</text>
              <text x={cx} y={PAD.top + plotH + 30} textAnchor="middle" fontSize="10" fill="#888">n={g.total}</text>
            </Fragment>
          );
        })}
        <line x1={PAD.left} x2={PAD.left} y1={PAD.top} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
        <line x1={PAD.left} x2={PAD.left + plotW} y1={PAD.top + plotH} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
      </svg>
      <div style={{display:'flex', gap:'1.5em', justifyContent:'center', marginTop:'0.5em'}}>
        {(['M','F'] as const).map(sex => (
          <span key={sex} style={{display:'flex', alignItems:'center', gap:'0.3em', fontSize:'0.85em'}}>
            <span style={{width:12,height:12,borderRadius:2,background:sexColors[sex],display:'inline-block'}} />
            {sexLabels[sex]}
          </span>
        ))}
      </div>
      <table style={{margin:'1em auto', borderCollapse:'collapse', fontSize:'0.85em'}}>
        <thead>
          <tr style={{borderBottom:'1px solid #ddd'}}>
            <th style={{padding:'0.3em 1em', textAlign:'left'}}>Size</th>
            <th style={{padding:'0.3em 1em'}}>Total</th>
            <th style={{padding:'0.3em 1em'}}>Male</th>
            <th style={{padding:'0.3em 1em'}}>Female</th>
            <th style={{padding:'0.3em 1em'}}>% Male</th>
            <th style={{padding:'0.3em 1em'}}>% Female</th>
          </tr>
        </thead>
        <tbody>
          {sizes.map(size => {
            const g = groups[size];
            if (!g) return null;
            const known = g.M + g.F;
            return (
              <tr key={size} style={{borderBottom:'1px solid #eee'}}>
                <td style={{padding:'0.3em 1em', fontWeight:600}}>{sizeLabels[size]}</td>
                <td style={{padding:'0.3em 1em', textAlign:'center'}}>{known}</td>
                <td style={{padding:'0.3em 1em', textAlign:'center', color:sexColors.M}}>{g.M}</td>
                <td style={{padding:'0.3em 1em', textAlign:'center', color:sexColors.F}}>{g.F}</td>
                <td style={{padding:'0.3em 1em', textAlign:'center'}}>{known > 0 ? (g.M / known * 100).toFixed(1) + '%' : '—'}</td>
                <td style={{padding:'0.3em 1em', textAlign:'center'}}>{known > 0 ? (g.F / known * 100).toFixed(1) + '%' : '—'}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {bothReturnedTotal > 0 && <p className="muted" style={{marginTop:'0.5em'}}>{bothReturnedTotal} nest{bothReturnedTotal !== 1 ? 's' : ''} total where both chicks returned from a single nest.</p>}
    </div>
  );
}

function ChickReturnChart() {
  const data = useChickReturn();

  if (!data || Object.keys(data.by_season || {}).length === 0) return <div className="report-card"><p className="muted">No data available</p></div>;

  const sizes = ['LC', 'BC', 'SC'] as const;
  const sizeLabels: Record<string, string> = { LC: 'Little Chick', BC: 'Big Chick', SC: 'Single Chick' };
  const sizeColors: Record<string, string> = { LC: '#4CAF50', BC: '#FF9800', SC: '#9C27B0' };
  const totals = data.totals;
  const bySeason = data.by_season;
  const seasons = Object.keys(bySeason).sort();

  // Bar chart: overall return rate per size
  const W = 500, H = 280, PAD = { top: 30, right: 20, bottom: 50, left: 50 };
  const plotW = W - PAD.left - PAD.right;
  const plotH = H - PAD.top - PAD.bottom;
  const barGroupW = plotW / sizes.length;
  const barW = barGroupW * 0.5;
  const maxPct = Math.max(...sizes.map(s => { const t = totals[s]; return t && t.chipped > 0 ? (t.returned / t.chipped) * 100 : 0; }));
  const yMax = Math.ceil(maxPct / 10) * 10 + 5; // round up to next 10 + a little headroom
  const yScale = (v: number) => PAD.top + plotH - (v / yMax) * plotH;
  const yTicks = Array.from({ length: Math.floor(yMax / 10) + 1 }, (_, i) => i * 10).filter(v => v <= yMax);

  return (
    <>
    <div className="report-card">
      <h3>Chick return rate by size</h3>
      <p className="muted">Percentage of chicks that returned to the colony in a later season, by size at chipping</p>
      <svg viewBox={`0 0 ${W} ${H}`} className="report-chart">
        {/* Grid lines */}
        {yTicks.filter(v => v > 0).map(pct => (
          <line key={pct} x1={PAD.left} x2={PAD.left + plotW} y1={yScale(pct)} y2={yScale(pct)} stroke="#e8ecef" strokeWidth="1" />
        ))}
        {/* Y axis labels */}
        {yTicks.map(pct => (
          <text key={pct} x={PAD.left - 8} y={yScale(pct) + 4} textAnchor="end" fontSize="11" fill="#888">{pct}%</text>
        ))}
        {/* Bars */}
        {sizes.map((size, i) => {
          const t = totals[size];
          if (!t || t.chipped === 0) return null;
          const pct = (t.returned / t.chipped) * 100;
          const cx = PAD.left + barGroupW * i + barGroupW / 2;
          const x = cx - barW / 2;
          const barH = (pct / yMax) * plotH;
          return (
            <Fragment key={size}>
              <rect x={x} y={yScale(pct)} width={barW} height={barH} fill={sizeColors[size]} opacity="0.85" rx="2" />
              <text x={cx} y={yScale(pct) - 6} textAnchor="middle" fontSize="12" fill={sizeColors[size]} fontWeight="700">{pct.toFixed(1)}%</text>
              <text x={cx} y={PAD.top + plotH + 16} textAnchor="middle" fontSize="12" fill="#666" fontWeight="600">{sizeLabels[size]}</text>
              <text x={cx} y={PAD.top + plotH + 30} textAnchor="middle" fontSize="10" fill="#888">{t.returned}/{t.chipped}</text>
            </Fragment>
          );
        })}
        {/* Axes */}
        <line x1={PAD.left} x2={PAD.left} y1={PAD.top} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
        <line x1={PAD.left} x2={PAD.left + plotW} y1={PAD.top + plotH} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
      </svg>
      {/* Average return age */}
      <div style={{display:'flex', gap:'2em', justifyContent:'center', margin:'0.8em 0', flexWrap:'wrap'}}>
        {sizes.map(size => {
          const t = totals[size];
          if (!t || !t.avg_return_age) return null;
          return (
            <div key={size} style={{textAlign:'center'}}>
              <div style={{fontSize:'1.4em', fontWeight:700, color:sizeColors[size]}}>{t.avg_return_age}y</div>
              <div style={{fontSize:'0.8em', color:'#888'}}>{sizeLabels[size]} avg return age</div>
              <div style={{fontSize:'0.75em', color:'#aaa'}}>median {t.median_return_age}y</div>
            </div>
          );
        })}
      </div>
      <p className="muted" style={{fontSize:'0.8em', textAlign:'center', margin:'0.5em 1em'}}>Chicks from the 2025/26 season are excluded as they haven't had a chance to return yet.</p>
      {/* Season breakdown table */}
      {seasons.length > 0 && (
        <table style={{margin:'1em auto', borderCollapse:'collapse', fontSize:'0.85em'}}>
          <thead>
            <tr style={{borderBottom:'1px solid #ddd'}}>
              <th style={{padding:'0.3em 0.8em', textAlign:'left'}}>Season</th>
              {sizes.map(s => (
                <th key={s} colSpan={2} style={{padding:'0.3em 0.8em', textAlign:'center', color: sizeColors[s]}}>{sizeLabels[s]}</th>
              ))}
              <th style={{padding:'0.3em 0.8em', textAlign:'center'}}>Total</th>
            </tr>
            <tr style={{borderBottom:'1px solid #eee'}}>
              <th></th>
              {sizes.map(s => (
                <Fragment key={s}>
                  <th style={{padding:'0.2em 0.5em', fontSize:'0.85em', color:'#888'}}>Return</th>
                  <th style={{padding:'0.2em 0.5em', fontSize:'0.85em', color:'#888'}}>Total</th>
                </Fragment>
              ))}
              <th style={{padding:'0.2em 0.5em', fontSize:'0.85em', color:'#888'}}>Chicks</th>
            </tr>
          </thead>
          <tbody>
            {seasons.map(season => (
              <tr key={season} style={{borderBottom:'1px solid #eee'}}>
                <td style={{padding:'0.3em 0.8em', fontWeight:600}}>{season}</td>
                {sizes.map(size => {
                  const g = bySeason[season]?.[size];
                  return (
                    <Fragment key={size}>
                      <td style={{padding:'0.3em 0.5em', textAlign:'center'}}>{g ? g.returned : '—'}</td>
                      <td style={{padding:'0.3em 0.5em', textAlign:'center', color:'#888'}}>{g ? g.chipped : '—'}</td>
                    </Fragment>
                  );
                })}
                <td style={{padding:'0.3em 0.5em', textAlign:'center', fontWeight:600}}>
                  {sizes.reduce((sum, size) => sum + (bySeason[season]?.[size]?.chipped || 0), 0)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>

      {/* Histogram: age at first return */}
      {data.points && data.points.length > 0 && (() => {
        const pts = (data.points as { size: string; age: number; peng_num: string }[]).filter(p => p.age > 0);
        if (pts.length === 0) return null;
        return <AgeHistogramCard title="Age at first return" blurb={`How old penguins were when first scanned back at the colony (n=${pts.length})`} xLabel="Age at first return (months)" months={pts.map(p => Math.round(p.age * 12))} color="#2196F3" />;
      })()}
      <BreedingAgeHistograms />
    </>
  );
}

/** Month-binned age histogram in a report card — same chart for first return / first egg / first offspring. */
function AgeHistogramCard({ title, blurb, xLabel, months, color }: { title: string; blurb: string; xLabel: string; months: number[]; color: string }) {
  if (months.length === 0) return null;
  const maxMonth = Math.max(...months);
  const minMonth = Math.min(...months);
  // Don't waste the axis on the empty 0..first-bar range — start just below the first bar
  // (the age labels stay on 6-month gridlines).
  const startMonth = Math.max(0, minMonth - 2);
  const bins: number[] = Array(maxMonth + 1).fill(0);
  for (const m of months) bins[m]++;
  const maxCount = Math.max(...bins);

  const SW = 800, SH = 300, SP = { top: 30, right: 20, bottom: 45, left: 50 };
  const spW = SW - SP.left - SP.right;
  const spH = SH - SP.top - SP.bottom;
  const barW = spW / Math.max(1, maxMonth - startMonth);
  const xScale2 = (m: number) => SP.left + (m - startMonth - 1) * barW;
  const yScale2 = (v: number) => SP.top + spH - (v / maxCount) * spH;

  return (
    <div className="report-card" style={{marginTop: '0.5em'}}>
      <h3>{title}</h3>
      <p className="muted">{blurb}</p>
      <svg viewBox={`0 0 ${SW} ${SH}`} className="report-chart">
        {/* X axis labels - every 6 months, with year lines */}
        {Array.from({ length: Math.floor(maxMonth / 6) + 1 }, (_, i) => (i + 1) * 6).filter(m => m > startMonth && m <= maxMonth).map(m => (
          <Fragment key={m}>
            <line x1={xScale2(m) + barW / 2} x2={xScale2(m) + barW / 2} y1={SP.top} y2={SP.top + spH} stroke={m % 12 === 0 ? '#d0d0d0' : '#ececec'} strokeWidth="1" />
            <text x={xScale2(m) + barW / 2} y={SP.top + spH + 16} textAnchor="middle" fontSize="10" fill={m % 12 === 0 ? '#666' : '#999'} fontWeight={m % 12 === 0 ? '600' : '400'}>{m}m</text>
          </Fragment>
        ))}
        {/* Y grid */}
        {[0.25, 0.5, 0.75, 1].map(frac => (
          <line key={frac} x1={SP.left} x2={SP.left + spW} y1={yScale2(maxCount * frac)} y2={yScale2(maxCount * frac)} stroke="#e8ecef" strokeWidth="1" />
        ))}
        {/* Y axis labels */}
        {[0, 0.25, 0.5, 0.75, 1].map(frac => {
          const v = Math.round(maxCount * frac);
          return <text key={frac} x={SP.left - 8} y={yScale2(v) + 4} textAnchor="end" fontSize="11" fill="#888">{v}</text>;
        })}
        {/* Bars */}
        {bins.map((count, m) => {
          if (m === 0 || count === 0) return null;
          const barH = (count / maxCount) * spH;
          return (
            <Fragment key={m}>
              <rect x={xScale2(m)} y={yScale2(count)} width={Math.max(barW - 1, 1)} height={barH} fill={color} opacity="0.75" rx="1" />
              {count >= 3 && <text x={xScale2(m) + barW / 2} y={yScale2(count) - 3} textAnchor="middle" fontSize="8" fill={color} fontWeight="600">{count}</text>}
            </Fragment>
          );
        })}
        {/* Axes */}
        <line x1={SP.left} x2={SP.left} y1={SP.top} y2={SP.top + spH} stroke="#ccc" strokeWidth="1" />
        <line x1={SP.left} x2={SP.left + spW} y1={SP.top + spH} y2={SP.top + spH} stroke="#ccc" strokeWidth="1" />
        <text x={SP.left + spW / 2} y={SH - 2} textAnchor="middle" fontSize="12" fill="#666">{xLabel}</text>
      </svg>
    </div>
  );
}

/** Ages (from chip date, so ~a month or two under true age) at which chick-chipped birds
 *  first joined a breeding pair whose clutch produced an egg, and first had a chick
 *  chipped — from the shared computeBoxFamilies detection. */
function BreedingAgeHistograms() {
  const v = useDbVersion();
  const { eggMonths, chickMonths } = useMemo(() => {
    const firstEgg = new Map<string, number>();
    const firstChick = new Map<string, number>();
    for (const { families } of allColonyBoxes()) {
      for (const sd of families) {
        for (const fam of sd.families) {
          for (const parent of fam.parents) {
            if (parent.chipped_as_adult || !parent.chip_date) continue; // age only known for chick-chipped birds
            const key = parent.pit_id ? parent.pit_id.slice(-8) : parent.peng_num;
            if (!key) continue;
            const born = parseDate(parent.chip_date).getTime();
            const mo = (t: number) => Math.round((t - born) / (1000 * 60 * 60 * 24 * 30.44));
            // Both graphs read the SAME breeding windows the box view computes (segmentClutches via
            // computeBoxFamilies) — nothing about the window is recomputed here.
            const chipDates = fam.chicks.map((ck: any) => ck.chip_date).filter(Boolean).map((d: string) => parseDate(d).getTime());
            const producedChick = chipDates.length > 0;                 // window produced a chipped chick
            const producedEgg = fam.clutch.maxEggs >= 1 || producedChick; // …and any chick implies an egg
            // Graph 1: age when this window produced an egg (its estimated laid date).
            if (producedEgg) {
              const t = fam.clutch.laid ?? fam.clutch.windowStart;
              if (t) { const m = mo(t); if (m > 0 && (!firstEgg.has(key) || m < firstEgg.get(key)!)) firstEgg.set(key, m); }
            }
            // Graph 2: age when this window produced its first chipped chick — a strict subset of
            // graph 1 (a chick can't exist without an egg), trailing it by the egg→chip interval.
            if (producedChick) {
              const m = mo(Math.min(...chipDates));
              if (m > 0 && (!firstChick.has(key) || m < firstChick.get(key)!)) firstChick.set(key, m);
            }
          }
        }
      }
    }
    return { eggMonths: Array.from(firstEgg.values()), chickMonths: Array.from(firstChick.values()) };
  }, [v]);

  return (
    <>
      <AgeHistogramCard title="Age at first egg" blurb={`Age of chick-chipped birds the first time they were a parent in a breeding window that produced an egg — using the box view's breeding-window detection (n=${eggMonths.length}, from chip date)`} xLabel="Age at first egg (months)" months={eggMonths} color="#E91E63" />
      <AgeHistogramCard title="Age at first chipped offspring" blurb={`Age of chick-chipped birds the first time a breeding window they parented produced a chipped chick — a subset of the first-egg birds (n=${chickMonths.length}, from chip date)`} xLabel="Age at first chipped offspring (months)" months={chickMonths} color="#4CAF50" />
    </>
  );
}

/** Single age histogram with quarterly (3-month) buckets, reusable for chick/adult split.
 *  `quarters` array contains ages in quarter-year units (0 = 0–3 months, 1 = 3–6 months, etc). */
function AgeBarChart({ quarters, color, xLabel, hideFirst }: { quarters: number[]; color: string; xLabel: string; hideFirst?: boolean }) {
  if (quarters.length === 0) return <p className="muted">No data</p>;
  const filtered = hideFirst ? quarters.filter(q => q > 0) : quarters;
  if (filtered.length === 0) return <p className="muted">No data</p>;
  const maxQ = Math.max(...filtered, 3);
  const bins: number[] = Array(maxQ + 1).fill(0);
  for (const q of filtered) bins[q]++;
  const maxCount = Math.max(...bins);

  const W = 700, H = 260, PAD = { top: 25, right: 20, bottom: 45, left: 50 };
  const plotW = W - PAD.left - PAD.right;
  const plotH = H - PAD.top - PAD.bottom;
  const barW = Math.max(plotW / (maxQ + 1) - 1, 3);
  const xScale = (q: number) => PAD.left + q * (plotW / (maxQ + 1));
  const yScale = (v: number) => PAD.top + plotH - (v / maxCount) * plotH;
  const yTicks = Array.from({ length: 5 }, (_, i) => Math.round(maxCount * (i + 1) / 5)).filter((v, i, a) => a.indexOf(v) === i);

  return (
    <svg viewBox={`0 0 ${W} ${H}`} className="report-chart">
      {yTicks.map(v => (
        <Fragment key={v}>
          <line x1={PAD.left} x2={PAD.left + plotW} y1={yScale(v)} y2={yScale(v)} stroke="#e8ecef" strokeWidth="1" />
          <text x={PAD.left - 8} y={yScale(v) + 4} textAnchor="end" fontSize="11" fill="#888">{v}</text>
        </Fragment>
      ))}
      {bins.map((count, q) => {
        if (count === 0) return null;
        const x = xScale(q) + (plotW / (maxQ + 1) - barW) / 2;
        const barH = (count / maxCount) * plotH;
        return (
          <Fragment key={q}>
            <rect x={x} y={yScale(count)} width={barW} height={barH} fill={color} opacity="0.85" rx="1" />
            {count >= 3 && barW >= 8 && <text x={x + barW / 2} y={yScale(count) - 3} textAnchor="middle" fontSize="8" fill="#666" fontWeight="600">{count}</text>}
          </Fragment>
        );
      })}
      {/* Label every whole year */}
      {bins.map((_, q) => (
        q % 4 === 0 ? <text key={q} x={xScale(q) + plotW / (maxQ + 1) / 2} y={PAD.top + plotH + 16} textAnchor="middle" fontSize="10" fill="#666" fontWeight={q % 4 === 0 ? '600' : '400'}>{q / 4}y</text> : null
      ))}
      <line x1={PAD.left} x2={PAD.left} y1={PAD.top} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
      <line x1={PAD.left} x2={PAD.left + plotW} y1={PAD.top + plotH} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
      <text x={PAD.left + plotW / 2} y={H - 2} textAnchor="middle" fontSize="12" fill="#666">{xLabel}</text>
    </svg>
  );
}

/** Age distribution for the initial cohort of adult-chipped penguins — birds first
 *  scanned in the colony's earliest season (same cohort as the survival report). */
function PenguinAgeCharts() {
  const v = useDbVersion();
  const allPenguins = useAllPenguins();
  const { adultQs, firstSeason } = useMemo(() => {
    const firstSeen = new Map<string, number>();
    const lastSeen = new Map<string, number>();
    const adultChipped = new Set(allPenguins.filter((p: any) => p.chipped_as_adult).map((p: any) => p.peng_num));
    let firstSeason = '';
    for (const { detail: bd } of allColonyBoxes()) {
      for (const obs of bd.observations) {
        const t = parseDate(obs.observation_time_utc).getTime();
        const season = getSeasonLabel(parseDate(obs.observation_time_utc));
        if (!firstSeason || season < firstSeason) firstSeason = season;
        for (const s of obs.scans || []) {
          if (!s.peng_num || !adultChipped.has(s.peng_num)) continue;
          const prev = firstSeen.get(s.peng_num);
          if (prev === undefined || t < prev) firstSeen.set(s.peng_num, t);
          const prevL = lastSeen.get(s.peng_num);
          if (prevL === undefined || t > prevL) lastSeen.set(s.peng_num, t);
        }
      }
    }
    const qs: number[] = [];
    for (const [num, first] of firstSeen) {
      if (getSeasonLabel(new Date(first)) !== firstSeason) continue; // initial cohort only
      const last = lastSeen.get(num)!;
      if (last <= first) continue;
      qs.push(Math.floor((last - first) / (1000 * 60 * 60 * 24 * 365.25 / 4)));
    }
    return { adultQs: qs, firstSeason };
  }, [v, allPenguins]);

  if (adultQs.length === 0) return <div className="report-card"><h3>Penguin ages</h3><p className="muted">No data available</p></div>;

  return (
    <div className="report-card">
      <h3>Adult-chipped penguin ages</h3>
      <p className="muted">Time between earliest and most recent scan for adult-chipped penguins in the initial cohort — birds first scanned in {seasonRange(firstSeason)} (n={adultQs.length})</p>
      <AgeBarChart quarters={adultQs} color="#2196F3" xLabel="Time between first and last scan" />
    </div>
  );
}

/** Survival curve from the first-season adult cohort.
 *  Birds chipped in season 1 were a cross-section of ages; annual attrition from that
 *  cohort gives mortality rate → predicted expected lifespan. */
function SurvivalPredictionReport() {
  const v = useDbVersion();
  const allPenguins = useAllPenguins();
  const chickReturn = useChickReturn();
  const result = useMemo(() => {
    // Find the earliest season any adult-chipped bird was scanned in.
    const birdSeasons = new Map<string, Set<string>>();
    const adultChipped = new Set(allPenguins.filter((p: any) => p.chipped_as_adult).map((p: any) => p.peng_num));
    let allSeasons = new Set<string>();
    for (const { detail: bd } of allColonyBoxes()) {
      for (const obs of bd.observations) {
        const season = getSeasonLabel(parseDate(obs.observation_time_utc));
        allSeasons.add(season);
        for (const s of obs.scans || []) {
          if (!s.peng_num || !adultChipped.has(s.peng_num)) continue;
          let ss = birdSeasons.get(s.peng_num);
          if (!ss) { ss = new Set(); birdSeasons.set(s.peng_num, ss); }
          ss.add(season);
        }
      }
    }
    const sortedSeasons = Array.from(allSeasons).sort();
    if (sortedSeasons.length < 3) return null;
    const firstSeason = sortedSeasons[0];
    // Cohort: adult-chipped birds scanned in the first season
    const cohort = Array.from(birdSeasons.entries())
      .filter(([, ss]) => ss.has(firstSeason))
      .map(([num, ss]) => ({ num, seasons: ss }));
    if (cohort.length < 5) return null;

    // For each subsequent season, how many of the cohort were still seen
    const curve: { season: string; alive: number; pct: number }[] = [];
    for (const season of sortedSeasons) {
      const alive = cohort.filter(b => b.seasons.has(season)).length;
      curve.push({ season, alive, pct: alive / cohort.length * 100 });
    }

    return { cohort: cohort.length, firstSeason, curve, sortedSeasons };
  }, [v, allPenguins]);

  // Backtest slider: fit the model on only the first N observed seasons (2021–22, 2021–23, …),
  // while the full observed curve stays on the chart — so a prediction made with less history
  // can be judged against what actually happened since. null = use all observed seasons.
  const [fitCount, setFitCount] = useState<number | null>(null);
  const effFitCount = result ? Math.max(2, Math.min(fitCount ?? result.curve.length, result.curve.length)) : 0;

  const fit = useMemo(() => {
    if (!result) return null;
    const curveFit = result.curve.slice(0, effFitCount);

    // First principles survival model:
    // S(t) = max(0, 100 - b*t - d*(1 - e^(-k*t)))
    //   - Starts at 100% (t=0: 100 - 0 - 0 = 100)
    //   - b = steady linear attrition (% lost per season for established adults)
    //   - d = total early excess mortality (% that die young, saturating over time)
    //   - k = rate at which early mortality plays out
    //
    // For large t: S(t) ≈ (100 - d) - b*t, a line with intercept (100-d) and slope -b.
    // So fit a line to the stable portion to get b and d, then estimate k from early residuals.

    const stableStart = Math.min(2, curveFit.length - 2);
    const stablePts = curveFit.slice(stableStart).map((c, i) => ({ x: i + stableStart, y: c.pct }));
    let sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
    for (const p of stablePts) { sumX += p.x; sumY += p.y; sumXY += p.x * p.y; sumX2 += p.x * p.x; }
    const n = stablePts.length;
    const lateSlope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
    const lateIntercept = (sumY - lateSlope * sumX) / n;

    // b = steady loss rate, d = early excess mortality
    const b = -lateSlope; // positive: % lost per season
    const d = Math.max(0, 100 - lateIntercept); // gap between 100% and where the linear extrapolates to at t=0

    // Estimate k from early data: at t=1, S(1) = 100 - b - d*(1-e^(-k))
    // So d*(1-e^(-k)) = 100 - b - S(1), giving e^(-k) = 1 - (100-b-S(1))/d
    let k = 1.0;
    if (d > 0 && curveFit.length >= 2) {
      const earlyLoss = 100 - b - curveFit[1].pct; // how much was lost by season 1 beyond linear
      const ratio = earlyLoss / d;
      if (ratio > 0 && ratio < 1) {
        k = -Math.log(1 - ratio);
      }
    }

    const model = (t: number) => Math.max(0, 100 - b * t - d * (1 - Math.exp(-k * t)));

    // Season at which model hits zero (search forward)
    let zeroAt: number | null = null;
    for (let t = 0; t < 50; t += 0.1) {
      if (model(t) <= 0) { zeroAt = t; break; }
    }
    // Median residency: when model crosses 50%
    let medianAt: number | null = null;
    for (let t = 0; t < 50; t += 0.1) {
      if (model(t) <= 50) { medianAt = t; break; }
    }

    return { b, d, k, model, zeroAt, medianAt };
  }, [result, effFitCount]);

  if (!result || !fit) return null;

  const { cohort, firstSeason, curve } = result;
  const { b, d, k, model, zeroAt } = fit;

  // Extend prediction into future until model reaches zero
  const futureSeasons: string[] = [];
  const lastSeasonYear = parseInt(result.sortedSeasons[result.sortedSeasons.length - 1]);
  for (let y = lastSeasonYear + 1; ; y++) {
    const t = curve.length + futureSeasons.length;
    if (model(t) <= 0) { futureSeasons.push(String(y)); break; }
    futureSeasons.push(String(y));
    if (futureSeasons.length > 30) break;
  }
  const totalPoints = curve.length + futureSeasons.length;

  // Draw survival curve
  const W = 600, H = 280, PAD = { top: 30, right: 20, bottom: 55, left: 55 };
  const plotW = W - PAD.left - PAD.right;
  const plotH = H - PAD.top - PAD.bottom;
  const xScale = (i: number) => PAD.left + (i / (totalPoints - 1)) * plotW;
  const yScale = (pct: number) => PAD.top + plotH - (pct / 100) * plotH;

  // All x-axis labels (observed + future)
  const allLabels = [...curve.map(c => c.season), ...futureSeasons];

  return (
    <div className="report-card">
      <h3>Adult residency (first-season cohort)</h3>
      <p className="muted">Survival curve of {cohort} adult-chipped birds from the first monitoring season ({firstSeason}) — annual attrition predicts median time an adult remains in the colony</p>
      <div style={{display:'flex', alignItems:'center', gap:10, justifyContent:'center', margin:'0.2em 0 0.5em', flexWrap:'wrap'}}>
        <span style={{fontSize:'0.8em', color:'#888'}}>Predictor data:</span>
        <input type="range" min={2} max={curve.length} step={1} value={effFitCount}
          onChange={e => { const n = parseInt(e.target.value); setFitCount(n >= curve.length ? null : n); }}
          style={{width:180}} title="How many observed seasons the model is fitted on — hollow points are held out, so you can see how an earlier prediction compares with what actually happened" />
        <span style={{fontSize:'0.8em', fontWeight:600, color: effFitCount < curve.length ? '#FF9800' : '#888'}}>
          {firstSeason}–{curve[effFitCount - 1].season}{effFitCount === curve.length ? ' (all data)' : ` (${curve.length - effFitCount} season${curve.length - effFitCount !== 1 ? 's' : ''} held out)`}
        </span>
      </div>
      <svg viewBox={`0 0 ${W} ${H}`} className="report-chart">
        {[25, 50, 75, 100].map(pct => (
          <Fragment key={pct}>
            <line x1={PAD.left} x2={PAD.left + plotW} y1={yScale(pct)} y2={yScale(pct)} stroke="#e8ecef" strokeWidth="1" />
            <text x={PAD.left - 8} y={yScale(pct) + 4} textAnchor="end" fontSize="11" fill="#888">{pct}%</text>
          </Fragment>
        ))}
        {/* Boundary between observed and predicted */}
        <line x1={xScale(curve.length - 1)} x2={xScale(curve.length - 1)} y1={PAD.top} y2={PAD.top + plotH} stroke="#ddd" strokeWidth="1" strokeDasharray="3,3" />
        {/* Backtest cutoff: model fitted only on data left of this line */}
        {effFitCount < curve.length && (
          <line x1={xScale(effFitCount - 1)} x2={xScale(effFitCount - 1)} y1={PAD.top} y2={PAD.top + plotH} stroke="#FF9800" strokeWidth="1.5" strokeDasharray="4,3" />
        )}
        {/* Actual curve */}
        <polyline
          points={curve.map((c, i) => `${xScale(i)},${yScale(c.pct)}`).join(' ')}
          fill="none" stroke="#2196F3" strokeWidth="2.5"
        />
        {/* Combined model: linear + exponential early mortality */}
        <polyline
          points={Array.from({ length: totalPoints }, (_, i) => `${xScale(i)},${yScale(model(i))}`).join(' ')}
          fill="none" stroke="#f44336" strokeWidth="1.5" strokeDasharray="4,3" opacity="0.7"
        />
        {/* Data points — hollow beyond the fit cutoff (held out from the predictor) */}
        {curve.map((c, i) => (
          <circle key={i} cx={xScale(i)} cy={yScale(c.pct)} r="3"
            fill={i < effFitCount ? '#2196F3' : '#fff'} stroke="#2196F3" strokeWidth={i < effFitCount ? 0 : 1.5} />
        ))}
        {/* X axis labels */}
        {allLabels.map((label, i) => (
          i % 2 === 0 || totalPoints <= 12 ? <text key={i} x={xScale(i)} y={PAD.top + plotH + 16} textAnchor="middle" fontSize="9" fill={i >= curve.length ? '#bbb' : '#666'} transform={`rotate(-30, ${xScale(i)}, ${PAD.top + plotH + 16})`}>{label}</text> : null
        ))}
        <line x1={PAD.left} x2={PAD.left} y1={PAD.top} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
        <line x1={PAD.left} x2={PAD.left + plotW} y1={PAD.top + plotH} y2={PAD.top + plotH} stroke="#ccc" strokeWidth="1" />
      </svg>
      <div style={{display:'flex', gap:'2em', justifyContent:'center', margin:'0.8em 0', flexWrap:'wrap'}}>
        <div style={{textAlign:'center'}}>
          <div style={{fontSize:'1.4em', fontWeight:700, color:'#f44336'}}>{b.toFixed(1)}%</div>
          <div style={{fontSize:'0.8em', color:'#888'}}>Lost per season (steady)</div>
        </div>
        <div style={{textAlign:'center'}}>
          <div style={{fontSize:'1.4em', fontWeight:700, color:'#FF9800'}}>{d.toFixed(0)}%</div>
          <div style={{fontSize:'0.8em', color:'#888'}}>Early excess mortality</div>
        </div>
        {zeroAt && (() => {
          const decayLifespan = (100 - d) / b;
          // Mean age at first return across all size classes
          const returnTotals = chickReturn?.totals;
          const allAges = returnTotals ? ['LC','BC','SC'].flatMap((s: string) => {
            const t = returnTotals[s];
            return t?.avg_return_age ? [{ age: t.avg_return_age, n: t.returned }] : [];
          }) : [];
          const meanReturnAge = allAges.length > 0
            ? allAges.reduce((s, a) => s + a.age * a.n, 0) / allAges.reduce((s, a) => s + a.n, 0)
            : null;
          const totalLifespan = meanReturnAge ? decayLifespan + meanReturnAge : null;
          return (
            <div style={{textAlign:'center'}}>
              <div style={{fontSize:'1.4em', fontWeight:700, color:'#4CAF50'}}>{totalLifespan ? totalLifespan.toFixed(1) : decayLifespan.toFixed(1)} years</div>
              <div style={{fontSize:'0.8em', color:'#888'}}>Predicted age at last scan</div>
              <div style={{fontSize:'0.7em', color:'#aaa'}}>{meanReturnAge ? `${meanReturnAge.toFixed(1)}y return age + ${decayLifespan.toFixed(1)}y adult residency` : `${decayLifespan.toFixed(1)}y adult residency`}</div>
            </div>
          );
        })()}
      </div>
      <div style={{display:'flex', gap:'1.5em', justifyContent:'center', fontSize:'0.85em', margin:'0.3em 0'}}>
        <span><span style={{display:'inline-block', width:16, height:3, backgroundColor:'#2196F3', verticalAlign:'middle', marginRight:4}}></span> Observed</span>
        {effFitCount < curve.length && <span><span style={{display:'inline-block', width:9, height:9, borderRadius:'50%', border:'1.5px solid #2196F3', verticalAlign:'middle', marginRight:4}}></span> Held out of fit</span>}
        <span><span style={{display:'inline-block', width:16, height:3, backgroundColor:'#f44336', verticalAlign:'middle', marginRight:4, borderTop:'1.5px dashed #f44336'}}></span> S(t) = 100 − {b.toFixed(1)}t − {d.toFixed(0)}(1 − e<sup style={{fontSize:'0.75em'}}>−{k.toFixed(1)}t</sup>)</span>
      </div>
    </div>
  );
}

/** Pair bond duration: how many consecutive seasons the same two adults share a box. */
function PairBondReport({ onOpenBird }: { onOpenBird: (num: string) => void }) {
  const v = useDbVersion();
  // Count non-consecutive breeding years too: rank and threshold on total seasons together
  // rather than only the longest unbroken run.
  const [nonConsec, setNonConsec] = useState(false);
  const allRows = useMemo(() => {
    // For each box+season, find the detected breeding pair. Then track the seasons the same
    // pair appears together at ANY box.
    const pairSeasons = new Map<string, { a: any; b: any; seasons: Set<string>; boxes: Set<string> }>();
    for (const { loc, families } of allColonyBoxes()) {
      for (const sd of families) {
        for (const fam of sd.families) {
          if (fam.parents.length < 2) continue;
          const nums = fam.parents.map((p: any) => p.peng_num).filter(Boolean).sort();
          if (nums.length < 2) continue;
          const key = nums[0] + '+' + nums[1];
          let e = pairSeasons.get(key);
          if (!e) { e = { a: fam.parents.find((p: any) => p.peng_num === nums[0]), b: fam.parents.find((p: any) => p.peng_num === nums[1]), seasons: new Set(), boxes: new Set() }; pairSeasons.set(key, e); }
          e.seasons.add(sd.label);
          e.boxes.add(String(loc.location_name).trim());
        }
      }
    }
    // Longest consecutive run, and the full season list (for the non-consecutive reading).
    return Array.from(pairSeasons.values()).map(e => {
      const sorted = Array.from(e.seasons).map(Number).sort((a, b) => a - b);
      let maxRun = 1, run = 1;
      for (let i = 1; i < sorted.length; i++) {
        if (sorted[i] === sorted[i - 1] + 1) { run++; if (run > maxRun) maxRun = run; }
        else run = 1;
      }
      return { a: e.a, b: e.b, totalSeasons: sorted.length, consecutive: maxRun,
        years: sorted.map(y => seasonRange(String(y))).join(', '),
        boxes: Array.from(e.boxes).sort((a, b) => a.localeCompare(b, undefined, { numeric: true })) };
    });
  }, [v]);
  // Duration is the metric being ranked/filtered: consecutive run, or total seasons when
  // non-consecutive years are counted. Keep only bonds lasting more than two seasons.
  const rows = useMemo(() => {
    const dur = (r: typeof allRows[number]) => nonConsec ? r.totalSeasons : r.consecutive;
    return allRows.filter(r => dur(r) > 2)
      .sort((a, b) => dur(b) - dur(a) || b.totalSeasons - a.totalSeasons || b.consecutive - a.consecutive);
  }, [allRows, nonConsec]);
  const [shown, showAllBtn] = useTopRows(rows);

  return (
    <div className="report-card">
      <h3>Pair bond duration</h3>
      <div className="group-method-row">
        <button className={!nonConsec ? 'active' : ''} onClick={() => setNonConsec(false)}>Consecutive years</button>
        <button className={nonConsec ? 'active' : ''} onClick={() => setNonConsec(true)}>Incl. non-consecutive</button>
      </div>
      <p className="muted">
        Breeding pairs detected together for more than two seasons, ranked by {nonConsec
          ? 'total seasons bred together (gaps allowed)'
          : 'longest unbroken run of seasons'}.
      </p>
      {rows.length === 0 ? <p className="muted">No data available</p> : (
        <table className="guess-rank-table mini-list-table">
          <thead><tr><th>Pair</th><th>Consecutive</th><th>Total seasons</th><th>Seasons</th><th>Boxes</th></tr></thead>
          <tbody>
            {shown.map((r, i) => (
              <tr key={i}>
                <td>
                  <div className="group-members">
                    {[r.a, r.b].sort(scanSortMFC).map((p, k) => (
                      <PenguinMini key={k} scan={p} onClick={() => onOpenBird(p.peng_num)} />
                    ))}
                  </div>
                </td>
                <td>{nonConsec ? r.consecutive : <strong>{r.consecutive}</strong>}</td>
                <td>{nonConsec ? <strong>{r.totalSeasons}</strong> : r.totalSeasons}</td>
                <td className="muted">{r.years}</td>
                <td>{r.boxes.join(', ')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {showAllBtn}
    </div>
  );
}

/** Philanderers & philandereuses — the inverse of the pair-bond report. Two readings,
 *  toggled at the top:
 *   • "Shared sightings" — every bird ever co-scanned in the same observation. The literal
 *     reading, so it ranks sociable birds: own chicks, siblings and box visitors all count.
 *   • "Breeding partners" — distinct DETECTED mates across clutches (same nest-family
 *     detection as the pair bond / top parents reports), plus how often the bird ran two
 *     mates in one season and how often it swapped mate while the old one was still alive. */
function PhilandererReport({ onOpenBird }: { onOpenBird: (num: string) => void }) {
  const v = useDbVersion();
  const [mode, setMode] = useState<'shared' | 'mates'>('mates');
  const [sex, setSex] = useState<'all' | 'M' | 'F'>('all');

  const data = useMemo(() => {
    // Shared sightings: distinct co-scanned birds, and how many observations the bird
    // shared with anyone at all.
    if (mode === 'shared') {
      const info = new Map<string, any>();
      const withOthers = new Map<string, Map<string, number>>();
      const sharedObs = new Map<string, number>();
      const boxes = new Map<string, Set<string>>();
      for (const { box: rawBox, detail: bd } of allColonyBoxes()) {
        const box = rawBox.trim();
        for (const o of bd?.observations || []) {
          // One entry per bird — a bird scanned twice in one observation is one presence.
          const present = new Map<string, any>();
          for (const s of o.scans || []) if (s.peng_num && !present.has(s.peng_num)) present.set(s.peng_num, s);
          for (const [num, s] of present) if (!info.has(num)) info.set(num, s);
          if (present.size < 2) continue;
          for (const num of present.keys()) {
            sharedObs.set(num, (sharedObs.get(num) || 0) + 1);
            if (!boxes.has(num)) boxes.set(num, new Set());
            boxes.get(num)!.add(box);
            let m = withOthers.get(num);
            if (!m) { m = new Map(); withOthers.set(num, m); }
            for (const other of present.keys()) if (other !== num) m.set(other, (m.get(other) || 0) + 1);
          }
        }
      }
      return Array.from(withOthers.entries()).map(([num, m]) => ({
        bird: info.get(num),
        n: m.size,
        sightings: sharedObs.get(num) || 0,
        boxes: boxes.get(num)?.size || 0,
        top: Array.from(m.entries()).sort((a, b) => b[1] - a[1]).slice(0, 8)
          .map(([n2, c]) => ({ bird: info.get(n2), label: `×${c}` })),
      }));
    }

    // Breeding partners: mates from detected nest families, with two-timing and divorce counts.
    const deathOf = new Map<string, string | null>(computeAllPenguinsRows().map((p: any) => [p.peng_num, p.death_date]));
    const info = new Map<string, any>();
    const mates = new Map<string, Map<string, { bird: any; seasons: Set<string>; boxes: Set<string> }>>();
    const bySeason = new Map<string, Map<string, Set<string>>>(); // bird -> season -> mates that season
    const clutches = new Map<string, number>();
    for (const { box: rawBox, families } of allColonyBoxes()) {
      const box = rawBox.trim();
      for (const sd of families) {
        for (const fam of sd.families) {
          // Solo-parent fallbacks tell us nothing about mate choice — need both slots filled.
          const ps = fam.parents.filter((p: any) => p?.peng_num);
          if (ps.length < 2) continue;
          for (const a of ps) {
            if (!info.has(a.peng_num)) info.set(a.peng_num, a);
            clutches.set(a.peng_num, (clutches.get(a.peng_num) || 0) + 1);
            let mm = mates.get(a.peng_num);
            if (!mm) { mm = new Map(); mates.set(a.peng_num, mm); }
            let ss = bySeason.get(a.peng_num);
            if (!ss) { ss = new Map(); bySeason.set(a.peng_num, ss); }
            let sset = ss.get(sd.label);
            if (!sset) { sset = new Set(); ss.set(sd.label, sset); }
            for (const b of ps) {
              if (b.peng_num === a.peng_num) continue;
              let e = mm.get(b.peng_num);
              if (!e) { e = { bird: b, seasons: new Set(), boxes: new Set() }; mm.set(b.peng_num, e); }
              e.seasons.add(sd.label);
              e.boxes.add(box);
              sset.add(b.peng_num);
            }
          }
        }
      }
    }
    return Array.from(mates.entries()).map(([num, mm]) => {
      const seasons = Array.from(bySeason.get(num) || []).sort((a, b) => a[0].localeCompare(b[0]));
      const twoTimed = seasons.filter(([, set]) => set.size >= 2).length;
      // Divorce: between two seasons the bird bred in, a mate is dropped while still alive.
      // Deaths are the innocent explanation for a switch, so they don't count.
      let divorces = 0;
      for (let i = 1; i < seasons.length; i++) {
        const start = new Date(parseInt(seasons[i][0], 10), SEASON_START_MONTH - 1, SEASON_START_DAY).getTime();
        for (const old of seasons[i - 1][1]) {
          if (seasons[i][1].has(old)) continue;
          const d = deathOf.get(old);
          if (!d || parseDate(d).getTime() > start) { divorces++; break; }
        }
      }
      return {
        bird: info.get(num),
        n: mm.size,
        clutches: clutches.get(num) || 0,
        twoTimed,
        divorces,
        top: Array.from(mm.values())
          .sort((a, b) => b.seasons.size - a.seasons.size)
          .slice(0, 8)
          .map(e => ({ bird: e.bird, label: `${e.seasons.size}s` })),
      };
    });
  }, [v, mode]);

  const COLS: { key: string; label: string; value: (r: any) => number }[] = mode === 'shared'
    ? [{ key: 'n', label: 'Birds seen with', value: r => r.n },
       { key: 'sightings', label: 'Shared sightings', value: r => r.sightings },
       { key: 'boxes', label: 'Boxes', value: r => r.boxes }]
    : [{ key: 'n', label: 'Mates', value: r => r.n },
       { key: 'twoTimed', label: 'Two-timed seasons', value: r => r.twoTimed },
       { key: 'divorces', label: 'Divorces', value: r => r.divorces },
       { key: 'clutches', label: 'Clutches', value: r => r.clutches }];
  // One-way sorting, as on the parent leaderboards: clicking a column ranks by it, highest first.
  const [sortKey, setSortKey] = useState('n');
  const rows = useMemo(() => {
    const col = COLS.find(c => c.key === sortKey) || COLS[0];
    return data
      .filter((r: any) => r.bird && (sex === 'all' || guessedSex(r.bird) === sex))
      .filter((r: any) => r.n >= 2) // one partner is a faithful bird, not a philanderer
      .sort((a: any, b: any) => (col.value(b) - col.value(a))
        || (COLS[0].value(b) - COLS[0].value(a))
        || comparePengNum(a.bird.peng_num, b.bird.peng_num));
  }, [data, sortKey, sex, mode]);

  // Podium by default, like the other leaderboards — the tail is long and rarely read.
  const [shown, showAllBtn] = useTopRows(rows);
  const arrow = (key: string) => sortKey === key ? ' ▼' : '';

  return (
    <div className="report-card">
      <h3>Philanderers &amp; philandereuses</h3>
      <div className="group-method-row">
        <button className={mode === 'mates' ? 'active' : ''}
          onClick={() => { setMode('mates'); setSortKey('n'); }}>Breeding partners</button>
        <button className={mode === 'shared' ? 'active' : ''}
          onClick={() => { setMode('shared'); setSortKey('n'); }}>Shared sightings</button>
        <span style={{ width: 12 }} />
        {([['all', 'Both'], ['M', '♂ Philanderers'], ['F', '♀ Philandereuses']] as const).map(([id, label]) => (
          <button key={id} className={sex === id ? 'active' : ''} onClick={() => setSex(id)}>{label}</button>
        ))}
      </div>
      <p className="muted">
        {mode === 'mates'
          ? 'Birds detected as a parent alongside more than one mate, ranked by distinct mates. Two-timed seasons are seasons with two or more mates; a divorce is a mate dropped between seasons while still alive (deaths don’t count). Click a column to sort.'
          : 'Birds co-scanned in the same observation with the most others — the literal shared-sighting count, so a bird’s own chicks, siblings and passing visitors all count towards it. Click a column to sort.'}
      </p>
      <p className="muted" style={{ fontSize: 12 }}>
        With only a couple of checks per clutch, an extra "mate" is often just a bird standing in the box, and unsexed
        birds fill a pair slot on a guessed sex — so some of this ranking is detection artefact, not infidelity.
      </p>
      {rows.length === 0 ? <p className="muted">No data available</p> : (
        <div className="table-scroll">
          <table className="guess-rank-table mini-list-table">
            <thead><tr>
              <th>Penguin</th>
              {COLS.map(c => (
                <th key={c.key} className="clickable" style={{ cursor: 'pointer', whiteSpace: 'nowrap' }} onClick={() => setSortKey(c.key)}>{c.label}{arrow(c.key)}</th>
              ))}
              <th>{mode === 'mates' ? 'Mates (seasons)' : 'Seen with (times)'}</th>
            </tr></thead>
            <tbody>
              {shown.map((r: any) => (
                <tr key={r.bird.peng_num}>
                  <td><PenguinMini scan={r.bird} onClick={() => onOpenBird(r.bird.peng_num)} /></td>
                  {COLS.map((c, ci) => <td key={c.key}>{ci === 0 ? <strong>{c.value(r)}</strong> : c.value(r)}</td>)}
                  <td>
                    <div className="group-members">
                      {r.top.map((t: any, ti: number) => (
                        <PenguinMini key={ti} scan={t.bird} title={t.label} onClick={() => onOpenBird(t.bird.peng_num)} />
                      ))}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {showAllBtn}
        </div>
      )}
    </div>
  );
}

/** Non-breeding "floaters": birds scanned at multiple boxes but never detected as a breeding parent. */
function FloaterReport({ onOpenBird }: { onOpenBird: (num: string) => void }) {
  const v = useDbVersion();
  const rows = useMemo(() => {
    const parentNums = new Set<string>();
    const birdBoxes = new Map<string, { info: any; boxes: Map<string, number>; totalScans: number }>();
    for (const { box: rawBox, detail: bd, families } of allColonyBoxes()) {
      const box = rawBox.trim();
      // Collect parents
      for (const sd of families) {
        for (const fam of sd.families) {
          for (const p of fam.parents) if (p.peng_num) parentNums.add(p.peng_num);
        }
      }
      // Collect scan counts per box
      for (const obs of bd.observations) {
        for (const s of obs.scans || []) {
          if (!s.peng_num) continue;
          let e = birdBoxes.get(s.peng_num);
          if (!e) { e = { info: s, boxes: new Map(), totalScans: 0 }; birdBoxes.set(s.peng_num, e); }
          e.boxes.set(box, (e.boxes.get(box) || 0) + 1);
          e.totalScans++;
        }
      }
    }
    // Only adults (chipped_as_adult or >90 days since chip), scanned at 2+ boxes, never a parent
    return Array.from(birdBoxes.entries())
      .filter(([num, e]) => {
        if (parentNums.has(num)) return false;
        if (e.boxes.size < 2) return false;
        const info = e.info;
        if (info.chipped_as_adult) return true;
        if (info.chip_date) {
          const daysSinceChip = (Date.now() - parseDate(info.chip_date).getTime()) / (1000 * 60 * 60 * 24);
          return daysSinceChip > 90;
        }
        return false;
      })
      .map(([, e]) => ({
        bird: e.info,
        boxCount: e.boxes.size,
        totalScans: e.totalScans,
        boxes: Array.from(e.boxes.entries()).sort((a, b) => b[1] - a[1]).slice(0, 8).map(([b, c]) => `${b} (${c})`),
      }))
      .sort((a, b) => b.boxCount - a.boxCount || b.totalScans - a.totalScans)
      .slice(0, 25);
  }, [v]);
  const [shown, showAllBtn] = useTopRows(rows);

  return (
    <div className="report-card">
      <h3>Possible floaters</h3>
      <p className="muted">Adult birds scanned at 2+ boxes but never detected as a breeding parent — possible non-breeding floaters (top 25)</p>
      {rows.length === 0 ? <p className="muted">No data available</p> : (
        <table className="guess-rank-table mini-list-table">
          <thead><tr><th>Penguin</th><th>Boxes</th><th>Scans</th><th>Seen at</th></tr></thead>
          <tbody>
            {shown.map((r, i) => (
              <tr key={i}>
                <td><PenguinMini scan={r.bird} onClick={() => onOpenBird(r.bird.peng_num)} /></td>
                <td><strong>{r.boxCount}</strong></td>
                <td>{r.totalScans}</td>
                <td style={{fontSize:'0.85em'}}>{r.boxes.join(', ')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {showAllBtn}
    </div>
  );
}

function DayCalendar({ date, dates, onDayClick }: { date: string; dates: string[]; onDayClick: (day: string) => void }) {
  const { show: showTip, hide: hideTip, statsCache, registeredFmDates } = useContext(DateTooltipCtx);
  const dateSet = useMemo(() => new Set(dates), [dates]);
  // Partial Monitor dates: registered as PM in the enter-date workflow. Teal, and always
  // teal — they're a deliberate partial round, so they never read as full (green) or missed
  // (orange) regardless of how many boxes were observed.
  const partialMonitorDates = useMemo(() => {
    const s = new Set<string>();
    for (const [d, r] of registeredFmDates) { if (r.partial) s.add(d); }
    return s;
  }, [registeredFmDates]);
  const fullMonitorDates = useMemo(() => {
    const fm = new Set<string>();
    for (const d of dates) { const s = statsCache.get(d); if (s?.isFullMonitor && !partialMonitorDates.has(d)) fm.add(d); }
    return fm;
  }, [dates, statsCache, partialMonitorDates]);
  // Dates registered as FM in the enter-date workflow but not achieved (missing
  // observations) — flagged red so a skipped monitor day is obvious. PM dates are excluded.
  const missedFmDates = useMemo(() => {
    const s = new Set<string>();
    for (const d of registeredFmDates.keys()) { if (!statsCache.get(d)?.isFullMonitor && !partialMonitorDates.has(d)) s.add(d); }
    return s;
  }, [registeredFmDates, statsCache, partialMonitorDates]);

  // Group dates by month, show months around current date. With no date (e.g. a brand-new
  // colony with no observations) fall back to today so the calendar still renders.
  const valid = date && !isNaN(new Date(date + 'T00:00:00').getTime());
  const current = valid ? new Date(date + 'T00:00:00') : new Date();
  const currentMonth = current.getFullYear() * 12 + current.getMonth();

  // All months from first to last date (inclusive, no gaps)
  const allMonths = useMemo(() => {
    if (dates.length === 0) { const t = new Date(); return [t.getFullYear() * 12 + t.getMonth()]; } // empty colony → at least the current month
    const first = dates[0];
    const last = dates[dates.length - 1];
    const [fy, fm] = first.split('-').map(Number);
    const [ly, lm] = last.split('-').map(Number);
    const start = fy * 12 + (fm - 1);
    const end = ly * 12 + (lm - 1);
    const months: number[] = [];
    for (let m = start; m <= end; m++) months.push(m);
    return months;
  }, [dates]);

  const calRef = useRef<HTMLDivElement>(null);
  // Key the centring effect on stable primitives, not the `dates` array identity: callers
  // often rebuild `dates` every render (e.g. `[...x].sort()`), so depending on the array
  // itself re-fired this on unrelated re-renders — a date tooltip appearing — yanking the
  // horizontal scroll. Length + first/last capture the only change that matters (data loaded).
  const datesKey = dates.length ? `${dates.length}:${dates[0]}:${dates[dates.length - 1]}` : '';
  useEffect(() => {
    // Defer to the next frame so the scroll runs after the calendar (and its flex
    // parent) have laid out — otherwise the active day can be centred against a
    // stale width and the calendar opens scrolled to the wrong place.
    const raf = requestAnimationFrame(() => {
      // Centre the active day's month, not the day itself — switching days within a
      // month then leaves the calendar still, instead of jerking to re-centre each day.
      // Only this strip's own scrollLeft moves: scrollIntoView walks every scrollable
      // ancestor including the document, pulling the header and toolbar off the top.
      const el = calRef.current;
      const target = el?.querySelector('.cal-month.current') || el?.querySelector('.cal-day.active');
      if (!el || !target) return;
      const t = target.getBoundingClientRect(), c = el.getBoundingClientRect();
      el.scrollLeft += (t.left + t.width / 2) - (c.left + c.width / 2);
    });
    return () => cancelAnimationFrame(raf);
  }, [currentMonth, datesKey]);

  const MONTH_NAMES = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];

  return (
    <div className="day-calendar" ref={calRef}>
      {allMonths.map(monthKey => {
        const year = Math.floor(monthKey / 12);
        const month = monthKey % 12;
        const daysInMonth = new Date(year, month + 1, 0).getDate();
        const isCurrentMonth = monthKey === currentMonth;

        // Build weeks (Mon=0 ... Sun=6)
        const weeks: (number | null)[][] = [];
        let week: (number | null)[] = [];
        const firstDow = (new Date(year, month, 1).getDay() + 6) % 7; // Mon=0
        for (let i = 0; i < firstDow; i++) week.push(null);
        for (let day = 1; day <= daysInMonth; day++) {
          week.push(day);
          if (week.length === 7) { weeks.push(week); week = []; }
        }
        if (week.length > 0) { while (week.length < 7) week.push(null); weeks.push(week); }

        return (
          <div key={monthKey} className={`cal-month${isCurrentMonth ? ' current' : ''}`}>
            <div className="cal-month-label">{MONTH_NAMES[month]} {year}</div>
            <div className="cal-weeks">
              {weeks.map((w, wi) => (
                <div key={wi} className="cal-week">
                  {w.map((day, di) => {
                    if (day === null) return <span key={di} className="cal-day empty" />;
                    const d = `${year}-${String(month + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
                    const hasData = dateSet.has(d);
                    const isActive = d === date;
                    const isMissedFm = missedFmDates.has(d);
                    const isPartialFm = partialMonitorDates.has(d);
                    // A missed or partial-monitor FM date is interactive even with no observations,
                    // so its cell can be hovered/opened to see the registered FM/PM detail.
                    const interactive = hasData || isMissedFm || isPartialFm;
                    return (
                      <span
                        key={di}
                        className={`cal-day${hasData ? ' has-data' : ''}${isActive ? ' active' : ''}${isPartialFm ? ' pm-monitor' : fullMonitorDates.has(d) ? ' full-monitor' : ''}${isMissedFm ? ' fm-missed' : ''}`}
                        onClick={interactive ? () => onDayClick(d) : undefined}
                        onMouseEnter={interactive ? e => showTip(d, e) : undefined}
                        onMouseLeave={interactive ? hideTip : undefined}
                      >{day}</span>
                    );
                  })}
                </div>
              ))}
            </div>
          </div>
        );
      })}
    </div>
  );
}

/** Relative time for the day view's stalker mode: "12 minutes ago", "1h 20m ago". */
function timeAgo(utc: string): string {
  const mins = Math.floor((Date.now() - parseDate(utc).getTime()) / 60000);
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins} minute${mins === 1 ? '' : 's'} ago`;
  const h = Math.floor(mins / 60), m = mins % 60;
  return m ? `${h}h ${m}m ago` : `${h} hour${h === 1 ? '' : 's'} ago`;
}

/** Hover popup for a box's date on the day view: location name, the watched toggle,
 *  and the box's last five observations with their scanned birds and notes.
 *  Kept open while the pointer is over it. */
function BoxPeekPopup({ box, token, canEdit, pos, onMouseEnter, onMouseLeave, onBirdClick, viewDate }: {
  box: string; token?: string; canEdit?: boolean; pos: { x: number; y: number };
  onMouseEnter: () => void; onMouseLeave: () => void; onBirdClick?: (num: string) => void; viewDate?: string;
}) {
  const loc = queryAllLocations().find((l: any) => String(l.location_name) === String(box));
  // Show the box's history LEADING UP TO the day being viewed — the 5 most recent observations
  // strictly before it (the viewed day itself is already on the page). Newest first.
  const obs = (queryBoxDetailSync(box)?.observations || [])
    .filter((o: any) => !viewDate || toNzDateStr(o.observation_time_utc) < viewDate)
    .slice(0, 5);
  // Grow the popup toward whichever side of the cursor has more room, using that whole side's
  // width. Anchoring only to the right (and capping to the right-hand space) squeezed a
  // right-half box into a narrow column and forced the bird minis to wrap; this lets it widen
  // leftward instead. It wraps only when one observation genuinely can't fit the larger side.
  const PEEK_PAD = 8;
  const roomRight = window.innerWidth - pos.x - PEEK_PAD;
  const roomLeft = pos.x - PEEK_PAD;
  const horiz = roomRight >= roomLeft ? { left: pos.x } : { right: window.innerWidth - pos.x };
  const maxWidth = Math.max(roomRight, roomLeft);
  const top = pos.y + 170 > window.innerHeight ? Math.max(4, pos.y - 178) : pos.y;
  // Birds + notes can make the popup much taller than the flip heuristic assumes —
  // cap it to the space below `top` so it scrolls internally instead of overflowing.
  const maxHeight = Math.min(window.innerHeight * 0.6, window.innerHeight - top - 8);
  return (
    <div className="box-peek" style={{ ...horiz, top, maxHeight, maxWidth }} onMouseEnter={onMouseEnter} onMouseLeave={onMouseLeave}>
      <div className="box-peek-head">
        <b>Box {box}</b>
        {loc && <WatchedTick location={loc} token={token} canEdit={!!canEdit} />}
      </div>
      {obs.map((o: any) => (
        <div key={o.observation_id} className="box-peek-obs">
          <div className="box-peek-row">
            <span className="box-peek-date">{formatDate(o.observation_time_utc)}</span>
            {(o.adults || 0) > 0 && <span>{'🐧'.repeat(Math.min(o.adults, 4))}</span>}
            {(o.eggs || 0) > 0 && <span>{'🥚'.repeat(Math.min(o.eggs, 4))}</span>}
            {(o.chicks || 0) > 0 && <span>{'🐣'.repeat(Math.min(o.chicks, 4))}</span>}
            {o.breeding_status && o.breeding_status !== 'NO' && (
              <span className={`badge ${DARK_TEXT_STATUSES.has(o.breeding_status) ? 'bordered' : ''}`}
                style={{ background: STATUS_COLORS[o.breeding_status] || '#ccc', color: DARK_TEXT_STATUSES.has(o.breeding_status) ? '#333' : '#fff', fontSize: 10, padding: '1px 5px' }}>{o.breeding_status}</span>
            )}
            {o.gate_status && <span className="muted">{o.gate_status}</span>}
            {/* Scanned birds share this row — they stay inline while there's room and only
                wrap once the popup reaches its width cap. */}
            {[...(o.scans || [])].sort(scanSortMFC).map((s: any, j: number) => (
              <PenguinMini key={j} scan={s} onClick={() => onBirdClick?.(s.peng_num || s.pit_id)} observationDate={o.observation_time_utc} />
            ))}
          </div>
          {o.notes && <div className="box-peek-notes">{o.notes}</div>}
        </div>
      ))}
      {obs.length === 0 && <div className="muted">No records</div>}
    </div>
  );
}

function DayView({ date, dates, highlightBox, onBoxClick, onBirdClick: _onBirdClick, onDayClick, externalBird, token, canEdit, allPenguins: _allPenguins, peekCalendar, hideCalendar }: { date: string; dates: string[]; highlightBox?: string | null; onBoxClick: (box: string, date?: string) => void; onBirdClick: (num: string) => void; onDayClick: (day: string) => void; externalBird?: string | null; token?: string; canEdit?: boolean; allPenguins?: any[]; peekCalendar?: boolean; hideCalendar?: boolean }) {
  const data = useDayData(date);
  const loading = !data;
  const [sideBird, setSideBird] = useState<string|null>(null);
  const sideBirdData = useBirdDetail(sideBird);
  // Stable identity so DayCalendar's centre-on-mount effect (keyed on `dates`) doesn't
  // re-fire on unrelated re-renders — e.g. a date tooltip appearing — and yank the
  // calendar's horizontal scroll to the current month.
  const sorted = useMemo(() => [...dates].sort(), [dates]);

  useEffect(() => {
    if (externalBird) setSideBird(externalBird);
  }, [externalBird]);

  const handleBirdClick = (num: string) => setSideBird(num);
  // Day-view filters persist across days/sessions so a chosen view sticks as you navigate,
  // but only within the colony they were set on — the stored set is stamped with its colony
  // and ignored (then overwritten) once a different one is being viewed.
  const colonyId = getColonyId();
  const readChangedFields = (): string[] => { try { if (Number(localStorage.getItem('ww_day_changed_colony')) !== colonyId) return []; const a = JSON.parse(localStorage.getItem('ww_day_changed') || '[]'); return Array.isArray(a) ? a : []; } catch { return []; } };
  const [showCarryForward, setShowCarryForward] = useState(() => localStorage.getItem('ww_day_showall') === '1');
  const [hideDcm, setHideDcm] = useState(() => localStorage.getItem('ww_day_hidedcm') === '1');
  // "Only changed" filter: show boxes whose observation differs from the previous one (before this day)
  const [changedFields, setChangedFields] = useState<Set<string>>(() => new Set(readChangedFields()));
  // Expand the Changed section on load when any changed filter is already active.
  const [changedExpanded, setChangedExpanded] = useState(() => readChangedFields().length > 0);
  const toggleChangedField = (f: string) => setChangedFields(prev => {
    const next = new Set(prev);
    if (next.has(f)) next.delete(f); else next.add(f);
    return next;
  });
  useEffect(() => { localStorage.setItem('ww_day_showall', showCarryForward ? '1' : '0'); }, [showCarryForward]);
  useEffect(() => { localStorage.setItem('ww_day_hidedcm', hideDcm ? '1' : '0'); }, [hideDcm]);
  useEffect(() => {
    localStorage.setItem('ww_day_changed', JSON.stringify([...changedFields]));
    localStorage.setItem('ww_day_changed_colony', String(colonyId));
  }, [changedFields, colonyId]);
  // Switching colony while the day view stays mounted (the embedded panel's wwSetColony)
  // releases the filters too, so a "Changed" view never hides boxes in the colony you land on.
  const filterColony = useRef(colonyId);
  useEffect(() => {
    if (colonyId === filterColony.current) return;
    filterColony.current = colonyId;
    setChangedFields(new Set());
    setChangedExpanded(false);
  }, [colonyId]);

  // Stalker mode (today only): rows in scanned order, earliest first, each stamped
  // "N minutes ago". A minute tick keeps the relative times fresh while it's on.
  const isToday = date === toNzDateStr(new Date().toISOString());
  const [stalker, setStalker] = useState(() => localStorage.getItem('ww_day_stalker') === '1');
  useEffect(() => { localStorage.setItem('ww_day_stalker', stalker ? '1' : '0'); }, [stalker]);
  const [, setAgoTick] = useState(0);
  useEffect(() => {
    if (!stalker || !isToday) return;
    const id = setInterval(() => setAgoTick(t => t + 1), 60000);
    return () => clearInterval(id);
  }, [stalker, isToday]);
  const stalking = stalker && isToday;

  // Box peek popup on date hover. The delayed hide lets the pointer travel from the
  // date into the popup, where re-entering cancels the hide so it stays open.
  const [peek, setPeek] = useState<{ box: string; x: number; y: number } | null>(null);
  const peekTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const peekShow = (box: string, e: React.MouseEvent) => {
    clearTimeout(peekTimer.current);
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    peekTimer.current = setTimeout(() => setPeek({ box, x: rect.left, y: rect.bottom + 4 }), 250);
  };
  const peekHide = () => { clearTimeout(peekTimer.current); peekTimer.current = setTimeout(() => setPeek(null), 250); };
  const peekKeep = () => clearTimeout(peekTimer.current);

  // Quick status change from a day row: clicking the badge opens the same radial
  // picker as the observation card and writes breeding_status on that observation.
  const [dayPicker, setDayPicker] = useState<{ obsId: number; cur: string; x: number; y: number } | null>(null);
  const [statusOverrides, setStatusOverrides] = useState<Record<number, string>>({});
  const openDayPicker = (o: any, e: React.MouseEvent) => {
    e.stopPropagation();
    setDayPicker({ obsId: o.observation_id, cur: o.breeding_status || '', ...ringPos(e) });
  };
  const pickDayStatus = async (val: string) => {
    const p = dayPicker; setDayPicker(null);
    if (!p || !token) return;
    // Re-picking the current status clears it.
    const cur = statusOverrides[p.obsId] ?? p.cur;
    const next = val === cur ? '' : val;
    if (next === cur) return;
    setStatusOverrides(s => ({ ...s, [p.obsId]: next }));
    await updateRecord(token, 'observations', p.obsId, { breeding_status: next });
  };

  if (loading) return <div className="day-page"><p className="muted">Loading...</p></div>;
  if (!data || data.error) return <div className="day-page"><p className="muted">{data?.error || 'Failed to load'}</p></div>;


  // Group observations and chippings by box
  const byBox: Record<string, { obs: any[]; chips: any[] }> = {};
  for (const obs of data.observations) {
    const box = obs.box_name;
    if (!byBox[box]) byBox[box] = { obs: [], chips: [] };
    byBox[box].obs.push(obs);
  }
  for (const c of data.chippings) {
    const box = c.chip_box || '?';
    if (!byBox[box]) byBox[box] = { obs: [], chips: [] };
    byBox[box].chips.push(c);
  }
  const sortedBoxes = Object.keys(byBox).sort((a, b) => {
    const na = parseInt(a), nb = parseInt(b);
    return (!isNaN(na) && !isNaN(nb)) ? na - nb : a.localeCompare(b);
  });

  const totalObs = data.observations.length;
  const totalChips = data.chippings.length;

  const dayPageRef = useRef<HTMLDivElement>(null);
  const [calHidden, setCalHidden] = useState(false);

  // When the peng detail dock is open, the collapsed "show calendar" button sits to the
  // LEFT of it (at the dock's left edge) rather than over it. The dock is variable width,
  // so measure it and offset the fixed button by that width.
  const docked = !!(sideBird && sideBirdData?.penguin);
  const dockRef = useRef<HTMLDivElement>(null);
  const [calRight, setCalRight] = useState(16);
  useLayoutEffect(() => {
    const update = () => setCalRight(docked && dockRef.current ? dockRef.current.offsetWidth + 16 : 16);
    update();
    window.addEventListener('resize', update);
    return () => window.removeEventListener('resize', update);
  }, [docked, sideBirdData]);

  // When arriving from a box's date link, centre that box's row up-front (before paint,
  // so it doesn't jerk into place after the user has started scrolling) and highlight it.
  useLayoutEffect(() => {
    if (!highlightBox || !data) return;
    const el = dayPageRef.current?.querySelector(`[data-daybox="${(window.CSS && CSS.escape) ? CSS.escape(highlightBox) : highlightBox}"]`);
    if (el) (el as HTMLElement).scrollIntoView({ behavior: 'auto', block: 'nearest', inline: 'center' });
  }, [highlightBox, data]);

  return (
    <div className={`day-page${sideBird && sideBirdData?.penguin ? ' day-page-docked' : ''}`} ref={dayPageRef}>
      <div className="day-main">
      {!hideCalendar && (!calHidden || peekCalendar) && (
        <div style={{position:'relative'}}>
          <DayCalendar date={date} dates={sorted} onDayClick={onDayClick} />
          <button onClick={() => setCalHidden(true)} className="cal-toggle" style={{position:'absolute', bottom:-10, right:16}} title="Hide calendar">
            <svg viewBox="0 0 10 6" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
              <polyline points="1,5 5,1 9,5" />
            </svg>
          </button>
        </div>
      )}
      {!hideCalendar && calHidden && !peekCalendar && (
        <button onClick={() => setCalHidden(false)} className="cal-toggle cal-toggle-collapsed" style={{ right: calRight }} title="Show calendar">
          <svg viewBox="0 0 10 6" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
            <polyline points="1,1 5,5 9,1" />
          </svg>
        </button>
      )}
      {(totalObs > 0 || totalChips > 0) && (
        <div className="day-section">
          <h3 className="day-header-row">
            {/* The note sits inside the stats span, where the read-only label used to be, so it
                flows on with the sentence instead of becoming a flex item that wraps to its own row. */}
            <span className="day-stats">
              <DateStatsLine stats={{ ...(getDateStats().get(date) || { boxes:0, obs:0, adults:0, eggs:0, chicks:0, penguins:0, label:null, isFullMonitor:false, totalLocations:0 }), chipped: totalChips }} showDate date={date} hideLabel />
              {' '}<DayNoteEditor date={date} token={token} canEdit={canEdit} />
            </span>
            <button type="button" className={`day-changed-toggle${changedFields.size ? ' active' : ''}`} onClick={() => setChangedExpanded(v => !v)} title="Only show boxes whose observation differs from the previous one">
              Changed {changedExpanded ? '▴' : '▾'}
            </button>
            {isToday && (
              <button type="button" className={`day-changed-toggle${stalker ? ' active' : ''}`} onClick={() => setStalker(v => !v)}
                title="Show boxes in scanned order, earliest first, with how long ago each was visited">
                Stalker
              </button>
            )}
            {changedExpanded && CHANGED_FIELDS.map(f => (
              <label key={f.key} className="day-cf-toggle">
                <input type="checkbox" checked={changedFields.has(f.key)} onChange={() => toggleChangedField(f.key)} /> {f.label}
              </label>
            ))}
            {changedExpanded && changedFields.size > 0 && (
              <button type="button" className="day-changed-clear" onClick={() => setChangedFields(new Set())}>clear</button>
            )}
            <span className="day-cf-toggles">
              <label className="day-cf-toggle"><input type="checkbox" checked={showCarryForward} onChange={e => {
                const v = e.target.checked;
                setShowCarryForward(v);
                if (v) setChangedFields(new Set()); else setHideDcm(false);
              }} /> Show all</label>
              {showCarryForward && (
                <label className="day-cf-toggle"><input type="checkbox" checked={hideDcm} onChange={e => setHideDcm(e.target.checked)} /> Hide DCM</label>
              )}
            </span>
          </h3>
          <div className="day-grid">
          {(() => {
            // DCM boxes for this date
            const dcmBoxes = hideDcm ? getDcmBoxes(date) : new Set<string>();
            // Build carry-forward data if enabled
            const observedBoxes = new Set(sortedBoxes);
            const cfData = showCarryForward ? queryCarryForward(date, observedBoxes) : [];
            const cfByBox: Record<string, any> = {};
            for (const cf of cfData) cfByBox[cf.box_name] = cf;

            // Merge all boxes: observed + carry-forward, filter out DCM if enabled
            let allBoxNames = (showCarryForward
              ? [...new Set([...sortedBoxes, ...cfData.map((c: any) => c.box_name)])]
              : [...sortedBoxes]
            ).filter(b => !dcmBoxes.has(b)).sort((a, b) => {
              const na = parseInt(a), nb = parseInt(b);
              return (!isNaN(na) && !isNaN(nb)) ? na - nb : a.localeCompare(b);
            });

            // "Only changed" filter: keep boxes observed today whose observation differs from the
            // box's previous observation (before today) in a selected field. Carry-forward-only
            // boxes have no new observation, so they're excluded while this filter is active.
            let hiddenByChange = 0;
            if (changedFields.size > 0) {
              const observedBefore = allBoxNames.filter(b => observedBoxes.has(b)).length;
              const prevByBox = queryPreviousObservations(date, [...observedBoxes]);
              allBoxNames = allBoxNames.filter(box => {
                if (!observedBoxes.has(box)) return false;
                const prev = prevByBox[box];
                return (byBox[box]?.obs || []).some((o: any) => obsDiffersFromPrev(o, prev, changedFields));
              });
              hiddenByChange = observedBefore - allBoxNames.length;
            }

            // Stalker mode: boxes visited today in scanned order, earliest first.
            // Carry-forward-only boxes weren't visited, so they drop out; chip-only
            // boxes have no observation time and sink to the end.
            if (stalking) {
              const earliest = (box: string) => {
                const obs = byBox[box]?.obs || [];
                return obs.length ? obs.reduce((m: string, o: any) => o.observation_time_utc < m ? o.observation_time_utc : m, obs[0].observation_time_utc) : '9999';
              };
              allBoxNames = allBoxNames
                .filter(b => observedBoxes.has(b) || (byBox[b]?.chips.length || 0) > 0)
                .sort((a, b) => earliest(a).localeCompare(earliest(b)));
            }

            const rows = allBoxNames.map(box => {
              const cf = cfByBox[box];
              if (cf && !observedBoxes.has(box)) {
                // Carry-forward row (orange)
                const cfScans = (cf.scans || []).filter((s: any, i: number, arr: any[]) => s.peng_num && arr.findIndex((x: any) => x.peng_num === s.peng_num) === i)
                  .sort(scanSortMFC);
                const cfDs = (cf.observation_id && statusOverrides[cf.observation_id]) || displayStatusOrPrev(cf, box);
                return (
                  <div key={box} data-daybox={box} className={`day-row day-row-cf${box === highlightBox ? ' day-box-highlight' : ''}`}
                    onClick={() => onBoxClick(box, cf.observation_time_utc)} style={{cursor:'pointer'}}>
                    <a className="day-box-link" href={`/box/${box}`} onClick={e => navClick(e, () => onBoxClick(box, cf.observation_time_utc))}
                      onMouseEnter={e => peekShow(box, e)} onMouseLeave={peekHide}><b>Box {box}</b></a>
                    <span className={`badge ${DARK_TEXT_STATUSES.has(cfDs || '')?'bordered':''}${canEdit && token && cf.observation_id ? ' clickable' : ''}`}
                      style={{background:STATUS_COLORS[cfDs || '']||'#ccc',color:DARK_TEXT_STATUSES.has(cfDs || '')?'#333':'#fff',fontSize:10,padding:'1px 5px'}}
                      title={canEdit && token && cf.observation_id ? 'Change breeding status' : undefined}
                      onClick={canEdit && token && cf.observation_id ? (e) => openDayPicker(cf, e) : undefined}>{cfDs || '\u2014'}</span>
                    {cf.adults > 0 && <span>{'\uD83D\uDC27'.repeat(Math.min(cf.adults, 4))}</span>}
                    {cf.eggs > 0 && <span>{'\uD83E\uDD5A'.repeat(Math.min(cf.eggs, 4))}</span>}
                    {cf.chicks > 0 && <span>{'\uD83D\uDC23'.repeat(Math.min(cf.chicks, 4))}</span>}
                    {cfScans.map((s: any) => <PenguinMini key={s.peng_num} scan={s} onClick={() => handleBirdClick(s.peng_num)} observationDate={cf.observation_time_utc} />)}
                    {cf.gate_status && <span>{cf.gate_status}</span>}
                    <span className="day-cf-date" onMouseEnter={e => peekShow(box, e)} onMouseLeave={peekHide}>{formatDate(cf.observation_time_utc)}</span>
                  </div>
                );
              }
              // Normal row(s) — show each observation separately
              const { obs, chips } = byBox[box];
              // A bird chipped here today may also appear in today's scans — the scan
              // mini already renders it (with the green chipped-here treatment), so
              // only show chip minis for birds not scanned in this box today.
              const scannedPits = new Set(obs.flatMap((o: any) => (o.scans || []).map((s: any) => s.pit_id)));
              // A chipping day lists a nest's chicks biggest first, like everywhere else — the
              // chips arrive in whatever order they were entered.
              const sortedChips = [...chips].sort(scanSortMFC);
              const chipMinis = sortedChips.filter((c: any) => !c.pit_id || !scannedPits.has(c.pit_id));
              // Chipping-only box (no observation today): show each chipped penguin as a green
              // mini labelled "Chipped in Box x". Boxes observed today show the chip mini inline
              // on their observation row instead (handled below), so they aren't repeated here.
              if (obs.length === 0 && chips.length > 0) {
                return (
                  <div key={box} data-daybox={box} className={`day-row${box === highlightBox ? ' day-box-highlight' : ''}`}>
                    {sortedChips.map((c: any) => (
                      <span key={c.pit_id} className="day-chip-item">
                        <PenguinMini scan={c} onClick={() => handleBirdClick(c.peng_num)} observationDate={date} />
                        <span className="muted"> Chipped in Box {box}</span>
                      </span>
                    ))}
                  </div>
                );
              }
              return (
              <div key={box} data-daybox={box} className={box === highlightBox ? 'day-box-highlight' : undefined}>
                {obs.map((o: any, oi: number) => {
                  // Keep duplicate scans visible — the same penguin scanned >1x in one observation is a
                  // data-entry error worth surfacing, not noise to hide.
                  const oScans = (o.scans || []).filter((s: any) => s.peng_num).sort(scanSortMFC);
                  const scanCounts: Record<string, number> = {};
                  for (const s of oScans) scanCounts[s.peng_num] = (scanCounts[s.peng_num] || 0) + 1;
                  const hasDupScan = Object.values(scanCounts).some((n: number) => n > 1);
                  const oDs = (o.observation_id && statusOverrides[o.observation_id]) || displayStatusOrPrev(o, box);
                  const isDup = obs.length > 1;
                  return (
                  <div key={o.observation_id || oi}>
                    <div className="day-row" onClick={() => onBoxClick(box, o.observation_time_utc)} style={{cursor:'pointer', borderLeft: isDup ? '3px solid #F44336' : undefined}}>
                      {oi === 0 && <a className="day-box-link" href={`/box/${box}`} onClick={e => navClick(e, () => onBoxClick(box, o.observation_time_utc))}
                        onMouseEnter={e => peekShow(box, e)} onMouseLeave={peekHide}><b>Box {box}</b></a>}
                      {oi > 0 && <span className="day-box-link" style={{opacity:0.4}} onMouseEnter={e => peekShow(box, e)} onMouseLeave={peekHide}>Box {box}</span>}
                      {/* Nest status leads every row and is always shown \u2014 NO and a blank
                          status are as much a result as I or G, so neither is hidden. */}
                      <span className={`badge ${DARK_TEXT_STATUSES.has(oDs || '')?'bordered':''}${canEdit && token && o.observation_id ? ' clickable' : ''}`}
                        style={{background:STATUS_COLORS[oDs || '']||'#ccc',color:DARK_TEXT_STATUSES.has(oDs || '')?'#333':'#fff',fontSize:10,padding:'1px 5px'}}
                        title={canEdit && token && o.observation_id ? 'Change breeding status' : undefined}
                        onClick={canEdit && token && o.observation_id ? (e) => openDayPicker(o, e) : undefined}>{oDs || '\u2014'}</span>
                      {(o.adults || 0) > 0 && <span>{'\uD83D\uDC27'.repeat(Math.min(o.adults, 4))}</span>}
                      {(o.eggs || 0) > 0 && <span>{'\uD83E\uDD5A'.repeat(Math.min(o.eggs, 4))}</span>}
                      {(o.chicks || 0) > 0 && <span>{'\uD83D\uDC23'.repeat(Math.min(o.chicks, 4))}</span>}
                      {/* A chick the monitor found dead on this visit, in the same badge the
                          box card uses — the day of the visit is where it is looked for. */}
                      {Array.from({ length: Math.min(Number(o.dead_chicks) || 0, 4) }).map((_, k) => (
                        <span key={`dc${k}`} title="Chick recorded as dead on this visit"><OffspringFinal kind="chick" active={false} /></span>
                      ))}
                      {oScans.map((s: any, si: number) => (
                        <span key={s.scan_id || `${s.peng_num}-${si}`}
                          style={scanCounts[s.peng_num] > 1 ? {outline:'2px solid #F44336', borderRadius:3} : undefined}
                          title={scanCounts[s.peng_num] > 1 ? `Duplicate scan: #${displayPengNum(s.peng_num)} recorded ${scanCounts[s.peng_num]}× in this observation` : undefined}>
                          <PenguinMini scan={s} onClick={() => handleBirdClick(s.peng_num)} observationDate={o.observation_time_utc} />
                        </span>
                      ))}
                      {Array.from({ length: Number(o.no_scan) || 0 }).map((_, k) => (
                        <span key={`ns${k}`} className="scan no-scan">No scan</span>
                      ))}
                      {oi === 0 && chipMinis.map((c: any) => (
                        <PenguinMini key={c.pit_id} scan={c} onClick={() => handleBirdClick(c.peng_num)} observationDate={o.observation_time_utc} />
                      ))}
                      {o.gate_status && <span className="muted">{o.gate_status}</span>}
                      {isDup && <span style={{color:'#F44336', fontSize:10, fontWeight:600}}>⚠ dup</span>}
                      {hasDupScan && <span style={{color:'#F44336', fontSize:10, fontWeight:600}}>⚠ dup scan</span>}
                      {o.notes && <span className="day-note">{o.notes}</span>}
                      {stalking && <span className="stalker-ago">{timeAgo(o.observation_time_utc)}</span>}
                    </div>
                  </div>
                  );
                })}
              </div>
              );
            });

            return (<>
              {rows}
              {hiddenByChange > 0 && (
                <div className="day-hidden-note">{hiddenByChange} box{hiddenByChange === 1 ? '' : 'es'} hidden by change filter</div>
              )}
            </>);
          })()}
          </div>
        </div>
      )}

      {totalObs === 0 && totalChips === 0 && (
        <p className="muted">No activity recorded on this date.</p>
      )}
      {peek && <BoxPeekPopup box={peek.box} token={token} canEdit={canEdit} pos={peek} onMouseEnter={peekKeep} onMouseLeave={peekHide} onBirdClick={handleBirdClick} viewDate={date} />}
      {dayPicker && <StatusPickRing pos={dayPicker} current={statusOverrides[dayPicker.obsId] ?? dayPicker.cur} onPick={pickDayStatus} onClose={() => setDayPicker(null)} />}
      </div>

      {sideBird && sideBirdData?.penguin && (
        <div className="day-bird-dock" ref={dockRef}>
          <BirdPage data={sideBirdData} onBirdClick={handleBirdClick}
            onBoxClick={(box: string) => onBoxClick(box)}
            onSightingClick={(box: string, d: string) => onBoxClick(box, d)}
            onDayClick={onDayClick} onClose={() => setSideBird(null)}
            token={token} canEdit={canEdit} />
        </div>
      )}
    </div>
  );
}

function parseUrl(): { box?: string; bird?: string; enter?: boolean; admin?: boolean; reports?: boolean; docs?: boolean; birds?: boolean; day?: string; obs?: string } {
  // Query-param form (current): box and bird are independent so a bird panel can
  // stay open across box changes and survive refresh/back — e.g. /?box=12&bird=PM1234.
  // obs (observation time) is a click-only anchor that deep-links to one observation.
  const q = new URLSearchParams(window.location.search);
  if (Array.from(q.keys()).length > 0) {
    return {
      box: q.get('box') || undefined,
      // Full form, so a bare number in an old link or bookmark names the same bird as the
      // app's own links do.
      bird: q.get('bird') ? fullPengNum(q.get('bird')) : undefined,
      day: q.get('day') || undefined,
      obs: q.get('obs') || undefined,
      enter: q.has('enter'),
      admin: q.has('admin'),
      reports: q.has('reports'),
      docs: q.has('docs'),
      birds: q.has('birds'),
    };
  }
  // Legacy path form — old bookmarks and cmd+click on path-style hrefs still resolve.
  const path = window.location.pathname;
  const boxMatch = path.match(/^\/box\/(.+)/);
  const birdMatch = path.match(/^\/bird\/(.+)/);
  const dayMatch = path.match(/^\/day\/(.+)/);
  return { box: boxMatch?.[1], bird: birdMatch ? fullPengNum(birdMatch[1]) : undefined, enter: path === '/enter', admin: path === '/admin', reports: path === '/reports', docs: path === '/docs', birds: path === '/birds', day: dayMatch?.[1] };
}

/**
 * Chrome-less panel for embedding (nestcheck WebView modal). Renders ONLY the bird OR box
 * panel. Syncs the whole colony into the SAME per-colony IndexedDB the browser uses
 * (primeFromCache for instant paint + offline, then syncDatabase), so after the first sync
 * every panel — and every bird/box link tapped inside it — is an instant, offline-capable
 * local query. Reuses the same BirdPage / BoxPanel / computeBoxFamilies as the full app.
 *
 * URL: /bird/<peng>?embed=1&colony_id=<n>  or  /box/<name>?embed=1&colony_id=<n>
 * Token: window.__WW_TOKEN__ (injected by host), or ?token=, or the stored web token.
 *
 * Host JS bridge (for a persistent pre-warmed WebView):
 *   window.wwShow(kind, id)  — render a bird/box without reloading the page
 *   window.wwSetColony(n)    — switch + re-sync colony in the background
 */
export function EmbeddedPanel() {
  const params = new URLSearchParams(window.location.search);
  const initialKind: 'box'|'bird'|'day' = /\/day\//.test(window.location.pathname) ? 'day'
    : /\/box\//.test(window.location.pathname) ? 'box' : 'bird';
  const initialId = decodeURIComponent(
    window.location.pathname.match(/\/(?:box|bird|day)\/([^/?#]+)/)?.[1]
    || params.get('peng') || params.get('peng_num') || params.get('box') || '');
  const token = (window as any).__WW_TOKEN__ || params.get('token') || localStorage.getItem('ww_token') || '';

  const [colonyId, setEmbedColony] = useState<number>(parseInt(params.get('colony_id') || '1', 10) || 1);
  const [view, setView] = useState<{ kind: 'box'|'bird'|'day'; id: string }>({ kind: initialKind, id: initialId });
  const [status, setStatus] = useState<'loading'|'ready'|'error'>('loading');
  const [errMsg, setErrMsg] = useState('');
  const [progress, setProgress] = useState('');
  const [highlightObs, setHighlightObs] = useState<string|null>(null);
  const [scrollToObs, setScrollToObs] = useState<string|null>(null);

  const birdData = useBirdDetail(status === 'ready' && view.kind === 'bird' ? view.id : null);
  const boxData = useBoxDetail(status === 'ready' && view.kind === 'box' ? view.id : null);
  const allPenguins = useAllPenguins();
  const dateStats = useDateStats(); // calendar dates for the embedded day view

  // Registered FM/PM dates + date stats feed DateLink so dates colour green/teal/orange
  // exactly like the full app (the default context is empty maps → plain dates).
  const [registeredFmDates, setRegisteredFmDates] = useState<Map<string, { season: number; number: number; partial: boolean }>>(new Map());
  useEffect(() => {
    if (!token) return;
    fetch(`/api/crud.php?action=all_fm_dates&colony_id=${colonyId}`, { headers: { Authorization: `Bearer ${token}` } })
      .then(r => r.json())
      .then(rows => {
        const m = new Map<string, { season: number; number: number; partial: boolean }>();
        if (Array.isArray(rows)) for (const r of rows) if (r.actual_date) m.set(r.actual_date, { season: Number(r.season_year), number: Number(r.date_number), partial: !!Number(r.partial_monitor) });
        setRegisteredFmDates(m);
      })
      .catch(() => {});
  }, [token, colonyId]);
  const embedDateCtx = useMemo(() => ({ show: () => {}, hide: () => {}, statsCache: dateStats, registeredFmDates }), [dateStats, registeredFmDates]);

  // Sync the colony once into its own IndexedDB. primeFromCache paints instantly from a prior
  // sync (and lets the panel work fully offline); syncDatabase refreshes in the background.
  // Re-runs only on colony change (setActiveColony clears mem + swaps DB).
  useEffect(() => {
    let cancelled = false;
    if (token) localStorage.setItem('ww_token', token); // so snapshot.php / fetchHistory authenticate
    setActiveColony(colonyId, `1-${colonyId}`);          // this colony's cache (region is irrelevant to the sync)
    setStatus('loading'); setProgress('');
    (async () => {
      let primed = false;
      try { primed = await primeFromCache(); if (!cancelled && primed) setStatus('ready'); }
      catch { /* fall through to full sync */ }
      try {
        await syncDatabase((msg) => { if (!cancelled) setProgress(msg); });
        if (!cancelled) setStatus('ready');
      } catch (e) {
        if (!cancelled && !primed) { setStatus('error'); setErrMsg(String((e as any)?.message || e)); }
      }
      // The colony prefixes decide how a peng# reads and what a bare one from the host means
      // (nestcheck hands over the number as it shows it). Held in localStorage, so after the
      // first time this is only a background refresh and offline still works.
      fetchColonies().catch(() => { /* offline — the held list stands */ });
      // Same 30s change-poll as the full app (events.php watermark -> triggerSync). The
      // store-version bump re-renders any open panel; no extra onChanged work needed.
      if (!cancelled) startPolling(() => {});
    })();
    return () => { cancelled = true; stopPolling(); };
  }, [colonyId, token]);

  // Navigation is instant — the whole colony is in mem, so no fetch per bird/box.
  // A view-history stack backs the host app's ◀/▶ buttons (window.wwBack/wwForward).
  const histRef = useRef<{ stack: { kind: 'box'|'bird'|'day'; id: string }[]; idx: number }>(
    { stack: initialId ? [{ kind: initialKind, id: initialId }] : [], idx: initialId ? 0 : -1 });
  // The host app watches document.title (WebChromeClient.onReceivedTitle) to
  // show/hide its ◀/▶ buttons — there's no other JS→native channel here.
  const updateNavTitle = () => {
    const h = histRef.current;
    document.title = `wwnav:${h.idx > 0 ? 1 : 0}:${h.idx < h.stack.length - 1 ? 1 : 0}`;
  };
  const navTo = (v: { kind: 'box'|'bird'|'day'; id: string }) => {
    const h = histRef.current;
    h.stack = h.stack.slice(0, h.idx + 1);
    h.stack.push(v);
    h.idx = h.stack.length - 1;
    setHighlightObs(null); setScrollToObs(null); setView(v);
    updateNavTitle();
  };
  const goBird = (num: string) => { if (num) navTo({ kind: 'bird', id: num }); };
  const goBox = (box: string) => { if (box) navTo({ kind: 'box', id: box }); };
  const goDay = (day: string) => { if (day) navTo({ kind: 'day', id: day }); };
  const scrollObs = (t: string) => { setHighlightObs(null); setScrollToObs(null); setTimeout(() => { setHighlightObs(t); setScrollToObs(t); }, 10); };

  // Tell the host app when the colony sync has finished (it watches document.title) —
  // drives the "Web view" line in nestcheck's sync modal.
  useEffect(() => { if (status === 'ready') document.title = `wwready:${Date.now()}`; }, [status]);

  // JS bridge for a persistent host WebView: render a new bird/box or switch colony
  // without a page reload (see EMBED-FULLSYNC-PLAN.md Phase 2).
  useEffect(() => {
    (window as any).wwShow = (kind: 'box'|'bird'|'day', id: string) => {
      if (!id) return;
      // Host is opening a fresh panel — start a new history session so ◀ only
      // appears once the user has navigated within the panel.
      const v = { kind: kind === 'box' ? 'box' : kind === 'day' ? 'day' : 'bird', id: String(id) } as const;
      histRef.current = { stack: [v], idx: 0 };
      setHighlightObs(null); setScrollToObs(null); setView(v);
      updateNavTitle();
    };
    (window as any).wwSetColony = (n: number) => {
      const c = parseInt(String(n), 10);
      if (c > 0) setEmbedColony(c);
    };
    const step = (dir: number) => {
      const h = histRef.current;
      const i = h.idx + dir;
      if (i < 0 || i >= h.stack.length) return false;
      h.idx = i;
      setHighlightObs(null); setScrollToObs(null); setView(h.stack[i]);
      updateNavTitle();
      return true;
    };
    (window as any).wwBack = () => step(-1);
    (window as any).wwForward = () => step(1);
    // NestCheck's "Chip only" overview filter reads this after each wwready signal.
    (window as any).wwChipOnlyBoxes = (days?: number) => queryChipOnlyBoxes(days || 30);
    updateNavTitle();
    return () => { delete (window as any).wwShow; delete (window as any).wwSetColony; delete (window as any).wwBack; delete (window as any).wwForward; delete (window as any).wwChipOnlyBoxes; };
  }, []);

  if (status === 'error') return <div className="embed-state embed-error">Couldn't load colony data<div className="muted" style={{marginTop:6, fontSize:12}}>{errMsg}</div></div>;
  if (status !== 'ready') return <div className="embed-state">Syncing colony…<div className="muted" style={{marginTop:6, fontSize:12}}>{progress}</div></div>;

  let body: React.ReactNode;
  if (view.kind === 'day') {
    body = (
      <div className="embed-day">
        <DayView date={view.id} dates={[...dateStats.keys()].sort()} hideCalendar
          onBoxClick={(box: string, obsTime?: string) => { goBox(box); if (obsTime) setTimeout(() => scrollObs(obsTime), 50); }}
          onBirdClick={goBird} onDayClick={goDay}
          token={token} canEdit={false} allPenguins={allPenguins} />
      </div>
    );
  } else if (view.kind === 'box') {
    if (!boxData?.location) return <div className="embed-state embed-error">Box {view.id} not found</div>;
    body = (
      <div className="embed-box">
        <div className="page-header"><div className="box-header-left"><h2>Box {view.id}</h2><WatchedTick location={boxData.location} canEdit={false} /><StatusLegend /></div></div>
        <BreedingStatusBar observations={boxData.observations} box={view.id} hideLegend onHighlight={setHighlightObs} onScrollTo={scrollObs} />
        <div className="detail-split">
          <BoxPanel key={view.id} data={boxData} boxName={view.id} allPenguins={allPenguins}
            onBirdClick={goBird} onDayClick={goDay}
            highlightObs={highlightObs} scrollToObs={scrollToObs} onScrollToObs={scrollObs}
            token={token} canEdit={false} />
        </div>
      </div>
    );
  } else {
    if (!birdData?.penguin) return <div className="embed-state embed-error">Bird {displayPengNum(view.id)} not found</div>;
    body = (
      <div className="embed-bird">
        <BirdPage data={birdData} onBirdClick={goBird} onBoxClick={goBox} onSightingClick={(box: string) => goBox(box)} onDayClick={goDay} token={token} canEdit={false} />
      </div>
    );
  }
  // Date links (box/bird panels + day view) read stats + registered FM dates from this
  // context to colour themselves — without it they render plain.
  return <DateTooltipCtx.Provider value={embedDateCtx}>{body}</DateTooltipCtx.Provider>;
}

function App() {
  const [authToken, setAuthToken] = useState<string|null>(localStorage.getItem('ww_token'));
  const [userName, setUserName] = useState<string|null>(localStorage.getItem('ww_user'));
  const [userRole, setUserRole] = useState<string>(localStorage.getItem('ww_role') || 'viewer');

  const handleLogin = (token: string, name: string, observerId?: number | string, role?: string) => {
    localStorage.setItem('ww_token', token);
    localStorage.setItem('ww_user', name);
    localStorage.setItem('ww_role', role || 'viewer');
    if (observerId) localStorage.setItem('ww_observer_id', String(observerId));
    setAuthToken(token);
    setUserName(name);
    setUserRole(role || 'viewer');
  };

  const handleLogout = () => {
    localStorage.removeItem('ww_token');
    localStorage.removeItem('ww_user');
    localStorage.removeItem('ww_role');
    setAuthToken(null);
    setUserName(null);
    setUserRole('viewer');
  };

  // Refresh role from server on load (in case it changed since login)
  useEffect(() => {
    if (!authToken) return;
    fetch('/api/crud.php?action=me', { headers: { 'Authorization': `Bearer ${authToken}` } })
      .then(r => r.json())
      .then(d => {
        if (d.role && d.role !== userRole) {
          setUserRole(d.role);
          localStorage.setItem('ww_role', d.role);
        }
        // Only the backup mirror reports is_mirror; drives the admin "Mirror" tab.
        localStorage.setItem('ww_is_mirror', d.is_mirror ? '1' : '0');
      })
      .catch(() => {});
  }, [authToken]);

  // Emailed set-password link (invite or forgot-password) — takes over even when a
  // session exists, since the link may be for a different account on this device.
  const setpwToken = new URLSearchParams(window.location.search).get('setpw');
  if (setpwToken) {
    return <SetPasswordScreen setpwToken={setpwToken} onLogin={handleLogin} />;
  }

  if (!authToken) {
    return <LoginScreen onLogin={handleLogin} />;
  }

  return <AuthenticatedAppWithTooltip token={authToken} userName={userName || ''} userRole={userRole} onLogout={handleLogout} />;
}

function ChangePasswordDialog({ token, userName, onClose }: { token: string; userName?: string; onClose: () => void }) {
  const [current, setCurrent] = useState('');
  const [newPass, setNewPass] = useState('');
  const [msg, setMsg] = useState('');
  const [saving, setSaving] = useState(false);
  const [showCurrent, setShowCurrent] = useState(false);
  const [showNew, setShowNew] = useState(false);

  const pwProblem = newPass ? passwordProblem(newPass, userName ? [userName] : []) : null;

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (pwProblem) { setMsg(pwProblem); return; }
    setSaving(true); setMsg('');
    try {
      const r = await fetch('/api/crud.php?action=change_password', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
        body: JSON.stringify({ current_password: current, new_password: newPass })
      });
      const d = await r.json();
      if (d.success) { setMsg('success'); setCurrent(''); setNewPass(''); }
      else setMsg(d.error || 'Failed');
    } catch { setMsg('Connection failed'); }
    setSaving(false);
  };

  if (msg === 'success') {
    return (
      <div className="login-page" onClick={onClose}>
        <div className="login-card" onClick={e => e.stopPropagation()}>
          <h2>Password changed</h2>
          <p style={{textAlign:'center', color:'#4CAF50', fontSize:'16px', margin:'20px 0'}}>Your password has been updated.</p>
          <button onClick={onClose}>Done</button>
        </div>
      </div>
    );
  }

  return (
    <div className="login-page" onClick={onClose}>
      <div className="login-card" onClick={e => e.stopPropagation()}>
        <h2>Change password</h2>
        <form onSubmit={handleSubmit}>
          <div className="password-field">
            <input type={showCurrent ? 'text' : 'password'} placeholder="Current password" value={current} onChange={e => setCurrent(e.target.value)} required />
            <button type="button" className="toggle-pw" onClick={() => setShowCurrent(!showCurrent)}>{'\u{1F441}'}</button>
          </div>
          <div className="password-field">
            <input className={pwProblem ? 'pw-invalid' : ''} type={showNew ? 'text' : 'password'} placeholder="New password" value={newPass} onChange={e => setNewPass(e.target.value)} required minLength={8} />
            <button type="button" className="toggle-pw" onClick={() => setShowNew(!showNew)}>{'\u{1F441}'}</button>
          </div>
          {pwProblem && <div className="pw-hint pw-invalid-text">{pwProblem}</div>}
          {msg && <div className="login-error">{msg}</div>}
          <button type="submit" disabled={saving || !!pwProblem || newPass.length === 0 || current.length === 0}>{saving ? 'Saving...' : 'Change password'}</button>
        </form>
        <button className="toggle-auth" onClick={onClose}>Cancel</button>
      </div>
    </div>
  );
}

/** Add a penguin: creates the penguin, its chip (the tag's 15 ISO digits) and a biometric record (matching
 *  nestcheck's biometric fields). Launched from a box (chipBox set) for a fresh chipping, or
 *  standalone (chipBox '') to enter an already-chipped bird — box is optional in that case, and
 *  the colony prefix numbers it (e.g. RH1). */
function AddPenguinDialog({ token, chipBox, colonyPrefix, defaultChipperId, allPenguins, onClose, onAdded }: {
  token: string; chipBox: string; colonyPrefix: string; defaultChipperId: number | null; allPenguins: any[];
  onClose: () => void; onAdded: (pengNum: string) => void;
}) {
  const today = new Date().toLocaleDateString('en-CA', { timeZone: 'Pacific/Auckland' });
  const [date, setDate] = useState(today);
  const [pit, setPit] = useState('');
  const [box, setBox] = useState(chipBox);
  const [chipperId, setChipperId] = useState<number | null>(defaultChipperId);
  const [isAdult, setIsAdult] = useState(true);
  const [chickSize, setChickSize] = useState('');
  const [weight, setWeight] = useState('');
  const [flipper, setFlipper] = useState('');
  const [observedSex, setObservedSex] = useState('');
  const [notes, setNotes] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  // Rechip mode: same form, but the chip goes on an existing bird and its old chip retires.
  // The New penguin / Rechip choice sits at the top of the form (mirrors nestcheck).
  const [mode, setMode] = useState<'new' | 'rechip'>('new');
  const [rechipTarget, setRechipTarget] = useState<any>(null);
  const [rechipSearch, setRechipSearch] = useState('');

  // A tag is its 15 ISO digits — the number printed on the chip label. Readers prepend a
  // two-letter manufacturer code ("LA") that no label carries, so requiring it here meant
  // inventing it (issue #55). A pasted reader-form tag still works: the prefix comes off.
  const pitNorm = pit.toUpperCase().trim().replace(/[^A-Z0-9]/g, '').replace(/^[A-Z]+/, '');
  const pitValid = /^\d{15}$/.test(pitNorm);
  const dup = pitValid ? allPenguins.find((p: any) =>
    (p.pit_id || '').toUpperCase().replace(/^[A-Z]+/, '') === pitNorm) : null;
  // Predict the peng_num the server will assign, scoped to THIS colony. allPenguins spans every
  // colony, each number in its full prefixed form, so match only "<this prefix><digits>" and take
  // MAX + 1 — counting every colony's birds together once gave a new RR bird PT's global max + 1.
  const prefix = (colonyPrefix || activeColonyPrefix()).toUpperCase();
  const nextPengNum = useMemo(() => {
    const re = new RegExp(`^${prefix}(\\d+)$`);
    const max = allPenguins.reduce((m: number, p: any) => {
      const hit = String(p.peng_num).match(re);
      return hit ? Math.max(m, parseInt(hit[1], 10)) : m;
    }, 0);
    return max + 1;
  }, [allPenguins, prefix]);
  // Read the way the colony reads its numbers: bare at PT, prefixed everywhere else.
  const nextPengLabel = displayPengNum(`${prefix}${nextPengNum}`);

  // An adult being chipped is often the very bird recorded as "no scan" on that day's visit —
  // offer to swap the marker for a real scan of the new bird.
  const noScanObs = useMemo(() => {
    if (!isAdult || !date || !box.trim()) return null;
    return (queryBoxDetailSync(box.trim())?.observations || [])
      .find((o: any) => toNzDateStr(o.observation_time_utc) === date && (o.no_scan || 0) > 0) || null;
  }, [isAdult, date, box]);

  const save = async () => {
    setError('');
    if (!date) { setError('Date required'); return; }
    if (!pitValid) { setError('PIT id must be the 15-digit number on the chip label'); return; }
    if (dup) { setError(`PIT already assigned to #${displayPengNum(dup.peng_num)}`); return; }
    // Chip box is optional — a bird chipped elsewhere (e.g. rehab intake) often has no box here.
    if (!chipperId) { setError('Chipper is required'); return; }
    if (mode === 'rechip' && !rechipTarget) { setError('Search for the penguin to rechip'); return; }
    if (rechipTarget && !confirm(`Are you sure you would like to rechip #${displayPengNum(rechipTarget.peng_num)}?`)) return;
    if (!rechipTarget && !confirm(`Are you sure you would like to add penguin #${nextPengLabel}?`)) return;
    setSaving(true);
    try {
      let pengNum: string;
      if (rechipTarget) {
        pengNum = rechipTarget.peng_num;
      } else {
        const pengRes = await createRecord(token, 'penguins', {
          chipped_as_adult: isAdult ? 1 : 0, chick_size_code: (!isAdult && chickSize) ? chickSize : null,
        });
        if (!pengRes.success) { setError('Penguin: ' + (pengRes.error || 'failed')); setSaving(false); return; }
        pengNum = pengRes.peng_num;
      }

      const chipLoc = queryAllLocations().find((l: any) => String(l.location_name) === box.trim());
      const chipRes = await createRecord(token, 'penguin_chips', {
        pit_id: pitNorm, peng_num: pengNum, chip_date: date,
        chip_box: box.trim() || null, location_id: chipLoc?.location_id ?? null, chipper_id: chipperId, is_active: 1,
      }, rechipTarget ? `Rechip of #${pengNum}` : undefined);
      if (!chipRes.success) { setError('Chip: ' + (chipRes.error || 'failed') + (rechipTarget ? '' : ` (penguin #${displayPengNum(pengNum)} was created)`)); setSaving(false); return; }

      // Retire the bird's previous chip so the new PIT becomes the active one.
      if (rechipTarget?.pit_id && rechipTarget.pit_id.toUpperCase() !== pitNorm) {
        try {
          await updateRecord(token, 'penguin_chips', rechipTarget.pit_id, { is_active: 0 }, `Rechipped to ${pitNorm}`);
        } catch (e: any) {
          alert(`New chip was saved, but retiring old chip ${rechipTarget.pit_id.slice(-8)} failed: ${e?.message || e}`);
        }
      }

      const bio: Record<string, any> = {
        peng_num: pengNum, observation_date: date,
        // Sex applies to new adults only — the field is hidden for chicks and rechips.
        observed_sex: (isAdult && !rechipTarget && observedSex) ? observedSex : null,
        notes: notes.trim() || null,
      };
      if (weight.trim()) bio.weight = parseFloat(weight);
      if (flipper.trim()) bio.flipper_length = parseFloat(flipper);
      await createRecord(token, 'penguin_biometric_data', bio);

      // Swap the day's "no scan" marker for a real scan of the new bird: scan +1, no_scan −1,
      // so the observation's adults = scans + no-scans balance is preserved. Asked only
      // now — after the server confirmed the bird and chip were created.
      if (noScanObs && confirm(`#${displayPengNum(pengNum)} saved. This visit recorded ${noScanObs.no_scan} unscanned adult${noScanObs.no_scan === 1 ? '' : 's'} in box ${box.trim()} — replace a no-scan with this bird?`)) {
        try {
          const why = `Replaced a no-scan with ${rechipTarget ? 'rechipped' : 'newly chipped'} #${pengNum}`;
          await createRecord(token, 'penguin_scans', {
            observation_id: noScanObs.observation_id, pit_id: pitNorm,
            scan_time_utc: noScanObs.observation_time_utc,
          }, why);
          await updateRecord(token, 'observations', noScanObs.observation_id,
            { no_scan: (noScanObs.no_scan || 1) - 1 }, why);
        } catch (e: any) {
          alert(`Penguin #${displayPengNum(pengNum)} was added, but replacing the no-scan failed: ${e?.message || e}`);
        }
      }

      onAdded(pengNum);
    } catch (e: any) {
      setError('Error: ' + e.message);
      setSaving(false);
    }
  };

  return (
    <div className="login-page" onClick={onClose}>
      <div className="login-card add-penguin-card" onClick={e => e.stopPropagation()}>
        <h2>{rechipTarget ? `Rechip penguin #${displayPengNum(rechipTarget.peng_num)}` : mode === 'rechip' ? 'Rechip penguin' : `New bird #${nextPengLabel}`}{(box.trim() || chipBox) ? ` · Box ${box.trim() || chipBox}` : ''}</h2>
        {/* New penguin / Rechip mode row with the rechip search inline (mirrors nestcheck). */}
        <div className="rechip-penguin">
          <div className="app-toggle" style={{ flexShrink: 0 }}>
            <button type="button" className={mode === 'new' ? 'active' : ''} onClick={() => { setMode('new'); setRechipTarget(null); setRechipSearch(''); }}>New penguin</button>
            <button type="button" className={mode === 'rechip' ? 'active' : ''} onClick={() => setMode('rechip')}>Rechip</button>
          </div>
          {mode === 'rechip' && !rechipTarget && (
            <PenguinSearch penguins={allPenguins} search={rechipSearch} onSearchChange={setRechipSearch}
              onBirdClick={(tag: string) => {
                const p = allPenguins.find((x: any) => x.peng_num === tag || x.pit_id === tag);
                if (p) { setRechipTarget(p); setRechipSearch(''); }
              }} />
          )}
          {rechipTarget && (
            <>
              <PenguinMini scan={rechipTarget} onClick={() => {}} />
              <span className="rechip-deselect" title="Pick a different penguin" onClick={() => setRechipTarget(null)}>✕</span>
            </>
          )}
        </div>
        {/* Field order/rows mirror nestcheck's new-bird dialog; Date is web-only (nestcheck stamps today). */}
        <div className="app-row">
          <div className="app-field"><label className="req">Date</label>
            <input type="date" value={date} onChange={e => setDate(e.target.value)} /></div>
          <div className="app-field"><label className="req" title="The 15 digits on the chip label">PIT id</label>
            <input type="text" value={pit} maxLength={17} placeholder="956000016349556" autoFocus
              style={{ fontFamily: 'monospace', borderColor: pit && !pitValid ? '#c0392b' : undefined }}
              onChange={e => setPit(e.target.value.toUpperCase())} /></div>
        </div>
        {pit && !pitValid && <div className="app-pit-error">{(() => {
          // pitNorm has had a leading reader prefix stripped, so say exactly what is still needed.
          if (/\D/.test(pitNorm)) return 'Digits only — the 15-digit number on the chip label';
          if (pitNorm.length > 15) return `${pitNorm.length - 15} digit${pitNorm.length === 16 ? '' : 's'} too many — 15 required`;
          const n = 15 - pitNorm.length;
          return `${n} more digit${n === 1 ? '' : 's'} required`;
        })()}</div>}
        {dup && <div className="app-pit-error">Already assigned to #{displayPengNum(dup.peng_num)}</div>}
        {/* Chipped as + Sex share a row; the right column swaps to Chick size for chicks.
            The whole row is hidden in rechip mode (the bird's identity already exists). */}
        {mode === 'new' && (
          <div className="app-row">
            <div className="app-field"><label className="req">Chipped as</label>
              <div className="app-toggle">
                <button type="button" className={isAdult ? 'active' : ''} onClick={() => setIsAdult(true)}>Adult</button>
                <button type="button" className={!isAdult ? 'active' : ''} onClick={() => setIsAdult(false)}>Chick</button>
              </div></div>
            {isAdult ? (
              <div className="app-field"><label>Sex</label>
                <select value={observedSex} onChange={e => setObservedSex(e.target.value)}>
                  <option value="">Not recorded</option>
                  <option value="PM">Confident M</option>
                  <option value="MM">Maybe M</option>
                  <option value="U">Unsure</option>
                  <option value="MF">Maybe F</option>
                  <option value="PF">Confident F</option>
                </select></div>
            ) : (
              <div className="app-field"><label>Chick size</label>
                <select value={chickSize} onChange={e => setChickSize(e.target.value)}>
                  <option value="">Unknown</option>
                  <option value="SC">Single Chick (SC)</option>
                  <option value="BC">Big Chick (BC)</option>
                  <option value="LC">Little Chick (LC)</option>
                </select></div>
            )}
          </div>
        )}
        <div className="app-row">
          <div className="app-field"><label>Chip box</label>
            <input type="text" value={box} onChange={e => setBox(e.target.value)} placeholder="Optional" /></div>
          <div className="app-field"><label className="req">Chipper</label>
            <select value={chipperId ?? ''} title="Who fitted the transponder"
              onChange={e => setChipperId(e.target.value ? Number(e.target.value) : null)}
              style={{ borderColor: !chipperId ? '#c0392b' : undefined }}>
              <option value="">Select chipper</option>
              {getUsers().map(u => <option key={u.id} value={u.id}>{u.name}{u.active ? '' : ' (inactive)'}</option>)}
            </select></div>
        </div>
        <div className="app-bio-header">Biometric Data (optional)</div>
        <div className="app-row">
          <div className="app-field"><label>Weight (g)</label>
            <input type="number" value={weight} onChange={e => setWeight(e.target.value)} placeholder="e.g. 1250" /></div>
          <div className="app-field"><label>Flipper (mm)</label>
            <input type="number" value={flipper} onChange={e => setFlipper(e.target.value)} placeholder="e.g. 185" /></div>
        </div>
        <div className="app-field"><label>Notes</label>
          <textarea value={notes} onChange={e => setNotes(e.target.value)} rows={2} placeholder="Notes" /></div>
        {error && <div className="login-error">{error}</div>}
        <div className="app-actions">
          <button type="button" className="ghost-btn" onClick={onClose} disabled={saving}>Cancel</button>
          <button type="button" onClick={save} disabled={saving || !pitValid || !!dup || !chipperId || (mode === 'rechip' && !rechipTarget)}>{saving ? 'Saving…' : mode === 'rechip' ? 'Save rechip' : 'Save chip'}</button>
        </div>
      </div>
    </div>
  );
}

/** The permission-filtered, colony-named bird list from the server, held for the session so
 *  reopening the reports tab doesn't re-ask. Invalidated by a colony switch or a local DB sync. */
let allPenguinsServerCache: { rows: any[] | null; colonyId: number; dbVersion: number } =
  { rows: null, colonyId: -1, dbVersion: -1 };

/** Every penguin across the colonies the user can view, newest initial chip first.
 *  peng_nums stay fully prefixed — the list spans colonies, so bare numbers would be ambiguous. */
function AllPenguinsPage({ token, colonyName, onBack, onOpenBird, onEnterBird }: { token: string; colonyName?: string; onBack?: () => void; onOpenBird?: (n: string) => void; onEnterBird?: () => void }) {
  // The snapshot's penguins/chips/biometrics are global, so the table builds straight from
  // the local cache — no network needed. A background server fetch then replaces it: it adds
  // real colony names and applies colony permissions.
  const dbv = useDbVersion();
  const localRows = useMemo(() => computeAllPenguinsRows(), [dbv]);
  // The server answer can't come from the cache: the snapshot's penguin rows are global, and it
  // is this endpoint that applies the account's colony permissions (and names the colonies). So
  // it stays a fetch — but held for the session, because the page is opened and closed often
  // (it's a reports tab) and the answer only moves when a bird is chipped or a grant changes.
  // A sync bumps dbVersion, which is when it's worth asking again.
  const fresh = allPenguinsServerCache.colonyId === getColonyId() && allPenguinsServerCache.dbVersion === dbv;
  const [serverRows, setServerRows] = useState<any[] | null>(allPenguinsServerCache.rows);
  const [error, setError] = useState('');
  useEffect(() => {
    if (fresh) return;
    const colonyId = getColonyId();
    let live = true;
    fetch(`/api/penguins.php?all=1&colony_id=${colonyId}`, { headers: { Authorization: `Bearer ${token}` } })
      .then(r => r.json())
      .then(d => {
        if (!live) return;
        if (!Array.isArray(d)) { setError(d?.error || 'Failed to load'); return; }
        allPenguinsServerCache = { rows: d, colonyId, dbVersion: dbv };
        setServerRows(d);
      })
      .catch(e => { if (live) setError(String(e?.message || e)); });
    return () => { live = false; };
  }, [token, dbv]);
  const rows = serverRows || (localRows.length ? localRows : null);
  // Until the server refresh lands, label birds by their peng_num prefix (every number carries it).
  const colonyOf = (r: any) => r.colony_name || String(r.peng_num).match(/^[A-Z]{2,4}/)?.[0] || colonyName || '—';

  // Adults are the default — the column only calls out chick chippings.
  const chippedAs = (r: any) => r.chipped_as_adult ? '' : `Chipped as chick${r.chick_size_code ? ` (${r.chick_size_code})` : ''}`;
  // Confirmed sex wins; otherwise the majority of observed-sex guesses shows as UM/UF
  // (same rule as observedSexGuess elsewhere in the app).
  const sexDisplay = (r: any) => {
    const s = (r.sex || '').toUpperCase();
    if (s === 'M' || s === 'F') return s;
    const m = r.guess_m || 0, f = r.guess_f || 0;
    return m > f ? 'UM' : f > m ? 'UF' : '';
  };

  // Column sorting. Each column defines its sort value; numeric/date columns open descending
  // (newest/heaviest first), text columns ascending. Clicking the active column flips it.
  const COLS: { key: string; label: string; value: (r: any) => any; desc?: boolean }[] = [
    { key: 'peng', label: 'Penguin', value: r => [String(r.peng_num).replace(/\d+$/, ''), parseInt(String(r.peng_num).replace(/^\D+/, ''), 10) || 0] },
    { key: 'colony', label: 'Colony', value: r => colonyOf(r) },
    { key: 'chipped', label: 'Chipped', value: r => r.first_chip_date || '', desc: true },
    { key: 'box', label: 'Chip box', value: r => { const b = r.first_chip_box || ''; const n = parseInt(b, 10); return isNaN(n) ? [b, 0] : ['', n]; } },
    { key: 'by', label: 'By', value: r => r.first_chip_by || '' },
    { key: 'as', label: 'As', value: r => chippedAs(r) },
    { key: 'sex', label: 'Sex', value: r => sexDisplay(r) },
    { key: 'weight', label: 'Chip weight (g)', value: r => r.chip_weight != null ? Number(r.chip_weight) : null, desc: true },
    { key: 'flipper', label: 'Chip flipper (mm)', value: r => r.chip_flipper != null ? Number(r.chip_flipper) : null, desc: true },
    { key: 'pits', label: 'PIT ids', value: r => r.pits?.[0]?.pit_id || '' },
  ];
  // CSV for the EID reader: pit_id, then a ≤16-char activity field — the chipping box, then
  // boxes the bird has been seen in (most recent first, as many as fit), ending with:
  //  - chicks: chick size + 2-digit breeding-season year (e.g. "BC23", "SC24")
  //  - adults: sex — confirmed birds get the bare letter ("-M"), unconfirmed get "U" plus the
  //    majority observed-sex guess if one exists ("-UM", "-U").
  // The reader import requires each activity field to be UNIQUE (duplicates import with no
  // history). Most boxes fledge two chicks a year, so the year alone can't dedupe siblings;
  // any remaining collisions get trailing hyphens appended (bdot's convention: "12-F-",
  // "12-F--"), trimmed to keep the field ≤16 chars.
  const exportCsv = () => {
    const boxesByPit = computeBoxesSeenByPit();
    const lines: string[] = [];
    // Sorted by penguin number ascending so rows come out in chipping order (peng_num is
    // sequential), independent of the table's current sort. Prefix then number, so a mixed
    // list (e.g. PT/NI) groups by colony and orders numerically within each.
    // Tarakohe (PT) birds export as a bare number; every other colony prepends its acronym.
    // This is the reader's convention, not the viewing colony's display rule, so it holds
    // whichever colony is being viewed. colony_prefix comes from the server payload; the
    // peng_num's own prefix covers the cache-built rows.
    const acronymOf = (r: any): string => r.colony_prefix || String(r.peng_num).match(/^[A-Z]+/)?.[0] || '';
    const bareNum = (r: any): string => String(r.peng_num).replace(/^[A-Z]+/, '');
    const pengOut = (r: any): string => acronymOf(r) === 'PT' ? bareNum(r) : `${acronymOf(r)}${bareNum(r)}`;
    const pengKey = (r: any): [string, number] => [String(r.peng_num).replace(/\d+$/, ''), parseInt(String(r.peng_num).replace(/^\D+/, ''), 10) || 0];
    const ordered = [...(rows || [])].sort((a, b) => {
      const [pa, na] = pengKey(a), [pb, nb] = pengKey(b);
      return pa.localeCompare(pb) || na - nb;
    });
    // Pass 1: build each row's base activity field and tally how many rows share it.
    type Entry = { peng: string; pit: string; base: string };
    const entries: Entry[] = [];
    const baseCount = new Map<string, number>();
    for (const r of ordered) {
      const activePits = (r.pits || []).filter((p: any) => p.is_active);
      if (!activePits.length) continue;
      const chipBox = String(r.first_chip_box || '');
      // Chicks end with size + breeding-season year (BC23); adults end with sex (M/F/UM/UF/U).
      let suffix: string;
      if (!r.chipped_as_adult) {
        const yr = r.first_chip_date ? getSeasonLabel(parseDate(r.first_chip_date)).slice(-2) : '';
        suffix = `${r.chick_size_code || ''}${yr}`;
      } else {
        const s = (r.sex || '').toUpperCase();
        const m = r.guess_m || 0, f = r.guess_f || 0;
        suffix = (s === 'M' || s === 'F') ? s : `U${m > f ? 'M' : f > m ? 'F' : ''}`;
      }
      const end = suffix ? `-${suffix}` : '';
      const seen = (boxesByPit.get(activePits[0].pit_id) || []).filter(b => b !== chipBox);
      let middle = '';
      for (const b of seen) {
        const cand = middle ? `${middle}-${b}` : b;
        if (`${chipBox}-${cand}${end}`.length > 16) break;
        middle = cand;
      }
      const base = `${chipBox}-${middle}${end}`
        .replace(/-{2,}/g, '-').replace(/ {2,}/g, ' ')
        .replace(/^-+/, '').replace(/-+$/, '').slice(0, 16);
      for (const p of activePits) {
        entries.push({ peng: pengOut(r), pit: String(p.pit_id).replace(/^[A-Za-z]+/, ''), base });
        baseCount.set(base, (baseCount.get(base) || 0) + 1);
      }
    }
    // Pass 2: the reader import needs every activity field unique. A base used by just one
    // row stays clean; rows that share a base get a trailing letter (12-BC23a, 12-BC23b, …),
    // trimmed to keep the field ≤16 chars. peng_num order makes the lettering deterministic.
    const used = new Set<string>();
    const nextIdx = new Map<string, number>();
    for (const e of entries) {
      let field = e.base;
      if ((baseCount.get(e.base) || 0) > 1 || used.has(field)) {
        let i = nextIdx.get(e.base) || 0;
        do {
          const suf = i < 26 ? String.fromCharCode(97 + i) : String(i + 1);
          field = (e.base.length + suf.length > 16 ? e.base.slice(0, 16 - suf.length) : e.base) + suf;
          i++;
        } while (used.has(field));
        nextIdx.set(e.base, i);
      }
      used.add(field);
      // peng_num, then the bare 15-digit tag (stored pit_ids carry an "LA" prefix the reader
      // doesn't want), then the unique activity field.
      lines.push(`${e.peng},${e.pit},${field}`);
    }
    const url = URL.createObjectURL(new Blob([lines.join('\r\n') + '\r\n'], { type: 'text/csv' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = `penguins-${new Date().toISOString().slice(0, 10)}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const [sortKey, setSortKey] = useState('chipped');
  const [sortDesc, setSortDesc] = useState(true);
  const clickSort = (c: typeof COLS[number]) => {
    if (sortKey === c.key) setSortDesc(d => !d);
    else { setSortKey(c.key); setSortDesc(!!c.desc); }
  };
  const sorted = useMemo(() => {
    if (!rows) return null;
    const col = COLS.find(c => c.key === sortKey) || COLS[2];
    const cmp = (a: any, b: any): number => {
      if (Array.isArray(a)) return cmp(a[0], b[0]) || cmp(a[1], b[1]);
      if (typeof a === 'number' && typeof b === 'number') return a - b;
      return String(a).localeCompare(String(b));
    };
    const empty = (v: any) => v === null || v === undefined || v === '';
    return [...rows].sort((a, b) => {
      const va = col.value(a), vb = col.value(b);
      // Rows with no value sink to the bottom in either direction.
      if (empty(va) || empty(vb)) return empty(va) && empty(vb) ? 0 : empty(va) ? 1 : -1;
      const r = cmp(va, vb);
      return sortDesc ? -r : r;
    });
  }, [rows, sortKey, sortDesc]);

  return (
    <div style={{ maxWidth: 1100, margin: '0 auto', padding: '16px 20px' }}>
      {onBack && <a className="page-back clickable" onClick={onBack}>&larr; Colony</a>}
      <div className="report-card">
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
          <h3 style={{ margin: 0 }}>All penguins{rows ? ` (${rows.length})` : ''}</h3>
          <div style={{ display: 'flex', gap: 8 }}>
            {onEnterBird && <button className="action-btn" onClick={onEnterBird} title="Enter an already-chipped bird into this colony (no re-chipping)">+ Enter bird</button>}
            {rows && <button className="action-btn" onClick={exportCsv}>Export CSV</button>}
          </div>
        </div>
        <p className="muted">Every penguin in the colonies you can view. Chip details are from the bird's initial chipping; rechipped birds list every PIT they have worn. Click a column to sort.</p>
        {error && !rows && <p style={{ color: '#F44336' }}>{error}</p>}
        {!rows && !error && <p className="muted">Loading…</p>}
        {sorted && (
          <div className="table-scroll">
            <table className="guess-rank-table zebra fit">
              <thead><tr>
                {COLS.map(c => (
                  <th key={c.key} className="clickable" style={{ cursor: 'pointer' }} onClick={() => clickSort(c)}>
                    {c.label}{sortKey === c.key ? (sortDesc ? ' ▼' : ' ▲') : ''}
                  </th>
                ))}
              </tr></thead>
              <tbody>
                {sorted.map((r: any) => (
                  <tr key={r.peng_num}>
                    <td style={{ fontWeight: 600 }}>
                      {onOpenBird
                        ? <span className="clickable" style={{ color: '#1a6b8f' }} onClick={() => onOpenBird(String(r.peng_num))}>{r.peng_num}</span>
                        : r.peng_num}
                      {r.is_dead ? <span title={r.death_date ? `Died ${String(r.death_date).slice(0, 10)}` : 'Dead'}> †</span> : null}
                    </td>
                    <td>{colonyOf(r)}</td>
                    <td>{r.first_chip_date ? String(r.first_chip_date).slice(0, 10) : '—'}</td>
                    <td>{r.first_chip_box || '—'}</td>
                    <td>{r.first_chip_by || '—'}</td>
                    <td>{chippedAs(r)}</td>
                    <td>{(() => { const s = sexDisplay(r); return s ? <span style={s.startsWith('U') ? { color: '#888' } : undefined} title={s.startsWith('U') ? 'Unconfirmed — from observed-sex guesses' : undefined}>{s}</span> : '—'; })()}</td>
                    <td>{r.chip_weight ?? '—'}</td>
                    <td>{r.chip_flipper ?? '—'}</td>
                    <td style={{ fontFamily: 'monospace', fontSize: 12 }}>
                      {(r.pits || []).length === 0 ? '—' : (r.pits || []).map((p: any) => (
                        <div key={p.pit_id} style={p.is_active ? undefined : { color: '#999' }} title={p.is_active ? undefined : 'Retired tag'}>{p.pit_id}</div>
                      ))}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

function CollapsibleSeason({ label, observations, box, onBirdClick, onDayClick, highlightObs, scrollToObs, token, canEdit, allPenguins, onDataChange }: any) {
  const [expanded, setExpanded] = useState(false);
  useEffect(() => {
    const target = scrollToObs || highlightObs;
    if (target && observations.some((o: any) => o.observation_time_utc === target)) setExpanded(true);
  }, [scrollToObs, highlightObs]);
  return (
    <div>
      <div className="season-divider clickable" onClick={() => setExpanded(!expanded)}><hr/><span>{seasonRange(label)} ({observations.length}) {expanded ? '▲' : '▼'}</span><hr/></div>
      {expanded && mergeSameDayChips(observations).map((o: any, i: number) => o._chip
        ? <ChipCard key={`chip${o.pit_id}`} date={o.chip_date} birds={o._chipBirds} onBirdClick={onBirdClick} onDayClick={onDayClick} />
        : <ObsCard key={o.observation_id || `${label}${i}`} obs={o} box={box} onBirdClick={onBirdClick} onDayClick={onDayClick} highlight={highlightObs !== null && o.observation_time_utc === highlightObs} scrollTo={scrollToObs !== null && o.observation_time_utc === scrollToObs} token={token} canEdit={canEdit} allPenguins={allPenguins} onDataChange={onDataChange} />)}
    </div>
  );
}

/** Audit-log plumbing columns — never interesting to a human reading the change list. */
// monitor_filename is gone from observations, but audit rows written before it was dropped still carry it.
const AUDIT_HIDDEN_FIELDS = ['location_id','observer_id','colony_id','monitor_filename','is_deleted','observation_id','scan_id','biometric_id'];
/** On observation rows the box is already in the header, and the scan count is superseded by the minis. */
const AUDIT_HIDDEN_OBS_FIELDS = [...AUDIT_HIDDEN_FIELDS, 'box', 'scans'];

/** The birds on an observation audit row: real scans as minis, unscanned adults as "No scan". */
function AuditObsBirds({ entry }: { entry: any }) {
  const scans: any[] = entry.obs_scans || [];
  const noScan: number = entry.obs_no_scan || 0;
  if (scans.length === 0 && noScan === 0) return null;
  return (
    <span className="scans" style={{ display: 'inline-flex', flexWrap: 'wrap', gap: 2, marginTop: 4 }}>
      {[...scans].sort(scanSortMFC).map((s, i) => (
        <PenguinMini key={s.pit_id || i} scan={s} observationDate={entry.obs_time || undefined}
          onClick={() => s.peng_num && _adminOpenBird?.(String(s.peng_num))} />
      ))}
      {Array.from({ length: noScan }).map((_, k) => <span key={`ns${k}`} className="scan no-scan">No scan</span>)}
    </span>
  );
}

/** "observation_time_utc" -> "Observation time utc" reads better than a raw column name. */
const fieldLabel = (k: string) => k.replace(/_/g, ' ').replace(/^./, c => c.toUpperCase());

/** Render an audit value: blanks are explicit, timestamps lose their seconds/T, objects stay readable. */
const auditValue = (v: any) => {
  if (v === null || v === undefined || v === '') return '—';
  if (typeof v === 'boolean') return v ? 'yes' : 'no';
  if (typeof v === 'object') return JSON.stringify(v);
  const s = String(v);
  return /^\d{4}-\d\d-\d\d[ T]/.test(s) ? s.slice(0, 16).replace('T', ' ') : s;
};

/** One "Field  old → new" line (or just "Field  value" when there's no before-state). */
function FieldDiff({ name, value }: { name: string; value: any }) {
  const isDiff = value && typeof value === 'object' && !Array.isArray(value) && ('old' in value || 'new' in value);
  return (
    <div style={{ display: 'flex', gap: 8, alignItems: 'baseline', fontSize: 11, padding: '1px 0' }}>
      <span style={{ minWidth: 130, flexShrink: 0, color: '#555', fontWeight: 600 }}>{fieldLabel(name)}</span>
      {isDiff ? (
        <span style={{ wordBreak: 'break-word' }}>
          <span style={{ textDecoration: 'line-through', color: '#c0392b' }}>{auditValue(value.old)}</span>
          <span style={{ color: '#999', margin: '0 6px' }}>→</span>
          <span style={{ color: '#2e7d32', fontWeight: 600 }}>{auditValue(value.new)}</span>
        </span>
      ) : (
        <span style={{ color: '#333', wordBreak: 'break-word' }}>{auditValue(value)}</span>
      )}
    </div>
  );
}

// Tables crud.php's update action (→ db_write.php wwAuditedUpdate) accepts; a Revert is
// just an audited update setting each field back to its old value, so it's limited to these.
const REVERTABLE_TABLES = new Set(['observations', 'penguins', 'penguin_scans', 'penguin_biometric_data', 'penguin_chips', 'observation_locations']);
// Audit keys that aren't real columns (display/count helpers) — never send them in a revert.
const NON_COLUMN_AUDIT_KEYS = new Set(['scans', 'box']);

/** An UPDATE audit entry can be undone by writing each changed field back to its "old"
 *  value. Returns the {column: old} map, or null if there's nothing revertible. */
function revertFields(fields: any): Record<string, any> | null {
  if (!fields || typeof fields !== 'object') return null;
  const out: Record<string, any> = {};
  for (const [k, v] of Object.entries(fields)) {
    if (NON_COLUMN_AUDIT_KEYS.has(k)) continue;
    if (v && typeof v === 'object' && !Array.isArray(v) && 'old' in (v as any)) out[k] = (v as any).old;
  }
  return Object.keys(out).length ? out : null;
}

function RevertButton({ entry, fields, token, onReverted }: { entry: any; fields: any; token: string; onReverted: () => void }) {
  const [busy, setBusy] = useState(false);
  const revert = revertFields(fields);
  if (!revert) return null;
  const onClick = async () => {
    const lines = Object.entries(revert).map(([k, oldV]) => `  ${fieldLabel(k)}: ${auditValue((fields[k] as any).new)} → ${auditValue(oldV)}`);
    if (!window.confirm(`Revert this change on ${entry.table_name} #${displayPengNum(entry.record_id)}?\n\n${lines.join('\n')}\n\nThis is itself recorded as an audited edit.`)) return;
    setBusy(true);
    try {
      const res = await updateRecord(token, entry.table_name, entry.record_id, revert, `Revert of change #${entry.audit_id} (${entry.nz_time || ''} by ${entry.observer_name || 'unknown'})`);
      if (res?.error) { alert(`Revert failed: ${res.error}`); return; }
      onReverted();
    } catch (e: any) {
      alert(`Revert failed: ${e?.message || e}`);
    } finally {
      setBusy(false);
    }
  };
  return <button className="edit-btn" style={{ fontSize: 11, padding: '1px 8px' }} disabled={busy} onClick={onClick}>{busy ? 'Reverting…' : 'Revert'}</button>;
}

function ChangeDateGroup({ date, entries, token, onReverted }: { date: string; entries: any[]; token: string; onReverted: () => void }) {
  const [expanded, setExpanded] = useState(false);
  const dateLabel = new Date(date + 'T00:00:00').toLocaleDateString('en-NZ', { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' });
  return <div style={{marginBottom:4}}>
    <div className="clickable" style={{padding:'6px 10px', background:'#f5f5f5', borderRadius:6, fontWeight:600, fontSize:13, display:'flex', justifyContent:'space-between'}} onClick={() => setExpanded(!expanded)}>
      <span>{expanded ? '▾' : '▸'} {dateLabel}</span>
      <span className="muted">{entries.length} change{entries.length === 1 ? '' : 's'}</span>
    </div>
    {expanded && <div style={{maxHeight:300, overflowY:'auto'}}>
      {entries.map((e: any, i: number) => {
        const fields = typeof e.changed_fields === 'string' ? (() => { try { return JSON.parse(e.changed_fields); } catch { return null; } })() : e.changed_fields;
        return (
          <div key={i} className="obs-card" style={{marginBottom:2, padding:'4px 10px', marginLeft:8}}>
            <div style={{display:'flex', gap:8, alignItems:'center', flexWrap:'wrap', fontSize:12}}>
              <span style={{fontFamily:'monospace', fontSize:11, color:'#666', minWidth:34}}>{e.nz_time || ''}</span>
              <span style={{background: e.action === 'DELETE' ? '#F44336' : e.action === 'INSERT' ? '#4CAF50' : e.action === 'IMPORT' ? '#00897b' : '#2196F3', color:'#fff', fontSize:10, padding:'1px 6px', borderRadius:3}}>{e.action}</span>
              <span>{e.table_name === '__sql_console' ? 'SQL console' : e.table_name === '__import' ? (fields?.filename || 'Import') : e.table_name === 'date_mappings' ? `Date table · season ${String(e.record_id).slice(-2)}` : `${e.table_name}${e.box_name ? ` · Box ${e.box_name}` : ''} #${displayPengNum(e.record_id)}`}</span>
              <span style={{color:'#1a6b8f', fontWeight:600}}>{e.observer_name || 'unknown user'}</span>
              {e.change_reason && <span style={{fontStyle:'italic', color:'#666'}}>"{e.change_reason}"</span>}
              {e.action === 'UPDATE' && REVERTABLE_TABLES.has(e.table_name) && (
                <span style={{marginLeft:'auto'}}><RevertButton entry={e} fields={fields} token={token} onReverted={onReverted} /></span>
              )}
            </div>
            {e.table_name === '__sql_console' && fields?.sql && (
              <div style={{fontSize:11, marginTop:2, fontFamily:'monospace', color:'#555', whiteSpace:'pre-wrap', wordBreak:'break-word'}}>{fields.sql}</div>
            )}
            {e.table_name === '__import' && fields && (
              <div className="muted" style={{fontSize:11, marginTop:2}}>
                {fields.observations} observation{fields.observations !== 1 ? 's' : ''}
                {fields.scans ? `, ${fields.scans} scan${fields.scans !== 1 ? 's' : ''}` : ''}
                {fields.biometrics ? `, ${fields.biometrics} biometric${fields.biometrics !== 1 ? 's' : ''}` : ''}
                {fields.colony ? ` · ${fields.colony}` : ''}
              </div>
            )}
            {/* Date-table edits store old/new as arrays of date rows — render a per-number diff, not [object Object]. */}
            {e.action === 'UPDATE' && e.table_name === 'date_mappings' && fields && (() => {
              const norm = (r: any) => ({ n: Number(r.n ?? r.date_number), date: String(r.date ?? r.actual_date ?? '').slice(0, 10), pm: !!(r.partial ?? Number(r.partial_monitor)) });
              const oldRows: any[] = Array.isArray(fields.old) ? fields.old.map(norm) : [];
              const newRows: any[] = Array.isArray(fields.new) ? fields.new.map(norm) : [];
              const oldByN = new Map(oldRows.map(r => [r.n, r]));
              const newByN = new Map(newRows.map(r => [r.n, r]));
              const fmt = (r: any) => r ? `${r.date.slice(8, 10)}/${r.date.slice(5, 7)}/${r.date.slice(0, 4)}${r.pm ? ' PM' : ''}` : '—';
              const nums = Array.from(new Set([...oldByN.keys(), ...newByN.keys()])).sort((a, b) => a - b);
              const changes = nums.map(n => ({ n, o: oldByN.get(n), nw: newByN.get(n) }))
                .filter(c => !c.o || !c.nw || c.o.date !== c.nw.date || c.o.pm !== c.nw.pm);
              return <div style={{fontSize:11, marginTop:2}}>
                <span className="muted">{oldRows.length} → {newRows.length} dates{changes.length === 0 ? ' · no per-date changes' : ''}</span>
                {changes.length > 0 && <div style={{marginTop:2}}>
                  {changes.map(c => (
                    <span key={c.n} className="muted" style={{marginRight:8}}>#{c.n}: {c.o && c.nw ? <><s>{fmt(c.o)}</s> → {fmt(c.nw)}</> : c.nw ? <>+ {fmt(c.nw)}</> : <>− {fmt(c.o)}</>}</span>
                  ))}
                </div>}
              </div>;
            })()}
            {e.action === 'UPDATE' && e.table_name !== 'date_mappings' && fields && (
              <div style={{marginTop:4, paddingLeft:6, borderLeft:'2px solid #e0e0e0'}}>
                {Object.entries(fields)
                  .filter(([k]) => !(e.table_name === 'observations' && AUDIT_HIDDEN_OBS_FIELDS.includes(k) && k !== 'scans'))
                  .map(([k, v]: [string, any]) => <FieldDiff key={k} name={k} value={v} />)}
              </div>
            )}
            {/* INSERTs store the whole new row — show its meaningful fields (drop the ids/plumbing). */}
            {e.action === 'INSERT' && fields && e.table_name !== '__sql_console' && (
              <div style={{marginTop:4, paddingLeft:6, borderLeft:'2px solid #e0e0e0'}}>
                {Object.entries(fields)
                  .filter(([k, v]: [string, any]) => !(e.table_name === 'observations' ? AUDIT_HIDDEN_OBS_FIELDS : AUDIT_HIDDEN_FIELDS).includes(k) && v !== null && v !== '')
                  .map(([k, v]: [string, any]) => <FieldDiff key={k} name={k} value={v} />)}
              </div>
            )}
            {/* Who was in the box — the audit's scan count says nothing about which birds. */}
            {e.table_name === 'observations' && <AuditObsBirds entry={e} />}
          </div>
        );
      })}
    </div>}
  </div>;
}

/** How far apart two checks can sit and still date the event between them. A stage is only
 *  measured where a visit found the box without it and the next found it with — wider than a
 *  week and the midpoint says more about the visiting rota than about the birds. */
const CAL_BRACKET_DAYS = 7;

/** Where the chip window sits between the two stages either side of it.
 *
 *  The window is the one date on the calendar that CANNOT be measured from the record. Every
 *  other stage leaves a mark a monitor found — an egg, a chick, an empty nest — but a chipping
 *  records the day someone arrived with a reader, not the day the chick was ready for one. Read
 *  the chippings as the window and you have measured the roster.
 *
 *  So it is proposed from the two observable stages that bracket it. It opens three quarters of
 *  the way from the end of guard to fledging, when the chicks are near adult size, and closes
 *  short of fledging so a visit still finds them in the burrow rather than at sea. The chippings
 *  actually made are then a CHECK on that proposal, shown beside it — never its definition.
 */
const CHIP_OPEN_FRACTION = 0.75;
const CHIP_CLOSE_BEFORE_FLEDGE = 3;

/** One measured stage: every observation of it, as days after the first egg, summarised. */
interface CalStage { days: number[]; n: number; mean: number; sd: number; median: number }
const calMean = (a: number[]) => a.reduce((s, x) => s + x, 0) / a.length;
const calSd = (a: number[]) => {
  const m = calMean(a);
  return Math.sqrt(a.reduce((s, x) => s + (x - m) ** 2, 0) / a.length);
};
const calSummarise = (days: number[]): CalStage | null => {
  if (!days.length) return null;
  const s = [...days].sort((a, b) => a - b);
  return { days, n: days.length, mean: calMean(days), sd: calSd(days), median: s[Math.floor(s.length / 2)] };
};
/** Signed to one decimal: these are offsets from the first egg, and the sign is the point. */
const calDays = (n: number) => (n >= 0 ? '+' : '') + n.toFixed(1);
/** A stage measured too few times to mean anything is shown, greyed, with its n — the count
 *  is the finding. Hiding it would read as "not measured" rather than "barely happens". */
const CAL_MIN_N = 10;

/** One stage's row: what the two-egg clutches say, what the single-egg ones say, and the
 *  fixed offset in use — flagged amber where the record disagrees by three days or more. */
function CalRow({ label, stage, single, inUse, how }: {
  label: string; stage: CalStage | null; single: CalStage | null; inUse: number; how: string;
}) {
  const thin = !!single && single.n < CAL_MIN_N;
  return (
    <tr>
      <td style={{ whiteSpace: 'nowrap', fontWeight: 600 }}>{label}</td>
      <td style={{ whiteSpace: 'nowrap' }}>{stage ? `${calDays(stage.mean)} d` : <span className="muted">—</span>}</td>
      <td className="muted" style={{ whiteSpace: 'nowrap' }}>{stage ? `± ${stage.sd.toFixed(1)}` : ''}</td>
      <td className="muted" style={{ whiteSpace: 'nowrap' }}>{stage ? calDays(stage.median) : ''}</td>
      <td className="muted" style={{ textAlign: 'right' }}>{stage ? stage.n : ''}</td>
      <td style={{ whiteSpace: 'nowrap', color: thin ? '#aaa' : undefined }}
        title={thin ? `Only ${single!.n} single-egg clutch${single!.n === 1 ? '' : 'es'} reached this stage — too few to compare` : undefined}>
        {single ? `${calDays(single.mean)} d (n=${single.n})` : <span className="muted">—</span>}
      </td>
      <td style={{ whiteSpace: 'nowrap', color: stage && Math.abs(stage.mean - inUse) >= 3 ? '#b8541a' : undefined }}>
        +{inUse} d
      </td>
      <td className="muted">{how}</td>
    </tr>
  );
}

/**
 * What the colony's own record says each breeding stage takes — the working behind the dates
 * in the report below.
 *
 * Every stage is measured the same way: the midpoint of the last check without it and the first
 * check with it, counted from the first egg dated the same way (the last empty check and the
 * first with eggs). Nothing here goes through the laid ESTIMATE — that estimate is itself built
 * from these intervals, so measuring against it would only hand back the offsets it was given.
 * A clutch contributes to a stage only where both checks exist and sit within
 * {@link CAL_BRACKET_DAYS} of each other, which is why every row carries its own n.
 *
 * Two stages need a word:
 *
 *   Guard ends — no monitor records "PG" as a status, so it is inferred: the first check that
 *   found chicks with no adult on them. It can only be found at a visit, so it is an UPPER
 *   bound — the parents left at some point between that check and the one before it.
 *
 *   Fledge — an empty nest looks the same after a fledging as after a predation, so only nests
 *   that got a chick to chipping are counted. The rest would drag the mean down with deaths.
 *
 * Means, as asked for: the median is shown beside each because a single miscounted check can
 * pull a mean several days and the pair together says whether that has happened.
 */
function BreedingCalibration() {
  const v = useDbVersion();
  const [show, setShow] = useState(true);

  const cal = useMemo(() => {
    const two: Record<string, number[]> = {}, one: Record<string, number[]> = {};
    const add = (a: Record<string, number[]>, k: string, d: number) => { (a[k] = a[k] || []).push(d); };
    let clutches = 0, anchored = 0, oneEgg = 0, oneEggBred = 0;

    for (const { detail: bd, families } of allColonyBoxes()) {
      const chrono = [...bd.observations].sort((a: any, b: any) =>
        parseDate(a.observation_time_utc).getTime() - parseDate(b.observation_time_utc).getTime());
      // Chicks chipped in each clutch, from the same family detection the report rows use.
      const famByStart = new Map<number, any>();
      for (const sd of families)
        for (const fam of sd.families)
          if (fam.clutch.startObsId != null) famByStart.set(fam.clutch.startObsId, fam);

      const T = (o: any) => parseDate(o.observation_time_utc).getTime();
      for (const c of segmentClutches(chrono)) {
        clutches++;
        if (c.maxEggs === 1) oneEgg++;
        if (c.maxEggs === 1 && c.maxChicks > 0) oneEggBred++;
        const inC = chrono.filter((o: any) => T(o) >= c.start && T(o) <= (c.end ?? Infinity));

        // The anchor: the first egg, bracketed by the last check that found the box empty.
        // No anchor, no measurements — everything below is relative to this one date.
        const firstEgg = inC.find((o: any) => (o.eggs || 0) > 0);
        if (!firstEgg) continue;
        const empty = [...chrono].reverse().find((o: any) =>
          T(o) < T(firstEgg) && (o.eggs || 0) === 0 && (o.chicks || 0) === 0);
        if (!empty || (T(firstEgg) - T(empty)) / DAY > CAL_BRACKET_DAYS) continue;
        const anchor = (T(empty) + T(firstEgg)) / 2;
        anchored++;
        const A = c.maxEggs >= 2 ? two : one;
        const since = (t: number) => (t - anchor) / DAY;
        const bracket = (before: any, after: any) =>
          before && after && (T(after) - T(before)) / DAY <= CAL_BRACKET_DAYS
            ? since((T(before) + T(after)) / 2) : null;
        const push = (k: string, d: number | null) => { if (d !== null) add(A, k, d); };

        // Second egg: one egg in the box, then two.
        const oneEggObs = [...inC].reverse().find((o: any) => (o.eggs || 0) === 1);
        push('2nd egg', bracket(oneEggObs, inC.find((o: any) => (o.eggs || 0) >= 2 && (!oneEggObs || T(o) > T(oneEggObs)))));

        // Hatch: eggs only, then chicks.
        const firstChick = inC.find((o: any) => (o.chicks || 0) > 0);
        if (firstChick) push('hatch', bracket(
          [...inC].reverse().find((o: any) => T(o) < T(firstChick) && (o.chicks || 0) === 0), firstChick));

        // Guard ends: the first check that found the chicks on their own (an upper bound).
        const alone = inC.find((o: any) => (o.chicks || 0) > 0 && (o.adults || 0) === 0);
        if (alone) push('guard ends', since(T(alone)));

        // Chippings — the check on the proposed window, not a measurement of it.
        const fam = c.startObsId != null ? famByStart.get(c.startObsId) : null;
        const chipped: number[] = (fam?.chicks || [])
          .filter((b: any) => b.chip_date)
          .map((b: any) => since(parseDate(b.chip_date).getTime()));
        for (const d of chipped) add(A, 'chipped', d);

        // Fledge: chicks, then none — and only where a chick got as far as being chipped, so
        // a nest that lost its brood isn't counted as having fledged early.
        if (chipped.length && firstChick) {
          const withChicks = inC.filter((o: any) => (o.chicks || 0) > 0);
          const lastChick = withChicks[withChicks.length - 1];
          push('fledge', bracket(lastChick, chrono.find((o: any) => T(o) > T(lastChick) && (o.chicks || 0) === 0)));
        }
      }
    }
    return { two, one, clutches, anchored, oneEgg, oneEggBred };
  }, [v]);

  const st = (k: string) => calSummarise(cal.two[k] || []);
  const st1 = (k: string) => calSummarise(cal.one[k] || []);
  const guard = st('guard ends'), fledge = st('fledge'), chipped = st('chipped');
  // The proposal, from the two measured stages either side of it.
  const chipOpen = guard && fledge ? guard.mean + CHIP_OPEN_FRACTION * (fledge.mean - guard.mean) : null;
  const chipClose = fledge ? fledge.mean - CHIP_CLOSE_BEFORE_FLEDGE : null;

  return (
    <div className="cal-panel">
      <button className="edit-btn" onClick={() => setShow(s => !s)}>
        {show ? 'Hide' : 'Show'} the working — what this colony's record says each stage takes
      </button>
      {show && <>
        <p className="muted" style={{ marginBottom: 4 }}>
          Mean days after the first egg, over every clutch in the cache ({cal.anchored} of {cal.clutches} have
          an empty check close enough before the eggs to date laying from). Each stage is the midpoint of the
          last check without it and the first check with it, at most {CAL_BRACKET_DAYS} days apart — so n differs
          per row, and none of it goes through the estimated laid date, which is derived from these same intervals.
        </p>
        <table className="guess-rank-table mini-list-table">
          <thead>
            <tr>
              <th colSpan={5} style={{ textAlign: 'left' }}>Two-egg clutches</th>
              <th title="Clutches that never held more than one egg, timed the same way">Single-egg</th>
              <th /><th />
            </tr>
            <tr>
              <th>Stage</th><th>Mean</th><th>SD</th><th>Median</th><th style={{ textAlign: 'right' }}>n</th>
              <th>Mean</th>
              <th title="The fixed offset the report and nestcheck use today">In use</th><th>Measured as</th>
            </tr>
          </thead>
          <tbody>
            <CalRow label="2nd egg" stage={st('2nd egg')} single={st1('2nd egg')} inUse={SECOND_EGG_LAG_DAYS}
              how="one egg in the box, then two" />
            <CalRow label="Hatch" stage={st('hatch')} single={st1('hatch')} inUse={BREEDING_OFFSETS.hatch}
              how="eggs only, then chicks" />
            <CalRow label="Guard ends" stage={guard} single={st1('guard ends')} inUse={BREEDING_OFFSETS.pg}
              how="first check finding chicks with no adult — an upper bound, it needs a visit" />
            <CalRow label="Fledge" stage={fledge} single={st1('fledge')} inUse={BREEDING_OFFSETS.fledge}
              how="chicks, then none — nests that got a chick to chipping only" />
          </tbody>
        </table>
        <p className="muted" style={{ marginTop: 8 }}>
          <b>The chip window is proposed, not measured.</b> A chipping dates the visit, not the chick —
          reading the window off the chippings would measure the roster. So it is placed between the two
          stages either side of it: it opens {Math.round(CHIP_OPEN_FRACTION * 100)}% of the way from guard
          ending to fledging, when the chicks are near adult size, and closes {CHIP_CLOSE_BEFORE_FLEDGE} days
          before fledging, while a visit can still find them in the burrow.
          {guard && fledge && chipOpen !== null && chipClose !== null && <>
            {' '}From the rows above — guard ends {calDays(guard.mean)}, fledge {calDays(fledge.mean)} — that puts the
            window at <b>{calDays(chipOpen)} to {calDays(chipClose)}</b> days (in use: +{BREEDING_OFFSETS.chip} to
            {' '}+{BREEDING_OFFSETS.chip + 7}).
          </>}
          {chipped && <> As a check, the {chipped.n} chippings actually made average {calDays(chipped.mean)} days
            (median {calDays(chipped.median)}) — inside the window if the proposal is right.</>}
        </p>
        <p className="muted">
          <b>Single-egg clutches are kept apart</b> — a brood of one may well run to a different clock — but
          this colony can't answer the question yet: {cal.oneEgg} of {cal.clutches} clutches never held more
          than one egg, and {cal.oneEggBred} of those produced a chick. Their column is shown for what it is
          worth, greyed below {CAL_MIN_N} clutches; the two-egg figures are the ones the offsets should follow.
        </p>
      </>}
    </div>
  );
}

/** Season breeding report — one row per box (per clutch), ordered by box number, with the
 *  breeding calendar across the columns in date order: 1st egg, 2nd egg, hatch, guard ends
 *  (PG), chip window opens, fledge. Dates derive from the estimated laid date using the same
 *  offsets as nestcheck's Next Breeding Dates; the second egg is laid ~2 days after the first.
 *  Hatch switches to the observed midpoint once chicks have actually been recorded. Dates that
 *  have already passed are dimmed a touch; the next one due is bold. Built entirely from the
 *  local cache. */
function SeasonBreedingReport() {
  const v = useDbVersion();
  const [seasonYear, setSeasonYear] = useState<number>(() => getSeasonStart().getFullYear());

  const { rows, seasons } = useMemo(() => {
    const seasons = new Set<number>();
    const all: any[] = [];
    for (const { box, detail: bd, families } of allColonyBoxes()) {
      const sObs = [...bd.observations].sort((a: any, b: any) =>
        parseDate(a.observation_time_utc).getTime() - parseDate(b.observation_time_utc).getTime());
      // Segment over the box's whole history: a clutch laid just after 1 April needs the
      // pre-season empty check to anchor its laid estimate.
      const clutches = segmentClutches(sObs);
      // Chick outcome per clutch — chipped and fledged counts — from the SAME family detection
      // the box and bird views use, matched to each clutch by its anchor observation. Chipped
      // chicks reached chipping age (~fledge); fledged also counts unchipped chicks a monitor
      // recorded as presumed fledged.
      const famByStart = new Map<number, any>();
      for (const sd of families)
        for (const fam of sd.families)
          if (fam.clutch.startObsId != null) famByStart.set(fam.clutch.startObsId, fam);
      clutches.forEach((c, i) => {
        const y = getSeasonStart(new Date(c.start)).getFullYear();
        seasons.add(y);
        if (y !== seasonYear) return;
        // Observed hatch: midpoint between the last egg-only check and the first check with
        // chicks, both inside this clutch. Beats the +38d prediction once it has happened.
        let hatch: number | null = null, hatchObserved = false;
        const inClutch = sObs.filter(o => {
          const t = parseDate(o.observation_time_utc).getTime();
          return t >= c.start && t <= (c.end ?? Infinity);
        });
        const firstChick = inClutch.find(o => (o.chicks || 0) > 0);
        if (firstChick) {
          const ft = parseDate(firstChick.observation_time_utc).getTime();
          const before = [...inClutch].reverse().find(o =>
            parseDate(o.observation_time_utc).getTime() < ft && (o.chicks || 0) === 0);
          const bt = before ? parseDate(before.observation_time_utc).getTime() : null;
          hatch = bt === null ? ft : bt + Math.ceil((ft - bt) / 2 / DAY) * DAY;
          hatchObserved = true;
        } else if (c.laid !== null) {
          hatch = c.laid + BREEDING_OFFSETS.hatch * DAY;
        }
        const latest = inClutch[inClutch.length - 1];
        const fam = c.startObsId != null ? famByStart.get(c.startObsId) : null;
        all.push({
          box, boxNum: parseInt(box, 10),
          attempt: i, // index within the box's whole history; renumbered per season below
          clutch: c, hatch, hatchObserved,
          twoEggs: (c.maxEggs || 2) >= 2,
          chipped: fam ? fam.chicks.length : 0,
          fledged: fam ? fam.chicks.length + fam.fledgedUnchipped : 0,
          status: latest ? displayStatusOrPrev(latest, box) : null,
          startObsTime: c.startObsTime,
        });
      });
    }
    // Earliest first egg first — the season reads as the laying order. A clutch with no laid
    // estimate falls back to the date its eggs were found; box number breaks ties.
    const laidOf = (r: any) => r.clutch.laid ?? r.clutch.start;
    all.sort((a, b) => laidOf(a) - laidOf(b)
      || (isNaN(a.boxNum) ? 1e9 : a.boxNum) - (isNaN(b.boxNum) ? 1e9 : b.boxNum)
      || String(a.box).localeCompare(String(b.box)));
    // Renumber attempts within the season so a box's second clutch reads "2nd", not "5th".
    const seen = new Map<string, number>();
    for (const r of all) { const n = (seen.get(r.box) || 0) + 1; seen.set(r.box, n); r.attempt = n; }
    // Running clutch number down the whole report (first column).
    all.forEach((r, i) => { r.clutchNo = i + 1; });
    return { rows: all, seasons: [...seasons].sort((a, b) => b - a) };
  }, [v, seasonYear]);
  const [shown, showAllBtn] = useTopRows(rows, 3, 'Show first 3');

  const now = Date.now();
  /** A milestone cell. Past dates go slightly grey (still near-black); the next one due is
   *  bold. `est` marks a date predicted from the laid estimate rather than observed. */
  const Cell = ({ t, next, est, title }: { t: number | null; next?: boolean; est?: boolean; title?: string }) => {
    if (t === null) return <td style={{ color: '#bbb' }}>—</td>;
    const past = t < now;
    return <td title={title || (est ? 'Predicted from the estimated laid date' : 'From observations')}
      style={{ whiteSpace: 'nowrap', color: past ? '#4a4a4a' : '#111', fontWeight: next ? 700 : 400 }}>{fmtMs(t)}</td>;
  };
  /** Chick-outcome cell: one ✓ per chick, or a dash for none. More visual than a bare count. */
  const Ticks = ({ n, title }: { n: number; title: string }) =>
    n > 0
      ? <td title={title} style={{ whiteSpace: 'nowrap', color: '#2f8f5b', fontSize: 15, letterSpacing: 2 }}>{'✓'.repeat(n)}</td>
      : <td title={title} style={{ color: '#ccc', textAlign: 'center' }}>—</td>;

  return (
    <div className="report-card">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
        <h3 style={{ margin: 0 }}>Breeding report — {seasonRange(String(seasonYear))}</h3>
        <select value={seasonYear} onChange={e => setSeasonYear(parseInt(e.target.value, 10))} style={{ fontSize: 13, padding: '3px 6px' }}>
          {(seasons.length ? seasons : [seasonYear]).map(y => <option key={y} value={y}>{seasonRange(String(y))}</option>)}
        </select>
      </div>
      <p className="muted">
        One row per breeding attempt ({rows.length}), earliest first egg first. Dates come from the estimated
        laid date (2nd egg +2d, hatch +{BREEDING_OFFSETS.hatch}d, guard ends +{BREEDING_OFFSETS.pg}d,
        chip window +{BREEDING_OFFSETS.chip}d to +{BREEDING_OFFSETS.chip + 7}d, fledge +{BREEDING_OFFSETS.fledge}d);
        an observed hatch replaces the predicted one. Chipped / Fledged show one ✓ per chick.
        Past dates are dimmed, the next one due is bold.
      </p>
      <BreedingCalibration />
      {rows.length === 0 ? <p className="muted">No breeding attempts recorded this season.</p> : (
        <table className="guess-rank-table mini-list-table">
          <thead>
            <tr>
              <th>Clutch</th><th>Box</th><th>1st egg</th><th>2nd egg</th><th>Hatch</th><th>PG (guard ends)</th>
              <th>Chip from</th><th>Chip to</th><th>Fledge</th>
              <th title="Chicks chipped in this clutch">Chipped</th>
              <th title="Chicks fledged (chipped chicks reach fledge age; plus unchipped chicks recorded as fledged)">Fledged</th>
            </tr>
          </thead>
          <tbody>
            {shown.map(r => {
              const c = r.clutch as Clutch;
              const off = (n: number) => c.laid === null ? null : c.laid + n * DAY;
              const active = clutchActive(c);
              // Chip window: opens at +chip days, runs 7 days. "Chip to" = chip from + 7.
              const parts: (number | null)[] = [off(0), r.twoEggs ? off(2) : null, r.hatch,
                off(BREEDING_OFFSETS.pg), off(BREEDING_OFFSETS.chip), off(BREEDING_OFFSETS.chip + 7),
                off(BREEDING_OFFSETS.fledge)];
              // Only a running clutch has a "next" milestone to highlight.
              const nextIdx = active ? parts.findIndex(p => p !== null && p >= now) : -1;
              const unc = c.laidUncertainty !== null && c.laidUncertainty > 0
                ? `± ${c.laidUncertainty} day${c.laidUncertainty !== 1 ? 's' : ''}` : '';
              return (
                <tr key={`${r.box}-${c.start}`}>
                  <td style={{ color: '#666', fontVariantNumeric: 'tabular-nums' }}>{r.clutchNo}</td>
                  <td style={{ whiteSpace: 'nowrap', fontWeight: 600 }}>
                    <a className="day-box-link" href={`/?box=${encodeURIComponent(r.box)}&obs=${encodeURIComponent(r.startObsTime)}`}>Box {r.box}</a>
                    {r.attempt > 1 && <span className="muted" style={{ fontWeight: 400 }}> ({ordinal(r.attempt)})</span>}
                  </td>
                  {c.laid === null
                    ? <td colSpan={2} style={{ color: '#999', fontStyle: 'italic', whiteSpace: 'nowrap' }}
                        title="No empty check before the eggs — the laid date can't be estimated">
                        eggs found {fmtMs(c.start)}
                      </td>
                    : <>
                        <Cell t={parts[0]} next={nextIdx === 0} est title={`Estimated laid date${unc ? ` ${unc}` : ''}`} />
                        <Cell t={parts[1]} next={nextIdx === 1} est title={`Second egg, ~2 days after the first${unc ? ` ${unc}` : ''}`} />
                      </>}
                  <Cell t={parts[2]} next={nextIdx === 2} est={!r.hatchObserved}
                    title={r.hatchObserved ? 'Observed: midpoint of the last egg-only check and the first chick check' : undefined} />
                  <Cell t={parts[3]} next={nextIdx === 3} est />
                  <Cell t={parts[4]} next={nextIdx === 4} est title="Chip window opens (estimated)" />
                  <Cell t={parts[5]} next={nextIdx === 5} est title="Chip window closes — 7 days after it opens (estimated)" />
                  <Cell t={parts[6]} next={nextIdx === 6} est />
                  <Ticks n={r.chipped} title={`${r.chipped} chick${r.chipped !== 1 ? 's' : ''} chipped`} />
                  <Ticks n={r.fledged} title={`${r.fledged} chick${r.fledged !== 1 ? 's' : ''} fledged`} />
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      {showAllBtn}
    </div>
  );
}

/** Reports page body: the report cards grouped into tabs (mirrors AdminPanel's tab bar).
 *  The active tab is persisted to the URL's ?tab= param, just like the admin page. */
/** How to get around: the keys the app answers to. Kept next to the algorithm explanation
 *  because both are things a monitor reads rather than operates. */
function SiteNavigationDoc() {
  const rows: [string, string][] = [
    ['/', 'Jump to the search box'],
    ['↓ ↑', 'Move through the search results'],
    ['Enter', 'Open the highlighted result (or the top one)'],
    ['Esc', 'Clear the search — or close the box or day you are in'],
    ['> and <', 'Next / previous box (same keys as . and ,)'],
    ['→ ←', 'Next / previous penguin, with a penguin panel open'],
  ];
  return (
    <section className="doc-section">
      <h2>Site navigation</h2>
      <p className="muted">Shortcuts are ignored while you are typing in a field, so a "/" or a comma in a note is just text.</p>
      <table className="doc-keys">
        <tbody>
          {rows.map(([key, what]) => (
            <tr key={key}><td><kbd>{key}</kbd></td><td>{what}</td></tr>
          ))}
        </tbody>
      </table>
      <p className="muted">
        The search covers boxes, penguin numbers, chip numbers, dates, and the text of penguin,
        observation and day notes. <code>"gate open"</code> matches that exact wording;
        <code> bs|mv</code> matches either.
      </p>
      <p className="muted">In the day view the arrow keys step between dates instead of penguins.</p>
    </section>
  );
}

function DocsPage() {
  return (
    <div className="docs-page">
      <h1 className="docs-title">Documentation</h1>
      <SiteNavigationDoc />
      <section className="doc-section">
        <h2>Wildwatch analysis detail</h2>
        <Suspense fallback={<p className="muted">Loading…</p>}>
          <AlgorithmDoc seasonStartMonth={SEASON_START_MONTH} seasonStartDay={SEASON_START_DAY} />
        </Suspense>
      </section>
    </div>
  );
}

// Module scope so the deferred-build order is a stable reference across renders.
const REPORT_TABS = ['birds', 'colony', 'breeding', 'population', 'social', 'quality'] as const;
type ReportTab = typeof REPORT_TABS[number];

function ReportsPage({ onOpenBird, onDayClick, token, colonyName }: { onOpenBird: (num: string) => void; onDayClick: (day: string) => void; token: string; colonyName?: string }) {
  const [tab, setTab] = useState<ReportTab>(() => {
    const t = new URLSearchParams(window.location.search).get('tab');
    return (REPORT_TABS as readonly string[]).includes(t || '') ? (t as ReportTab) : 'birds';
  });
  const selectTab = (id: ReportTab) => {
    setTab(id);
    const u = new URL(window.location.href);
    if (id === 'birds') u.searchParams.delete('tab'); else u.searchParams.set('tab', id);
    window.history.replaceState(null, '', u.pathname + u.search);
  };
  // Every one of these tabs costs a pass over the colony. Build the open one, then the rest
  // while the browser is idle, so the page appears at once and switching stays instant.
  const built = useDeferredTabs(tab, REPORT_TABS);

  return (
    <>
      <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap', margin: '0 0 16px', borderBottom: '1px solid #ddd' }}>
        {(([['birds', 'All penguins'], ['colony', 'Colony'], ['breeding', 'Breeding & chicks'], ['population', 'Population'], ['social', 'Pairs & groups'], ['quality', 'Data quality']]) as const).map(([id, label]) => (
          <button key={id} onClick={() => selectTab(id)}
            style={{ padding: '8px 14px', border: 'none', borderBottom: tab === id ? '2px solid #1a6b8f' : '2px solid transparent',
              background: 'none', cursor: 'pointer', fontWeight: tab === id ? 600 : 400, color: tab === id ? '#1a6b8f' : '#555', fontSize: 14, marginBottom: -1 }}>
            {label}
          </button>
        ))}
      </div>

      <div style={{ display: tab === 'birds' ? undefined : 'none' }}>
        {built('birds') && <AllPenguinsPage token={token} colonyName={colonyName} onOpenBird={onOpenBird} />}
      </div>

      <div style={{ display: tab === 'colony' ? undefined : 'none' }}>
        {built('colony') && <>
          <DistinctAdultsChart />
          <PeakAdultsChart onDayClick={onDayClick} />
          <FirstEggReport onDayClick={onDayClick} />
          <EggArrivalChart />
        </>}
      </div>

      <div style={{ display: tab === 'breeding' ? undefined : 'none' }}>
        {built('breeding') && <>
          <SeasonBreedingReport />
          <ChickReturnChart />
          <ChickSexChart />
          <ChickSexBothReturnedChart />
          <TopChickParentsReport onOpenBird={onOpenBird} />
          <UnproductiveParentsReport onOpenBird={onOpenBird} />
        </>}
      </div>

      <div style={{ display: tab === 'population' ? undefined : 'none' }}>
        {built('population') && <>
          <PenguinAgeCharts />
          <SurvivalPredictionReport />
        </>}
      </div>

      <div style={{ display: tab === 'social' ? undefined : 'none' }}>
        {built('social') && <>
          <PairBondReport onOpenBird={onOpenBird} />
          <PhilandererReport onOpenBird={onOpenBird} />
          <FloaterReport onOpenBird={onOpenBird} />
          <PenguinGroupsReport onOpenBird={onOpenBird} />
        </>}
      </div>

      <div style={{ display: tab === 'quality' ? undefined : 'none' }}>
        {built('quality') && <>
          <MissedScansReport />
          <UnsexedByGuessesReport />
        </>}
      </div>
    </>
  );
}

/** Which boxes are DCM over time — one row per box ever recorded DCM, one column per period. DCM
 *  carries forward: a box counts as DCM from a DCM observation until its next NON-DCM status
 *  (blank statuses don't end it), so the carried-forward periods with no observation still show.
 *  Built from the local cache. */
function DcmBoxesChart() {
  const v = useDbVersion();
  const [gran, setGran] = useState<'season' | 'month'>('month');
  const pad2 = (n: number) => String(n).padStart(2, '0');
  const seasonYearOfNz = (nz: string): number => { const [y, m, d] = nz.split('-').map(Number); return getSeasonStart(new Date(y, m - 1, d)).getFullYear(); };

  const { boxes, intervals, minNz, maxNz } = useMemo(() => {
    const todayNz = toNzDateStr(new Date().toISOString());
    // Per box: DCM intervals [start, end) in NZ dates (end exclusive).
    const intervals = new Map<string, { start: string; end: string }[]>();
    let minNz: string | null = null, maxNz: string | null = null;
    for (const { box, detail: bd } of allColonyBoxes()) {
      const sorted = [...bd.observations].sort((a: any, b: any) => String(a.observation_time_utc).localeCompare(String(b.observation_time_utc)));
      let runStart: string | null = null;
      const ivs: { start: string; end: string }[] = [];
      for (const o of sorted) {
        const st = (o.breeding_status || '').trim().toUpperCase();
        if (!st) continue; // blank status carries the previous one forward — doesn't end a run
        const nz = toNzDateStr(o.observation_time_utc);
        if (st === 'DCM') {
          if (runStart === null) runStart = nz;
        } else if (runStart !== null) {
          ivs.push({ start: runStart, end: nz }); runStart = null;
        }
      }
      if (runStart !== null) ivs.push({ start: runStart, end: todayNz > runStart ? todayNz : runStart }); // still DCM → through today
      if (!ivs.length) continue;
      intervals.set(box, ivs);
      for (const iv of ivs) { if (minNz === null || iv.start < minNz) minNz = iv.start; if (maxNz === null || iv.end > maxNz) maxNz = iv.end; }
    }
    const boxes = [...intervals.keys()].sort((a, b) => (parseInt(a, 10) || 1e9) - (parseInt(b, 10) || 1e9) || a.localeCompare(b));
    return { boxes, intervals, minNz, maxNz };
  }, [v]);

  // Contiguous timeline columns, each with its [start, end) NZ-date bounds for overlap testing.
  const columns = useMemo(() => {
    if (!minNz || !maxNz) return [] as { key: string; label: string; start: string; end: string }[];
    const out: { key: string; label: string; start: string; end: string }[] = [];
    const seasonStartStr = (Y: number) => `${Y}-${pad2(SEASON_START_MONTH)}-${pad2(SEASON_START_DAY)}`;
    if (gran === 'season') {
      for (let y = seasonYearOfNz(minNz); y <= seasonYearOfNz(maxNz); y++)
        out.push({ key: String(y), label: `${pad2(y % 100)}/${pad2((y + 1) % 100)}`, start: seasonStartStr(y), end: seasonStartStr(y + 1) });
    } else {
      const MON = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
      const [fy, fm] = minNz.slice(0, 7).split('-').map(Number);
      const [ly, lm] = maxNz.slice(0, 7).split('-').map(Number);
      for (let i = fy * 12 + (fm - 1); i <= ly * 12 + (lm - 1); i++) {
        const y = Math.floor(i / 12), m = i % 12;
        const key = `${y}-${pad2(m + 1)}`;
        const end = m === 11 ? `${y + 1}-01-01` : `${y}-${pad2(m + 2)}-01`;
        out.push({ key, label: `${MON[m]} ${pad2(y % 100)}`, start: `${key}-01`, end });
      }
    }
    return out;
  }, [gran, minNz, maxNz]);

  const isDcm = (box: string, col: { start: string; end: string }): boolean => {
    for (const iv of intervals.get(box) || []) if (iv.start < col.end && iv.end > col.start) return true;
    return false;
  };

  return (
    <div className="report-card">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
        <h3 style={{ margin: 0 }}>Which boxes are DCM over time</h3>
        <div style={{ display: 'flex', gap: 6 }}>
          {(['season', 'month'] as const).map(g => (
            <button key={g} className="edit-btn" onClick={() => setGran(g)}
              style={{ background: gran === g ? '#1a6b8f' : undefined, color: gran === g ? '#fff' : undefined }}>
              {g === 'season' ? 'Per season' : 'Per month'}
            </button>
          ))}
        </div>
      </div>
      <p className="muted">One row per box ever recorded DCM ({boxes.length}); a filled cell means the box was DCM in that {gran === 'season' ? 'season' : 'month'}, carried forward until its next non-DCM status.</p>
      {boxes.length === 0 ? <p className="muted">No DCM boxes recorded.</p> : (
        <div style={{ overflow: 'auto', maxHeight: '70vh', border: '1px solid #eee', borderRadius: 6 }}>
          <table style={{ borderCollapse: 'collapse', fontSize: 11 }}>
            <thead>
              <tr>
                <th style={{ position: 'sticky', left: 0, top: 0, zIndex: 3, background: '#fff', padding: '4px 8px', textAlign: 'left', borderRight: '1px solid #eee', borderBottom: '1px solid #eee' }}>Box</th>
                {columns.map(c => (
                  <th key={c.key} style={{ position: 'sticky', top: 0, zIndex: 2, background: '#fff', padding: '3px 4px', whiteSpace: 'nowrap', fontWeight: 500, color: '#666', borderBottom: '1px solid #eee' }}>{c.label}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {boxes.map(box => (
                <tr key={box}>
                  <td style={{ position: 'sticky', left: 0, zIndex: 1, background: '#fff', padding: '2px 8px', fontWeight: 600, whiteSpace: 'nowrap', borderRight: '1px solid #eee' }}>{box}</td>
                  {columns.map(c => {
                    const dcm = isDcm(box, c);
                    return <td key={c.key} title={dcm ? `Box ${box} · DCM · ${c.label}` : undefined}
                      style={{ minWidth: 18, height: 18, background: dcm ? '#8D6E63' : undefined, borderRight: '1px solid #f3f3f3', borderBottom: '1px solid #f3f3f3' }} />;
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// Module scope so the deferred-build order is a stable reference across renders.
const ADMIN_TABS = ['enter', 'io', 'validation', 'users', 'database', 'system', 'mirror'] as const;
type AdminTab = typeof ADMIN_TABS[number];
// Which tabs are worth building ahead of being asked for. "enter" is a form with its own state
// and its own workflow — people go there deliberately, and pre-mounting a data-entry form to
// save a few ms is the wrong trade. "mirror" only exists on the mirror host and its status
// view is a live fetch. Both still build the moment they're opened.
const ADMIN_PRELOAD_TABS = ['io', 'validation', 'users', 'database', 'system'] as const;

function AdminPanel({ token, observationDates, checkTarget, allPenguins, fmColony, onLeaveEntry, mirrorAlert }: {
  token: string; observationDates?: string[];
  // Why the mirror is unhealthy (stale, unverified, or unreachable), or null. Puts the same red
  // "1" the Admin nav item shows on the Mirror tab, so the trail from "something's wrong" to the
  // card that explains it is unbroken.
  mirrorAlert?: string | null;
  // A header pin was clicked: jump to the validation tab and scroll to this check. The nonce
  // makes a repeat click on the same pin re-trigger the effect.
  checkTarget?: { slug: string; nonce: number } | null;
  // For the data-entry tab, which is the same page it was as a standalone route.
  allPenguins?: any[]; fmColony?: boolean; onLeaveEntry?: () => void;
}) {
  const [users, setUsers] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);
  const [diskTest, setDiskTest] = useState<any>(null);
  const [diskTesting, setDiskTesting] = useState(false);
  const [serverDisk, setServerDisk] = useState<any>(null);
  const [datePreview, setDatePreview] = useState<any>(null);
  const [recentChanges, setRecentChanges] = useState<any[]|null>(null);
  const [changesLoading, setChangesLoading] = useState(false);
  const [changesUser, setChangesUser] = useState('');   // '' = all users
  const [changesDays, setChangesDays] = useState(7);    // admin.php caps this at 30
  const [exporting, setExporting] = useState(false);
  const dbVersion = useDbVersion(); // bumps whenever the local DB (IndexedDB) syncs
  // FM completeness: every incomplete registered book FM day up to today, in the order
  // the book tables list them, with the number of box observations still needed for it
  // to count as a full monitor. Grouped by season, oldest season first.
  const { registeredFmDates } = useContext(DateTooltipCtx);
  const fmCompleteness = useMemo(() => {
    const today = toNzDateStr(new Date().toISOString());
    const bySeason = new Map<number, { day: string; number: number; missing: number; boxes: string[] }[]>();
    let total = 0;
    for (const [day, fm] of registeredFmDates) {
      if (day > today) continue;
      if (fm.partial) continue; // Partial Monitor days are deliberately incomplete — not actionable
      const st = computeDateStats(day);
      if (!st) continue;
      if (st.isFullMonitor) continue; // complete days aren't actionable — hide them
      const boxes: string[] = st.missingBoxes || [];
      // Group by the Apr 1 – Mar 31 season the date actually falls in, not the book's
      // season_year label (a book can number dates past 1 Apr into the next season).
      const seasonYear = Number(day.slice(0, 4)) - (Number(day.slice(5, 7)) >= 4 ? 0 : 1);
      if (!bySeason.has(seasonYear)) bySeason.set(seasonYear, []);
      bySeason.get(seasonYear)!.push({ day, number: fm.number, missing: boxes.length, boxes });
      total++;
    }
    const seasons = Array.from(bySeason.entries()).sort((a, b) => a[0] - b[0]);
    return { seasons, total };
  }, [registeredFmDates, dbVersion]);
  // "Mirror" tab is only meaningful on the backup mirror (set by the /me response). Its
  // status view + action buttons all 404 on production.
  const isMirror = localStorage.getItem('ww_is_mirror') === '1';
  const [adminTab, setAdminTab] = useState<AdminTab>(() => {
    const t = new URLSearchParams(window.location.search).get('tab');
    return (ADMIN_TABS as readonly string[]).includes(t || '') ? (t as AdminTab) : 'io';
  });
  const selectTab = (id: AdminTab) => {
    setAdminTab(id);
    const u = new URL(window.location.href);
    if (id === 'io') u.searchParams.delete('tab'); else u.searchParams.set('tab', id);
    window.history.replaceState(null, '', u.pathname + u.search);
  };
  // Validation alone is a full pass over the colony, and each tab's data comes from its own
  // fetch. Build the open tab, then the rest during idle time.
  const built = useDeferredTabs<AdminTab>(adminTab, ADMIN_PRELOAD_TABS);
  // A pinned-check link lands as /?admin&tab=validation#check-slug. The check's rows come from
  // localdb, so the section can be a stub on first paint — re-run as the db version bumps,
  // but only until a scroll has happened with real data (dbVersion > 0). Without the guard,
  // every later db bump (an edit like "+ No scan", the 30s poll) yanks the page back to the
  // hash target.
  const hashScrollDone = useRef<string | null>(null);
  useEffect(() => {
    if (adminTab !== 'validation') return;
    const hash = window.location.hash.slice(1);
    if (!hash.startsWith('check-')) return;
    if (hashScrollDone.current === hash) return;
    document.getElementById(hash)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    if (dbVersion > 0) hashScrollDone.current = hash;
  }, [adminTab, dbVersion]);

  // ---- Mirror tab: backup-status view + on-demand actions (mirror server only) ----
  const [mirrorHtml, setMirrorHtml] = useState<string>('');
  const [mirrorMsg, setMirrorMsg] = useState<string>('');
  const [mirrorRunning, setMirrorRunning] = useState(false);
  const mirrorPoll = useRef<number | null>(null);
  const loadMirrorStatus = () => {
    const t = localStorage.getItem('ww_token') || '';
    fetch('/api/status.php', { headers: { 'Authorization': `Bearer ${t}` } })
      .then(r => r.ok ? r.text() : Promise.reject(r.status))
      .then(setMirrorHtml)
      .catch(() => setMirrorHtml('<p style="font-family:system-ui;padding:2rem;color:#900">Could not load backup status.</p>'));
  };
  const stopMirrorPoll = () => { if (mirrorPoll.current) { clearInterval(mirrorPoll.current); mirrorPoll.current = null; } setMirrorRunning(false); };
  useEffect(() => { if (adminTab === 'mirror') loadMirrorStatus(); }, [adminTab]);
  useEffect(() => stopMirrorPoll, []); // clear the interval if the tab/app unmounts mid-run

  // One action: the full nightly — fresh dump, restore, verify, AND code refresh. (The
  // code-only path still exists server-side as action=release, but a standalone "update code"
  // button was a footgun: run right after a schema change, the mirror serves new code against
  // an un-restored old database. Backup does everything and is always safe.) The status page
  // only changes when the run finishes writing it, so we poll it ourselves rather than leaving
  // a manual "refresh" button — every 15s for ~8 min, which covers the ~1 min queue + restore.
  const triggerMirror = async () => {
    const t = localStorage.getItem('ww_token') || '';
    setMirrorRunning(true);
    setMirrorMsg('Backup + restore queued — starts within a minute, takes a few. The status below updates itself.');
    try {
      const r = await fetch('/api/status.php?action=backup', { method: 'POST', headers: { 'Authorization': `Bearer ${t}` } });
      const d = await r.json().catch(() => ({}));
      if (!r.ok) { setMirrorMsg(d.error || `Failed (HTTP ${r.status})`); stopMirrorPoll(); return; }
      setMirrorMsg(d.message || 'Queued.');
    } catch { setMirrorMsg('Failed to reach the server.'); stopMirrorPoll(); return; }
    stopMirrorPoll();
    setMirrorRunning(true);
    let ticks = 0;
    mirrorPoll.current = window.setInterval(() => { loadMirrorStatus(); if (++ticks >= 32) stopMirrorPoll(); }, 15000);
  };

  // In-app arrival from a header pin. selectTab writes ?tab=validation; the scroll waits a
  // tick because the target section is inside a display:none tab until this render commits,
  // and scrollIntoView on a hidden element does nothing. Same once-per-target guard as the
  // hash effect above; a fresh click bumps the nonce, so repeat pins still scroll.
  const pinSlug = checkTarget?.slug;
  const pinNonce = checkTarget?.nonce;
  const pinScrollDone = useRef<string | null>(null);
  useEffect(() => {
    if (!pinSlug) return;
    const key = `${pinSlug}:${pinNonce}`;
    if (pinScrollDone.current === key) return;
    selectTab('validation');
    const t = setTimeout(() => {
      document.getElementById(pinSlug)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
      if (dbVersion > 0) pinScrollDone.current = key;
    }, 0);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pinSlug, pinNonce, dbVersion]);

  // --- Monitor CSV import (two-phase: analyze -> confirm -> commit) ---
  const [impFile, setImpFile] = useState<string>('');        // filename
  const [impCsv, setImpCsv] = useState<string>('');          // raw CSV text
  const [impColony, setImpColony] = useState<number>(getColonyId());
  const [impAnalysis, setImpAnalysis] = useState<any>(null);
  const [impAnalyzing, setImpAnalyzing] = useState(false);
  const [impCommitting, setImpCommitting] = useState(false);
  // Override: import rows that conflict with same-day existing data as second observations.
  const [impConflicts, setImpConflicts] = useState(false);
  const [impResult, setImpResult] = useState<any>(null);
  const [impError, setImpError] = useState('');
  const [impRowFilter, setImpRowFilter] = useState<'issues' | 'all'>('issues');

  // --- Old NestCheck monitor JSON import (v37-era penguin_monitors.json) ---
  const [jsonFile, setJsonFile] = useState('');
  const [jsonText, setJsonText] = useState('');
  const [jsonColony, setJsonColony] = useState<number>(getColonyId());
  const [jsonAnalysis, setJsonAnalysis] = useState<any>(null);
  const [jsonAnalyzing, setJsonAnalyzing] = useState(false);
  const [jsonCommitting, setJsonCommitting] = useState(false);
  const [jsonResult, setJsonResult] = useState<any>(null);
  const [jsonError, setJsonError] = useState('');
  // Bird panel docked on the right of the admin screen (opened from #peng cells / import minis).
  const [adminBird, setAdminBird] = useState<string|null>(null);
  const adminBirdData = useBirdDetail(adminBird);
  useEffect(() => { _adminOpenBird = setAdminBird; return () => { if (_adminOpenBird === setAdminBird) _adminOpenBird = null; }; }, []);
  const allPengsForMini = useAllPenguins();
  const pengByNumMini = useMemo(() => {
    const m = new Map<string, any>();
    for (const p of (allPengsForMini || [])) m.set(String(p.peng_num), p);
    return m;
  }, [allPengsForMini]);

  // Data-integrity checks — computed locally from the colony cache (instant).
  const iBirdTwoBoxes = useBirdTwoBoxes();
  const iScanBeforeChip = useScanBeforeChip();
  const iVerify = useMemo(() => computeVerifyConflicts(), [dbVersion]);
  const iMissingChipMeasures = useMissingChipMeasures();
  const iDeadScanned = useDeadScanned();
  const iImprobable = useImprobableCounts();
  const iFuture = useFutureObservations();
  const iRetired = useRetiredTagScans();
  const iChicksNoScan = useChicksNoScan();
  const iDupObs = useDuplicateObservations();
  const iDupScans = useDuplicateScans();
  const iSameGender = useSameGenderConflicts();
  const iChickSize = useChickSizeMismatch();
  // A Little Chick that out-measures its Big Chick nest-mate on EITHER chip-day measurement —
  // weight or flipper — is a likely swapped/mis-entered size code. Both orderings are checked
  // and both metrics shown, with the offending (LC > BC) value flagged.
  const chickSizeRows = useMemo(() => {
    const num = (v: any) => v == null ? null : Number(v);
    return iChickSize
      .map((r: any) => {
        const bcW = num(r.bc_weight), lcW = num(r.lc_weight);
        const bcF = num(r.bc_flipper), lcF = num(r.lc_flipper);
        const wInv = bcW != null && lcW != null && lcW > bcW;
        const fInv = bcF != null && lcF != null && lcF > bcF;
        return { box_name: r.box_name, chip_date: r.chip_date, chipper: r.chipper, bc_peng: r.bc_peng, lc_peng: r.lc_peng,
          bc_w: bcW, lc_w: lcW, bc_f: bcF, lc_f: lcF, wInv, fInv,
          wDiff: wInv ? lcW! - bcW! : 0, fDiff: fInv ? lcF! - bcF! : 0, _href: r._href };
      })
      .filter((r: any) => r.wInv || r.fInv)
      .sort((a: any, b: any) =>
        ((b.wInv ? 1 : 0) + (b.fInv ? 1 : 0)) - ((a.wInv ? 1 : 0) + (a.fInv ? 1 : 0))
        || b.wDiff - a.wDiff || b.fDiff - a.fDiff);
  }, [iChickSize]);

  const impReset = () => { setImpAnalysis(null); setImpResult(null); setImpError(''); setImpConflicts(false); };

  // Analyze immediately (from args, since state updates are async on file pick / colony change).
  const impAnalyze = async (csv = impCsv, filename = impFile, colony = impColony) => {
    if (!csv.trim()) { setImpError('Choose a CSV file first'); return; }
    setImpAnalyzing(true); setImpError(''); setImpResult(null); setImpAnalysis(null);
    try {
      const r = await fetch('/api/admin.php?action=import_csv_analyze', {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
        body: JSON.stringify({ csv, filename, colony_id: colony }),
      });
      const d = await r.json();
      if (!r.ok || d.error) throw new Error(d.error || `HTTP ${r.status}`);
      setImpAnalysis(d);
      setImpRowFilter('issues');   // problems first; toggle to all rows if wanted
    } catch (e: any) { setImpError(e.message || 'Analysis failed'); }
    setImpAnalyzing(false);
  };

  const impPickFile = async (f: File | null) => {
    impReset();
    if (!f) { setImpFile(''); setImpCsv(''); return; }
    const text = await f.text();
    setImpFile(f.name); setImpCsv(text);
    impAnalyze(text, f.name, impColony);   // analyze on selection — no button
  };

  const impCommit = async () => {
    if (!impAnalysis || impCommitting) return;
    setImpCommitting(true); setImpError('');
    try {
      const r = await fetch('/api/admin.php?action=import_csv_commit', {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
        body: JSON.stringify({ csv: impCsv, filename: impFile, colony_id: impColony, import_conflicts: impConflicts }),
      });
      const d = await r.json();
      if (!r.ok || d.error) throw new Error(d.error || `HTTP ${r.status}`);
      setImpResult(d);
      setImpAnalysis(null);
      // If we imported into the colony currently being viewed, pull the new rows into the cache.
      if (impColony === getColonyId()) triggerSync();
    } catch (e: any) { setImpError(e.message || 'Import failed'); }
    setImpCommitting(false);
  };

  // Old NestCheck monitor JSON import — same analyze -> confirm -> commit shape as the CSV flow.
  const jsonReset = () => { setJsonAnalysis(null); setJsonResult(null); setJsonError(''); };
  const jsonAnalyze = async (text = jsonText, filename = jsonFile, colony = jsonColony) => {
    if (!text.trim()) { setJsonError('Choose a JSON file first'); return; }
    setJsonAnalyzing(true); setJsonError(''); setJsonResult(null); setJsonAnalysis(null);
    try {
      const r = await fetch('/api/admin.php?action=import_json_analyze', {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
        body: JSON.stringify({ json: text, filename, colony_id: colony }),
      });
      const d = await r.json();
      if (!r.ok || d.error) throw new Error(d.error || `HTTP ${r.status}`);
      setJsonAnalysis(d);
    } catch (e: any) { setJsonError(e.message || 'Analysis failed'); }
    setJsonAnalyzing(false);
  };
  const jsonPickFile = async (f: File | null) => {
    jsonReset();
    if (!f) { setJsonFile(''); setJsonText(''); return; }
    const text = await f.text();
    setJsonFile(f.name); setJsonText(text);
    jsonAnalyze(text, f.name, jsonColony);
  };
  const jsonCommit = async () => {
    if (!jsonAnalysis || jsonCommitting) return;
    setJsonCommitting(true); setJsonError('');
    try {
      const r = await fetch('/api/admin.php?action=import_json_commit', {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
        body: JSON.stringify({ json: jsonText, filename: jsonFile, colony_id: jsonColony }),
      });
      const d = await r.json();
      if (!r.ok || d.error) throw new Error(d.error || `HTTP ${r.status}`);
      setJsonResult(d); setJsonAnalysis(null);
      if (jsonColony === getColonyId()) triggerSync();
    } catch (e: any) { setJsonError(e.message || 'Import failed'); }
    setJsonCommitting(false);
  };

  // Read-only DB browser + SQL console. Available to all admins (enforced server-side too).
  const canSql = localStorage.getItem('ww_role') === 'admin';
  const PAGE = 10000;
  const qId = (name: string) => '`' + String(name).replace(/`/g, '``') + '`';   // backtick-quote an identifier

  // Low-level: run one read-only statement, return the result JSON or throw.
  const execSql = async (sql: string) => {
    const r = await fetch('/api/admin.php?action=sql', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
      body: JSON.stringify({ sql }),
    });
    const d = await r.json();
    if (d.error) throw new Error(d.error);
    return d;
  };

  // --- Schema tree state ---
  const [tables, setTables] = useState<string[] | null>(null);
  const [expandedCols, setExpandedCols] = useState<Record<string, any[]>>({});   // table -> columns (SHOW COLUMNS rows)
  const [selTable, setSelTable] = useState<string | null>(null);
  const [tableData, setTableData] = useState<any>(null);
  const [tableCount, setTableCount] = useState<number>(0);
  const [page, setPage] = useState(0);
  const [sortCol, setSortCol] = useState<string | null>(null);
  const [sortDir, setSortDir] = useState<'ASC' | 'DESC'>('ASC');
  const [browseErr, setBrowseErr] = useState('');
  const [browseLoading, setBrowseLoading] = useState(false);

  const loadTables = async () => {
    setBrowseErr('');
    try {
      const d = await execSql("SELECT table_name AS t FROM information_schema.tables WHERE table_schema = DATABASE() ORDER BY table_name");
      setTables(d.rows.map((r: any) => r.t ?? r.TABLE_NAME ?? Object.values(r)[0]));
    } catch (e: any) { setBrowseErr(e.message); setTables([]); }
  };

  const toggleCols = async (table: string) => {
    if (expandedCols[table]) { const { [table]: _, ...rest } = expandedCols; setExpandedCols(rest); return; }
    try {
      const d = await execSql(`SHOW COLUMNS FROM ${qId(table)}`);
      setExpandedCols(prev => ({ ...prev, [table]: d.rows }));
    } catch (e: any) { setBrowseErr(e.message); }
  };

  // Fetch one page. Sorting/paging re-query server-side (ORDER BY is applied to the
  // whole table, not just the loaded page). recount only on a fresh table selection.
  const loadPage = async (table: string, toPage: number, sCol: string | null, sDir: 'ASC' | 'DESC', recount: boolean) => {
    setSelTable(table); setPage(toPage); setSortCol(sCol); setSortDir(sDir);
    setBrowseErr(''); setBrowseLoading(true);
    try {
      if (recount) {
        const c = await execSql(`SELECT COUNT(*) AS n FROM ${qId(table)}`);
        setTableCount(Number(c.rows[0]?.n ?? 0));
      }
      const orderBy = sCol ? ` ORDER BY ${qId(sCol)} ${sDir}` : '';
      const d = await execSql(`SELECT * FROM ${qId(table)}${orderBy} LIMIT ${PAGE} OFFSET ${toPage * PAGE}`);
      setTableData(d);
    } catch (e: any) { setBrowseErr(e.message); setTableData(null); }
    setBrowseLoading(false);
  };

  const openTable = (table: string) => loadPage(table, 0, null, 'ASC', true);        // fresh selection: reset sort
  const gotoPage = (p: number) => { if (selTable) loadPage(selTable, p, sortCol, sortDir, false); };
  const sortBy = (col: string) => {
    if (!selTable) return;
    const dir: 'ASC' | 'DESC' = sortCol === col && sortDir === 'ASC' ? 'DESC' : 'ASC';
    loadPage(selTable, 0, col, dir, false);                                          // re-sort from page 0
  };

  useEffect(() => { if (canSql && tables === null) loadTables(); }, [canSql]);

  // --- Free-form SQL console state ---
  const [sqlText, setSqlText] = useState('');
  const [sqlResult, setSqlResult] = useState<any>(null);
  const [sqlError, setSqlError] = useState('');
  const [sqlRunning, setSqlRunning] = useState(false);

  const runSql = async () => {
    if (!sqlText.trim() || sqlRunning) return;
    setSqlRunning(true); setSqlError('');
    try { setSqlResult(await execSql(sqlText)); }
    catch (e: any) { setSqlError(e.message); setSqlResult(null); }
    setSqlRunning(false);
  };

  const copyCsv = (res: any) => {
    const esc = (v: any) => v === null || v === undefined ? ''
      : /[",\n]/.test(String(v)) ? '"' + String(v).replace(/"/g, '""') + '"' : String(v);
    const lines = [res.columns.join(','), ...res.rows.map((row: any) => res.columns.map((c: string) => esc(row[c])).join(','))];
    navigator.clipboard.writeText(lines.join('\n'));
  };

  // Shared read-only results grid. Pass `sort` (browser only) to make headers clickable —
  // sorting re-queries the whole table server-side rather than sorting the current page.
  const resultGrid = (res: any, sort?: { col: string | null; dir: 'ASC' | 'DESC'; onSort: (c: string) => void }) => (
    <div style={{ overflow: 'auto', maxHeight: 460, border: '1px solid #ddd' }}>
      <table style={{ fontSize: 12, fontFamily: 'monospace', borderCollapse: 'collapse' }}>
        <thead><tr>{res.columns.map((c: string) => (
          <th key={c} onClick={sort ? () => sort.onSort(c) : undefined}
            title={sort ? 'Sort by this column' : undefined}
            style={{ position: 'sticky', top: 0, background: '#f5f5f5', padding: '4px 8px', textAlign: 'left', borderBottom: '1px solid #ccc', whiteSpace: 'nowrap', cursor: sort ? 'pointer' : 'default', userSelect: 'none' }}>
            {c}{sort && sort.col === c ? (sort.dir === 'ASC' ? ' ▲' : ' ▼') : ''}
          </th>
        ))}</tr></thead>
        <tbody>
          {res.rows.map((row: any, i: number) => (
            <tr key={i}>{res.columns.map((c: string) => (
              <td key={c} style={{ padding: '3px 8px', borderBottom: '1px solid #eee', whiteSpace: 'nowrap', maxWidth: 360, overflow: 'hidden', textOverflow: 'ellipsis' }} title={row[c] === null ? 'NULL' : String(row[c])}>
                {row[c] === null ? <span className="muted">NULL</span> : String(row[c])}
              </td>
            ))}</tr>
          ))}
        </tbody>
      </table>
    </div>
  );

  const loadRecentChanges = async () => {
    setChangesLoading(true);
    const r = await fetch(`/api/admin.php?action=recent_changes&days=${changesDays}`, { headers: { 'Authorization': `Bearer ${token}` } });
    setRecentChanges(await r.json());
    setChangesLoading(false);
  };
  // Auto-load, and re-fetch whenever the local DB syncs (a sync means the server data — and so
  // the audit log — has changed) or the window length changes. Only while the table is on
  // screen, though: it lives on the io tab, and re-querying up to 30 days of audit log on every
  // poll-detected change from another tab bought nothing. Coming back to the tab after a sync
  // catches up, because the version it last loaded at no longer matches.
  const changesKey = `${dbVersion}|${changesDays}`;
  const changesLoadedAt = useRef('');
  useEffect(() => {
    if (adminTab !== 'io' || changesLoadedAt.current === changesKey) return;
    changesLoadedAt.current = changesKey;
    loadRecentChanges();
  }, [adminTab, changesKey]);

  const previewDate = async (date: string) => {
    setDatePreview({ loading: true, date });
    const r = await fetch(`/api/admin.php?action=preview_date&date=${date}`, { headers: { 'Authorization': `Bearer ${token}` } });
    const d = await r.json();
    if (d.error) { setDatePreview(null); alert(d.error); return; }
    setDatePreview(d);
  };

  // Each of these belongs to one tab, so each waits for that tab to be built rather than firing
  // three requests the moment the panel opens. In practice they still land early — the tabs
  // build during the first idle slice — but a tab you never open costs nothing.
  const usersBuilt = built('users'), ioBuilt = built('io'), systemBuilt = built('system');
  useEffect(() => {
    if (!usersBuilt) return;
    fetch('/api/admin.php?action=users', { headers: { 'Authorization': `Bearer ${token}` } })
      .then(r => r.json()).then(d => { setUsers(Array.isArray(d) ? d : []); setLoading(false); })
      .catch(() => setLoading(false));
  }, [token, usersBuilt]);
  useEffect(() => {
    if (!ioBuilt) return;
    fetch('/api/admin.php?action=colonies', { headers: { 'Authorization': `Bearer ${token}` } })
      .then(r => r.json()).then(d => setColonies(Array.isArray(d) ? d : [])).catch(() => {});
  }, [token, ioBuilt]);
  useEffect(() => {
    if (!systemBuilt) return;
    fetch(`/api/server_stats.php?_=${Date.now()}`, { headers: { 'Authorization': `Bearer ${token}` } })
      .then(r => r.json()).then(d => setServerDisk(d)).catch(() => {});
  }, [token, systemBuilt]);

  const [userErr, setUserErr] = useState('');
  // f_name is UNIQUE and email can collide, so a save can legitimately fail. Show the server's
  // reason and leave the row as the server has it, rather than optimistically claiming success.
  const updateUser = async (id: number, field: string, value: string) => {
    setUserErr('');
    try {
      const res = await fetch('/api/admin.php?action=update_user', {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
        body: JSON.stringify({ observer_id: id, [field]: value })
      });
      const d = await res.json().catch(() => ({}));
      if (!res.ok || d.error) { setUserErr(d.error || `Could not save ${field}`); return; }
      setUsers(users.map(u => u.observer_id === id ? { ...u, [field]: value } : u));
    } catch (e: any) {
      setUserErr(e.message || `Could not save ${field}`);
    }
  };

  const [colonies, setColonies] = useState<any[]>([]);
  // Soft delete. The server refuses while the account owns data and says what it owns.
  const [deletingUser, setDeletingUser] = useState<number | null>(null);
  const deleteUser = async (u: any) => {
    const name = [u.observer_name, u.surname].filter(Boolean).join(' ');
    if (!confirm(`Delete ${name}?\n\nThey are hidden from the user list and every people picker. Their name still shows on anything they previously recorded. Refused if they own any data.`)) return;
    setUserErr(''); setDeletingUser(u.observer_id);
    try {
      const res = await fetch('/api/admin.php?action=delete_user', {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
        body: JSON.stringify({ observer_id: u.observer_id })
      });
      const d = await res.json().catch(() => ({}));
      if (!res.ok || d.error) { setUserErr(d.error || 'Could not delete user'); return; }
      setUsers(users.filter(x => x.observer_id !== u.observer_id));
    } catch (e: any) { setUserErr(e.message || 'Could not delete user'); }
    finally { setDeletingUser(null); }
  };

  const emptyNewUser = { observer_name: '', surname: '', chip_acronym: '', falcon_id: '', email: '', role: 'viewer', password: '' };
  const [newUser, setNewUser] = useState(emptyNewUser);
  const [newUserColonies, setNewUserColonies] = useState<Record<string, string>>({}); // colony_id -> 'view' | 'edit'
  const [addUserErr, setAddUserErr] = useState('');
  const [addUserOk, setAddUserOk] = useState('');
  const [addingUser, setAddingUser] = useState(false);
  const createUser = async () => {
    setAddUserErr(''); setAddUserOk('');
    if (!newUser.observer_name.trim()) { setAddUserErr('A first name is required'); return; }
    if (newUser.password) { const p = passwordProblem(newUser.password, [newUser.observer_name, newUser.surname, newUser.email]); if (p) { setAddUserErr(p); return; } }
    setAddingUser(true);
    try {
      const r = await fetch('/api/admin.php?action=create_user', {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
        body: JSON.stringify({ observer_name: newUser.observer_name, surname: newUser.surname, chip_acronym: newUser.chip_acronym, falcon_id: newUser.falcon_id, email: newUser.email, role: newUser.role, password: newUser.password }),
      });
      const d = await r.json();
      if (!r.ok) throw new Error(d.error || `HTTP ${r.status}`);
      // Optionally grant access to one or more colonies (skipped for Admins — they see all).
      if (newUser.role !== 'admin') {
        const grants = Object.entries(newUserColonies).filter(([, role]) => role === 'view' || role === 'edit');
        await Promise.all(grants.map(([cid, role]) => fetch('/api/admin.php?action=save_colony_permission', {
          method: 'POST', headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
          body: JSON.stringify({ colony_id: Number(cid), observer_id: d.observer_id, role }),
        })));
      }
      setUsers([...users, d]);
      setNewUser(emptyNewUser); setNewUserColonies({});
      if (d.invited) setAddUserOk(d.email_sent ? `✓ Invite emailed to ${d.email}` : `Created, but the invite email failed — use "Email link" to retry`);
      // No email and no password: the row exists so work can be credited to them, but there is
      // no way in yet. Say so plainly rather than letting it look like a normal account.
      else if (d.no_login) setAddUserOk(`✓ Added ${[d.observer_name, d.surname].filter(Boolean).join(' ')} — no login yet (no email, no password). Use "Reset password" to give them one.`);
      else setAddUserOk(`✓ Added ${[d.observer_name, d.surname].filter(Boolean).join(' ')}`);
    } catch (e: any) { setAddUserErr(e.message || 'Failed to add user'); }
    setAddingUser(false);
  };

  // Email a set-password link (invite resend / forgot-password on the user's behalf)
  const [sendingResetFor, setSendingResetFor] = useState<number | null>(null);
  const [sendResetMsg, setSendResetMsg] = useState('');
  const sendResetEmail = async (u: any) => {
    setSendResetMsg('');
    setSendingResetFor(u.observer_id);
    try {
      const r = await fetch('/api/admin.php?action=send_reset', {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
        body: JSON.stringify({ observer_id: u.observer_id }),
      });
      const d = await r.json();
      if (!r.ok) throw new Error(d.error || `HTTP ${r.status}`);
      setSendResetMsg(`✓ Set-password link emailed to ${d.email} (${u.observer_name})`);
    } catch (e: any) { setSendResetMsg(`${u.observer_name}: ${e.message || 'failed to send email'}`); }
    setSendingResetFor(null);
  };

  // 12-char password, avoiding visually ambiguous chars (0/O/1/l/I) for easy dictation.
  const genPassword = (len = 12) => {
    const chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789';
    const a = new Uint32Array(len); crypto.getRandomValues(a);
    return Array.from(a, n => chars[n % chars.length]).join('');
  };
  const [resetFor, setResetFor] = useState<any | null>(null);
  const [resetPw, setResetPw] = useState('');
  const [resetMsg, setResetMsg] = useState('');
  const [resetting, setResetting] = useState(false);
  const resetPassword = async () => {
    if (!resetFor) return;
    setResetMsg('');
    { const p = passwordProblem(resetPw, resetFor ? [resetFor.observer_name, resetFor.surname, resetFor.email] : []); if (p) { setResetMsg(p); return; } }
    setResetting(true);
    try {
      const r = await fetch('/api/admin.php?action=reset_password', {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
        body: JSON.stringify({ observer_id: resetFor.observer_id, password: resetPw }),
      });
      const d = await r.json();
      if (!r.ok) throw new Error(d.error || `HTTP ${r.status}`);
      setResetMsg(`✓ Password for ${resetFor.observer_name} set to:  ${resetPw}`);
    } catch (e: any) { setResetMsg(e.message || 'Failed to reset password'); }
    setResetting(false);
  };



  return (
    <>
    <div className={`admin-panel${adminBird && adminBirdData?.penguin ? ' admin-page-docked' : ''}`}>
      <div className="admin-tabs">
        {(([['enter', 'Enter data'], ['io', 'Import & export'], ['validation', 'Data validation'], ['users', 'Users & colonies'], ['database', 'Database'], ['system', 'System'], ['mirror', 'Mirror']]) as [AdminTab, string][]).map(([id, label]) => (
          <button key={id} onClick={() => selectTab(id)} className={`admin-tab${adminTab === id ? ' active' : ''}`}>
            {label}{id === 'mirror' && mirrorAlert && <MirrorAlertBadge reason={mirrorAlert} />}
          </button>
        ))}
      </div>

      <div className="admin-section" style={{ display: adminTab === 'io' ? undefined : 'none' }}>
        <h3>Export</h3>
        <button className="action-btn" disabled={exporting} onClick={async () => {
          setExporting(true);
          try {
            const r = await fetch(`/api/admin.php?action=export_nestcheck_zip&token=${token}`);
            const blob = await r.blob();
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = `nestcheck-export-${new Date().toISOString().slice(0,10)}.zip`;
            a.click();
            URL.revokeObjectURL(url);
          } catch (e: any) { alert('Export failed: ' + e.message); }
          setExporting(false);
        }}>{exporting ? 'Exporting...' : 'Export all days as Nestcheck ZIP'}</button>
      </div>

      {adminTab === 'enter' && (
        <DataEntryPage token={token} allPenguins={allPenguins || []} onBack={() => onLeaveEntry?.()} fmColony={!!fmColony} />
      )}

      <div style={{ display: adminTab === 'io' ? undefined : 'none' }}>
        <NestcheckJsonImport token={token} colonyId={impColony} />
      </div>

      <div className="admin-section" style={{ display: adminTab === 'io' ? undefined : 'none' }}>
        <h3>Import monitor CSV</h3>
        <p className="muted" style={{ fontSize: 12, marginTop: 0 }}>
          Columns: <code>Date, Box, Adults, Eggs, Chicks, Bird-1…, No scan, Notes</code>. Dates must be year-first (<code>YYYY-MM-DD</code>); other formats are skipped.
          “Decom” in Adults imports as a DCM observation. Bird cells are chip numbers; unmatched chips are reported, not created.
          Problematic rows are flagged for review (e.g. <strong>Adults ≠ birds listed + No scan</strong>, unmatched chips) but still import once you accept.
          Only rows that can’t become an observation — unknown box, unreadable date/number, or an existing duplicate — are skipped.
          Rows import as observations attributed to you. Nothing is written until you confirm.
        </p>
        <div style={{ display: 'flex', gap: 12, alignItems: 'center', flexWrap: 'wrap', marginBottom: 8 }}>
          <label style={{ fontSize: 13 }}>Colony:{' '}
            <select value={impColony} onChange={e => { const cid = Number(e.target.value); setImpColony(cid); if (impCsv) impAnalyze(impCsv, impFile, cid); else impReset(); }}>
              {colonies.length === 0 && <option value={impColony}>Colony {impColony}</option>}
              {colonies.map((c: any) => (
                <option key={c.colony_id} value={c.colony_id}>{c.region_name} · {c.colony_name}</option>
              ))}
            </select>
          </label>
          <input type="file" accept=".csv,text/csv" onChange={e => impPickFile(e.target.files?.[0] ?? null)} />
          {impAnalyzing && <span className="muted" style={{ fontSize: 12 }}>Analyzing…</span>}
          {impFile && !impAnalyzing && <span className="muted" style={{ fontSize: 12 }}>{impFile}</span>}
        </div>

        {impError && <p style={{ color: '#c0392b', fontSize: 13, whiteSpace: 'pre-wrap' }}>{impError}</p>}

        {impResult && (
          <div style={{ border: '1px solid #b7e0b7', background: '#f2fbf2', borderRadius: 6, padding: 12, fontSize: 13 }}>
            <strong>✓ Imported into {impResult.colony_name}.</strong>{' '}
            {impResult.imported} observation(s), {impResult.scans} scan(s){impResult.biometrics ? `, ${impResult.biometrics} biometric(s)` : ''} written.
            {impResult.skipped_duplicates > 0 && <> {impResult.skipped_duplicates} duplicate row(s) skipped.</>}
            {impResult.imported_conflicts > 0 && <> <span style={{ color: '#d35400' }}>{impResult.imported_conflicts} conflicting row(s) imported as second observations.</span></>}
            {impResult.skipped_conflicts > 0 && <> {impResult.skipped_conflicts} conflicting row(s) skipped.</>}
            {impResult.skipped_errors > 0 && <> {impResult.skipped_errors} error row(s) skipped.</>}
            {impResult.unmatched_chips?.length > 0 && (
              <div style={{ marginTop: 6 }}>
                <span className="muted">Unmatched chips (no scan written): </span>
                {impResult.unmatched_chips.map((u: any) => `${u.chip}×${u.count}`).join(', ')}
              </div>
            )}
          </div>
        )}

        {impAnalysis && (() => {
          const t = impAnalysis.totals;
          const tiles: [string, any, string?][] = [
            ['Rows', t.rows], ['Will import', t.importable, '#1a7a1a'],
            ['Flagged', t.flagged, t.flagged ? '#8a6d3b' : undefined],
            ['Duplicates (skip)', t.duplicates, t.duplicates ? '#b8860b' : undefined],
            ['Conflicts', t.conflicts || 0, t.conflicts ? '#c0392b' : undefined],
            ['Errors (skip)', t.error_rows, t.error_rows ? '#c0392b' : undefined],
            ['Boxes', t.boxes], ['Not in sheet', t.boxes_missing, t.boxes_missing ? '#8a6d3b' : undefined], ['Decom→DCM', t.decom],
            ['Adults', t.adults], ['Eggs', t.eggs], ['Chicks', t.chicks], ['No-scan', t.no_scan],
            ['Biometrics', t.biometrics, t.biometrics ? '#1a7a1a' : undefined],
            ['No-scans created', t.noscan_confirm, t.noscan_confirm ? '#8a6d3b' : undefined],
            ['Scans matched', t.scans_matched, '#1a7a1a'],
            ['Chips unresolved', t.scans_unmatched, t.scans_unmatched ? '#c0392b' : undefined],
          ];
          const rows = impAnalysis.rows || [];
          const shown = impRowFilter === 'issues'
            ? rows.filter((r: any) => r.status !== 'ok' || r.warnings?.length)
            : rows;
          const conflictCount = t.conflicts || 0;
          const willImport = t.importable + (impConflicts ? conflictCount : 0);
          const canImport = willImport > 0 && !impCommitting;
          return (
            <div style={{ border: '1px solid #ddd', borderRadius: 6, padding: 12 }}>
              <div style={{ fontSize: 13, marginBottom: 8 }}>
                <strong>{impAnalysis.filename}</strong> → {impAnalysis.colony_name}
                {impAnalysis.date_min && <span className="muted"> · {impAnalysis.date_min}{impAnalysis.date_max !== impAnalysis.date_min ? ` – ${impAnalysis.date_max}` : ''}</span>}
              </div>

              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8, marginBottom: 10 }}>
                {tiles.map(([label, val, color]) => (
                  <div key={label} style={{ border: '1px solid #eee', borderRadius: 4, padding: '6px 10px', minWidth: 78 }}>
                    <div style={{ fontSize: 18, fontWeight: 600, color: color || '#222' }}>{val}</div>
                    <div className="muted" style={{ fontSize: 11 }}>{label}</div>
                  </div>
                ))}
              </div>

              {impAnalysis.file_flags?.length > 0 && impAnalysis.file_flags.map((f: string, i: number) => (
                <p key={i} style={{ color: '#8a6d3b', fontSize: 13, margin: '2px 0' }}>⚑ {f}</p>
              ))}

              {impAnalysis.coverage_missing?.length > 0 && (
                <details style={{ marginBottom: 8 }}>
                  <summary style={{ cursor: 'pointer', fontSize: 13 }}>{impAnalysis.coverage_missing.length} colony box(es) not in this sheet</summary>
                  <div style={{ fontSize: 12, marginTop: 6, maxHeight: 120, overflow: 'auto' }}>{impAnalysis.coverage_missing.join(', ')}</div>
                </details>
              )}

              {impAnalysis.unknown_boxes?.length > 0 && (
                <p style={{ color: '#c0392b', fontSize: 13 }}>
                  <strong>Unknown boxes</strong> (rows skipped — not locations in this colony): {impAnalysis.unknown_boxes.join(', ')}
                </p>
              )}

              {impAnalysis.unmatched_chips?.length > 0 && (
                <details style={{ marginBottom: 8 }} open>
                  <summary style={{ cursor: 'pointer', fontSize: 13 }}>
                    <strong>{impAnalysis.unmatched_chips.length} unmatched chip(s)</strong> — scans skipped, add these birds first if wanted
                  </summary>
                  <div style={{ fontSize: 12, fontFamily: 'monospace', marginTop: 6, maxHeight: 140, overflow: 'auto' }}>
                    {impAnalysis.unmatched_chips.map((u: any) => (
                      <div key={u.chip}>{u.chip} · ×{u.count} · box {u.boxes.join(', ')} · <span style={{ color: '#c0392b' }}>{u.reason}</span>{u.suggest ? <span style={{ color: '#1a7a1a' }}> → maybe #{displayPengNum(u.suggest)}</span> : ''}</div>
                    ))}
                  </div>
                </details>
              )}

              <div style={{ display: 'flex', gap: 8, alignItems: 'center', margin: '8px 0' }}>
                <span className="muted" style={{ fontSize: 12 }}>Show:</span>
                <button className="action-btn" style={{ opacity: impRowFilter === 'issues' ? 1 : 0.6 }} onClick={() => setImpRowFilter('issues')}>Issues only</button>
                <button className="action-btn" style={{ opacity: impRowFilter === 'all' ? 1 : 0.6 }} onClick={() => setImpRowFilter('all')}>All rows</button>
                <span className="muted" style={{ fontSize: 12 }}>{shown.length} shown</span>
              </div>

              <div style={{ overflow: 'auto', maxHeight: 380, border: '1px solid #eee' }}>
                <table style={{ fontSize: 12, borderCollapse: 'collapse', width: '100%' }}>
                  <thead><tr>{['Line', 'Box', 'Date', 'A', 'E', 'C', 'NS', 'Bio', 'Status', 'Scans', 'Notes'].map(h => (
                    <th key={h} style={{ position: 'sticky', top: 0, background: '#f5f5f5', padding: '3px 6px', textAlign: 'left', borderBottom: '1px solid #ccc', whiteSpace: 'nowrap' }}>{h}</th>
                  ))}</tr></thead>
                  <tbody>
                    {shown.map((r: any) => {
                      const bg = r.status === 'error' ? '#fdecea' : r.status === 'conflict' ? '#ffe9d6' : r.status === 'duplicate' ? '#fff6e0' : r.warnings?.length ? '#fffbe6' : 'transparent';
                      // Click a row (incl. errors) to investigate: the box anchored at this row's own
                      // date/observation, or — if the box is unknown — the whole day.
                      const invHref = r.location_id
                        ? `/?box=${encodeURIComponent(r.box)}${(r.obs_time || r.prev_obs) ? `&obs=${encodeURIComponent(r.obs_time || r.prev_obs)}` : ''}`
                        : (r.date ? `/?day=${encodeURIComponent(r.date)}` : null);
                      const openBox = invHref ? () => window.open(invHref, '_blank') : undefined;
                      return (
                        <tr key={r.line} style={{ background: bg, cursor: openBox ? 'pointer' : 'default' }}
                          onClick={openBox}
                          title={openBox ? (r.prev_obs ? `Open box ${r.box} — previous observation (${String(r.prev_obs).slice(0, 10)})` : `Open box ${r.box} (no earlier observation)`) : undefined}>
                          <td style={{ padding: '2px 6px' }}>{r.line}</td>
                          <td style={{ padding: '2px 6px' }}>{r.box}</td>
                          <td style={{ padding: '2px 6px', whiteSpace: 'nowrap' }}>{r.date || '—'}</td>
                          <td style={{ padding: '2px 6px' }}>{r.is_decom ? 'Decom' : r.adults}</td>
                          <td style={{ padding: '2px 6px' }}>{r.eggs}</td>
                          <td style={{ padding: '2px 6px' }}>{r.chicks}</td>
                          <td style={{ padding: '2px 6px' }}>{r.no_scan}{r.confirm_no_scan ? <span style={{ color: '#8a6d3b' }}> ?</span> : ''}</td>
                          <td style={{ padding: '2px 6px', color: '#1a7a1a' }}>{r.bios?.length ? r.bios.map((b: any) => b.observed_sex).join(',') : ''}</td>
                          <td style={{ padding: '2px 6px', whiteSpace: 'nowrap' }}>
                            {r.status === 'error' ? <span style={{ color: '#c0392b' }}>error</span>
                              : r.status === 'conflict' ? <span style={{ color: '#d35400', fontWeight: 600 }}>conflict{impConflicts ? ' → import' : ''}</span>
                              : r.status === 'duplicate' ? <span style={{ color: '#b8860b' }}>duplicate</span>
                              : r.warnings?.length ? <span style={{ color: '#8a6d3b' }}>flag</span>
                              : r.breeding_status === 'DCM' ? <span style={{ color: '#8a6d3b' }}>DCM</span>
                              : <span style={{ color: '#1a7a1a' }}>ok</span>}
                          </td>
                          <td style={{ padding: '2px 6px' }}>
                            {r.scans?.length ? `${r.scans.length}✓` : ''}
                            {r.unmatched?.length ? <span style={{ color: '#c0392b' }}> {r.unmatched.length}✗</span> : ''}
                          </td>
                          <td style={{ padding: '2px 6px', color: '#c0392b' }}>
                            {(r.errors || []).join('; ')}
                            {r.conflict ? <span style={{ color: '#d35400' }}>{r.errors?.length ? ' · ' : ''}{r.conflict}</span> : ''}
                            {r.warnings?.length ? <span style={{ color: '#8a6d3b' }}>{(r.errors?.length || r.conflict) ? ' · ' : ''}{r.warnings.join('; ')}</span> : ''}
                            {r.notes ? <span className="muted" style={{ fontStyle: 'italic' }}>{(r.errors?.length || r.warnings?.length) ? ' · ' : ''}“{r.notes}”</span> : ''}
                            {r.mini_pengs?.length > 0 && (
                              <span onClick={e => e.stopPropagation()} style={{ display: 'inline-flex', gap: 6, flexWrap: 'wrap', marginLeft: 6, verticalAlign: 'middle' }}>
                                {r.mini_pengs.map((pn: string) => {
                                  const p = pengByNumMini.get(String(pn));
                                  if (!p) return <span key={pn} className="muted" style={{ fontSize: 11 }}>#{displayPengNum(pn)}</span>;
                                  return <PenguinMini key={pn}
                                    scan={{ peng_num: p.peng_num, pit_id: p.pit_id, sex: p.sex, chip_date: p.chip_date, chipped_as_adult: p.chipped_as_adult, chick_size_code: p.chick_size_code, hasReturned: p.hasReturned }}
                                    onClick={() => setAdminBird(String(pn))} observationDate={r.date} />;
                                })}
                              </span>
                            )}
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>

              {conflictCount > 0 && (
                <label style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 10, fontSize: 13, color: '#d35400' }}>
                  <input type="checkbox" checked={impConflicts} onChange={e => setImpConflicts(e.target.checked)} />
                  Import {conflictCount} conflicting row(s) anyway — each becomes a second observation for that box+day
                </label>
              )}
              <div style={{ display: 'flex', gap: 10, alignItems: 'center', marginTop: 12 }}>
                <button className="action-btn" disabled={!canImport}
                  style={{ background: canImport ? '#1a7a1a' : undefined, color: canImport ? '#fff' : undefined }}
                  onClick={impCommit}>
                  {impCommitting ? 'Importing…' : `Confirm import of ${willImport} observation(s)`}
                </button>
                <button className="action-btn" disabled={impCommitting} onClick={impReset}>Cancel</button>
                {t.error_rows > 0 && <span className="muted" style={{ fontSize: 12 }}>{t.error_rows} error row(s) will be skipped.</span>}
                {conflictCount > 0 && !impConflicts && <span className="muted" style={{ fontSize: 12 }}>{conflictCount} conflicting row(s) will be skipped.</span>}
              </div>
            </div>
          );
        })()}
      </div>

      <div className="admin-section" style={{ display: adminTab === 'io' ? undefined : 'none' }}>
        <h3>Import old NestCheck monitor JSON</h3>
        <p className="muted" style={{ fontSize: 12, marginTop: 0 }}>
          Accepts the legacy <code>penguin_monitors.json</code> export from NestCheck v37 (an array of monitor
          records, each a <code>BoxData</code> map of box → <code>Adults/Eggs/Chicks/BreedingChance/GateStatus/Notes/ScannedIds</code>).
          Each box becomes one observation; scans resolve by last-8 chip match within the chosen colony (box tags dropped,
          unmatched chips reported). Deleted monitors and boxes already having an observation that NZ day are skipped.
          Nothing is written until you confirm.
        </p>
        <div style={{ display: 'flex', gap: 12, alignItems: 'center', flexWrap: 'wrap', marginBottom: 8 }}>
          <label style={{ fontSize: 13 }}>Colony:{' '}
            <select value={jsonColony} onChange={e => { const cid = Number(e.target.value); setJsonColony(cid); if (jsonText) jsonAnalyze(jsonText, jsonFile, cid); else jsonReset(); }}>
              {colonies.length === 0 && <option value={jsonColony}>Colony {jsonColony}</option>}
              {colonies.map((c: any) => (
                <option key={c.colony_id} value={c.colony_id}>{c.region_name} · {c.colony_name}</option>
              ))}
            </select>
          </label>
          <input type="file" accept=".json,application/json" onChange={e => jsonPickFile(e.target.files?.[0] ?? null)} />
          {jsonAnalyzing && <span className="muted" style={{ fontSize: 12 }}>Analyzing…</span>}
          {jsonFile && !jsonAnalyzing && <span className="muted" style={{ fontSize: 12 }}>{jsonFile}</span>}
        </div>

        {jsonError && <p style={{ color: '#c0392b', fontSize: 13, whiteSpace: 'pre-wrap' }}>{jsonError}</p>}

        {jsonResult && (
          <div style={{ border: '1px solid #b7e0b7', background: '#f2fbf2', borderRadius: 6, padding: 12, fontSize: 13 }}>
            <strong>✓ Imported into {jsonResult.colony_name}.</strong>{' '}
            {jsonResult.imported} observation(s), {jsonResult.scans} scan(s) written.
            {jsonResult.skipped_duplicates > 0 && <> {jsonResult.skipped_duplicates} duplicate(s) skipped.</>}
            {jsonResult.skipped_errors > 0 && <> {jsonResult.skipped_errors} error row(s) skipped.</>}
            {jsonResult.unmatched_chips?.length > 0 && (
              <div style={{ marginTop: 6 }}>
                <span className="muted">Unmatched chips (no scan written): </span>
                {jsonResult.unmatched_chips.map((u: any) => `${u.chip}×${u.count}`).join(', ')}
              </div>
            )}
          </div>
        )}

        {jsonAnalysis && (() => {
          const t = jsonAnalysis.totals;
          const tiles: [string, any, string?][] = [
            ['Monitors', t.monitors], ['Observations', t.observations], ['Will import', t.importable, '#1a7a1a'],
            ['Duplicates (skip)', t.duplicates, t.duplicates ? '#b8860b' : undefined],
            ['Errors (skip)', t.error_rows, t.error_rows ? '#c0392b' : undefined],
            ['Adults', t.adults], ['Eggs', t.eggs], ['Chicks', t.chicks],
            ['Scans matched', t.scans_matched, '#1a7a1a'],
            ['Chips unresolved', t.scans_unmatched, t.scans_unmatched ? '#c0392b' : undefined],
            ['Box tags dropped', t.box_tags_skipped],
          ];
          const canImport = t.importable > 0 && !jsonCommitting;
          const problems = (jsonAnalysis.rows || []).filter((r: any) => r.status !== 'ok');
          return (
            <div style={{ border: '1px solid #ddd', borderRadius: 6, padding: 12 }}>
              <div style={{ fontSize: 13, marginBottom: 8 }}>
                <strong>{jsonAnalysis.filename}</strong> → {jsonAnalysis.colony_name}
                {jsonAnalysis.date_min && <span className="muted"> · {jsonAnalysis.date_min}{jsonAnalysis.date_max !== jsonAnalysis.date_min ? ` – ${jsonAnalysis.date_max}` : ''}</span>}
              </div>
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8, marginBottom: 10 }}>
                {tiles.map(([label, val, color]) => (
                  <div key={label} style={{ border: '1px solid #eee', borderRadius: 4, padding: '6px 10px', minWidth: 78 }}>
                    <div style={{ fontSize: 18, fontWeight: 600, color: color || '#222' }}>{val}</div>
                    <div className="muted" style={{ fontSize: 11 }}>{label}</div>
                  </div>
                ))}
              </div>
              {jsonAnalysis.unknown_boxes?.length > 0 && (
                <p style={{ color: '#c0392b', fontSize: 13 }}>
                  <strong>Unknown boxes</strong> (rows skipped — not locations in this colony): {jsonAnalysis.unknown_boxes.join(', ')}
                </p>
              )}
              {jsonAnalysis.unmatched_chips?.length > 0 && (
                <details style={{ marginBottom: 8 }}>
                  <summary style={{ cursor: 'pointer', fontSize: 13 }}>{jsonAnalysis.unmatched_chips.length} unresolved chip(s)</summary>
                  <div style={{ fontSize: 12, marginTop: 6, maxHeight: 120, overflow: 'auto' }}>
                    {jsonAnalysis.unmatched_chips.map((u: any) => `${u.chip}×${u.count}`).join(', ')}
                  </div>
                </details>
              )}
              {problems.length > 0 && (
                <details style={{ marginBottom: 8 }}>
                  <summary style={{ cursor: 'pointer', fontSize: 13 }}>{problems.length} skipped row(s)</summary>
                  <div style={{ fontSize: 12, marginTop: 6, maxHeight: 160, overflow: 'auto' }}>
                    {problems.map((r: any, i: number) => (
                      <div key={i}>Box {r.box} {r.date || '?'} — <span style={{ color: r.status === 'error' ? '#c0392b' : '#b8860b' }}>{r.error || r.status}</span></div>
                    ))}
                  </div>
                </details>
              )}
              {(() => {
                // Preview the actual observations that will be written, grouped by date —
                // same box + counts + status + bird-mini look as the colony day view.
                const ok = (jsonAnalysis.rows || []).filter((r: any) => r.status === 'ok');
                const byDate = new Map<string, any[]>();
                for (const r of ok) { const d = r.date || '?'; if (!byDate.has(d)) byDate.set(d, []); byDate.get(d)!.push(r); }
                const dates = Array.from(byDate.keys()).sort();
                if (!dates.length) return null;
                return (
                  <details style={{ marginTop: 8 }} open>
                    <summary style={{ cursor: 'pointer', fontSize: 13 }}>Preview {ok.length} observation(s) to import</summary>
                    <div style={{ marginTop: 6, maxHeight: 380, overflow: 'auto' }}>
                      {dates.map(d => (
                        <div key={d} style={{ marginBottom: 10 }}>
                          <div style={{ fontSize: 12, fontWeight: 600, color: '#1a5276', marginBottom: 4 }}>{formatDate(d)} · {byDate.get(d)!.length} box(es)</div>
                          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                            {byDate.get(d)!.map((r: any, i: number) => (
                              <div key={i} className="obs-card" style={{ minWidth: 150, flex: '0 0 auto', padding: '6px 8px' }}>
                                <div style={{ display: 'flex', gap: 6, alignItems: 'center', flexWrap: 'wrap', fontSize: 12 }}>
                                  <b>Box {r.box}</b>
                                  <span>{'🐧'.repeat(Math.min(r.adults, 6))}{'🥚'.repeat(Math.min(r.eggs, 6))}{'🐣'.repeat(Math.min(r.chicks, 6))}</span>
                                  {r.breeding_status && <span className="badge" style={{ background: STATUS_COLORS[r.breeding_status] || '#ccc', color: '#333' }}>{r.breeding_status}</span>}
                                  {r.gate_status && <span className="gate">{r.gate_status}</span>}
                                </div>
                                {r.scans?.length > 0 && (
                                  <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, marginTop: 4 }}>
                                    {[...r.scans].sort(scanSortMFC).map((s: any, j: number) => <PenguinMini key={j} scan={s} onClick={() => s.peng_num && setAdminBird(String(s.peng_num))} />)}
                                  </div>
                                )}
                                {r.notes && <div className="muted" style={{ fontSize: 11, fontStyle: 'italic', marginTop: 3 }}>"{r.notes}"</div>}
                                {r.unmatched?.length > 0 && <div style={{ fontSize: 11, color: '#c0392b', marginTop: 3 }}>{r.unmatched.length} unresolved: {r.unmatched.join(', ')}</div>}
                              </div>
                            ))}
                          </div>
                        </div>
                      ))}
                    </div>
                  </details>
                );
              })()}
              <div style={{ display: 'flex', gap: 10, alignItems: 'center', marginTop: 12 }}>
                <button className="action-btn" disabled={!canImport}
                  style={{ background: canImport ? '#1a7a1a' : undefined, color: canImport ? '#fff' : undefined }}
                  onClick={jsonCommit}>
                  {jsonCommitting ? 'Importing…' : `Confirm import of ${t.importable} observation(s)`}
                </button>
                <button className="action-btn" disabled={jsonCommitting} onClick={jsonReset}>Cancel</button>
              </div>
            </div>
          );
        })()}
      </div>

      <div className="admin-section" style={{ display: adminTab === 'io' ? undefined : 'none' }}>
        <h3>FM completeness{fmCompleteness.total > 0 ? `, ${fmCompleteness.total} missing` : ''}</h3>
        <p className="muted">Incomplete registered book FM days and how many box observations each still needs to be a complete full monitor</p>
        {fmCompleteness.total === 0 ? <p className="muted">All registered FM days are complete</p> : (<>
          {fmCompleteness.seasons.map(([season, rows]) => (
            <div key={season} style={{ marginBottom: 10 }}>
              <div className="season-title">{seasonRange(String(season))} · {rows.length} missing</div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 2, marginTop: 2 }}>
                {rows.map(r => (
                  <a key={r.day} href={`/day/${r.day}`} style={{ fontSize: 12, display: 'flex', gap: 8, alignItems: 'baseline', textDecoration: 'none', color: 'inherit' }}>
                    <span className="date-link" style={{ minWidth: 90 }}>{formatDate(r.day)}</span>
                    <span className="fm-tag" style={{ minWidth: 46 }}>(FM {r.number})</span>
                    <span style={{ color: '#E65100' }}>{r.missing} more needed{r.missing < 5
                      ? `, box${r.missing !== 1 ? 'es' : ''} ${r.boxes.join(', ')}` : ''}</span>
                  </a>
                ))}
              </div>
            </div>
          ))}
        </>)}
      </div>

      {canSql && (
      <div className="admin-section" style={{ display: adminTab === 'database' ? undefined : 'none', width: '100vw', position: 'relative', left: '50%', right: '50%', marginLeft: '-50vw', marginRight: '-50vw', padding: '0 24px', boxSizing: 'border-box' }}>
        {built('database') && <>
        <h3>Database <span className="muted" style={{ fontSize: 12, fontWeight: 'normal' }}>· read-only</span></h3>
        {browseErr && <p style={{ color: '#c0392b', fontFamily: 'monospace', fontSize: 12, whiteSpace: 'pre-wrap' }}>{browseErr}</p>}
        <div style={{ display: 'flex', gap: 12, alignItems: 'flex-start', flexWrap: 'wrap' }}>
          {/* Schema tree */}
          <div style={{ flex: '0 0 240px', border: '1px solid #ddd', borderRadius: 4, maxHeight: 520, overflow: 'auto', fontSize: 13 }}>
            <div style={{ padding: '6px 8px', fontWeight: 600, borderBottom: '1px solid #eee', background: '#fafafa' }}>
              wildwatch_nestcheck {tables && <span className="muted" style={{ fontWeight: 400 }}>· {tables.length}</span>}
            </div>
            {tables === null ? <div className="muted" style={{ padding: 8 }}>Loading…</div> : tables.map(t => (
              <div key={t}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 4, padding: '3px 6px', cursor: 'pointer', background: selTable === t ? '#e8f0fe' : undefined }}>
                  <span onClick={() => toggleCols(t)} style={{ width: 14, textAlign: 'center', color: '#888', userSelect: 'none' }}>{expandedCols[t] ? '▾' : '▸'}</span>
                  <span onClick={() => openTable(t)} style={{ flex: 1, fontFamily: 'monospace', fontWeight: selTable === t ? 600 : 400 }}>{t}</span>
                </div>
                {expandedCols[t] && (
                  <div style={{ paddingLeft: 24, paddingBottom: 4 }}>
                    {expandedCols[t].map((c: any) => (
                      <div key={c.Field} style={{ fontFamily: 'monospace', fontSize: 11, color: '#555', padding: '1px 0' }}>
                        {c.Key === 'PRI' ? '🔑 ' : ''}{c.Field} <span className="muted">{c.Type}</span>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            ))}
          </div>
          {/* Data grid for the selected table */}
          <div style={{ flex: '1 1 420px', minWidth: 0 }}>
            {!selTable ? <p className="muted">Select a table to view its rows.</p> : (
              <>
                <div style={{ display: 'flex', gap: 10, alignItems: 'center', marginBottom: 6, flexWrap: 'wrap' }}>
                  <strong style={{ fontFamily: 'monospace' }}>{selTable}</strong>
                  <span className="muted" style={{ fontSize: 12 }}>{tableCount.toLocaleString()} row{tableCount === 1 ? '' : 's'}</span>
                  {tableCount > PAGE && (
                    <span style={{ display: 'inline-flex', gap: 6, alignItems: 'center' }}>
                      <button className="action-btn" disabled={page === 0 || browseLoading} onClick={() => gotoPage(page - 1)}>‹ Prev</button>
                      <span className="muted" style={{ fontSize: 12 }}>
                        {(page * PAGE + 1).toLocaleString()}–{Math.min((page + 1) * PAGE, tableCount).toLocaleString()}
                      </span>
                      <button className="action-btn" disabled={(page + 1) * PAGE >= tableCount || browseLoading} onClick={() => gotoPage(page + 1)}>Next ›</button>
                    </span>
                  )}
                  {tableData && tableData.rowCount > 0 && <button className="action-btn" onClick={() => copyCsv(tableData)}>Copy CSV</button>}
                </div>
                {browseLoading ? <p className="muted">Loading…</p>
                  : tableData && tableData.columns.length > 0 ? resultGrid(tableData, { col: sortCol, dir: sortDir, onSort: sortBy })
                  : <p className="muted">Empty table.</p>}
              </>
            )}
          </div>
        </div>

        {/* Free-form SQL console */}
        <details style={{ marginTop: 16 }}>
          <summary style={{ cursor: 'pointer', fontWeight: 600 }}>SQL console</summary>
          <textarea
            value={sqlText}
            onChange={e => setSqlText(e.target.value)}
            onKeyDown={e => { if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') { e.preventDefault(); runSql(); } }}
            placeholder="SELECT * FROM penguins LIMIT 20"
            spellCheck={false}
            style={{ width: '100%', minHeight: 90, fontFamily: 'monospace', fontSize: 13, padding: 8, boxSizing: 'border-box', marginTop: 8 }}
          />
          <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginTop: 6, flexWrap: 'wrap' }}>
            <button className="action-btn" disabled={sqlRunning} onClick={runSql}>{sqlRunning ? 'Running…' : 'Run (⌘/Ctrl+Enter)'}</button>
            {sqlResult && !sqlError && (
              <>
                <span className="muted" style={{ fontSize: 12 }}>
                  {sqlResult.rowCount} row{sqlResult.rowCount === 1 ? '' : 's'}{sqlResult.truncated ? ' (capped at 1000)' : ''} · {sqlResult.ms} ms
                </span>
                {sqlResult.rowCount > 0 && <button className="action-btn" onClick={() => copyCsv(sqlResult)}>Copy CSV</button>}
              </>
            )}
          </div>
          {sqlError && <p style={{ color: '#c0392b', fontFamily: 'monospace', fontSize: 12, marginTop: 8, whiteSpace: 'pre-wrap' }}>{sqlError}</p>}
          {sqlResult && !sqlError && sqlResult.columns.length > 0 && <div style={{ marginTop: 8 }}>{resultGrid(sqlResult)}</div>}
          {sqlResult && !sqlError && sqlResult.columns.length === 0 && <p className="muted" style={{ marginTop: 8 }}>Query ran; no rows returned.</p>}
        </details>
        </>}
      </div>
      )}

      <div className="admin-section" style={{ display: adminTab === 'users' ? undefined : 'none' }}>
        <h3>Users</h3>
        {loading ? <p className="muted">Loading...</p> : (
          <table className="bird-table" style={{width:'100%'}}>
            <thead><tr><th>First name</th><th>Surname</th><th>Chip</th><th>Falcon ID</th><th>Email</th><th>Role</th><th>Active</th><th></th></tr></thead>
            <tbody>
              {users.map(u => (
                <tr key={u.observer_id}>
                  <td><EditableField value={u.observer_name} onSave={(v: any) => updateUser(u.observer_id, 'observer_name', v)} placeholder="-" canEdit={true} /></td>
                  <td><EditableField value={u.surname} onSave={(v: any) => updateUser(u.observer_id, 'surname', v)} placeholder="-" canEdit={true} /></td>
                  <td><EditableField value={u.chip_acronym} onSave={(v: any) => updateUser(u.observer_id, 'chip_acronym', v)} placeholder="-" canEdit={true} /></td>
                  <td><EditableField value={u.falcon_id} onSave={(v: any) => updateUser(u.observer_id, 'falcon_id', v)} placeholder="-" canEdit={true} /></td>
                  <td><EditableField value={u.email} onSave={(v: any) => updateUser(u.observer_id, 'email', v)} placeholder="-" canEdit={true} /></td>
                  <td>
                    {/* The service account's role is load-bearing — it is what keeps it out of the
                        people pickers — and the dropdown has no 'api' option, so it would render
                        as "Viewer" and silently downgrade on any change. Show it, don't offer it. */}
                    {u.role === 'api'
                      ? <span className="muted" title="Service account — authenticates by API key">API</span>
                      : <select value={u.role || 'viewer'} onChange={e => updateUser(u.observer_id, 'role', e.target.value)}>
                          <option value="viewer">Viewer</option>
                          <option value="editor">Editor</option>
                          <option value="admin">Admin</option>
                        </select>}
                  </td>
                  <td><input type="checkbox" checked={u.active == 1} onChange={e => updateUser(u.observer_id, 'active', e.target.checked ? '1' : '0')} title="Inactive accounts are kept for their history" /></td>
                  <td style={{ whiteSpace: 'nowrap' }}>
                    <button className="edit-btn" onClick={() => { setResetFor(u); setResetPw(genPassword()); setResetMsg(''); }}>Reset password</button>
                    {u.email && <button className="edit-btn" style={{ marginLeft: 6 }} disabled={sendingResetFor === u.observer_id}
                      title={`Email ${u.email} a link to set their own password`}
                      onClick={() => sendResetEmail(u)}>{sendingResetFor === u.observer_id ? 'Sending…' : 'Email link'}</button>}
                    <button className="edit-btn" style={{ marginLeft: 6, color: '#c0392b' }} disabled={deletingUser === u.observer_id}
                      title="Hide this user. Refused if they own any chips, observations or day records."
                      onClick={() => deleteUser(u)}>{deletingUser === u.observer_id ? 'Deleting…' : 'Delete'}</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        {userErr && <div style={{ marginTop: 8, fontSize: 13, color: '#c0392b' }}>{userErr}</div>}
        {sendResetMsg && <div style={{ marginTop: 8, fontSize: 13, color: sendResetMsg.startsWith('✓') ? '#2e7d32' : '#c0392b' }}>{sendResetMsg}</div>}
        {resetFor && (
          <div style={{ marginTop: 12, padding: 10, border: '1px solid #ddd', borderRadius: 6, background: '#fafafa' }}>
            <b>Reset password for {resetFor.observer_name}</b>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8, alignItems: 'center', marginTop: 8 }}>
              <input type="text" value={resetPw} onChange={e => setResetPw(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') resetPassword(); }} className={resetPw && resetFor && passwordProblem(resetPw, [resetFor.observer_name, resetFor.surname, resetFor.email]) ? 'pw-invalid' : ''} style={{ padding: '5px 8px', fontFamily: 'monospace', minWidth: 180 }} />
              <button className="edit-btn" type="button" onClick={() => setResetPw(genPassword())}>Generate</button>
              <button className="edit-btn" onClick={resetPassword} disabled={resetting}>{resetting ? 'Setting…' : 'Set password'}</button>
              <button className="edit-btn" onClick={() => { setResetFor(null); setResetPw(''); setResetMsg(''); }}>Close</button>
            </div>
            {resetMsg && <div style={{ marginTop: 6, fontSize: 13, fontFamily: resetMsg.startsWith('✓') ? 'monospace' : undefined, color: resetMsg.startsWith('✓') ? '#2e7d32' : '#c0392b' }}>{resetMsg}</div>}
          </div>
        )}
        <div style={{ marginTop: 12, display: 'flex', flexWrap: 'wrap', gap: 8, alignItems: 'center' }}>
          <input type="text" placeholder="First name" value={newUser.observer_name} onChange={e => setNewUser({ ...newUser, observer_name: e.target.value })} style={{ padding: '5px 8px' }} />
          <input type="text" placeholder="Surname (optional)" value={newUser.surname} onChange={e => setNewUser({ ...newUser, surname: e.target.value })} style={{ padding: '5px 8px' }} />
          <input type="text" placeholder="Chip acronym (e.g. BS)" value={newUser.chip_acronym} onChange={e => setNewUser({ ...newUser, chip_acronym: e.target.value })} style={{ padding: '5px 8px', width: 130 }} />
          <input type="text" placeholder="Falcon ID (optional)" value={newUser.falcon_id} onChange={e => setNewUser({ ...newUser, falcon_id: e.target.value })} style={{ padding: '5px 8px' }} />
          <input type="email" placeholder="Email (optional)" value={newUser.email} onChange={e => setNewUser({ ...newUser, email: e.target.value })} style={{ padding: '5px 8px' }} />
          <select value={newUser.role} onChange={e => setNewUser({ ...newUser, role: e.target.value })} style={{ padding: '5px 8px' }}>
            <option value="viewer">Viewer</option>
            <option value="editor">Editor</option>
            <option value="admin">Admin</option>
          </select>
          <input type="text" placeholder="Password (blank = invite, or no login)" value={newUser.password} onChange={e => setNewUser({ ...newUser, password: e.target.value })} onKeyDown={e => { if (e.key === 'Enter') createUser(); }} className={newUser.password && passwordProblem(newUser.password, [newUser.observer_name, newUser.surname, newUser.email]) ? 'pw-invalid' : ''} style={{ padding: '5px 8px', fontFamily: 'monospace', minWidth: 200 }} />
          <button className="edit-btn" type="button" onClick={() => setNewUser({ ...newUser, password: genPassword() })}>Generate</button>
          <button className="edit-btn" onClick={createUser} disabled={addingUser}>{addingUser ? 'Adding…' : (!newUser.password && newUser.email.trim()) ? 'Add & send invite' : (!newUser.password ? 'Add (no login)' : 'Add user')}</button>
          {addUserErr && <span style={{ color: '#c0392b', fontSize: 13 }}>{addUserErr}</span>}
          {addUserOk && <span style={{ color: addUserOk.startsWith('✓') ? '#2e7d32' : '#c0392b', fontSize: 13 }}>{addUserOk}</span>}
        </div>
        {newUser.role !== 'admin' && colonies.length > 0 && (
          <div style={{ marginTop: 8 }}>
            <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>Colony access — grant one or more:</div>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
              {colonies.map((c: any) => (
                <label key={c.colony_id} style={{ display: 'inline-flex', alignItems: 'center', gap: 6, border: '1px solid #ddd', borderRadius: 4, padding: '3px 8px', fontSize: 13 }}>
                  {c.colony_name}{c.region_name ? ` — ${c.region_name}` : ''}
                  <select value={newUserColonies[c.colony_id] || ''} onChange={e => setNewUserColonies({ ...newUserColonies, [c.colony_id]: e.target.value })} style={{ padding: '2px 4px' }}>
                    <option value="">No access</option>
                    <option value="view">View</option>
                    <option value="edit">Edit</option>
                  </select>
                </label>
              ))}
            </div>
          </div>
        )}
        <p className="muted" style={{ fontSize: 12, marginTop: 6 }}>With a password, new users can log in immediately. Leave the password blank (email required) to send an invite from no-reply@wildwatch.co.nz — the user sets their own password via a 7-day link. Non-Admins see nothing until granted colony access — set it per colony above, or manage it later under Colony access.</p>
      </div>

      <div className="admin-section" style={{ display: adminTab === 'io' ? undefined : 'none' }}>
        <h3>Last {changesDays} days DB changes</h3>
        <div style={{display:'flex', gap:8, alignItems:'center', flexWrap:'wrap'}}>
          <button className="edit-btn" onClick={loadRecentChanges} disabled={changesLoading}>
            {changesLoading ? 'Loading...' : recentChanges ? 'Refresh' : 'Load'}
          </button>
          <label style={{fontSize:13, color:'#555'}}>Period</label>
          <select value={changesDays} onChange={e => setChangesDays(Number(e.target.value))} style={{padding:'4px 8px'}}>
            <option value={1}>Last 24 hours</option>
            <option value={7}>Last 7 days</option>
            <option value={14}>Last 14 days</option>
            <option value={30}>Last 30 days</option>
          </select>
          {recentChanges && (() => {
            // Users come from the changes themselves — only people who actually changed
            // something in the window are worth offering as a filter.
            const counts = new Map<string, number>();
            for (const e of recentChanges) {
              const n = e.observer_name || 'unknown user';
              counts.set(n, (counts.get(n) || 0) + 1);
            }
            const names = Array.from(counts.keys()).sort((a, b) => a.localeCompare(b));
            return <>
              <label style={{fontSize:13, color:'#555'}}>User</label>
              <select value={changesUser} onChange={e => setChangesUser(e.target.value)} style={{padding:'4px 8px'}}>
                <option value="">All users ({recentChanges.length})</option>
                {names.map(n => <option key={n} value={n}>{n} ({counts.get(n)})</option>)}
              </select>
            </>;
          })()}
        </div>
        {recentChanges && (() => {
          const shown = changesUser ? recentChanges.filter(e => (e.observer_name || 'unknown user') === changesUser) : recentChanges;
          if (shown.length === 0) return <p className="muted" style={{marginTop:8}}>No changes by {changesUser} in this period.</p>;
          const byDate = new Map<string, any[]>();
          for (const e of shown) {
            const d = e.nz_date || 'Unknown';
            if (!byDate.has(d)) byDate.set(d, []);
            byDate.get(d)!.push(e);
          }
          return <div style={{marginTop:8}}>
            {Array.from(byDate.entries()).map(([date, entries]) => (
              <ChangeDateGroup key={date + changesUser} date={date} entries={entries} token={token} onReverted={loadRecentChanges} />
            ))}
          </div>;
        })()}
      </div>

      <div className="admin-section" style={{ display: adminTab === 'io' ? undefined : 'none' }}>
        <h3>Delete observations by date</h3>
        <p className="muted">Preview and delete all observations from a specific date, then re-sync from server</p>
        <DateSearch dates={observationDates || []} onDayClick={previewDate} />
        {datePreview?.loading && <p className="muted" style={{marginTop:8}}>Loading {formatDate(datePreview.date)}...</p>}
        {datePreview && !datePreview.loading && (
          <div className="obs-card" style={{marginTop:8}}>
            <div style={{fontWeight:600, marginBottom:6}}>
              {formatDate(datePreview.date)}: {datePreview.totals.boxes} observations
              {datePreview.day_note && <span className="muted" style={{fontWeight:400}}> · {datePreview.day_note}</span>}
            </div>
            <div className="muted" style={{marginBottom:6}}>
              {'🐧'.repeat(datePreview.totals.adults)} {'🥚'.repeat(datePreview.totals.eggs)} {'🐣'.repeat(datePreview.totals.chicks)}
              {datePreview.totals.without_breeding > 0 && <span style={{color:'#F44336'}}> · ⚠️ {datePreview.totals.without_breeding} missing breeding status</span>}
            </div>
            <div style={{maxHeight:200, overflowY:'auto', fontSize:12}}>
              {datePreview.observations.map((o: any) => (
                <div key={o.observation_id} style={{padding:'2px 0', borderBottom:'1px solid #f0f0f0'}}>
                  Box {o.box_name}: {'🐧'.repeat(o.adults)}{'🥚'.repeat(o.eggs)}{'🐣'.repeat(o.chicks)} {o.breeding_status || <span style={{color:'#F44336'}}>no status</span>}
                </div>
              ))}
            </div>
            <div style={{display:'flex', gap:6, marginTop:8}}>
              <button className="edit-btn" style={{background:'#F44336', color:'#fff'}} onClick={() => {
                const reason = prompt(`Delete all ${datePreview.totals.boxes} observations from ${formatDate(datePreview.date)}?\n\nReason (optional):`);
                if (reason === null) return;
                fetch('/api/admin.php?action=delete_date', {
                  method: 'POST', headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
                  body: JSON.stringify({ date: datePreview.date, _reason: reason })
                }).then(r => r.json()).then(d => {
                  if (d.success) { setDatePreview(null); alert(`Deleted ${d.deleted} observations. Run Sync to re-import.`); }
                  else alert(d.error || 'Failed');
                });
              }}>Delete {datePreview.totals.boxes} observations</button>
              <button className="edit-btn" onClick={() => setDatePreview(null)}>Cancel</button>
            </div>
          </div>
        )}
      </div>

      <div style={{ display: adminTab === 'users' ? undefined : 'none' }}>
        <RegionsAndColonies token={token} />
        <ColonyAccess token={token} />
      </div>

      <div className="admin-section" style={{ display: adminTab === 'io' ? undefined : 'none' }}>
        <RemovePenguin token={token} />
      </div>

      <div className="admin-section" style={{ display: adminTab === 'io' ? undefined : 'none' }}>
        <RenumberPenguin token={token} />
      </div>

      <div style={{ display: adminTab === 'validation' ? undefined : 'none' }}>
        {built('validation') && <DcmBoxesChart />}
      </div>

      <div className="admin-section" style={{ display: adminTab === 'validation' ? undefined : 'none' }}>
        {built('validation') && <>
        <h3>Data integrity</h3>
        <MissingNoScansReport token={token} hrefFor={(box, time) => `/?box=${encodeURIComponent(box)}&obs=${encodeURIComponent(time)}`} />
        <IntegrityCheck rows={iDupObs} errorType="duplicate_observations" title="Duplicate observations"
          desc="More than one observation for a box on the same day." empty="No duplicate observations"
          columns={[{ key: 'obs_date', label: 'Date', render: dayCell }, { key: 'box_name', label: 'Box', render: boxCell }, { key: 'cnt', label: 'Count' }, { key: 'monitors', label: 'Monitors' }]} />
        <IntegrityCheck rows={iDupScans} errorType="duplicate_scans" title="Duplicate scans"
          desc="The same bird scanned more than once in one observation — kept as evidence of a data-entry error." empty="No duplicate scans"
          columns={[{ key: 'obs_date', label: 'Date', render: dayCell }, { key: 'box_name', label: 'Box', render: boxCell }, { key: 'peng_num', label: 'Penguin', render: pengCell }, { key: 'cnt', label: 'Count' }, { key: 'dup_type', label: 'Type' }]} />
        <IntegrityCheck rows={iSameGender} errorType="same_gender_conflicts" title="Same-gender conflicts"
          desc="Two+ penguins of the same sex scanned at one box on one day — a sex-assignment error or a genuine multi-bird visit." empty="No same-gender conflicts"
          columns={[{ key: 'obs_date', label: 'Date', render: dayCell }, { key: 'box_name', label: 'Box', render: boxCell }, { key: 'sex', label: 'Sex', render: (v: string) => v === 'M' ? 'Male' : v === 'F' ? 'Female' : v }, { key: 'cnt', label: 'Count' }, { key: 'peng_nums', label: 'Penguins', render: (v: string) => (v || '').split(',').map((n: string, i: number) => <Fragment key={i}>{i > 0 ? ' ' : ''}{pengCell(n.trim())}</Fragment>) }]} />
        <IntegrityCheck rows={iBirdTwoBoxes} errorType="bird_two_boxes" title="Bird in two boxes same day"
          desc="A penguin scanned at two different boxes on one day — can't be two places at once." empty="No birds in two boxes"
          columns={[{ key: 'obs_date', label: 'Date', render: dayCell }, { key: 'peng_num', label: 'Penguin', render: pengCell }, { key: 'boxes', label: 'Boxes', render: boxesCell }, { key: 'box_count', label: '#' }]} />
        <IntegrityCheck rows={iScanBeforeChip} errorType="scan_before_chip" title="Scan before chip date"
          desc="A scan dated before the bird's chip was fitted — impossible." empty="No pre-chip scans"
          columns={[{ key: 'obs_date', label: 'Scan date', render: dayCell }, { key: 'chip_date', label: 'Chip date' }, { key: 'box_name', label: 'Box', render: boxCell }, { key: 'peng_num', label: 'Penguin', render: pengCell }]} />
        <IntegrityCheck title="Breeding verifications"
          views={[
            { label: 'Rejected', rows: iVerify.rejected, errorType: 'verify_rejected',
              desc: 'Clutches where a reviewer recorded that the detected pair or offspring is wrong, and the detection still stands on the box page — the windows a person has said not to trust. A rejection the algorithm has since come round to isn\u2019t listed: those agree.',
              empty: 'No standing rejections',
              columns: [{ key: 'obs_date', label: 'Window start', render: dayCell }, { key: 'box', label: 'Box', render: boxCell }, { key: 'season', label: 'Season' }, { key: 'what', label: 'Rejected' }, { key: 'by', label: 'Reviewed by' }, { key: 'note', label: 'Note' }] },
            { label: 'Accepted, since changed', rows: iVerify.drifted, errorType: 'verify_drift',
              desc: 'A reviewer accepted the detection and it has since changed \u2014 a different pair or offspring, or no window starting at that observation at all. The stored truth no longer describes what the algorithm produces.',
              empty: 'No drifted verifications',
              columns: [{ key: 'obs_date', label: 'Window start', render: dayCell }, { key: 'box', label: 'Box', render: boxCell }, { key: 'season', label: 'Season' }, { key: 'what', label: 'Changed' }, { key: 'why', label: 'What happened' }, { key: 'by', label: 'Accepted by' }] },
          ]} />
        <IntegrityCheck rows={iDeadScanned} errorType="dead_scanned" title="Dead birds still scanned"
          desc="Birds scanned after their recorded death date — the death date or the scan is wrong." empty="No dead birds scanned after death"
          columns={[{ key: 'death_date', label: 'Died' }, { key: 'last_scan', label: 'Last scan', render: dayCell }, { key: 'peng_num', label: 'Penguin', render: pengCell }, { key: 'scan_count', label: 'Scans' }]} />
        <IntegrityCheck rows={iImprobable} errorType="improbable_counts" title="Improbable counts"
          desc="Adults > 2, or eggs + chicks > 2 — unusual for a little-penguin box." empty="No improbable counts"
          columns={[{ key: 'obs_date', label: 'Date', render: dayCell }, { key: 'box_name', label: 'Box', render: boxCell },
            { key: 'adults', label: 'Adults', render: (v: any) => Number(v) > 2 ? redNum(v) : v },
            { key: 'eggs', label: 'Eggs', render: (v: any, r: any) => (Number(r.eggs) + Number(r.chicks) > 3 && Number(v) > 0) ? redNum(v) : v },
            { key: 'chicks', label: 'Chicks', render: (v: any, r: any) => (Number(r.eggs) + Number(r.chicks) > 3 && Number(v) > 0) ? redNum(v) : v }]} />
        <IntegrityCheck rows={iFuture} errorType="future_observations" title="Future-dated observations"
          desc="Observations dated after today (NZ) — almost always a typo." empty="No future-dated observations"
          columns={[{ key: 'obs_date', label: 'Date', render: dayCell }, { key: 'box_name', label: 'Box', render: boxCell }, { key: 'monitor', label: 'Monitor' }]} />
        <IntegrityCheck rows={iRetired} errorType="retired_tag_scans" title="Retired-tag scans"
          desc="Scanned via a previous chip after the bird was rechipped (by chip date)." empty="No retired-tag scans"
          columns={[{ key: 'obs_date', label: 'Date', render: dayCell }, { key: 'box_name', label: 'Box', render: boxCell }, { key: 'peng_num', label: 'Penguin', render: pengCell }, { key: 'pit_id', label: 'Tag', render: (v: string) => String(v || '').slice(-8) }, { key: 'rechip_date', label: 'Rechipped' }]} />
        <IntegrityCheck rows={iChicksNoScan} errorType="chicks_no_scan" title="Chicks present but not scanned"
          desc="Chicks chipped in a box, then chicks recorded there within a month but no scans on that visit — a likely missed scan." empty="No unscanned-chick visits"
          columns={[{ key: 'obs_date', label: 'Date', render: dayCell }, { key: 'box_name', label: 'Box', render: boxCell }, { key: 'chicks', label: 'Chicks' }, { key: 'chicks_chipped', label: 'Chipped ≤1mo before' }]} />
        <IntegrityCheck rows={chickSizeRows} title="Little chick larger than big chick"
          desc="Nests where the chick coded LC out-measures its BC nest-mate on chip-day weight and/or flipper length — the size codes may be swapped or mis-entered. Both measurements are compared; the LC value that exceeds its BC is shown in red. Each metric is only compared where both chicks carry it."
          empty="None — every little chick is smaller than its big chick on both weight and flipper"
          columns={[
            { key: 'box_name', label: 'Box', render: boxCell },
            { key: 'chip_date', label: 'Chipped', render: dayCell },
            { key: 'chipper', label: 'Chipper', render: (v: any) => v || '—' },
            { key: 'bc_peng', label: 'BC', render: pengCell },
            { key: 'lc_peng', label: 'LC', render: pengCell },
            { key: 'bc_w', label: 'BC wt (g)', render: (v: any) => v ?? '—' },
            { key: 'lc_w', label: 'LC wt (g)', render: (v: any, r: any) => v == null ? '—' : (r.wInv ? redNum(v) : v) },
            { key: 'bc_f', label: 'BC flip (mm)', render: (v: any) => v ?? '—' },
            { key: 'lc_f', label: 'LC flip (mm)', render: (v: any, r: any) => v == null ? '—' : (r.fInv ? redNum(v) : v) },
          ]} />

        <IntegrityCheck rows={iMissingChipMeasures} errorType="missing_chip_measures" title="Missing chip measurements"
          desc="Chipped birds with no weight and/or no flipper length on their chip date. Chipping is the one time every bird is in the hand, so a gap here is a measurement that can never be taken later. Live from the cache — a value entered on the bird clears its row."
          empty="Every chipped bird has both measurements"
          columns={[{ key: 'chip_date', label: 'Chip date' }, { key: 'peng_num', label: 'Penguin', render: pengCell }, { key: 'chip_box', label: 'Box', render: boxCell }, { key: 'chip_by', label: 'By' }, { key: 'missing', label: 'Missing' }, { key: 'chip_weight', label: 'Weight (g)', render: (v: any) => v ?? '—' }, { key: 'chip_flipper', label: 'Flipper (mm)', render: (v: any) => v ?? '—' }]} />
        </>}
      </div>

      <div style={{ display: adminTab === 'system' ? undefined : 'none' }}>
        {built('system') && <>
          <BackupsPanel token={token} />
          <Suspense fallback={<div className="admin-section"><p className="muted">Loading chart...</p></div>}>
            <DiskHistoryChart token={token} />
          </Suspense>
        </>}
      </div>

      {adminTab === 'mirror' && !isMirror && <RemoteMirrorCard />}

      <div style={{ display: adminTab === 'mirror' && isMirror ? undefined : 'none' }}>
        <h3>Backup mirror</h3>
        <p className="muted">This server is the offline, restore-tested copy of Wildwatch. It pulls production&rsquo;s backup and code automatically each night; the button runs that on demand.</p>
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', margin: '8px 0' }}>
          <button className="edit-btn" disabled={mirrorRunning} onClick={triggerMirror}>
            {mirrorRunning ? 'Running…' : 'Back up & update mirror now'}
          </button>
        </div>
        {mirrorMsg && <p className="muted" style={{ color: '#1a6b8f' }}>{mirrorMsg}</p>}
        <iframe title="Backup status" srcDoc={mirrorHtml}
          style={{ width: '100%', height: '78vh', border: '1px solid #ddd', borderRadius: 8, marginTop: 8, background: '#fff' }} />
      </div>

      {adminTab === 'mirror' && <OffsiteTantrixlabCard token={token} />}

      <div className="admin-section" style={{ display: adminTab === 'system' ? undefined : 'none' }}>
        <h3>Disk write test</h3>
        {serverDisk && <p className="muted">Account: {serverDisk.files_mb} MB files + {serverDisk.db_mb} MB DB = {serverDisk.used_mb} MB / {serverDisk.quota_mb} MB ({serverDisk.pct}%) · {serverDisk.observations} observations · {serverDisk.penguins} penguins</p>}
        <div style={{display:'flex', gap:6, flexWrap:'wrap'}}>
          {[1, 10, 100, 1000, 5000].map(mb => (
            <button key={mb} className="edit-btn" disabled={diskTesting} onClick={() => {
              setDiskTesting(true);
              setDiskTest({ status: 'starting', target_mb: mb });
              let completed = false;
              const es = new EventSource(`/api/disk_check.php?mb=${mb}&token=${token}`);
              es.onmessage = (e) => {
                const d = JSON.parse(e.data);
                if (d.type === 'error') { completed = true; setDiskTest({ status: 'error', error: d.msg }); es.close(); setDiskTesting(false); }
                else if (d.type === 'start') { setDiskTest({ status: 'writing', target_mb: d.target_mb, server: d.server }); }
                else if (d.type === 'progress') { setDiskTest((prev: any) => ({ ...prev, status: 'writing', ...d })); }
                else if (d.type === 'done') { completed = true; setDiskTest({ status: d.status === 'OK' ? 'done' : 'failed', ...d }); es.close(); setDiskTesting(false); }
              };
              es.onerror = () => { if (!completed) { setDiskTest((prev: any) => ({ ...prev, status: 'error', error: 'Connection lost — test may still be running on server' })); } es.close(); setDiskTesting(false); };
            }}>{mb} MB</button>
          ))}
        </div>
        {diskTest && (
          <div className="obs-card" style={{marginTop:8}}>
            {diskTest.status === 'starting' && <div className="muted">Connecting...</div>}
            {diskTest.status === 'writing' && (
              <>
                <div style={{fontWeight:600}}>Writing {diskTest.target_mb} MB... {diskTest.pct || 0}%</div>
                <div style={{background:'#e8ecef', borderRadius:4, height:8, marginTop:4}}>
                  <div style={{background:'#2196F3', borderRadius:4, height:8, width:`${diskTest.pct || 0}%`, transition:'width 0.3s'}} />
                </div>
                <div className="muted" style={{marginTop:4}}>
                  {diskTest.written_mb || 0} MB written · {diskTest.speed_mbs || 0} MB/s · Free: {diskTest.disk_free_mb ?? '?'} MB
                </div>
              </>
            )}
            {diskTest.status === 'done' && (
              <>
                <div style={{color:'#4CAF50', fontWeight:600}}>OK — {diskTest.wrote_mb} MB in {diskTest.total_sec}s ({diskTest.speed_mbs} MB/s)</div>
                <div className="muted">Free before delete: {diskTest.disk_free_before_delete} MB · After: {diskTest.disk_free_after_delete} MB</div>
              </>
            )}
            {diskTest.status === 'failed' && (
              <div style={{color:'#F44336', fontWeight:600}}>FAILED: {diskTest.error}</div>
            )}
            {diskTest.status === 'error' && (
              <div style={{color:'#F44336', fontWeight:600}}>Error: {diskTest.error}</div>
            )}
            {diskTest.server && (
              <div className="muted" style={{marginTop:4, borderTop:'1px solid #e8ecef', paddingTop:4}}>
                Server: {diskTest.server.disk_free_mb} MB free · DB: {diskTest.server.db_mb} MB · {diskTest.server.observations} observations
              </div>
            )}
          </div>
        )}
      </div>
    </div>
    {adminBird && adminBirdData?.penguin && (
      <div className="day-bird-dock entry-bird-dock">
        <BirdPage data={adminBirdData} onBirdClick={(num: string) => setAdminBird(num)}
          onBoxClick={(box: string) => window.open(`/box/${box}`, '_blank')}
          onSightingClick={(box: string, date: string) => window.open(`/?box=${encodeURIComponent(box)}&obs=${encodeURIComponent(date)}`, '_blank')}
          onDayClick={(d: string) => window.open(`/?day=${encodeURIComponent(d)}`, '_blank')}
          onClose={() => setAdminBird(null)}
          token={token} canEdit={localStorage.getItem('ww_role') !== 'viewer'} />
      </div>
    )}
    </>
  );
}

/** Admin → System: backup inventory. Local = the dated dumps the nightly job stages on
 *  this server (kept 14 days) — listed live. Remote = devian, verified LIVE on every
 *  load via a restricted ssh listing; the status.json snapshot from the last run is
 *  only the fallback when the live check fails. */
function BackupsPanel({ token }: { token: string }) {
  const [data, setData] = useState<any | null>(null);
  const [err, setErr] = useState('');
  const load = () => {
    setErr('');
    fetch('/api/admin.php?action=backups', { headers: { Authorization: `Bearer ${token}` } })
      .then(r => r.json()).then(d => d.error ? setErr(d.error) : setData(d))
      .catch(e => setErr(String(e.message || e)));
  };
  useEffect(load, [token]);
  const fmtBytes = (b: number) => b >= 1048576 ? `${(b / 1048576).toFixed(1)} MB` : `${Math.round(b / 1024)} KB`;
  const s = data?.status;
  const remote = data?.remote; // live ssh listing of devian, run server-side on every load
  // Newest local dump per database (names are <db>_<YYYYMMDD>.sql.gz, list arrives newest-first)
  const newestLocal = new Map<string, any>();
  for (const f of (data?.local || [])) {
    const m = f.name.match(/^(.+)_(\d{8})\.sql\.gz$/);
    if (m && !newestLocal.has(m[1])) newestLocal.set(m[1], { ...f, day: `${m[2].slice(0,4)}-${m[2].slice(4,6)}-${m[2].slice(6)}` });
  }
  // Newest verified remote daily/monthly per database, from the live listing
  const remoteLatest = new Map<string, { daily?: any; monthly?: any }>();
  if (remote?.ok) for (const f of remote.files) {
    const m = f.name.match(/^(.+)_\d{6,8}\.sql\.gz$/);
    if (!m) continue;
    const e: any = remoteLatest.get(m[1]) || {};
    const k = f.kind as 'daily' | 'monthly';
    if (!e[k] || f.name > e[k].name) e[k] = f;
    remoteLatest.set(m[1], e);
  }
  const stateLabel = s?.state === 'success' ? <span style={{color:'#2e7d32', fontWeight:600}}>✓ OK</span>
    : s?.state === 'failed' ? <span style={{color:'#c0392b', fontWeight:600}}>✗ FAILED: {s.error}</span>
    : s?.state === 'running' ? <span style={{color:'#a15c00', fontWeight:600}}>⏳ running — {s.phase}</span>
    : <span className="muted">unknown</span>;
  const remoteCell = (live: any, snapshot: string | undefined) =>
    live ? <>{live.name} <span className="muted">({fmtBytes(live.bytes)})</span></>
    : snapshot ? <>{snapshot} <span style={{color:'#a15c00'}}>(unverified — from last run)</span></>
    : <span className="muted">—</span>;
  return (
    <div className="admin-section">
      <h3>Backups <button className="edit-btn" style={{marginLeft:8}} onClick={load}>Refresh</button></h3>
      {err && <p style={{color:'#c0392b'}}>{err}</p>}
      {!data && !err && <p className="muted">Loading...</p>}
      {data && (
        <>
          <p style={{marginBottom:4}}>Nightly offsite job: {stateLabel}
            {s?.last_success_at && <span className="muted"> · last success {formatDate(s.last_success_at)}</span>}
          </p>
          <p style={{marginBottom:8}}>Offsite (devian): {remote?.ok
            ? <span style={{color:'#2e7d32', fontWeight:600}}>✓ verified just now — {remote.files.length} file{remote.files.length !== 1 ? 's' : ''} present</span>
            : <span style={{color:'#c0392b', fontWeight:600}}>✗ live check failed ({remote?.error || 'no response'}) — showing last-run snapshot</span>}
          </p>
          <table className="bird-table" style={{marginBottom:6}}>
            <thead><tr><th>Database</th><th>Local (this server, 14 days)</th><th>Remote daily (devian)</th><th>Remote monthly (devian)</th></tr></thead>
            <tbody>
              {Array.from(new Set([...newestLocal.keys(), ...remoteLatest.keys(), ...Object.keys(s?.offsite_latest || {})])).sort().map(db => {
                const l = newestLocal.get(db);
                const r = remoteLatest.get(db);
                const snap = s?.offsite_latest?.[db];
                return (
                  <tr key={db}>
                    <td>{db}</td>
                    <td>{l ? <>{l.day} <span className="muted">({fmtBytes(l.bytes)})</span></> : <span className="muted">—</span>}</td>
                    <td>{remoteCell(r?.daily, snap?.daily)}</td>
                    <td>{remoteCell(r?.monthly, snap?.monthly)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          <p className="muted" style={{fontSize:12}}>
            {data.local.length} staged dump{data.local.length !== 1 ? 's' : ''} on this server
            {remote?.ok
              ? <> · devian holds {remote.files.filter((f: any) => f.kind === 'daily').length} dailies + {remote.files.filter((f: any) => f.kind === 'monthly').length} monthlies (verified live)</>
              : s?.offsite && <> · devian held {s.offsite.daily_count} dailies + {s.offsite.monthly_count} monthlies at last run</>}
            {s?.offsite?.media && <> · media mirror {s.offsite.media}</>}
          </p>
        </>
      )}
    </div>
  );
}

function ColonyAccess({ token }: { token: string }) {
  const [colonies, setColonies] = useState<any[]|null>(null);
  const [users, setUsers] = useState<any[]>([]);
  const [perms, setPerms] = useState<any[]>([]); // {colony_id, observer_id, role}
  const [colonyId, setColonyId] = useState<number|null>(null);
  const [loading, setLoading] = useState(false);
  const auth = { Authorization: `Bearer ${token}` };

  const load = async () => {
    setLoading(true);
    const [cr, ur, pr] = await Promise.all([
      fetch('/api/admin.php?action=colonies', { headers: auth }).then(r => r.json()),
      fetch('/api/admin.php?action=users', { headers: auth }).then(r => r.json()),
      fetch('/api/admin.php?action=colony_permissions', { headers: auth }).then(r => r.json()),
    ]);
    const cols = Array.isArray(cr) ? cr : [];
    setColonies(cols);
    setUsers(Array.isArray(ur) ? ur : []);
    setPerms(Array.isArray(pr) ? pr : []);
    if (cols.length && colonyId == null) setColonyId(Number(cols[0].colony_id));
    setLoading(false);
  };
  useEffect(() => { load(); }, []); // auto-load

  const roleFor = (observerId: number): string =>
    perms.find(p => Number(p.colony_id) === colonyId && Number(p.observer_id) === observerId)?.role || '';

  const setAccess = async (observerId: number, role: string) => {
    await fetch('/api/admin.php?action=save_colony_permission', {
      method: 'POST', headers: { ...auth, 'Content-Type': 'application/json' },
      body: JSON.stringify({ colony_id: colonyId, observer_id: observerId, role }),
    });
    setPerms(prev => {
      const rest = prev.filter(p => !(Number(p.colony_id) === colonyId && Number(p.observer_id) === observerId));
      return role ? [...rest, { colony_id: colonyId, observer_id: observerId, role }] : rest;
    });
  };

  return (
    <div className="admin-section">
      <h3>Colony access</h3>
      {!colonies && <button className="edit-btn" onClick={load} disabled={loading}>{loading ? 'Loading...' : 'Load'}</button>}
      {colonies && colonies.length === 0 && <p className="muted">No colonies.</p>}
      {colonies && colonies.length > 0 && (<>
        <p className="muted" style={{fontSize:12, marginBottom:8}}>Admins have full access to every colony automatically. Grant other users view/edit per colony here.</p>
        <label style={{fontSize:13}}>Colony:{' '}
          <select value={colonyId ?? ''} onChange={e => setColonyId(Number(e.target.value))}>
            {colonies.map((c:any) => <option key={c.colony_id} value={c.colony_id}>{c.colony_name}{c.region_name ? ` — ${c.region_name}` : ''}</option>)}
          </select>
        </label>
        <table className="bird-table" style={{width:'100%', marginTop:8}}>
          <thead><tr><th>User</th><th>Global role</th><th>Access to this colony</th></tr></thead>
          <tbody>
            {users.map((u:any) => (
              <tr key={u.observer_id}>
                <td>{u.observer_name}</td>
                <td className="muted">{u.role || 'viewer'}</td>
                <td>
                  {u.role === 'admin'
                    ? <span className="muted">all colonies (admin)</span>
                    : <select value={roleFor(Number(u.observer_id))} onChange={e => setAccess(Number(u.observer_id), e.target.value)}>
                        <option value="">No access</option>
                        <option value="view">View</option>
                        <option value="edit">Edit</option>
                      </select>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </>)}
    </div>
  );
}

// Base-26 helpers for alpha box ranges (AA, AB, AC...). A=1.
function alphaToNum(s: string): number { let n = 0; for (const ch of s.toUpperCase()) n = n * 26 + (ch.charCodeAt(0) - 64); return n; }
function numToAlpha(n: number, len: number): string { let s = ''; while (n > 0) { const r = (n - 1) % 26; s = String.fromCharCode(65 + r) + s; n = Math.floor((n - 1) / 26); } return s.padStart(len, 'A'); }

/** Expand a colony's box-sets string into box names. Handles {} wrappers, comma-separated
 *  tokens, and A-B ranges that are numeric (1-150), prefixed (N1-N6), or alpha (AA-AC). */
function expandLocationSets(str: string): string[] {
  const out: string[] = [];
  for (let token of (str || '').replace(/[{}]/g, ' ').split(',')) {
    token = token.trim();
    if (!token) continue;
    const dash = token.indexOf('-');
    if (dash > 0 && dash < token.length - 1) {
      const a = token.slice(0, dash).trim(), b = token.slice(dash + 1).trim();
      const ma = a.match(/^(.*?)(\d+)$/), mb = b.match(/^(.*?)(\d+)$/);
      if (ma && mb && ma[1] === mb[1]) { // prefixed/numeric: N1-N6, 1-150
        const start = parseInt(ma[2], 10), end = parseInt(mb[2], 10);
        if (start <= end && end - start < 1000) { for (let i = start; i <= end; i++) out.push(ma[1] + i); continue; }
      } else if (a.length === b.length && /^[A-Za-z]+$/.test(a) && /^[A-Za-z]+$/.test(b)) { // alpha: AA-AC
        const an = alphaToNum(a), bn = alphaToNum(b);
        if (an <= bn && bn - an < 1000) { for (let i = an; i <= bn; i++) out.push(numToAlpha(i, a.length)); continue; }
      }
    }
    out.push(token);
  }
  return [...new Set(out)];
}

function RegionsAndColonies({ token }: { token: string }) {
  const [regions, setRegions] = useState<any[]|null>(null);
  const [colonies, setColonies] = useState<any[]|null>(null);
  const [loading, setLoading] = useState(false);
  const [editRegion, setEditRegion] = useState<any|null>(null);
  const [editColony, setEditColony] = useState<any|null>(null);

  const load = async () => {
    setLoading(true);
    const [rr, cr] = await Promise.all([
      fetch('/api/admin.php?action=regions', { headers: { Authorization: `Bearer ${token}` } }).then(r => r.json()),
      fetch('/api/admin.php?action=colonies', { headers: { Authorization: `Bearer ${token}` } }).then(r => r.json()),
    ]);
    setRegions(Array.isArray(rr) ? rr : []);
    setColonies(Array.isArray(cr) ? cr : []);
    setLoading(false);
  };
  useEffect(() => { load(); }, []); // auto-load

  const saveRegion = async (data: any) => {
    await fetch('/api/admin.php?action=save_region', { method: 'POST', headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, body: JSON.stringify(data) });
    setEditRegion(null);
    load();
  };

  const saveColony = async (data: any) => {
    const auth = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
    const res = await fetch('/api/admin.php?action=save_colony', { method: 'POST', headers: auth, body: JSON.stringify(data) });
    const saved = await res.json().catch(() => ({}));
    const colonyId = data.colony_id || saved.colony_id;
    // Offer to materialise the box-sets string into boxes — but only the ones that don't exist yet.
    const boxes = expandLocationSets(data.location_sets_string || '');
    if (colonyId && boxes.length > 0) {
      const existing = await fetch(`/api/admin.php?action=colony_box_names&colony_id=${colonyId}`, { headers: auth }).then(r => r.json()).catch(() => []);
      const have = new Set((Array.isArray(existing) ? existing : []).map(String));
      const missing = boxes.filter(b => !have.has(String(b)));
      if (missing.length > 0) {
        const preview = missing.slice(0, 20).join(', ') + (missing.length > 20 ? ` … (+${missing.length - 20} more)` : '');
        if (confirm(`Create ${missing.length} new box${missing.length === 1 ? '' : 'es'} for these sets?\n\n${preview}`)) {
          const cr = await fetch('/api/admin.php?action=create_colony_boxes', { method: 'POST', headers: auth, body: JSON.stringify({ colony_id: colonyId, box_names: missing }) });
          const cd = await cr.json().catch(() => ({}));
          if (cd.success) alert(`Created ${cd.created} box${cd.created === 1 ? '' : 'es'}.`);
          else alert('Failed to create boxes: ' + (cd.error || 'unknown error'));
        }
      }
    }
    setEditColony(null);
    load();
  };

  return (
    <div className="admin-section">
      <h3>Regions & colonies</h3>
      {!regions && <button className="edit-btn" onClick={load} disabled={loading}>{loading ? 'Loading...' : 'Load'}</button>}

      {regions && (<>
        <h4 style={{color:'#1a5276', margin:'12px 0 6px'}}>Regions</h4>
        <table style={{fontSize:12, borderCollapse:'collapse', width:'100%', marginBottom:8}}>
          <thead><tr style={{borderBottom:'1px solid #ddd'}}><th style={{textAlign:'left'}}>Region</th><th>Colonies</th><th></th></tr></thead>
          <tbody>
            {regions.map((r: any) => (
              <tr key={r.region_id} style={{borderBottom:'1px solid #eee'}}>
                <td style={{padding:'4px 8px'}}>{r.region_name}</td>
                <td style={{padding:'4px 8px', textAlign:'center'}}>{r.colony_count}</td>
                <td><button className="edit-btn" onClick={() => setEditRegion({region_id: r.region_id, region_name: r.region_name})}>Edit</button></td>
              </tr>
            ))}
          </tbody>
        </table>
        <button className="edit-btn" onClick={() => setEditRegion({region_name: ''})}>+ Add region</button>

        {editRegion && (
          <div className="obs-card" style={{marginTop:8}}>
            <input type="text" defaultValue={editRegion.region_name} placeholder="Region name"
              style={{padding:'4px 8px', fontSize:13, border:'1px solid #ccc', borderRadius:4, width:'100%', marginBottom:6}}
              onChange={e => editRegion.region_name = e.target.value} />
            <div style={{display:'flex', gap:6}}>
              <button className="edit-btn" onClick={() => saveRegion(editRegion)}>Save</button>
              <button className="edit-btn" onClick={() => setEditRegion(null)}>Cancel</button>
            </div>
          </div>
        )}

        <h4 style={{color:'#1a5276', margin:'16px 0 6px'}}>Colonies</h4>
        <table style={{fontSize:12, borderCollapse:'collapse', width:'100%', marginBottom:8}}>
          <thead><tr style={{borderBottom:'1px solid #ddd'}}><th style={{textAlign:'left'}}>Colony</th><th style={{textAlign:'left'}}>Acronym</th><th style={{textAlign:'left'}}>Region</th><th style={{textAlign:'left'}}>Box sets</th><th style={{textAlign:'left'}}>FM-excluded</th><th></th></tr></thead>
          <tbody>
            {colonies!.map((c: any) => (
              <tr key={c.colony_id} style={{borderBottom:'1px solid #eee'}}>
                <td style={{padding:'4px 8px'}}>{c.colony_name}</td>
                <td style={{padding:'4px 8px', fontFamily:'monospace', fontSize:11}}>{c.colony_prefix || '—'}</td>
                <td style={{padding:'4px 8px'}} className="muted">{c.region_name}</td>
                <td style={{padding:'4px 8px', fontFamily:'monospace', fontSize:11}}>{c.location_sets_string}</td>
                <td style={{padding:'4px 8px', fontFamily:'monospace', fontSize:11}}>{c.fm_excluded_boxes}</td>
                <td><button className="edit-btn" onClick={() => setEditColony({colony_id: c.colony_id, colony_name: c.colony_name, region_id: c.region_id, location_sets_string: c.location_sets_string || '', fm_excluded_boxes: c.fm_excluded_boxes ?? '', colony_prefix: c.colony_prefix ?? ''})}>Edit</button></td>
              </tr>
            ))}
          </tbody>
        </table>
        <button className="edit-btn" onClick={() => setEditColony({colony_name: '', region_id: regions[0]?.region_id || 0, location_sets_string: '', fm_excluded_boxes: '0,AA,AB,AC', colony_prefix: ''})}>+ Add colony</button>

        {editColony && (
          <div className="obs-card" style={{marginTop:8}}>
            <input type="text" defaultValue={editColony.colony_name} placeholder="Colony name"
              style={{padding:'4px 8px', fontSize:13, border:'1px solid #ccc', borderRadius:4, width:'100%', marginBottom:6}}
              onChange={e => editColony.colony_name = e.target.value} />
            <label style={{fontSize:11, color:'#888', display:'block', marginBottom:2}}>Peng-number prefix (2–4 letters, e.g. RH → RH1). Can't be changed once the colony has birds.</label>
            <input type="text" defaultValue={editColony.colony_prefix} placeholder="e.g. RH" maxLength={4}
              style={{padding:'4px 8px', fontSize:13, border:'1px solid #ccc', borderRadius:4, width:'100%', marginBottom:6, fontFamily:'monospace'}}
              onChange={e => editColony.colony_prefix = e.target.value.toUpperCase().replace(/[^A-Z]/g, '')} />
            <select defaultValue={editColony.region_id} style={{padding:'4px 8px', fontSize:13, marginBottom:6, width:'100%'}}
              onChange={e => editColony.region_id = parseInt(e.target.value)}>
              {regions.map((r: any) => <option key={r.region_id} value={r.region_id}>{r.region_name}</option>)}
            </select>
            <input type="text" defaultValue={editColony.location_sets_string} placeholder="Box sets e.g. {1-150,AA-AC}"
              style={{padding:'4px 8px', fontSize:13, border:'1px solid #ccc', borderRadius:4, width:'100%', marginBottom:6, fontFamily:'monospace'}}
              onChange={e => editColony.location_sets_string = e.target.value} />
            <label style={{fontSize:11, color:'#888', display:'block', marginBottom:2}}>Excluded from Full Monitor (comma-separated)</label>
            <input type="text" defaultValue={editColony.fm_excluded_boxes} placeholder="e.g. 0,AA,AB,AC"
              style={{padding:'4px 8px', fontSize:13, border:'1px solid #ccc', borderRadius:4, width:'100%', marginBottom:6, fontFamily:'monospace'}}
              onChange={e => editColony.fm_excluded_boxes = e.target.value} />
            <div style={{display:'flex', gap:6}}>
              <button className="edit-btn" onClick={() => saveColony(editColony)}>Save</button>
              <button className="edit-btn" onClick={() => setEditColony(null)}>Cancel</button>
            </div>
          </div>
        )}
      </>)}
    </div>
  );
}

function RenumberPenguin({ token }: { token: string }) {
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<string|null>(null);

  const post = async (action: string, body: any, confirmMsg: string) => {
    if (!confirm(confirmMsg)) return;
    setBusy(true); setResult(null);
    try {
      const r = await fetch(`/api/admin.php?action=${action}`, {
        method: 'POST', headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({ ...body, colony_id: getColonyId() }),
      });
      const d = await r.json();
      if (d.success) {
        if (action === 'rename_penguin') {
          setResult(`Renamed ${displayPengNum(d.from)} → ${displayPengNum(d.to)} (${d.chips} chip${d.chips === 1 ? '' : 's'}, ${d.biometrics} biometric record${d.biometrics === 1 ? '' : 's'} carried; scans follow the chips).`);
        } else {
          setResult(`Swapped ${displayPengNum(d.a)} ↔ ${displayPengNum(d.b)}. Each bird keeps its chips, scans, biometrics and history under its new number.`);
        }
        setFrom(''); setTo('');
      } else {
        setResult(`Error: ${d.error}`);
      }
    } catch (e: any) {
      setResult(`Error: ${e?.message || e}`);
    }
    setBusy(false);
  };

  // Sent in full: a bare number is this colony's bird, and the server refuses a bare one on writes.
  const a = fullPengNum(from), b = fullPengNum(to);
  const da = displayPengNum(a), db = displayPengNum(b);
  const rename = () => post('rename_penguin', { from: a, to: b },
    `Rename penguin #${da} to #${db}?\n\nThe bird keeps its chips, scans, biometrics and audit history — only the number changes. #${db} must be vacant.`);
  const swap = () => post('swap_penguins', { a, b },
    `Swap the numbers of penguins #${da} and #${db}?\n\nEach bird keeps its own chips, scans, biometrics and audit history — the two numbers simply trade places.`);

  const box = { padding: '4px 8px', fontSize: 13, border: '1px solid #ccc', borderRadius: 4, width: 100 };
  return (
    <div>
      <h3>Renumber penguin</h3>
      <p className="muted" style={{fontSize:12, margin:'0 0 8px'}}>
        Rename gives the first penguin the second number (which must be free). Swap trades the two penguins' numbers.
        Chips, scans, biometrics and audit history follow each bird. A bare number means the colony you are viewing.
      </p>
      <div style={{display:'flex', gap:8, alignItems:'center', marginBottom:8}}>
        <input type="text" value={from} onChange={e => setFrom(e.target.value)} placeholder="Penguin #" style={box} />
        <span style={{color:'#999'}}>→</span>
        <input type="text" value={to} onChange={e => setTo(e.target.value)} placeholder="New / other #" style={box} />
        <button className="edit-btn" onClick={rename} disabled={busy || !a || !b}>{busy ? '...' : 'Rename'}</button>
        <button className="edit-btn" onClick={swap} disabled={busy || !a || !b}>{busy ? '...' : 'Swap'}</button>
      </div>
      {result && <p style={{fontSize:13, color: result.startsWith('Error') ? '#F44336' : '#2e7d32'}}>{result}</p>}
    </div>
  );
}

function RemovePenguin({ token }: { token: string }) {
  const [pengNum, setPengNum] = useState('');
  const [preview, setPreview] = useState<any>(null);
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<string|null>(null);
  const [compaction, setCompaction] = useState<any>(null);   // renumber plan offered after a delete
  const [compacting, setCompacting] = useState(false);

  const search = async () => {
    const num = fullPengNum(pengNum);
    if (!num) return;
    setLoading(true); setPreview(null); setResult(null);
    const r = await fetch(`/api/admin.php?action=preview_penguin_delete&peng_num=${encodeURIComponent(num)}&colony_id=${getColonyId()}`, { headers: { Authorization: `Bearer ${token}` } });
    const d = await r.json();
    if (d.error) { setResult(`Error: ${d.error}`); }
    else { setPreview(d); }
    setLoading(false);
  };

  const deletePenguin = async () => {
    if (!preview) return;
    const num = preview.penguin.peng_num;
    const scanTotal = preview.scan_count + (preview.scans_soft_deleted || 0);
    const bioTotal = preview.biometrics.length + (preview.bio_soft_deleted || 0);
    if (!confirm(`Permanently delete penguin #${displayPengNum(num)}?\n\nHard-deletes:\n- ${scanTotal} scan(s)\n- ${bioTotal} biometric record(s)\n- ${preview.chips.length} chip record(s)\n- the penguin itself\n\nEach deleted row is copied to the audit log first.`)) return;
    setLoading(true);
    const r = await fetch('/api/admin.php?action=delete_penguin', {
      method: 'POST', headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
      body: JSON.stringify({ peng_num: num }),
    });
    const d = await r.json();
    if (d.success) {
      setResult(`Penguin #${displayPengNum(num)} deleted. ${d.scans_deleted} scans removed, ${d.chips_deleted} chips removed.`);
      setPreview(null); setPengNum('');
      setCompaction(d.compaction || null);   // if a fillable gap was left, offer to close it
    } else {
      setResult(`Error: ${d.error}`);
    }
    setLoading(false);
  };

  const runCompaction = async () => {
    if (!compaction) return;
    setCompacting(true);
    const r = await fetch('/api/admin.php?action=compact_numbering', {
      method: 'POST', headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
      body: JSON.stringify({ colony_id: compaction.colony_id, gap: compaction.gap }),
    });
    const d = await r.json();
    if (d.success) {
      const moves = (d.applied || []).map((a: any) => `${displayPengNum(a.from)}→${displayPengNum(a.to)}`).join(', ');
      setResult(`Compacted: ${d.renumbered} penguin${d.renumbered === 1 ? '' : 's'} renumbered${moves ? ` (${moves})` : ''}.`);
      setCompaction(null);
    } else {
      setResult(`Compaction failed: ${d.error}`);
    }
    setCompacting(false);
  };

  return (
    <div>
      <h3>Remove penguin</h3>
      <div style={{display:'flex', gap:8, alignItems:'center', marginBottom:8}}>
        <input type="text" value={pengNum} onChange={e => setPengNum(e.target.value)} placeholder="Penguin #"
          onKeyDown={e => e.key === 'Enter' && search()}
          style={{padding:'4px 8px', fontSize:13, border:'1px solid #ccc', borderRadius:4, width:100}} />
        <button className="edit-btn" onClick={search} disabled={loading}>{loading ? '...' : 'Search'}</button>
      </div>

      {preview && (
        <div className="obs-card" style={{marginBottom:8}}>
          <table style={{fontSize:12, borderCollapse:'collapse', width:'100%'}}>
            <tbody>
              <tr><td style={{padding:'2px 8px', color:'#666'}}>Peng #</td><td style={{padding:'2px 8px', fontWeight:600}}>{displayPengNum(preview.penguin.peng_num)}</td></tr>
              <tr><td style={{padding:'2px 8px', color:'#666'}}>Sex</td><td style={{padding:'2px 8px'}}>{preview.penguin.sex || '—'}</td></tr>
              <tr><td style={{padding:'2px 8px', color:'#666'}}>Status</td><td style={{padding:'2px 8px'}}>{preview.penguin.death_date ? `Dead (${preview.penguin.death_date.slice(0, 10)})` : 'Alive'}</td></tr>
              <tr><td style={{padding:'2px 8px', color:'#666'}}>Chipped as</td><td style={{padding:'2px 8px'}}>{preview.penguin.chipped_as_adult ? 'Adult' : 'Chick'}</td></tr>
            </tbody>
          </table>

          <h4 style={{margin:'8px 0 4px', fontSize:13}}>Chips ({preview.chips.length})</h4>
          {preview.chips.map((c: any, i: number) => (
            <div key={i} style={{fontSize:12, padding:'2px 8px', fontFamily:'monospace'}}>{c.pit_id} {c.is_active ? '(active)' : '(inactive)'} — chipped {c.chip_date || '?'}</div>
          ))}

          <h4 style={{margin:'8px 0 4px', fontSize:13}}>Scans ({preview.scan_count})</h4>
          {preview.scans.length === 0 ? <p className="muted" style={{fontSize:12, margin:0}}>No scans</p> : (
            <table style={{fontSize:11, borderCollapse:'collapse', width:'100%'}}>
              <thead><tr style={{borderBottom:'1px solid #ddd'}}><th style={{textAlign:'left'}}>Date</th><th style={{textAlign:'left'}}>Box</th></tr></thead>
              <tbody>{preview.scans.slice(0, 20).map((s: any, i: number) => (
                <tr key={i} style={{borderBottom:'1px solid #eee'}}>
                  <td style={{padding:'2px 8px'}}>{s.observation_time_utc?.substring(0, 10)}</td>
                  <td style={{padding:'2px 8px'}}><a className="clickable" href={`/box/${s.box_name}`}>Box {s.box_name}</a></td>
                </tr>
              ))}</tbody>
            </table>
          )}
          {preview.scans.length > 20 && <p className="muted" style={{fontSize:11}}>...and {preview.scans.length - 20} more</p>}

          {preview.biometrics.length > 0 && (
            <h4 style={{margin:'8px 0 4px', fontSize:13}}>Biometrics ({preview.biometrics.length})</h4>
          )}

          <h4 style={{margin:'12px 0 4px', fontSize:13}}>What deleting will do</h4>
          <table style={{fontSize:12, borderCollapse:'collapse'}}>
            <tbody>
              {([
                ['penguin_scans', preview.scan_count + (preview.scans_soft_deleted || 0),
                  `hard-deleted (${preview.scan_count} live${preview.scans_soft_deleted ? `, ${preview.scans_soft_deleted} already soft-deleted` : ''}) — the scans disappear from their observations`],
                ['penguin_biometric_data', preview.biometrics.length + (preview.bio_soft_deleted || 0),
                  `hard-deleted (${preview.biometrics.length} live${preview.bio_soft_deleted ? `, ${preview.bio_soft_deleted} already soft-deleted` : ''})`],
                ['penguin_chips', preview.chips.length, 'hard-deleted'],
                ['penguins', 1, 'hard-deleted — the bird itself'],
              ] as [string, number, string][]).map(([table, n, what]) => (
                <tr key={table}>
                  <td style={{padding:'1px 8px', fontFamily:'monospace'}}>{table}</td>
                  <td style={{padding:'1px 8px', textAlign:'right'}}>{n} row{n === 1 ? '' : 's'}</td>
                  <td style={{padding:'1px 8px', color:'#666'}}>{what}</td>
                </tr>
              ))}
              <tr>
                <td style={{padding:'1px 8px', fontFamily:'monospace'}}>audit_log</td>
                <td style={{padding:'1px 8px', textAlign:'right'}}>
                  {preview.scan_count + (preview.scans_soft_deleted || 0) + preview.biometrics.length + (preview.bio_soft_deleted || 0) + preview.chips.length + 1} rows
                </td>
                <td style={{padding:'1px 8px', color:'#666'}}>added — one DELETE entry per row, carrying the full row, so the bird is reconstructable from the log</td>
              </tr>
            </tbody>
          </table>
          <p className="muted" style={{fontSize:11, margin:'4px 0 0'}}>
            Everything runs in one transaction — it all happens, or none of it does. There is no undo button; recovery means restoring from the audit log by hand.
          </p>

          <button onClick={deletePenguin} disabled={loading}
            style={{marginTop:12, background:'#F44336', color:'#fff', border:'none', padding:'8px 20px', borderRadius:4, cursor:'pointer', fontWeight:600}}>
            Delete penguin #{displayPengNum(preview.penguin.peng_num)}
          </button>
        </div>
      )}

      {compaction && (() => {
        const plan: any[] = compaction.plan || [];
        const newFree = plan.length ? plan[plan.length - 1].from : null;   // top of the run vacates
        return (
          <div className="obs-card" style={{marginBottom:8, border:'1px solid #ffb300', background:'#fff8e1'}}>
            <h4 style={{margin:'0 0 6px', fontSize:14}}>Numbering gap left by deleting {displayPengNum(compaction.gap_peng)}</h4>
            <p style={{fontSize:12, margin:'0 0 8px', color:'#5d4037'}}>
              Deleting {displayPengNum(compaction.gap_peng)} left a gap in this colony's numbering. The {plan.length} penguin{plan.length === 1 ? '' : 's'} below
              {' '}can each be shifted <b>down by one</b> to close it. Only penguins first chipped in the last 7 days are eligible, so
              {' '}the shift stops at the first established or unchipped bird. This renames each penguin's number — its chips, scans and
              {' '}biometrics move with it — and every change is recorded in the audit log. It cannot be undone from here.
            </p>
            <table style={{fontSize:12, borderCollapse:'collapse', width:'100%'}}>
              <thead><tr style={{borderBottom:'1px solid #e0c060', textAlign:'left'}}>
                <th style={{padding:'3px 8px'}}>Renumber</th><th style={{padding:'3px 8px'}}>Carries</th><th style={{padding:'3px 8px'}}>First chipped</th>
              </tr></thead>
              <tbody>
                {plan.map((s, i) => (
                  <tr key={i} style={{borderBottom:'1px solid #f0e0b0'}}>
                    <td style={{padding:'3px 8px', fontWeight:600}}>{displayPengNum(s.from)} <span style={{color:'#999'}}>→</span> {displayPengNum(s.to)}</td>
                    <td style={{padding:'3px 8px', color:'#666'}}>{s.chips} chip{s.chips === 1 ? '' : 's'}, {s.scans} scan{s.scans === 1 ? '' : 's'}, {s.biometrics} biometric{s.biometrics === 1 ? '' : 's'}</td>
                    <td style={{padding:'3px 8px', color:'#666'}}>{s.first_chip}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {newFree && <p style={{fontSize:12, margin:'8px 0 0', color:'#5d4037'}}>After compacting, <b>{displayPengNum(newFree)}</b> becomes the next free number.</p>}
            <div style={{display:'flex', gap:8, marginTop:12}}>
              <button onClick={runCompaction} disabled={compacting}
                style={{background:'#ff8f00', color:'#fff', border:'none', padding:'8px 20px', borderRadius:4, cursor:'pointer', fontWeight:600}}>
                {compacting ? 'Compacting…' : `Compact now (${plan.length} renumbered)`}
              </button>
              <button className="edit-btn" onClick={() => setCompaction(null)} disabled={compacting}>Leave gap</button>
            </div>
          </div>
        );
      })()}

      {result && <p style={{color: result.startsWith('Error') || result.startsWith('Compaction failed') ? '#F44336' : '#4CAF50', marginTop:8, fontSize:13}}>{result}</p>}
    </div>
  );
}

// Presentational integrity check: renders rows (computed locally) — 5 by default + "show all".
type CheckColumn = { key: string; label: string; render?: (v: any, row: any) => React.ReactNode };

/** Two findings that belong to one subject but need separate tables can pass `views` instead of
 *  rows/columns: the card then carries a toggle and shows one view at a time. */
type CheckView = { label: string; rows: any[]; desc?: string; empty?: string; errorType?: string; columns: CheckColumn[] };

function IntegrityCheck({ title, desc, rows, empty, columns, errorType, views }: {
  title: string; desc?: string; rows?: any[]; empty?: string;
  columns?: CheckColumn[];
  errorType?: string;   // when set, rows can be marked "valid" (reviewed & dismissed)
  views?: CheckView[];
}) {
  const [view, setView] = useState(0);
  const [showAll, setShowAll] = useState(false);
  const [showDismissed, setShowDismissed] = useState(false);
  const [busy, setBusy] = useState(false);
  const shownView: CheckView | undefined = views?.[Math.min(view, views.length - 1)];
  const vRows = shownView?.rows ?? rows ?? [];
  const vColumns = shownView?.columns ?? columns ?? [];
  const vEmpty = shownView?.empty ?? empty;
  const vDesc = shownView?.desc ?? desc;
  const vErrorType = shownView?.errorType ?? errorType;
  const { active, dismissed } = vErrorType ? splitDismissed(vErrorType, vRows) : { active: vRows, dismissed: [] as any[] };
  const liveCount = (v: CheckView) => (v.errorType ? splitDismissed(v.errorType, v.rows).active.length : v.rows.length);
  const shown = showAll ? active : active.slice(0, 5);

  const slug = checkSlug(title);

  const doDismiss = async (row: any) => {
    if (!vErrorType) return;
    const reason = window.prompt(`Mark this "${title}" item as reviewed & valid?\n\nOptional note (why it's fine):`, '');
    if (reason === null) return; // cancelled
    setBusy(true);
    try { await dismissError(vErrorType!, row, reason.trim()); }
    catch (e: any) { alert(e?.message || 'Could not dismiss'); }
    finally { setBusy(false); }
  };
  const doRestore = async (row: any) => {
    if (!vErrorType) return;
    setBusy(true);
    try { await undismissError(vErrorType, row); }
    catch (e: any) { alert(e?.message || 'Could not restore'); }
    finally { setBusy(false); }
  };
  // Cells are real anchors so middle-click / ctrl-click / "open in new tab" work. The <a>
  // fills the cell, so a click anywhere in the row still navigates as it did before.
  const navigate = useContext(CheckNavContext);
  const cell = (c: CheckColumn, row: any) => {
    const content = c.render ? c.render(row[c.key], row) : row[c.key];
    return row._href
      ? <a href={row._href} className="cell-link" title="Go to the observation"
          onClick={navigate ? e => navClick(e, () => navigate(row._href)) : undefined}>{content}</a>
      : content;
  };

  return (
    <div id={slug} className="report-card" style={{ scrollMarginTop: 70 }}>
      {/* The pinned badge counts every view, not just the one on screen — a card showing
          "Rejected" must still report a finding sitting behind the other tab. */}
      <PinnableTitle title={title} count={views ? views.reduce((n, v) => n + liveCount(v), 0) : active.length} />
      {views && views.length > 1 && (
        <div className="check-toggle">
          {views.map((v, i) => (
            <button key={v.label} className={v === shownView ? 'active' : ''}
              onClick={() => { setView(i); setShowAll(false); setShowDismissed(false); }}>
              {v.label} <span className="ct-count">{liveCount(v)}</span>
            </button>
          ))}
        </div>
      )}
      {vDesc && <p className="muted">{vDesc}</p>}
      {active.length === 0 ? <span style={{ color: '#4CAF50', fontSize: 13 }}>{vEmpty || 'None found'}</span> : (<>
        <p style={{ color: '#F44336', fontWeight: 600, fontSize: 13, margin: '4px 0' }}>{active.length} found{active.length > 5 && !showAll ? ' (showing 5)' : ''}</p>
        <div className="table-scroll">
        <table className="guess-rank-table zebra">
          <thead><tr>{vColumns.map(c => <th key={c.key}>{c.label}</th>)}{vErrorType && <th></th>}</tr></thead>
          <tbody>{shown.map((row: any, i: number) => (
            <tr key={i}>
              {vColumns.map(c => <td key={c.key}>{cell(c, row)}</td>)}
              {vErrorType && <td style={{ whiteSpace: 'nowrap', textAlign: 'right' }}>
                <button className="edit-btn" disabled={busy} onClick={() => doDismiss(row)} title="Reviewed — mark valid and hide from this list">✓ Valid</button>
              </td>}
            </tr>
          ))}</tbody>
        </table>
        </div>
        {active.length > 5 && <button className="edit-btn" style={{ marginTop: 6 }} onClick={() => setShowAll(s => !s)}>{showAll ? 'Show fewer' : `Show all (${active.length})`}</button>}
      </>)}
      {vErrorType && dismissed.length > 0 && (
        <div style={{ marginTop: 8 }}>
          <button className="edit-btn" onClick={() => setShowDismissed(s => !s)}>{showDismissed ? 'Hide' : 'Show'} {dismissed.length} dismissed</button>
          {showDismissed && (
            <div className="table-scroll" style={{ opacity: 0.65 }}>
            <table className="guess-rank-table zebra">
              <thead><tr>{vColumns.map(c => <th key={c.key}>{c.label}</th>)}<th>Reviewed by</th><th></th></tr></thead>
              <tbody>{dismissed.map((row: any, i: number) => (
                <tr key={i}>
                  {vColumns.map(c => <td key={c.key}>{cell(c, row)}</td>)}
                  <td style={{ fontSize: 11, color: '#666' }} title={row._dismissal?.dismissed_at || ''}>
                    {row._dismissal?.dismissed_by_name || '—'}{row._dismissal?.reason ? `: ${row._dismissal.reason}` : ''}
                  </td>
                  <td style={{ whiteSpace: 'nowrap', textAlign: 'right' }}>
                    <button className="edit-btn" disabled={busy} onClick={() => doRestore(row)} title="Move back to the error list">Restore</button>
                  </td>
                </tr>
              ))}</tbody>
            </table>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// Pinned integrity checks — shortcuts the user parks in the header, after the colony selector.
// Per-browser (localStorage), not per-account: these are a personal working set, and storing
// them server-side would mean a schema change for something that never needs to travel.
// Integrity-table cells are real anchors so they open in a new tab. A plain left-click should
// still navigate inside the SPA — a real navigation reboots the app and re-runs the colony
// load ("Loading colony data...") for data the page already has.
const CheckNavContext = createContext<((href: string) => void) | null>(null);

const PINNED_KEY = 'wildwatch.pinnedChecks';
// `count` is the check's remaining (undismissed) error count, cached from the last time the
// validation tab rendered — the header lives on pages where the checks aren't mounted, so it
// has nothing to recompute from. Absent until the check has been seen at least once.
type PinnedCheck = { slug: string; title: string; count?: number };
const checkSlug = (title: string) => 'check-' + title.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
const checkHref = (slug: string) => `/?admin&tab=validation#${slug}`;

const readPinned = (): PinnedCheck[] => {
  try { const v = JSON.parse(localStorage.getItem(PINNED_KEY) || '[]'); return Array.isArray(v) ? v : []; }
  catch { return []; }
};
let _pinned: PinnedCheck[] = readPinned();
const _pinnedSubs = new Set<() => void>();
const subscribePinned = (fn: () => void) => { _pinnedSubs.add(fn); return () => { _pinnedSubs.delete(fn); }; };
const getPinnedSnapshot = () => _pinned;
const writePinned = (next: PinnedCheck[]) => {
  _pinned = next;
  try { localStorage.setItem(PINNED_KEY, JSON.stringify(next)); } catch { /* quota/private mode — keep the in-memory pins */ }
  _pinnedSubs.forEach(fn => fn());
};
const togglePinned = (slug: string, title: string, count?: number) => {
  const has = _pinned.some(p => p.slug === slug);
  writePinned(has ? _pinned.filter(p => p.slug !== slug) : [..._pinned, { slug, title, count }]);
  return !has;
};
// Refresh a pinned check's cached count. No-op when the check isn't pinned, and when the
// count is unchanged — otherwise writePinned would notify on every render and loop.
const publishCount = (slug: string, count: number) => {
  const cur = _pinned.find(p => p.slug === slug);
  if (!cur || cur.count === count) return;
  writePinned(_pinned.map(p => p.slug === slug ? { ...p, count } : p));
};
function usePinnedChecks(): PinnedCheck[] {
  return React.useSyncExternalStore(subscribePinned, getPinnedSnapshot);
}

// An <h3> that pins/unpins its own check. Shared by IntegrityCheck and the standalone reports
// so every check title behaves the same way.
function PinnableTitle({ title, count }: { title: string; count: number }) {
  const slug = checkSlug(title);
  const pinned = usePinnedChecks().some(p => p.slug === slug);
  // Keep the header's cached count fresh while the check is on screen.
  useEffect(() => { publishCount(slug, count); }, [slug, count]);
  const onClick = () => togglePinned(slug, title, count);
  return (
    <h3 className="clickable check-title" style={{ margin: '0 0 4px' }} onClick={onClick}
      title={pinned ? 'Pinned to the header — click to remove' : 'Click to pin this check to the header'}>
      {title}{pinned && <span className="check-pinned" aria-label="pinned"> 📌</span>}
    </h3>
  );
}

// Cell renderers for integrity tables — styled plain text (not links), because IntegrityCheck
// wraps each cell in an anchor to the row's _href (the exact observation/date, highlighted)
// rather than a naked /day or /box.
const dayCell = (d: string) => d ? <span className="clickable">{d}</span> : '';
const boxCell = (b: string) => b ? <span className="clickable">Box {b}</span> : '';
// The mounted AdminPanel registers its bird-dock opener here so #peng cells in the integrity
// tables open the panel on the right instead of following the enclosing row anchor.
let _adminOpenBird: ((n: string) => void) | null = null;
const pengCell = (n: string) => n
  ? <span className="clickable" onClick={e => { if (_adminOpenBird && n) { e.preventDefault(); e.stopPropagation(); _adminOpenBird(String(n)); } }}>#{displayPengNum(n)}</span>
  : '';
const redNum = (v: any) => <span style={{ color: '#F44336', fontWeight: 600 }}>{v}</span>;
const boxesCell = (csv: string) => (csv || '').split(',').map((b: string, i: number) => (
  <Fragment key={i}>{i > 0 ? ', ' : ''}<span className="clickable">{b.trim()}</span></Fragment>
));


function AuthenticatedApp({ token, userName, userRole, onLogout }: { token: string; userName: string; userRole: string; onLogout: () => void }) {
  const [showChangePassword, setShowChangePassword] = useState(false);
  const [addPenguinBox, setAddPenguinBox] = useState<string | null>(null);
  const initial = parseUrl();
  // Both used to be fetched — dashboard.php?view=overview and boxtags.php — and both are
  // already in the snapshot the app syncs anyway. Reading them from the cache means the header
  // counts, the nest grid and the map paint with the data instead of a round trip behind it,
  // and the 30s change-poll no longer re-runs the heaviest query on the server.
  const boxTags = useBoxTags() as Record<string, BoxTag>;
  const stats = useOverview();
  const [selectedBox, setSelectedBox] = useState<string|null>(initial.box || null);
  // boxDetail from useBoxDetail hook
  const [showDeleted, setShowDeleted] = useState(false);
  const [deletedObs, setDeletedObs] = useState<any[]>([]);
  // false/false no longer needed — hooks return data synchronously
  const [loading, setLoading] = useState(true);
  const [selectedBird, setSelectedBird] = useState<string|null>(initial.bird || null);
  // A bird opened without a box (deep link ?bird=, search, admin) adopts its most-recently-
  // seen box so it renders in the box+bird split (panel docked on the right) instead of a
  // wide, centred, lone page. Cleared once the box is adopted; wide screens only.
  const [dockBirdToBox, setDockBirdToBox] = useState<boolean>(!!(initial.bird && !initial.box));
  // birdData from useBirdDetail hook
  const [highlightObs, setHighlightObs] = useState<string|null>(null);
  const [scrollToObs, setScrollToObs] = useState<string|null>(null);
  // Click-only deep-link anchor for a single observation. Unlike highlight/scroll (which
  // are hover-driven and transient) this persists into the URL as ?obs=, scoped to its box.
  const [obsAnchor, setObsAnchor] = useState<{box:string;time:string}|null>(initial.box && initial.obs ? { box: initial.box, time: initial.obs } : null);
  const [dayBox, setDayBox] = useState<string|null>(initial.day && initial.box ? initial.box : null); // box to centre+highlight in day view
  const allPenguins = useAllPenguins();
  const [penguinSearch, setPenguinSearch] = useState('');
  const [colonies, setColonies] = useState<any[]>([]);
  const [colonyId, setColonyIdState] = useState<number>(getColonyId());
  const [showEntry, setShowEntry] = useState(initial.enter || false);
  const [showAdmin, setShowAdmin] = useState(initial.admin || false);
  const [showReports, setShowReports] = useState(initial.reports || false);
  const [showDocs, setShowDocs] = useState(initial.docs || false);
  const [showAllBirds, setShowAllBirds] = useState(initial.birds || false);
  const [showSettings, setShowSettings] = useState(false);
  // Which searches currently hold the calendar open, by instance id. A plain boolean can't
  // work: several searches are mounted at once and each reports its own focus.
  const [dateFocusIds, setDateFocusIds] = useState<Set<string>>(new Set());
  const datePickerVisible = dateFocusIds.size > 0;
  const [datePickerCenter, setDatePickerCenter] = useState('');
  const onSearchFocus = useCallback((focused: boolean, center: string, id: string) => {
    setDateFocusIds(prev => {
      if (focused === prev.has(id)) return prev;   // unchanged — same Set, so no re-render
      const next = new Set(prev);
      if (focused) next.add(id); else next.delete(id);
      return next;
    });
    if (focused && center) setDatePickerCenter(center);
  }, []);
  const [selectedDay, setSelectedDay] = useState<string|null>(initial.day || null);
  const [scrollToBox, setScrollToBox] = useState<string|null>(null);
  const [previousBox, setPreviousBox] = useState<string|null>(null);
  // Data hooks — reactive, re-render automatically when localdb syncs
  const [loadProgress, setLoadProgress] = useState('');
  const [loadPct, setLoadPct] = useState<number|null>(null);
  // Admins only: the backup mirror going quiet is an admin's problem, and the endpoints are
  // admin-only anyway. Drives the red "1" on the Admin nav item and on the admin Mirror tab.
  const mirrorAlert = useMirrorAlert(userRole === 'admin');

  // Sync state to URL. Admin/reports/enter/day are standalone full-screen modes, so
  // they own the URL exclusively. box + bird compose — both are serialized together so
  // an open bird panel is preserved across box changes, refresh, and back/forward.
  useEffect(() => {
    let path = '/';
    if (showAdmin) { const t = new URLSearchParams(window.location.search).get('tab'); path = '/?admin=1' + (t ? `&tab=${t}` : ''); }
    else if (showReports) { const t = new URLSearchParams(window.location.search).get('tab'); path = '/?reports=1' + (t ? `&tab=${t}` : ''); }
    else if (showDocs) path = '/?docs=1';
    else if (showEntry) path = '/?enter=1';
    else if (showAllBirds) path = '/?birds=1';
    else if (selectedDay) path = `/?day=${encodeURIComponent(selectedDay)}${dayBox ? `&box=${encodeURIComponent(dayBox)}` : ''}`;
    else {
      const q = new URLSearchParams();
      if (selectedBox) q.set('box', selectedBox);
      if (selectedBird) q.set('bird', selectedBird);
      // Only carry the obs anchor while its own box is showing — never leak it onto another box.
      if (selectedBox && obsAnchor && obsAnchor.box === selectedBox) q.set('obs', obsAnchor.time);
      const s = q.toString();
      path = s ? `/?${s}` : '/';
    }
    if (window.location.pathname + window.location.search !== path) {
      window.history.pushState(null, '', path);
    }
  }, [selectedBox, selectedBird, showEntry, showAdmin, showReports, showDocs, showAllBirds, selectedDay, obsAnchor, dayBox]);

  // Handle browser back/forward
  useEffect(() => {
    const onPopState = () => {
      const { box, bird, obs, enter, admin: adm, reports, docs, birds, day } = parseUrl();
      // Back/forward lands on a fresh view — drop cross-view scroll targets. The obs anchor
      // is restored below; the box-load effect re-scrolls to it once the box data is ready.
      setHighlightObs(null); setScrollToObs(null); setDayBox(day && box ? box : null);
      setObsAnchor(box && obs ? { box, time: obs } : null);
      setSelectedBox(box || null);
      setSelectedBird(bird || null);
      setShowEntry(enter || false);
      setShowAdmin(adm || false);
      setShowReports(reports || false);
      setShowDocs(docs || false);
      setShowAllBirds(birds || false);
      setSelectedDay(day || null);
    };
    window.addEventListener('popstate', onPopState);
    return () => window.removeEventListener('popstate', onPopState);
  }, []);

  // An edit's own writer already triggers a sync, and the overview now rides the cache — so the
  // numbers refresh themselves when that sync lands. Kept as the name every editing surface
  // calls after a change, so there is still one place to hang extra work off.
  const refreshStats = useCallback(() => {}, []);

  // Date stats are precomputed in localdb on sync — just read the cache
  const dateStatsCache = useDateStats();
  // The day-view calendar reads its populated-day list from the LOCAL date-stats cache (same
  // source as the day data itself), so it paints as fast as the observations on refresh instead
  // of lagging behind the server overview fetch. Falls back to the server list only until the
  // local cache has primed (cold first load), so the calendar is never briefly empty.
  const dayDates = useMemo(
    () => (dateStatsCache.size ? [...dateStatsCache.keys()].sort() : (stats?.observation_dates || [])),
    [dateStatsCache, stats]
  );

  // FM dates registered in the enter-date workflow (all seasons), keyed by NZ date. Not in the
  // sync snapshot, so the local copy is painted first and the fetch revalidates behind it —
  // otherwise the tags land a round-trip after the dates they belong to.
  const [registeredFmDates, setRegisteredFmDates] = useState<Map<string, { season: number; number: number; partial: boolean }>>(new Map());
  useEffect(() => {
    if (!token) return;
    let live = true, revalidated = false;
    const asMap = (rows: any) => {
      const m = new Map<string, { season: number; number: number; partial: boolean }>();
      if (Array.isArray(rows)) for (const r of rows) if (r.actual_date) m.set(r.actual_date, { season: Number(r.season_year), number: Number(r.date_number), partial: !!Number(r.partial_monitor) });
      return m;
    };
    // `revalidated` guards the order, not the speed: a slow IndexedDB read must never land on
    // top of a server response that already arrived.
    getCachedFmDates().then(rows => { if (live && !revalidated && rows) setRegisteredFmDates(asMap(rows)); });
    fetch(`/api/crud.php?action=all_fm_dates&colony_id=${getColonyId()}`, { headers: { Authorization: `Bearer ${token}` } })
      .then(r => r.json())
      .then(rows => {
        if (!live) return;
        revalidated = true;
        setRegisteredFmDates(asMap(rows));
        if (Array.isArray(rows)) setCachedFmDates(rows);
      })
      .catch(() => {});
    return () => { live = false; };
  }, [token, colonyId]);

  const dateTip = useDateTooltip();
  const dateTipCtx = useMemo(() => ({ ...dateTip, statsCache: dateStatsCache, registeredFmDates }), [dateTip, dateStatsCache, registeredFmDates]);
  const [menuOpen, setMenuOpen] = useState(false);
  const [menuClosing, setMenuClosing] = useState(false);
  const [menuSide, setMenuSide] = useState<'left'|'right'>(() => (localStorage.getItem('ww_menu_side') as 'left'|'right') || 'right');
  // Must stay above the `if (loading)` early return — a hook below it renders on some passes
  // and not others (React error 310).
  const pinnedChecks = usePinnedChecks();
  /**
   * Legacy mode: the old Penguin/Box/Date toolbar, superseded by the unified search but kept
   * while people get used to it. On by default so nobody's app changes under them; the one
   * account trialling life without it starts off. Toggleable from the header, which is the
   * point — it's how you see what another user sees without logging in as them.
   *
   * A display choice, not a permission: everything the toolbar reaches is reachable from the
   * unified search either way.
   */
  const [legacyMode, setLegacyMode] = useState(() => {
    const saved = localStorage.getItem('ww_legacy_mode');
    if (saved !== null) return saved === '1';
    return (localStorage.getItem('ww_email') || '').toLowerCase() !== 'mark@wildwatch.co.nz';
  });
  const toggleLegacyMode = () => setLegacyMode(on => {
    localStorage.setItem('ww_legacy_mode', on ? '0' : '1');
    return !on;
  });

  // Nest-grid tiles straight from the cache — the same source the numbers come from, so their
  // colours and counts land together. This used to paint from the cache and then be replaced by
  // the overview fetch's box_info, with the cache's moulting flag laid back over the top because
  // the server query didn't know about it; now there is one answer and nothing to reconcile.
  const gridBoxInfo = useBoxInfo();
  // Header pin → admin/validation, in-app. AdminPanel reads its tab from the URL only at
  // mount, so an already-mounted panel needs this signal to switch tabs and scroll.
  const [checkTarget, setCheckTarget] = useState<{ slug: string; nonce: number } | null>(null);
  const openPinnedCheck = useCallback((slug: string) => {
    setSelectedBox(null); setSelectedBird(null); setSelectedDay(null);
    setShowReports(false); setShowEntry(false); setShowAdmin(true);
    setCheckTarget(t => ({ slug, nonce: (t?.nonce ?? 0) + 1 }));
  }, []);
  // Apply one of the integrity tables' hrefs as in-app state instead of letting the browser
  // navigate. Mirrors the shapes localdb builds: ?box&obs[&bird], ?day[&box].
  const openCheckHref = useCallback((href: string) => {
    const u = new URL(href, window.location.origin);
    const q = u.searchParams;
    const box = q.get('box'), obs = q.get('obs'), day = q.get('day'), bird = q.get('bird');
    window.history.pushState(null, '', u.pathname + u.search);
    setShowAdmin(false); setShowReports(false); setShowEntry(false);
    setSelectedBird(bird || null);
    if (day) { setDayBox(box ?? null); setSelectedDay(day); return; }
    setSelectedDay(null);
    if (box) {
      setHighlightObs(null); setScrollToObs(null);
      setObsAnchor(obs ? { box, time: obs } : null);
      setSelectedBox(box);
      if (obs) setTimeout(() => { setHighlightObs(obs); setScrollToObs(obs); }, 10);
    }
  }, []);
  const closeMenu = useCallback(() => {
    setMenuClosing(true);
    setTimeout(() => { setMenuOpen(false); setMenuClosing(false); }, 300);
  }, []);

  const lastLoadRef = useRef(0);
  const loadColony = useCallback(async () => {
    // Paint immediately from the cached snapshot if we have one — the sync below then
    // refreshes in the background. Only a first-ever visit (no cache) keeps the spinner up
    // for the full download. The grid, tiles and header counts all read the cache now, so
    // this is the only thing standing between a returning user and a painted page.
    try {
      if (await primeFromCache()) setLoading(false);
    } catch (e) {
      console.warn('primeFromCache failed; falling back to full sync', e);
    }
    // A sync failure (e.g. flaky mobile network on resume) must NOT block the colony list
    // fetch below.
    try {
      await syncDatabase((msg, pct) => { setLoadProgress(msg); setLoadPct(pct ?? null); });
    } catch (e) {
      console.warn('syncDatabase failed; continuing with cached data', e);
    }
    // The one thing left that isn't in the snapshot: which colonies this account may view.
    try {
      const cols = await fetchColonies();
      if (Array.isArray(cols) && cols.length > 0) setColonies(cols);
    } catch (e) {
      console.warn('colonies fetch failed', e);
    } finally {
      setLoading(false);
      lastLoadRef.current = Date.now();
    }
  }, []);

  // Switch the active colony: persist it (every colony-scoped fetch reads it), reset the
  // view to the new colony's overview, and reload — syncDatabase resets + re-syncs the cache.
  const switchColony = useCallback(async (id: number) => {
    if (id === getColonyId()) return;
    // Each colony has its own cache, keyed by "<region>-<colony>" so they never overlap.
    const c = colonies.find((x: any) => Number(x.colony_id) === id);
    setActiveColony(id, `${c?.region_id ?? 1}-${id}`);
    setColonyIdState(id);
    setSelectedBox(null); setSelectedBird(null); setSelectedDay(null);
    setShowAdmin(false); setShowReports(false); setShowEntry(false);
    window.history.pushState({}, '', '/');
    setLoading(true); setLoadProgress('Loading colony…'); setLoadPct(null);
    await loadColony();
  }, [loadColony, colonies]);

  useEffect(() => {
    loadColony(); // also fetches colonies via fetchColonies()
    // The poll syncs the cache when the watermark moves; everything on screen reads the cache,
    // so the store-version bump is the refresh — there is nothing left to re-fetch here.
    startPolling(() => {});

    // Re-sync when the app is reopened/refocused (mobile PWA resume) or network
    // returns — Britta's "doesn't refresh on opening" was the lack of this.
    const resume = () => {
      if (document.visibilityState === 'visible' && Date.now() - lastLoadRef.current > 15000) loadColony();
    };
    document.addEventListener('visibilitychange', resume);
    window.addEventListener('focus', resume);
    window.addEventListener('online', resume);
    return () => {
      stopPolling();
      document.removeEventListener('visibilitychange', resume);
      window.removeEventListener('focus', resume);
      window.removeEventListener('online', resume);
    };
  }, [loadColony]);

  // Expired/invalid session → bounce to login (automates the log-out/in fix).
  useEffect(() => {
    const onExpired = () => onLogout();
    window.addEventListener('ww-auth-expired', onExpired);
    return () => window.removeEventListener('ww-auth-expired', onExpired);
  }, [onLogout]);

  const boxDetail = useBoxDetail(loading ? null : selectedBox);

  // Leaving box view drops the transient highlight. Box + bird otherwise coexist — the
  // peng panel rides along across box changes (grid/map, arrows); only the box search
  // inputs reset it explicitly at their call sites.
  useEffect(() => {
    if (!selectedBox) setHighlightObs(null);
  }, [selectedBox]);

  // A box's obs anchor only makes sense for that box — drop a stale anchor when the box changes.
  useEffect(() => {
    if (obsAnchor && obsAnchor.box !== selectedBox) setObsAnchor(null);
  }, [selectedBox, obsAnchor]);

  // Deep-link / back-forward restore: once the anchored box's data is loaded, scroll to and
  // highlight the observation. Guarded so a background data refresh doesn't re-scroll.
  const lastRestoredObs = useRef<string|null>(null);
  useEffect(() => {
    if (!selectedBox || !boxDetail || !obsAnchor || obsAnchor.box !== selectedBox) return;
    const key = `${selectedBox}|${obsAnchor.time}`;
    if (lastRestoredObs.current === key) return;
    lastRestoredObs.current = key;
    setHighlightObs(obsAnchor.time);
    setScrollToObs(obsAnchor.time);
  }, [boxDetail, selectedBox, obsAnchor]);

  const birdData = useBirdDetail(loading ? null : selectedBird);
  // Reports page: clicking a bird docks a peng panel on the right instead of leaving.
  const [reportsBird, setReportsBird] = useState<string|null>(null);
  const reportsBirdData = useBirdDetail(reportsBird);
  // All-penguins page: clicking a peng docks the same side panel as the reports page.
  const [allBirdsBird, setAllBirdsBird] = useState<string|null>(null);
  const allBirdsBirdData = useBirdDetail(allBirdsBird);

  /**
   * Close whatever full-screen section is open (admin, reports, docs, entry, all-birds,
   * settings). Each of those returns early instead of rendering the colony view, so opening
   * a box or a bird from one of them — the header search is on every page — would set the
   * state under a screen that never shows it, and the click would look like it did nothing.
   */
  const leaveSection = () => {
    setShowAdmin(false); setShowReports(false); setShowDocs(false);
    setShowEntry(false); setShowAllBirds(false); setShowSettings(false);
  };

  const openBird = (pengNum: string) => {
    leaveSection();
    if (window.innerWidth < 900 && selectedBox) {
      setPreviousBox(selectedBox);
      setSelectedBox(null);
    }
    if (!selectedBox) setDockBirdToBox(true); // opened standalone -> dock beside its recent box
    setSelectedBird(pengNum);
  };

  // Adopt the bird's most-recently-seen box (sightings are newest-first) so a standalone bird
  // opens as the box+bird split with the panel docked right. Wide screens only — on narrow the
  // full-width bird page is fine.
  useEffect(() => {
    if (!dockBirdToBox || !selectedBird || selectedBox) return;
    if (window.innerWidth < 900) { setDockBirdToBox(false); return; }
    const box = birdData?.sightings?.[0]?.box;
    if (box) { setSelectedBox(box); setDockBirdToBox(false); }
  }, [dockBirdToBox, selectedBird, selectedBox, birdData]);

  const closeBird = () => {
    setSelectedBird(null);
  };

  // Navigate to a box from inside the bird panel. Desktop keeps the bird panel open
  // (it rides along in the split view); narrow screens can't show both, so we land on
  // the box and dismiss the bird. `date`, when given, highlights that observation.
  const goToBoxFromBird = (box: string, date?: string) => {
    leaveSection();
    setHighlightObs(null); setScrollToObs(null);
    if (window.innerWidth < 900) { setSelectedBird(null); setPreviousBox(null); }
    setObsAnchor(date ? { box, time: date } : null);
    setSelectedBox(box);
    if (date) setTimeout(() => { setHighlightObs(date); setScrollToObs(date); }, 10);
  };

  // Box navigation (grid, map): the docked peng panel rides along. Narrow screens
  // can't show box + bird side by side, so there the bird is dismissed.
  const openBox = (box: string) => {
    leaveSection();
    if (window.innerWidth < 900) setSelectedBird(null);
    setPreviousBox(null);
    setSelectedBox(box);
  };

  // All box IDs from observations (not just RFID-tagged ones)
  const sortedBoxIds = useMemo(() => {
    const ids = new Set([...Object.keys(boxTags || {}), ...Object.keys(stats?.box_info || {})]);
    return Array.from(ids).sort((a, b) => {
      const na = parseInt(a), nb = parseInt(b);
      return (!isNaN(na) && !isNaN(nb)) ? na - nb : a.localeCompare(b);
    });
  }, [boxTags]);

  const handleKeyDown = useCallback((e: KeyboardEvent) => {
    const t = e.target as HTMLElement | null;
    const typing = !!t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable);
    // "/" jumps to the unified search — including when the search already has focus, where it
    // is swallowed rather than typed, so leaning on the key never leaves slashes to delete.
    // getClientRects, not offsetParent: the toolbars and the mobile menu each mount a search,
    // and inside the day overlay (position:fixed) every one of them has a null offsetParent.
    if (e.key === '/') {
      const inSearch = !!t?.classList?.contains('uni-search-input');
      if (!typing || inSearch) {
        // The day view is an overlay laid OVER the page, which stays mounted underneath with a
        // search of its own — and the buried one still reports client rects, so searching the
        // whole document focused an input nobody could see and "/" looked dead. Whatever is on
        // top owns the key, so look inside the overlay first and fall back to the page.
        const visible = (root: ParentNode) => Array.from(root.querySelectorAll<HTMLInputElement>('.uni-search-input'))
          .find(i => i.getClientRects().length > 0);
        const overlay = document.querySelector('.day-overlay');
        const box = (overlay && visible(overlay)) || visible(document);
        e.preventDefault();
        if (box) { box.focus(); box.select(); }
        return;
      }
    }
    // Don't hijack arrow/Escape keys while typing in a field — they move the cursor / cancel the edit.
    if (typing) return;
    // Day view: . and , (shifted too) step to the next/previous date that has an observation —
    // the same keys that step boxes, because a day and a box are the same kind of thing to move
    // between. The arrows are left to the bird panel, which can be docked open here: it listens
    // on the window too, so sharing a key moved the peng AND the day on one press.
    if (selectedDay) {
      const ds = [...(stats?.observation_dates || [])].sort();
      const di = ds.indexOf(selectedDay);
      // Day → day: the box-highlight came from a specific box's date link — it
      // doesn't apply to a different day, so drop it.
      if ((e.key === '.' || e.key === '>') && di >= 0 && di < ds.length - 1) { e.preventDefault(); setDayBox(null); setSelectedDay(ds[di + 1]); }
      else if ((e.key === ',' || e.key === '<') && di > 0) { e.preventDefault(); setDayBox(null); setSelectedDay(ds[di - 1]); }
      else if (e.key === 'Escape') { setSelectedDay(null); }
      return;
    }
    if (!selectedBox || sortedBoxIds.length === 0) return;
    const idx = sortedBoxIds.indexOf(selectedBox);
    if (idx < 0) return;
    // Box → box on . and , (shifted too), leaving the arrow keys to the peng panel.
    // The date-scroll came from a day view link into the old box — it doesn't apply to a
    // different box, so drop it.
    if ((e.key === '.' || e.key === '>') && idx < sortedBoxIds.length - 1) {
      e.preventDefault();
      setHighlightObs(null); setScrollToObs(null);
      setSelectedBox(sortedBoxIds[idx + 1]);
    } else if ((e.key === ',' || e.key === '<') && idx > 0) {
      e.preventDefault();
      setHighlightObs(null); setScrollToObs(null);
      setSelectedBox(sortedBoxIds[idx - 1]);
    } else if (e.key === 'Escape') {
      setSelectedBox(null);
    }
  }, [selectedBox, selectedDay, sortedBoxIds, stats]);

  useEffect(() => {
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [handleKeyDown]);

  // While the day overlay is open, lock body scroll so the view underneath doesn't show a
  // second scrollbar or scroll behind it.
  useEffect(() => {
    if (!selectedDay) return;
    const prev = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => { document.body.style.overflow = prev; };
  }, [selectedDay]);

  if (loading) {
    // The all-penguins page reads nothing from localdb — one small server fetch — so don't
    // make it wait behind the IndexedDB colony prime. The full chrome appears once loaded.
    if (showAllBirds) return (
      <div className="app">
        <AllPenguinsPage token={token} colonyName={colonies.find((c: any) => c.colony_id === colonyId)?.colony_name} onBack={() => setShowAllBirds(false)} />
      </div>
    );
    return <div className="center loading-screen">
      {loadPct === null && <div className="spinner"/>}
      <p>{loadProgress || 'Loading colony data...'}</p>
      {loadPct !== null && <div className="progress-bar"><div className="progress-fill" style={{width: `${Math.round(loadPct * 100)}%`}}/></div>}
      <p className="muted" style={{fontSize:14, position:'absolute', bottom:16, right:16}}>Photo: Marty Melville</p>
    </div>;
  }

  // Password dialog renders on top of any page
  const passwordDialog = showChangePassword ? <ChangePasswordDialog token={token} userName={userName} onClose={() => setShowChangePassword(false)} /> : null;

  const goTo = (section: 'colony' | 'reports' | 'docs' | 'admin' | 'enter' | 'birds') => {
    // Drop any ?tab from the previous section so admin/reports don't inherit each other's tab.
    { const u = new URL(window.location.href); u.searchParams.delete('tab'); window.history.replaceState(null, '', u.pathname + u.search); }
    setSelectedBox(null); setSelectedBird(null); setSelectedDay(null);
    setShowAdmin(section === 'admin');
    setShowReports(section === 'reports');
    setShowDocs(section === 'docs');
    setShowEntry(section === 'enter');
    setShowAllBirds(section === 'birds');
  };

  const goToDay = (day: string, box?: string) => {
    // Day view is an overlay: keep the box + bird panel underneath so dismissing the day
    // (Escape / back / returning to the box) restores exactly where you were.
    leaveSection();
    setDayBox(box ?? null);
    setSelectedDay(day);
  };

  const currentSection = showAdmin ? 'admin' : showDocs ? 'docs' : showReports ? 'reports' : 'colony';

  const legacySearches = legacyMode;

  // One element, dropped into each toolbar. Defined here so it sits after the navigation
  // helpers it closes over and before the toolbars that use it. Every destination clears the
  // day overlay first — a result found from inside it should land on the thing, not behind it.
  const searchFor = (onDone?: () => void) => (
    <UnifiedSearch dates={dayDates}
      onBoxClick={(b) => { setSelectedDay(null); openBox(b); onDone?.(); }}
      onBirdClick={(t) => { setSelectedDay(null); openBird(t); onDone?.(); }}
      onDayClick={(d) => { goToDay(d); onDone?.(); }}
      onObsClick={(b, t) => { setSelectedDay(null); goToBoxFromBird(b, t); onDone?.(); }}
      onFocusChange={onSearchFocus} />
  );
  const unifiedSearch = searchFor();
  const mobileSearch = searchFor(closeMenu);

  const siteNav = (
    <nav className="site-nav">
      <a className={currentSection === 'colony' ? 'active' : ''} href="/" onClick={e => navClick(e, () => goTo('colony'))}>Colony</a>
      <a className={currentSection === 'reports' ? 'active' : ''} href="/reports" onClick={e => navClick(e, () => goTo('reports'))}>Reports</a>
      <a className={currentSection === 'docs' ? 'active' : ''} href="/docs" onClick={e => navClick(e, () => goTo('docs'))}>Docs</a>
      {userRole === 'admin' && <a className={currentSection === 'admin' ? 'active' : ''} href="/admin" onClick={e => navClick(e, () => goTo('admin'))}>Admin{mirrorAlert && <MirrorAlertBadge reason={mirrorAlert} />}</a>}
    </nav>
  );

  // Colony <option>s, grouped by region with <optgroup> once there's more than one region.
  const colonyOptionEls = (() => {
    const byRegion: Record<string, any[]> = {};
    for (const c of colonies) (byRegion[c.region_name || ''] ||= []).push(c);
    const regions = Object.keys(byRegion);
    const opts = (list: any[]) => list.map((c: any) => <option key={c.colony_id} value={c.colony_id}>{c.colony_name}</option>);
    return regions.length > 1 ? regions.map(r => <optgroup key={r} label={r}>{opts(byRegion[r])}</optgroup>) : opts(colonies);
  })();

  // The date search sits in the header, so the calendar it opens has to be available on every
  // page the header is on. It used to be rendered by the colony view alone, which left the
  // search on reports, docs, admin and the rest opening a picker nobody could see.
  const sortedDates = [...(stats?.observation_dates || [])].sort();
  const latestDay = sortedDates[sortedDates.length - 1] || new Date().toLocaleDateString('en-CA', { timeZone: 'Pacific/Auckland' });
  const datePicker = datePickerVisible
    ? <DayCalendar date={datePickerCenter || latestDay} dates={sortedDates} onDayClick={goToDay} />
    : null;

  const siteHeader = (
    <header>
      <h1 className="logo clickable" onClick={() => goTo('colony')}>Wildwatch</h1>
      <span className="header-desktop">
        {/* Search and the colony it searches sit left of the sections: what you're looking at,
            then where you're looking. */}
        <span className="header-search">{unifiedSearch}</span>
        {colonies.length > 1 && (
          <select className="colony-select" value={colonyId} onChange={e => switchColony(Number(e.target.value))} title="Switch colony">
            {colonyOptionEls}
          </select>
        )}
        {siteNav}
        {pinnedChecks.length > 0 && (
          <span className="pinned-checks">
            {pinnedChecks.map(p => (
              <a key={p.slug} className="pinned-check" href={checkHref(p.slug)} title={`Data validation — ${p.title}`}
                onClick={e => navClick(e, () => openPinnedCheck(p.slug))}>
                {p.title}
                {p.count !== undefined && <span className={`pinned-count${p.count === 0 ? ' zero' : ''}`}>{p.count}</span>}
              </a>
            ))}
          </span>
        )}
        <span className="header-user">
          {userName}
          <button className={`logout-btn${legacyMode ? ' legacy-on' : ''}`} onClick={toggleLegacyMode}
            title={legacyMode ? 'Legacy mode is on — the old Penguin/Box/Date toolbar is showing. Click to turn it off.'
                              : 'Legacy mode is off — search only. Click to bring the old Penguin/Box/Date toolbar back.'}>
            Legacy mode {legacyMode ? 'on' : 'off'}
          </button>
          <button className="logout-btn" onClick={() => setShowChangePassword(true)}>Password</button>
          <button className="logout-btn" onClick={onLogout}>Logout</button>
        </span>
      </span>
      <button className={`hamburger hamburger-${menuSide}`} onClick={() => setMenuOpen(o => !o)}>{'\u2630'}</button>
      {menuOpen && <>
        <div className={`mobile-backdrop${menuClosing ? ' closing' : ''}`} onClick={() => closeMenu()} />
        <div className={`mobile-panel mobile-panel-${menuSide}${menuClosing ? ' closing' : ''}`}>
          <nav className="mobile-nav">
            <a className={currentSection === 'colony' ? 'active' : ''} href="/" onClick={e => navClick(e, () => { goTo('colony'); closeMenu(); })}>Colony</a>
          </nav>
          {colonies.length > 1 && (
            <div className="mobile-search-group">
              <label className="mobile-label">Colony</label>
              <select className="colony-select" value={colonyId} onChange={e => { switchColony(Number(e.target.value)); closeMenu(); }}>
                {colonyOptionEls}
              </select>
            </div>
          )}
          {pinnedChecks.length > 0 && (
            <div className="mobile-search-group">
              <label className="mobile-label">Pinned checks</label>
              <nav className="mobile-nav">
                {pinnedChecks.map(p => (
                  <a key={p.slug} href={checkHref(p.slug)} onClick={e => navClick(e, () => { openPinnedCheck(p.slug); closeMenu(); })}>
                    {p.title}{p.count !== undefined ? ` (${p.count})` : ''}
                  </a>
                ))}
              </nav>
            </div>
          )}
          <div className="mobile-search-group">
            <label className="mobile-label">Search</label>
            {mobileSearch}
          </div>
          {legacySearches && (<>
            <div className="mobile-search-group">
              <label className="mobile-label">Penguin</label>
              <PenguinSearch penguins={allPenguins} search={penguinSearch} onSearchChange={setPenguinSearch} onBirdClick={(num) => { openBird(num); closeMenu(); }} />
            </div>
            <div className="mobile-search-group">
              <label className="mobile-label">Box</label>
              <input className="mobile-input" type="text" placeholder="Box number" onKeyDown={e => { if (e.key === 'Enter') { const v = (e.target as HTMLInputElement).value.replace(/#/g, '').trim(); if (v) { setSelectedBird(null); setSelectedDay(null); setHighlightObs(null); setScrollToObs(null); setSelectedBox(v); (e.target as HTMLInputElement).value = ''; closeMenu(); } } }} />
            </div>
          </>)}
          <div className="mobile-search-group">
            <label className="mobile-label">Date</label>
            {/* The recent-dates strip below isn't a search, so it stays for everyone. */}
            {legacySearches && <DateSearch dates={stats?.observation_dates || []} onDayClick={(d) => { goToDay(d); closeMenu(); }} onFocusChange={onSearchFocus} />}
            {(() => {
              const dates = (stats?.observation_dates || []).slice(0, 20).reverse();
              if (!dates.length) return null;
              const months = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
              return (
                <div ref={el => { if (el) el.scrollLeft = el.scrollWidth; }} style={{display:'flex', gap:4, overflowX:'auto', marginTop:6, paddingBottom:2}}>
                  {dates.map((d: string) => {
                    const ds = dateStatsCache.get(d);
                    const pm = registeredFmDates.get(d)?.partial;
                    const fm = ds?.isFullMonitor && !pm;
                    const [,m,day] = d.split('-');
                    const label = `${parseInt(day)} ${months[parseInt(m) - 1]}`;
                    return (
                      <span key={d} className="scan clickable" onClick={() => { goToDay(d); closeMenu(); }}
                        style={{fontSize:10, whiteSpace:'nowrap', background: pm ? '#b2dfdb' : fm ? '#c8e6c9' : '#e3f2fd', color: pm ? '#00695c' : fm ? '#2e7d32' : '#1a5276', borderColor: pm ? '#4db6ac' : fm ? '#81c784' : '#90caf9', display:'inline-flex', flexDirection:'column', alignItems:'center', gap:1, padding:'2px 5px', lineHeight:1.3}}>
                        <span style={{fontWeight:600}}>{label}</span>
                        {ds && <span style={{fontSize:8, opacity:0.8}}>
                          {'\uD83D\uDCE6'}{ds.boxes}{ds.penguins ? ` \uD83D\uDC27${ds.penguins}` : ''}{ds.eggs ? ` \uD83E\uDD5A${ds.eggs}` : ''}{ds.chicks ? ` \uD83D\uDC23${ds.chicks}` : ''}
                        </span>}
                      </span>
                    );
                  })}
                </div>
              );
            })()}
          </div>
          <nav className="mobile-nav">
            <a className={currentSection === 'reports' ? 'active' : ''} href="/reports" onClick={e => navClick(e, () => { goTo('reports'); closeMenu(); })}>Reports</a>
            <a className={currentSection === 'docs' ? 'active' : ''} href="/docs" onClick={e => navClick(e, () => { goTo('docs'); closeMenu(); })}>Docs</a>
            {userRole === 'admin' && <a className={currentSection === 'admin' ? 'active' : ''} href="/admin" onClick={e => navClick(e, () => { goTo('admin'); closeMenu(); })}>Admin{mirrorAlert && <MirrorAlertBadge reason={mirrorAlert} />}</a>}
            {userRole !== 'viewer' && <a className="mobile-nav-link" href="/enter" onClick={e => navClick(e, () => { goTo('enter'); closeMenu(); })}>Enter data</a>}
            <a className="mobile-nav-link" href="/birds" onClick={e => navClick(e, () => { goTo('birds'); closeMenu(); })}>All penguins</a>
          </nav>
          <div style={{marginTop:'auto'}}>
            <div className="mobile-nav-user" style={{display:'flex', alignItems:'center', gap:12, padding:'8px 12px'}}>
              <span className="mobile-username" style={{padding:0, flex:1}}>{userName}</span>
              <button onClick={() => { setShowSettings(true); closeMenu(); }} style={{background:'none', border:'none', fontSize:20, cursor:'pointer', padding:4, color:'#666'}} title="Settings">{'\u2699'}</button>
              <button onClick={() => { onLogout(); }} style={{background:'none', border:'none', fontSize:18, cursor:'pointer', padding:4, color:'#999'}} title="Logout">{'\uD83D\uDEAA'}</button>
            </div>
          </div>
        </div>
      </>}
    </header>
  );

  // Day view is a full-screen overlay (not an early return) so the box + bird panel beneath
  // it stay mounted — their scroll position and expanded sections survive the detour. Dialogs
  // (z-index ≥ 900) still layer above it; Escape / browser-back clear selectedDay to dismiss.
  const dayOverlay = (selectedDay && !showAdmin && !showReports && !showDocs && !showEntry && !showAllBirds && !showSettings) ? (
    <div className="app day-overlay">
      {siteHeader}
      {legacySearches && (
      <div className="colony-toolbar">
          <PenguinSearch penguins={allPenguins} search={penguinSearch} onSearchChange={setPenguinSearch} onBirdClick={(num) => setSelectedBird(num)} />
          <input className="box-search-input" type="text" placeholder="Box" onKeyDown={e => { if (e.key === 'Enter') { const v = (e.target as HTMLInputElement).value.replace(/#/g, '').trim(); if (v) { setSelectedDay(null); setHighlightObs(null); setScrollToObs(null); setSelectedBox(v); (e.target as HTMLInputElement).value = ''; } } }} />
          <DateSearch dates={dayDates} onDayClick={goToDay} onFocusChange={onSearchFocus} />
      </div>
      )}
      <DayView date={selectedDay} dates={dayDates} highlightBox={dayBox} onBoxClick={(box, date) => { setSelectedDay(null); if (window.innerWidth < 900) setSelectedBird(null); setObsAnchor(date ? { box, time: date } : null); setSelectedBox(box); if (date) { setHighlightObs(null); setScrollToObs(null); setTimeout(() => { setHighlightObs(date); setScrollToObs(date); }, 10); } else { setHighlightObs(null); setScrollToObs(null); } }} onBirdClick={openBird} onDayClick={goToDay} externalBird={selectedBird} token={token} canEdit={userRole !== 'viewer'} allPenguins={allPenguins} peekCalendar={datePickerVisible} />
    </div>
  ) : null;

  // Wrap any return with tooltip provider + portal, plus the day-view overlay on top.
  const wrap = (content: React.ReactNode) => (
    <DateTooltipCtx.Provider value={dateTipCtx}>
      {content}
      {dayOverlay}
      <DateTooltipPortal tip={dateTip.tip} statsCache={dateStatsCache} />
    </DateTooltipCtx.Provider>
  );

  // Settings page
  if (showSettings) {
    return wrap(
      <div className="app">
        {siteHeader}
        {datePicker}
        <div style={{maxWidth:400, margin:'0 auto', padding:'24px 20px'}}>
          <h2 style={{color:'#1a5276', margin:'0 0 20px'}}>Settings</h2>
          <div style={{marginBottom:20}}>
            <h3 style={{color:'#1a5276', margin:'0 0 8px'}}>Menu position</h3>
            <div style={{display:'flex', gap:8}}>
              {(['left', 'right'] as const).map(side => (
                <button key={side} className="edit-btn" style={menuSide === side ? {background:'#2196F3', color:'#fff', borderColor:'#2196F3'} : undefined}
                  onClick={() => { setMenuSide(side); localStorage.setItem('ww_menu_side', side); }}>
                  {side === 'left' ? 'Left' : 'Right'}
                </button>
              ))}
            </div>
          </div>
          <div style={{marginBottom:20}}>
            <h3 style={{color:'#1a5276', margin:'0 0 8px'}}>Password</h3>
            <button className="edit-btn" onClick={() => setShowChangePassword(true)}>Change password</button>
          </div>
          <button className="edit-btn" onClick={() => setShowSettings(false)} style={{marginTop:12}}>Back</button>
        </div>
        {passwordDialog}
      </div>
    );
  }

  // Admin page
  if (showAdmin && userRole === 'admin') {
    return wrap(
      <div className="app">
        {siteHeader}
        {datePicker}
        <CheckNavContext.Provider value={openCheckHref}>
          <AdminPanel token={token} observationDates={stats?.observation_dates} checkTarget={checkTarget}
            allPenguins={allPenguins} onLeaveEntry={() => goTo('colony')} mirrorAlert={mirrorAlert}
            fmColony={(colonies.find((c: any) => Number(c.colony_id) === colonyId)?.colony_prefix ?? 'PT') === 'PT'} />
        </CheckNavContext.Provider>
        {passwordDialog}
      </div>
    );
  }

  if (showEntry && userRole !== 'viewer') {
    return wrap(
      <div className="app">
        {siteHeader}
        {datePicker}
        <DataEntryPage token={token} allPenguins={allPenguins} onBack={() => goTo('colony')}
          fmColony={(colonies.find((c: any) => Number(c.colony_id) === colonyId)?.colony_prefix ?? 'PT') === 'PT'} />
        {passwordDialog}
      </div>
    );
  }

  // All-penguins page: every bird across the colonies this user can view
  if (showAllBirds) {
    return wrap(
      <div className="app">
        {siteHeader}
        {datePicker}
        <div className={allBirdsBird && allBirdsBirdData?.penguin ? 'reports-page-docked' : ''}>
          <AllPenguinsPage token={token} colonyName={colonies.find((c: any) => c.colony_id === colonyId)?.colony_name}
            onOpenBird={setAllBirdsBird} onEnterBird={() => setAddPenguinBox('')} />
        </div>
        {allBirdsBird && allBirdsBirdData?.penguin && (
          <div className="day-bird-dock entry-bird-dock">
            <BirdPage data={allBirdsBirdData} onBirdClick={(num: string) => setAllBirdsBird(num)}
              onBoxClick={(box: string) => { setShowAllBirds(false); openBox(box); }}
              onSightingClick={(box: string, date: string) => { setShowAllBirds(false); goToBoxFromBird(box, date); }}
              onDayClick={(d: string) => { setShowAllBirds(false); goToDay(d); }}
              onClose={() => setAllBirdsBird(null)}
              token={token} canEdit={userRole !== 'viewer'} />
          </div>
        )}
        {passwordDialog}
      </div>
    );
  }

  // Reports page
  if (showDocs) {
    return wrap(
      <div className="app">
        {siteHeader}
        {datePicker}
        <DocsPage />
        {passwordDialog}
      </div>
    );
  }

  if (showReports) {
    return wrap(
      <div className="app">
        {siteHeader}
        {datePicker}
        <div className={`reports-page${reportsBird && reportsBirdData?.penguin ? ' reports-page-docked' : ''}`}>
          <ReportsPage onOpenBird={setReportsBird} token={token}
            colonyName={colonies.find((c: any) => c.colony_id === colonyId)?.colony_name}
            onDayClick={(d: string) => { setShowReports(false); goToDay(d); }} />
        </div>
        {reportsBird && reportsBirdData?.penguin && (
          <div className="day-bird-dock entry-bird-dock">
            <BirdPage data={reportsBirdData} onBirdClick={(num: string) => setReportsBird(num)}
              onBoxClick={(box: string) => { setShowReports(false); openBox(box); }}
              onSightingClick={(box: string, date: string) => { setShowReports(false); goToBoxFromBird(box, date); }}
              onDayClick={(d: string) => { setShowReports(false); goToDay(d); }}
              onClose={() => setReportsBird(null)}
              token={token} canEdit={userRole !== 'viewer'} />
          </div>
        )}
        {passwordDialog}
      </div>
    );
  }

  // Bird page - replaces everything (only when no box is selected)
  if (selectedBird && !selectedBox) {
    return wrap(
      <div className="app">
        {siteHeader}
        {datePicker}
        {legacySearches && (
        <div className="colony-toolbar">
            <PenguinSearch penguins={allPenguins} search={penguinSearch} onSearchChange={setPenguinSearch} onBirdClick={openBird} />
            <input className="box-search-input" type="text" placeholder="Box" onKeyDown={e => { if (e.key === 'Enter') { const v = (e.target as HTMLInputElement).value.replace(/#/g, '').trim(); if (v) { setSelectedBird(null); setHighlightObs(null); setScrollToObs(null); setSelectedBox(v); (e.target as HTMLInputElement).value = ''; } } }} />
            <DateSearch dates={stats?.observation_dates || []} onDayClick={goToDay} onFocusChange={onSearchFocus} />
        </div>
        )}
        <div className="bird-page">
          <div className="page-header">
            <a className="page-back" href={previousBox ? `/box/${previousBox}` : '/'} onClick={e => navClick(e, () => { closeBird(); if (previousBox) { setHighlightObs(null); setScrollToObs(null); setSelectedBox(previousBox); setPreviousBox(null); } })}>&larr; {previousBox ? `Box ${previousBox}` : 'Colony'}</a>
          </div>
          {birdData?.penguin ? (
            <BirdPage data={birdData} onBirdClick={openBird} token={token} canEdit={userRole !== 'viewer'}
              onBoxClick={(box: string) => goToBoxFromBird(box)}
              onSightingClick={(box: string, date: string) => goToBoxFromBird(box, date)}
              onDayClick={goToDay} />
          ) : false ? (() => { const p = allPenguins.find((p: any) => p.peng_num === selectedBird || p.pit_id === selectedBird); return p ? <div style={{padding:'1em'}}><PenguinMini scan={p} onClick={() => {}} /><p className="muted">Loading bird data...</p></div> : <p className="muted">Loading bird data...</p>; })()
          : <p className="muted">Bird not found</p>}
        </div>
      </div>
    );
  }

  return wrap(
    <div className="app">
      {siteHeader}
      {legacySearches && (
      <div className="colony-toolbar">
          <PenguinSearch penguins={allPenguins} search={penguinSearch} onSearchChange={setPenguinSearch} onBirdClick={openBird} />
          <input className="box-search-input" type="text" placeholder="Box" onKeyDown={e => { if (e.key === 'Enter') { const v = (e.target as HTMLInputElement).value.replace(/#/g, '').trim(); if (v) { setSelectedBird(null); setHighlightObs(null); setScrollToObs(null); setSelectedBox(v); (e.target as HTMLInputElement).value = ''; } } }} />
          <DateSearch dates={stats?.observation_dates || []} onDayClick={goToDay} onFocusChange={onSearchFocus} />
        {stats && <span className="colony-stats">{stats.total_boxes} boxes &middot; {stats.season_observations} obs &middot; {stats.season_penguins} penguins this season</span>}
      </div>
      )}
      {(datePickerVisible || (!selectedBox && !selectedBird && !selectedDay)) && (
        <DayCalendar date={datePickerCenter || latestDay} dates={sortedDates} onDayClick={goToDay} />
      )}

      {!selectedBox && (
        <>
          <div className="top-row">
            <ColonyMap boxTags={boxTags} selectedBox={selectedBox} onBoxSelect={openBox} />
            <StatsPanel boxTags={boxTags} selectedBox={selectedBox} stats={stats} />
          </div>
        </>
      )}

      <div className={selectedBox ? 'split-view' : ''}>
        {/* Box grid - always visible */}
        <div className={selectedBox ? 'grid-sidebar' : 'grid-section'}>
          <BoxGrid boxTags={boxTags} selectedBox={selectedBox} onBoxSelect={openBox} boxInfo={gridBoxInfo} scrollToBox={scrollToBox} boxNames={queryAllLocations().map((l: any) => l.location_name)} />
        </div>

        {/* Box detail */}
        {selectedBox && (
        <div className="detail-area">
          {/* Header + status bar full width */}
          <div className="detail-full">
            <div className="page-header">
              <div className="box-header-left">
                <h2>Box {selectedBox}</h2>
                {boxDetail?.location && <WatchedTick location={boxDetail.location} token={token} canEdit={userRole !== 'viewer'} />}
                {boxDetail?.location && (
                  <div className="persistent-notes">
                    <EditableField value={boxDetail.location.persistent_notes || ''} onSave={(val) => updateRecord(token, 'observation_locations', boxDetail.location!.location_id, {persistent_notes: val})} placeholder="Box notes (persistent)" canEdit={userRole !== 'viewer'} />
                  </div>
                )}
                {boxDetail && <StatusLegend />}
              </div>
              <a className="page-back" href="/" onClick={e => navClick(e, () => { setScrollToBox(selectedBox); setSelectedBox(null); })}>&larr; Overview</a>
            </div>
            {false ? <p className="muted">Loading...</p> : boxDetail ? (
              <BreedingStatusBar observations={boxDetail.observations} box={selectedBox} hideLegend onHighlight={setHighlightObs} onScrollTo={(d) => { if (selectedBox) setObsAnchor({ box: selectedBox, time: d }); setHighlightObs(null); setScrollToObs(null); setTimeout(() => { setHighlightObs(d); setScrollToObs(d); }, 10); }} />
            ) : null}
          </div>

          {/* Split: observations+birds left, penguin detail right */}
          {!false && boxDetail && (
          <div className="detail-split">
            <BoxPanel
              key={selectedBox}
              data={boxDetail}
              boxName={selectedBox}
              allPenguins={allPenguins}
              onBirdClick={openBird}
              onDayClick={(day: string) => goToDay(day, selectedBox || undefined)}
              highlightObs={highlightObs}
              scrollToObs={scrollToObs}
              onScrollToObs={(t: string) => { setHighlightObs(null); setScrollToObs(null); setTimeout(() => { setHighlightObs(t); setScrollToObs(t); }, 10); }}
              token={token}
              canEdit={userRole !== 'viewer'}
              onDataChange={refreshStats}
              showDeleted={showDeleted}
              deletedObs={deletedObs}
              onToggleDeleted={async () => {
                if (!showDeleted && deletedObs.length === 0) {
                  const r = await fetch(`/api/dashboard.php?view=box&name=${encodeURIComponent(selectedBox!)}&include_deleted=1&colony_id=${getColonyId()}&_=${Date.now()}`, { headers: { 'Authorization': `Bearer ${token}` } });
                  const d = await r.json();
                  setDeletedObs(d.deleted || []);
                }
                setShowDeleted(!showDeleted);
              }}
              onAddPenguin={(box: string) => setAddPenguinBox(box)}
            />
            {selectedBird && (
            <div className="detail-bird">
              {birdData?.penguin ? (
                <BirdPage data={birdData} onBirdClick={openBird} token={token} canEdit={userRole !== 'viewer'} onClose={() => setSelectedBird(null)}
                  onBoxClick={(box: string) => goToBoxFromBird(box)}
                  onSightingClick={(box: string, date: string) => goToBoxFromBird(box, date)}
                  onDayClick={goToDay} />
              ) : <p className="muted">Loading bird...</p>}
            </div>
            )}
          </div>
          )}
        </div>
        )}
      </div>
      {passwordDialog}
      {addPenguinBox !== null && (
        <AddPenguinDialog
          token={token}
          chipBox={addPenguinBox}
          colonyPrefix={colonies.find((c: any) => Number(c.colony_id) === colonyId)?.colony_prefix ?? ''}
          defaultChipperId={Number(localStorage.getItem('ww_observer_id')) || null}
          allPenguins={allPenguins}
          onClose={() => setAddPenguinBox(null)}
          onAdded={async (pengNum) => {
            const fromBox = addPenguinBox;
            setAddPenguinBox(null);
            await triggerSync();
            refreshStats();
            setPreviousBox(fromBox);
            setSelectedBox(null);
            setSelectedBird(pengNum);
          }}
        />
      )}
    </div>
  );
}

function formatDate(d:string) {
  return parseDate(d).toLocaleDateString('en-NZ',{day:'numeric',month:'short',year:'numeric',timeZone:'Pacific/Auckland'});
}
function fmtDateTime(d:string) { return formatDate(d); }
/** The span between two dates, as "3 years, 4 months" — rounded to the nearest whole month,
 *  dropping whichever of years/months is zero. Under a month it counts in days, so a bird
 *  chipped last week doesn't read as "0 months". Empty string if the range is inverted or
 *  either end is unparseable, so callers can hide the row rather than print nothing useful. */
function durationBetween(from: Date, to: Date): string {
  if (isNaN(from.getTime()) || isNaN(to.getTime()) || from >= to) return '';
  let months = (to.getFullYear() - from.getFullYear()) * 12 + (to.getMonth() - from.getMonth());
  if (to.getDate() < from.getDate()) months--;  // this month's anniversary hasn't come round yet
  // Round up once past the midpoint of the current part-month.
  const anniv = new Date(from); anniv.setMonth(anniv.getMonth() + months);
  if ((to.getTime() - anniv.getTime()) / DAY >= 15) months++;
  if (months < 1) {
    const days = Math.max(1, Math.round((to.getTime() - from.getTime()) / DAY));
    return `${days} day${days !== 1 ? 's' : ''}`;
  }
  const y = Math.floor(months / 12), m = months % 12;
  return [y && `${y} year${y !== 1 ? 's' : ''}`, m && `${m} month${m !== 1 ? 's' : ''}`].filter(Boolean).join(', ');
}
/** How long ago a date was. */
function durationSince(d: string): string { return durationBetween(parseDate(d), new Date()); }
/** durationSince as an elapsed-time phrase — "3 years, 4 months ago". Empty for a missing
 *  or future date, so the bare word "ago" can never render on its own. */
function agoSince(d: string): string { const s = durationSince(d); return s ? `${s} ago` : ''; }
/** Returns YYYY-MM-DD in NZ time for a datetime string.
 *  Fixed +12 (NZST), matching the bucketing in localdb so dates can't roll over. */
function toNzDateStr(d: string): string {
  const ms = parseDate(d).getTime();
  if (!Number.isFinite(ms)) return '';   // never let an Invalid Date throw out of .toISOString()
  return new Date(ms + 12 * 3600000).toISOString().slice(0, 10);
}

function AuthenticatedAppWithTooltip(props: { token: string; userName: string; userRole: string; onLogout: () => void }) {
  return <AuthenticatedApp {...props} />;
}

export default App;
