/**
 * Client-side database cache.
 * Loads full snapshot on first visit, incremental syncs after.
 * Data lives in memory for instant queries; IndexedDB for persistence across reloads.
 */

import { SEASON_START_MONTH, SEASON_START_DAY } from '../config';

function authHeaders(): Record<string, string> {
  const token = localStorage.getItem('ww_token');
  return token ? { 'Authorization': `Bearer ${token}` } : {};
}

// ============ Active colony ============
// Each colony the user can view is cached in its OWN IndexedDB ("wildwatch-<region>-<colony>"),
// so switching is instant; only the currently-viewed colony is synced/updated. Persisted across reloads.
const COLONY_KEY = 'ww_colony';       // active colony_id
const COLONY_DBKEY = 'ww_colony_key'; // active "<region>-<colony>" key (per-colony DB name)
const COLONY_LIST_KEY = 'ww_colonies'; // every colony this account can view: id, name, prefix

/**
 * The colonies this account can view, held from the last time the server named them.
 *
 * Colony names are the one thing about a bird that the snapshot doesn't carry — penguin rows
 * are global and unprefixed by colony — so any screen naming a bird's colony used to wait on
 * a round trip to be told what "NI" stands for, and showed the bare acronym until it landed.
 * The list is three fields long and changes about never, so it is kept where the rest of the
 * offline state lives and refreshed in the background instead.
 */
type ColonyRow = { colony_id: number; colony_name: string; colony_prefix: string };
// Parsed once and held: computeAllPenguinsRows asks per bird, and a JSON.parse of localStorage
// a thousand times over is exactly the kind of cost this cache exists to avoid.
let coloniesMem: ColonyRow[] | null = null;

export function setCachedColonies(list: any[]): void {
  if (!Array.isArray(list) || list.length === 0) return;
  const slim: ColonyRow[] = list.map(c => ({ colony_id: Number(c.colony_id), colony_name: c.colony_name, colony_prefix: c.colony_prefix }));
  const changed = JSON.stringify(slim) !== JSON.stringify(getCachedColonies());
  coloniesMem = slim;
  colonyByPrefix = null;
  try { localStorage.setItem(COLONY_LIST_KEY, JSON.stringify(slim)); } catch { /* quota — the fetch still fills it in */ }
  // The prefixes decide how every peng# reads and how a typed one resolves, so screens that
  // rendered before the list first arrived (a fresh browser, the nestcheck embed) redraw.
  if (changed) notifySubscribers();
}
export function getCachedColonies(): ColonyRow[] {
  if (coloniesMem) return coloniesMem;
  try { coloniesMem = JSON.parse(localStorage.getItem(COLONY_LIST_KEY) || '[]'); } catch { coloniesMem = []; }
  return coloniesMem || [];
}
/** prefix (uppercase) -> colony name, rebuilt only when the list itself is replaced. */
let colonyByPrefix: Map<string, string> | null = null;
function prefixMap(): Map<string, string> {
  if (colonyByPrefix && coloniesMem) return colonyByPrefix;
  colonyByPrefix = new Map(getCachedColonies().map(c => [(c.colony_prefix || '').toUpperCase(), c.colony_name]));
  return colonyByPrefix;
}

/**
 * Which colony a peng# belongs to, by name.
 *
 * The prefix IS the colony: it's part of the primary key, and the API hands every number over
 * in that full stored form, which is what makes this answerable offline. Unknown prefixes fall
 * back to the acronym, which is still better than an empty cell.
 */
export function colonyNameForPeng(pengNum: string): string {
  // A bare number can only be something a person typed or an old link; bare is PT's standard.
  const prefix = (String(pengNum).match(/^[A-Z]{2,4}/)?.[0] || 'PT').toUpperCase();
  return prefixMap().get(prefix) || (prefix === 'PT' ? '' : prefix);
}

// ============ Peng# forms ============
// The API speaks full stored numbers only ("PT1039", "NI7") and the cache holds exactly what it
// was sent, so every key, comparison, route and write uses the full form. Stripping happens at
// one place — the moment a number is put in front of a person — and prefixing at one other —
// the moment a person hands one over. The server used to strip on the way out and re-add on the
// way in, and the one write path that forgot to re-add it wrote a bird into the wrong number.

// Mirror of BARE_NUMBER_PREFIXES in config.php: colonies whose local standard is bare numbers.
// Only these drop their prefix, and only at home — an NI view still reads "NI7", and a visiting
// bird always keeps its prefix so a bare number is never ambiguous on screen.
const BARE_NUMBER_PREFIXES = ['PT'];

/** The viewing colony's peng# prefix (uppercase), or '' while the colony list isn't known yet. */
export function activeColonyPrefix(): string {
  const id = getColonyId();
  return (getCachedColonies().find(c => c.colony_id === id)?.colony_prefix || '').toUpperCase();
}

/** A full peng# as a person should read it in the viewing colony: "PT1039" → "1039" in a PT
 *  view, everything else unchanged. For screens, prints and people-facing exports only —
 *  never as a key, a comparison or anything sent back to the server. */
export function displayPengNum(pengNum: string | null | undefined): string {
  if (pengNum === null || pengNum === undefined) return '';
  const s = String(pengNum);
  const vp = activeColonyPrefix();
  if (vp && BARE_NUMBER_PREFIXES.includes(vp) && s.startsWith(vp) && /^\d+$/.test(s.slice(vp.length))) return s.slice(vp.length);
  return s;
}

/**
 * The full stored form of a peng# a person typed (or an old link carried): letters first means
 * it's already full, so it's just uppercased; a bare number is the viewing colony's bird, as it
 * is on screen. Only short digit runs are prefixed — a PIT ID (15 digits, or the 8-char tail
 * the pills show) goes to the same bird-lookups and must come through untouched.
 */
export function fullPengNum(typed: string | null | undefined): string {
  const s = String(typed ?? '').trim().replace(/^#/, '').replace(/\s+/g, '').toUpperCase();
  if (/^\d{1,6}$/.test(s)) return activeColonyPrefix() + s;
  return s;
}

/** The numeric part of a peng# ("PT1039" → 1039), NaN when there is none. */
export function pengNumValue(pengNum: string | null | undefined): number {
  const m = String(pengNum ?? '').match(/(\d+)$/);
  return m ? parseInt(m[1], 10) : NaN;
}

/** Order peng#s by their number, then prefix, so PT2 < PT10 and a mixed list interleaves by
 *  number the way people read it. Numberless values sort last. */
export function comparePengNum(a: string | null | undefined, b: string | null | undefined): number {
  const na = pengNumValue(a), nb = pengNumValue(b);
  if (isNaN(na) !== isNaN(nb)) return isNaN(na) ? 1 : -1;
  if (!isNaN(na) && na !== nb) return na - nb;
  return String(a ?? '').localeCompare(String(b ?? ''));
}
export function getColonyId(): number {
  return parseInt(localStorage.getItem(COLONY_KEY) || '1', 10) || 1;
}
export function setColonyId(id: number): void {
  localStorage.setItem(COLONY_KEY, String(id));
}
/** "<region>-<colony>" key for the active colony (falls back to region 1 before any switch). */
export function getColonyKey(): string {
  return localStorage.getItem(COLONY_DBKEY) || `1-${getColonyId()}`;
}
/** Switch the active colony: persist its id + region-colony key and drop the in-memory cache so
 *  the next load uses the new colony's own DB (instant if already cached). */
export function setActiveColony(colonyId: number, regionColonyKey: string): void {
  localStorage.setItem(COLONY_KEY, String(colonyId));
  localStorage.setItem(COLONY_DBKEY, regionColonyKey);
  mem = null;
}
/** `colony_id=N` for appending to query strings. */
function colonyQS(): string { return `colony_id=${getColonyId()}`; }

// Old single-colony cache is replaced by per-colony DBs — remove it once to reclaim the space.
try { indexedDB.deleteDatabase('wildwatch'); } catch { /* ignore */ }
function dbName(): string { return 'wildwatch-' + getColonyKey(); }
const DB_VERSION = 5; // v5: observers store
const CACHE_VERSION = 24; // Bump to force all clients to full re-sync (v24: the API now sends every peng_num in full "PT1039" form — caches holding the old stripped "1039" must be replaced; v23: pit_id lost its "LA" reader prefix)
const STORES = ['observations', 'scans', 'penguins', 'chips', 'locations', 'biometrics',
  'verifications', 'day_notes', 'observers', 'meta'] as const;
// Stores from earlier DB versions that no longer exist; dropped on upgrade.
const OBSOLETE_STORES = ['verification_chicks', 'disagreements'];

// Locations excluded from Full Monitor detection. Now configured per-colony (colonies.fm_excluded_boxes,
// delivered in the snapshot). This is only the fallback when a snapshot predates that field.
const DEFAULT_FM_EXCLUDED = ['0', 'AA', 'AB', 'AC'];
/** Parse the comma/space-separated excluded-box list. undefined/null (field absent) → historical
 *  default; an explicit string (including "") → exactly that set, so admins can exclude nothing. */
function parseFmExcluded(raw?: string | null): Set<string> {
  if (raw === undefined || raw === null) return new Set(DEFAULT_FM_EXCLUDED);
  return new Set(raw.split(/[\s,]+/).map(s => s.trim().toUpperCase()).filter(Boolean));
}

// A box whose most recent breeding_status is one of these is excused from Full Monitor:
// DCM (dead/collapsed/missing) or IGN (deliberately ignored) — while it holds that status.
const FM_EXCUSED_STATUSES = new Set(['DCM', 'IGN']);

/** Sort box/location names numerically where possible ("2" before "140"), else lexically. */
function compareBoxNames(a: string, b: string): number {
  const na = parseInt(a), nb = parseInt(b);
  return (!isNaN(na) && !isNaN(nb)) ? na - nb : a.localeCompare(b);
}
type StoreNames = typeof STORES[number];

// ============ Store subscriptions ============

let storeVersion = 0;
const subscribers = new Set<() => void>();

function notifySubscribers() {
  storeVersion++;
  for (const cb of subscribers) cb();
}

export function subscribe(cb: () => void): () => void {
  subscribers.add(cb);
  return () => subscribers.delete(cb);
}

export function getStoreVersion(): number {
  return storeVersion;
}

// ============ In-memory cache ============

interface MemCache {
  observations: any[];
  scans: any[];
  penguins: any[];
  chips: any[];
  locations: any[];
  biometrics: any[];
  verifications: any[];
  dayNotes: any[];
  observers: any[];
  // Pre-built indexes
  obsById: Map<number, any>;            // observation_id → observation
  obsByLocation: Map<number, any[]>;   // location_id → observations
  scansByObs: Map<number, any[]>;      // observation_id → scans
  chipByPit: Map<string, any>;         // pit_id → chip
  pengByNum: Map<string, any>;         // peng_num → penguin
  locByName: Map<string, any>;         // location_name → location
  locById: Map<number, any>;           // location_id → location
  chipsByPeng: Map<string, any[]>;     // peng_num → chips
  // Where a chip was made. Chips ride the snapshot globally (visitor scans need them), so
  // "which chips belong to this box" used to mean a scan of the whole list per box — paid
  // once per box by every full-colony report. Indexed the two ways a chip identifies its box:
  // location_id when it has one, else the legacy chip_box name. The two are disjoint.
  chipsByLocId: Map<number, any[]>;    // location_id → chips made there
  chipsByBoxName: Map<string, any[]>;  // chip_box → chips with no location_id
  scansByPit: Map<string, any[]>;      // pit_id → scans
  bioByPeng: Map<string, any[]>;       // peng_num → biometrics
  // Precomputed date stats
  // Breeding verification (human ground truth): one row per verified clutch, chicks inline (JSON)
  verByObs: Map<number, any>;          // anchor observation_id → verification
  noteByDate: Map<string, string>;     // NZ date string → the day's note
  dayPeopleByDate: Map<string, { observer_id: number | null; scribe_id: number | null }>; // who was out that day
  observerById: Map<number, string>;   // observer_id → observer_name
  dateStats: Map<string, any>;         // NZ date string → stats
  observationDates: string[];          // sorted NZ dates with data
  obsByNzDate: Map<string, any[]>;     // NZ date string → observations
  // Per-colony Full Monitor exclusions (from snapshot's fm_excluded_boxes)
  fmExcludedRaw: string | null | undefined; // raw value used to build fmExcluded (for change detection)
  fmExcluded: Set<string>;             // uppercased location names excluded from FM detection
}

let mem: MemCache | null = null;

/** Convert UTC datetime string to NZ date string (YYYY-MM-DD).
 *  Uses a fixed +12 (NZST) offset, not DST-aware Pacific/Auckland, so a UTC
 *  instant always maps to a single NZ day and can't roll over a date boundary. */
/** Normalise a server datetime to a string every browser can parse. Values arrive as ISO
 *  ("…T…Z"), MySQL "YYYY-MM-DD HH:MM:SS", or date-only "YYYY-MM-DD" (chip_date). Safari, unlike
 *  Chrome, rejects a bare space AND a date-only value with a trailing "Z" ("2026-08-02Z" →
 *  Invalid Date), and an Invalid Date reaching toISOString() white-screens the page. */
function isoUtc(utc: string): string {
  if (!utc) return utc;
  if (utc.includes('T') || utc.includes('Z')) return utc;
  return utc.length <= 10 ? utc + 'T00:00:00Z' : utc.replace(' ', 'T') + 'Z';
}
function utcToNzDate(utc: string): string {
  const ms = new Date(isoUtc(utc)).getTime();
  if (!Number.isFinite(ms)) return '';
  return new Date(ms + 12 * 3600000).toISOString().slice(0, 10);
}

/** Compute stats for a single NZ date from cache data */
function computeDateStatsFromCache(nzDate: string, c: MemCache): any {
  const obs = c.obsByNzDate.get(nzDate) || [];
  const boxes = new Set(obs.map((o: any) => c.locById.get(o.location_id)?.location_name).filter(Boolean));
  const totalAdults = obs.reduce((s: number, o: any) => s + (o.adults || 0), 0);
  const totalEggs = obs.reduce((s: number, o: any) => s + (o.eggs || 0), 0);
  const totalChicks = obs.reduce((s: number, o: any) => s + (o.chicks || 0), 0);
  const allScans = obs.flatMap((o: any) => c.scansByObs.get(o.observation_id) || []);
  const uniquePenguins = new Set(allScans.map((s: any) => c.chipByPit.get(s.pit_id)?.peng_num).filter(Boolean));
  // Chippings on this date — only birds chipped at a location in the current colony.
  // Chips are global in the cache; location_id ties each to its colony (locById is colony-scoped).
  const chippedCount = c.chips.filter((ch: any) => ch.chip_date === nzDate && c.locById.has(ch.location_id)).length;
  // The day's note — what a person said this day's monitor was. One per colony per date, so
  // there is nothing to vote on the way the old per-observation monitor_filename needed.
  const label = c.noteByDate.get(nzDate) || null;
  // Full monitor: a box not observed today is excused if its most recent breeding_status (before today) is DCM
  // Convert NZ date to UTC cutoff: end of NZ day = nzDate T12:00:00 UTC (fixed +12, matching utcToNzDate)
  const utcCutoff = nzDate + ' 12:00:00';
  const excluded = c.fmExcluded;
  const excusedBoxes = new Set<string>();
  for (const loc of c.locations) {
    if (excluded.has(loc.location_name.toUpperCase())) continue; // not part of FM calculation
    if (boxes.has(loc.location_name)) continue; // observed today — status doesn't matter
    const locObs = (c.obsByLocation.get(loc.location_id) || [])
      .filter((o: any) => !o.is_deleted && o.breeding_status && o.observation_time_utc < utcCutoff)
      .sort((a: any, b: any) => b.observation_time_utc.localeCompare(a.observation_time_utc));
    if (locObs.length > 0 && FM_EXCUSED_STATUSES.has(locObs[0].breeding_status)) excusedBoxes.add(loc.location_name);
  }
  // Required = all locations not excluded/excused (DCM or IGN). FM = all required boxes observed.
  const missingBoxes = c.locations.filter(l => !excluded.has(l.location_name.toUpperCase()) && !boxes.has(l.location_name) && !excusedBoxes.has(l.location_name));
  const isFullMonitor = missingBoxes.length === 0 && boxes.size > 0;
  const missingNames = missingBoxes.map(l => l.location_name).sort(compareBoxNames);
  // Who recorded the day's box data. observations carry observer_id only; the name comes from
  // the snapshot's observers list. Busiest first, so the person who did most of the round leads.
  const obsPerObserver = new Map<number, number>();
  for (const o of obs) if (o.observer_id) obsPerObserver.set(Number(o.observer_id), (obsPerObserver.get(Number(o.observer_id)) || 0) + 1);
  const observers = Array.from(obsPerObserver.entries())
    .map(([id, count]) => ({ name: c.observerById.get(id) || `#${id}`, count }))
    .sort((a, b) => b.count - a.count || a.name.localeCompare(b.name));
  return { boxes: boxes.size, obs: obs.length, adults: totalAdults, eggs: totalEggs, chicks: totalChicks, penguins: uniquePenguins.size, chipped: chippedCount, label, isFullMonitor, missingBoxes: missingNames, totalLocations: c.locations.length, observers };
}

/** Single-date stats computed on demand from the current cache — the one place this logic lives,
 *  shared by the calendar's precomputed statsCache and any on-the-fly lookups (e.g. tooltips). */
export function computeDateStats(nzDate: string): any | null {
  return mem ? computeDateStatsFromCache(nzDate, mem) : null;
}

function buildDateStats(c: MemCache): void {
  c.obsByNzDate = new Map();
  c.dateStats = new Map();
  // Group observations by NZ date
  for (const o of c.observations) {
    if (o.is_deleted) continue;
    const nz = utcToNzDate(o.observation_time_utc);
    if (!c.obsByNzDate.has(nz)) c.obsByNzDate.set(nz, []);
    c.obsByNzDate.get(nz)!.push(o);
  }
  // Compute stats per date
  for (const [nz] of c.obsByNzDate) {
    c.dateStats.set(nz, computeDateStatsFromCache(nz, c));
  }
  c.observationDates = [...c.obsByNzDate.keys()].sort();
}

function buildIndexes(data: { observations: any[]; scans: any[]; penguins: any[]; chips: any[]; locations: any[]; biometrics: any[]; verifications?: any[]; dayNotes?: any[]; observers?: any[] }, fmExcludedRaw?: string | null): MemCache {
  const cache: MemCache = {
    ...data,
    verifications: data.verifications || [],
    dayNotes: data.dayNotes || [],
    observers: data.observers || [],
    fmExcludedRaw,
    fmExcluded: parseFmExcluded(fmExcludedRaw),
    obsById: new Map(),
    obsByLocation: new Map(),
    scansByObs: new Map(),
    chipByPit: new Map(),
    pengByNum: new Map(),
    locByName: new Map(),
    locById: new Map(),
    chipsByPeng: new Map(),
    chipsByLocId: new Map(),
    chipsByBoxName: new Map(),
    scansByPit: new Map(),
    bioByPeng: new Map(),
    verByObs: new Map(),
    noteByDate: new Map(),
    dayPeopleByDate: new Map(),
    observerById: new Map(),
    dateStats: new Map(),
    observationDates: [],
    obsByNzDate: new Map(),
  };
  for (const v of cache.verifications) cache.verByObs.set(v.observation_id, v);
  // note_date arrives as a DATE string; slice guards against a driver handing back a datetime.
  // A row can now exist for its people alone, so the note may be null while observer/scribe aren't.
  for (const n of cache.dayNotes) {
    const d = String(n.note_date).slice(0, 10);
    if (n.note) cache.noteByDate.set(d, n.note);
    if (n.observer_id || n.scribe_id) cache.dayPeopleByDate.set(d,
      { observer_id: n.observer_id ? Number(n.observer_id) : null, scribe_id: n.scribe_id ? Number(n.scribe_id) : null });
  }
  for (const ob of cache.observers) cache.observerById.set(Number(ob.observer_id), ob.observer_name);
  for (const l of data.locations) { cache.locByName.set(l.location_name, l); cache.locById.set(l.location_id, l); }
  for (const p of data.penguins) cache.pengByNum.set(p.peng_num, p);
  for (const c of data.chips) {
    cache.chipByPit.set(c.pit_id, c);
    if (!cache.chipsByPeng.has(c.peng_num)) cache.chipsByPeng.set(c.peng_num, []);
    cache.chipsByPeng.get(c.peng_num)!.push(c);
    // Same either/or the box query uses, so the two buckets never hold the same chip.
    if (c.location_id) {
      if (!cache.chipsByLocId.has(c.location_id)) cache.chipsByLocId.set(c.location_id, []);
      cache.chipsByLocId.get(c.location_id)!.push(c);
    } else if (c.chip_box) {
      if (!cache.chipsByBoxName.has(c.chip_box)) cache.chipsByBoxName.set(c.chip_box, []);
      cache.chipsByBoxName.get(c.chip_box)!.push(c);
    }
  }
  for (const o of data.observations) {
    cache.obsById.set(o.observation_id, o);
    if (!cache.obsByLocation.has(o.location_id)) cache.obsByLocation.set(o.location_id, []);
    cache.obsByLocation.get(o.location_id)!.push(o);
  }
  for (const s of data.scans) {
    if (!cache.scansByObs.has(s.observation_id)) cache.scansByObs.set(s.observation_id, []);
    cache.scansByObs.get(s.observation_id)!.push(s);
    if (!cache.scansByPit.has(s.pit_id)) cache.scansByPit.set(s.pit_id, []);
    cache.scansByPit.get(s.pit_id)!.push(s);
  }
  for (const b of data.biometrics) {
    if (!cache.bioByPeng.has(b.peng_num)) cache.bioByPeng.set(b.peng_num, []);
    cache.bioByPeng.get(b.peng_num)!.push(b);
  }
  // Compute hasReturned for chick-chipped penguins: scanned >90 days after chip date
  console.time('buildIndexes:hasReturned');
  const CHICK_WINDOW = 90 * 86400000;
  for (const p of data.penguins) {
    if (p.chipped_as_adult) { p.hasReturned = false; continue; }
    const chips = cache.chipsByPeng.get(p.peng_num) || [];
    const chipDate = chips[0]?.chip_date;
    if (!chipDate) { p.hasReturned = false; continue; }
    const chipTime = new Date(chipDate).getTime();
    let returned = false;
    for (const chip of chips) {
      const scans = cache.scansByPit.get(chip.pit_id) || [];
      for (const s of scans) {
        const obs = cache.obsById.get(s.observation_id);
        if (obs && new Date(obs.observation_time_utc).getTime() > chipTime + CHICK_WINDOW) {
          returned = true; break;
        }
      }
      if (returned) break;
    }
    p.hasReturned = returned;
  }
  console.timeEnd('buildIndexes:hasReturned');
  console.time('buildIndexes:dateStats');
  buildDateStats(cache);
  console.timeEnd('buildIndexes:dateStats');
  return cache;
}

// ============ IndexedDB helpers ============

function openDB(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(dbName(), DB_VERSION);
    req.onupgradeneeded = () => {
      const db = req.result;
      for (const store of STORES) {
        if (!db.objectStoreNames.contains(store)) {
          const keyPath = store === 'observations' ? 'observation_id'
            : store === 'scans' ? 'scan_id'
            : store === 'penguins' ? 'peng_num'
            : store === 'chips' ? 'pit_id'
            : store === 'locations' ? 'location_id'
            : store === 'biometrics' ? 'biometric_id'
            : store === 'verifications' ? 'verification_id'
            : store === 'day_notes' ? 'day_note_id'
            : store === 'observers' ? 'observer_id'
            : 'key';
          db.createObjectStore(store, { keyPath });
        }
      }
      for (const store of OBSOLETE_STORES) {
        if (db.objectStoreNames.contains(store)) db.deleteObjectStore(store);
      }
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function putAll(db: IDBDatabase, storeName: StoreNames, rows: any[]): Promise<void> {
  if (rows.length === 0) return;
  return new Promise((resolve, reject) => {
    const tx = db.transaction(storeName, 'readwrite');
    const store = tx.objectStore(storeName);
    for (const row of rows) store.put(row);
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
  });
}

/**
 * put(), but keeping any field the incoming row doesn't carry.
 *
 * An incremental sync sends whole rows and IndexedDB's put replaces the record, so a column
 * absent from that query is deleted from the cache — and since an incremental only re-sends
 * rows whose updated_at moved, it stays deleted. Merging turns that failure from "the field is
 * gone" into "the field is stale", which the next full sync fixes. The server-side check
 * (scripts/check-snapshot-columns.sh) is what stops the omission happening; this is the net
 * under it.
 */
async function mergeAll(db: IDBDatabase, storeName: StoreNames, rows: any[], keyPath: string): Promise<void> {
  if (rows.length === 0) return;
  return new Promise((resolve, reject) => {
    const tx = db.transaction(storeName, 'readwrite');
    const store = tx.objectStore(storeName);
    for (const row of rows) {
      const key = row[keyPath];
      if (key === undefined || key === null) { store.put(row); continue; }
      const req = store.get(key);
      req.onsuccess = () => {
        const existing = req.result;
        store.put(existing && typeof existing === 'object' ? { ...existing, ...row } : row);
      };
    }
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
  });
}

async function getAll(db: IDBDatabase, storeName: StoreNames): Promise<any[]> {
  return new Promise((resolve, reject) => {
    const tx = db.transaction(storeName, 'readonly');
    const req = tx.objectStore(storeName).getAll();
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function clearStores(db: IDBDatabase, stores: StoreNames[]): Promise<void> {
  return new Promise((resolve, reject) => {
    const tx = db.transaction(stores, 'readwrite');
    for (const s of stores) tx.objectStore(s).clear();
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
  });
}

async function getMeta(db: IDBDatabase, key: string): Promise<any> {
  return new Promise((resolve, reject) => {
    const tx = db.transaction('meta', 'readonly');
    const req = tx.objectStore('meta').get(key);
    req.onsuccess = () => resolve(req.result?.value);
    req.onerror = () => reject(req.error);
  });
}

async function setMeta(db: IDBDatabase, key: string, value: any): Promise<void> {
  return new Promise((resolve, reject) => {
    const tx = db.transaction('meta', 'readwrite');
    tx.objectStore('meta').put({ key, value });
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
  });
}

async function clearAll(db: IDBDatabase): Promise<void> {
  for (const store of STORES) {
    if (store === 'meta') continue;
    await new Promise<void>((resolve, reject) => {
      const tx = db.transaction(store, 'readwrite');
      tx.objectStore(store).clear();
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
    });
  }
}

// ============ Sync ============

function applyEditCounts(observations: any[], editCounts: Record<string, number>): any[] {
  if (!editCounts) return observations;
  return observations.map(o => ({ ...o, edit_count: editCounts[o.observation_id] || 0 }));
}

/** Load all data from IndexedDB into memory */
async function loadMemFromIDB(): Promise<void> {
  const db = await openDB();
  console.time('loadMemFromIDB:getAll');
  const [observations, scans, penguins, chips, locations, biometrics, verifications, dayNotes, observers] = await Promise.all([
    getAll(db, 'observations'), getAll(db, 'scans'), getAll(db, 'penguins'),
    getAll(db, 'chips'), getAll(db, 'locations'), getAll(db, 'biometrics'),
    getAll(db, 'verifications'), getAll(db, 'day_notes'), getAll(db, 'observers'),
  ]);
  console.timeEnd('loadMemFromIDB:getAll');
  console.log(`loadMemFromIDB: ${observations.length} obs, ${scans.length} scans, ${penguins.length} penguins, ${locations.length} locations`);
  const fmExcluded = await getMeta(db, 'fm_excluded_boxes');
  console.time('loadMemFromIDB:buildIndexes');
  mem = buildIndexes({ observations, scans, penguins, chips, locations, biometrics, verifications, dayNotes, observers }, fmExcluded);
  console.timeEnd('loadMemFromIDB:buildIndexes');
  notifySubscribers();
}

/** Store snapshot data into both IndexedDB and memory */
async function storeSnapshot(data: any, full: boolean): Promise<void> {
  const db = await openDB();
  const observations = applyEditCounts(data.observations, data.edit_counts);

  if (full) {
    console.time('idb-clear');
    await clearAll(db);
    console.timeEnd('idb-clear');
    console.time('idb-write');
    await Promise.all([
      putAll(db, 'observations', observations),
      putAll(db, 'scans', data.scans),
      putAll(db, 'penguins', data.penguins),
      putAll(db, 'chips', data.chips),
      putAll(db, 'locations', data.locations),
      putAll(db, 'biometrics', data.biometrics),
      putAll(db, 'verifications', data.verifications || []),
      putAll(db, 'day_notes', data.day_notes || []),
      putAll(db, 'observers', data.observers || []),
    ]);
    console.timeEnd('idb-write');
    await setMeta(db, 'snapshot_time', data.snapshot_time);
    await setMeta(db, 'fm_excluded_boxes', data.fm_excluded_boxes ?? null);
    console.time('buildIndexes');
    mem = buildIndexes({
      observations, scans: data.scans, penguins: data.penguins,
      chips: data.chips, locations: data.locations, biometrics: data.biometrics,
      verifications: data.verifications, dayNotes: data.day_notes, observers: data.observers,
    }, data.fm_excluded_boxes);
    console.timeEnd('buildIndexes');
    notifySubscribers();
  } else {
    // Incremental: merge into IDB, removing soft-deleted scans
    const activeScans = (data.scans || []).filter((s: any) => !s.scan_deleted);
    const deletedScanIds = (data.scans || []).filter((s: any) => s.scan_deleted).map((s: any) => s.scan_id);
    if (deletedScanIds.length > 0) {
      const tx = db.transaction('scans', 'readwrite');
      const store = tx.objectStore('scans');
      for (const id of deletedScanIds) store.delete(id);
      await new Promise<void>((resolve, reject) => { tx.oncomplete = () => resolve(); tx.onerror = () => reject(tx.error); });
    }
    // Verifications, day notes and observers ride every snapshot in full, so replace those
    // stores wholesale — this is how a deleted verification, a cleared note or a removed
    // observer leaves the cache.
    await clearStores(db, ['verifications', 'day_notes', 'observers']);
    await Promise.all([
      mergeAll(db, 'observations', observations, 'observation_id'),
      mergeAll(db, 'scans', activeScans, 'scan_id'),
      mergeAll(db, 'penguins', data.penguins, 'peng_num'),
      mergeAll(db, 'chips', data.chips, 'pit_id'),
      mergeAll(db, 'locations', data.locations, 'location_id'),
      mergeAll(db, 'biometrics', data.biometrics, 'biometric_id'),
      putAll(db, 'verifications', data.verifications || []),
      putAll(db, 'day_notes', data.day_notes || []),
      putAll(db, 'observers', data.observers || []),
    ]);
    // Also update edit counts for observations already in IDB
    if (data.edit_counts && Object.keys(data.edit_counts).length > 0 && mem) {
      for (const o of mem.observations) {
        if (data.edit_counts[o.observation_id] !== undefined) {
          o.edit_count = data.edit_counts[o.observation_id];
        }
      }
    }
    await setMeta(db, 'snapshot_time', data.snapshot_time);
    // Persist the latest FM-exclusion config before rebuilding so buildIndexes picks it up.
    if (data.fm_excluded_boxes !== undefined) await setMeta(db, 'fm_excluded_boxes', data.fm_excluded_boxes ?? null);
    // Rebuild memory from IDB (simplest way to merge)
    await loadMemFromIDB();
  }
}

// ---- Cache-integrity hashes: a diagnostic net for the incremental sync ----
// Mirror of WW_HASH_COLS in snapshot_hash.php — SAME columns, SAME order, SAME primary key. The
// server hashes the exact rows it serves; the client hashes its cache the same way, so a stranded
// edit (a row the delta failed to re-send) surfaces as a table-hash mismatch. Keep in lockstep with
// the server; float columns are excluded on both sides (they don't serialise identically).
const HASH_COLS: Record<string, { pk: string; cols: string[] }> = {
  penguins:     { pk: 'peng_num',       cols: ['peng_num','chipped_as_adult','sex','is_dead','death_date','chick_size_code','alert','notes','sex_guess_m','sex_guess_f'] },
  chips:        { pk: 'pit_id',         cols: ['pit_id','peng_num','chip_date','is_active','chip_box','location_id','chipper_id','assistant_id','solo'] },
  observations: { pk: 'observation_id', cols: ['observation_id','location_id','observation_time_utc','adults','eggs','chicks','breeding_status','gate_status','notes','no_scan','fledged_unchipped','failed_eggs','dead_chicks','is_deleted','observer_id'] },
  scans:        { pk: 'scan_id',        cols: ['scan_id','observation_id','pit_id'] },
  locations:    { pk: 'location_id',    cols: ['location_id','location_name','persistent_notes','watched','pit_id','scan_time_utc'] },
  biometrics:   { pk: 'biometric_id',   cols: ['biometric_id','peng_num','observation_id','observation_date','sex','observed_sex','condition_healthy','condition_ticks','is_moulting','disposition_aggressive','disposition_passive','notes','is_deleted'] },
};
const MEM_TABLE: Record<string, (m: any) => any[]> = {
  penguins: m => m.penguins, chips: m => m.chips, observations: m => m.observations,
  scans: m => m.scans, locations: m => m.locations, biometrics: m => m.biometrics,
};

// null/undefined => '' ; everything else => its string form. Must match PHP's (string)($v ?? '').
const canon = (v: any): string => (v === null || v === undefined ? '' : String(v));

async function sha1Hex(s: string): Promise<string> {
  const buf = await crypto.subtle.digest('SHA-1', new TextEncoder().encode(s));
  return Array.from(new Uint8Array(buf)).map(b => b.toString(16).padStart(2, '0')).join('');
}
async function hashTable(rows: any[], spec: { pk: string; cols: string[] }): Promise<string> {
  const lines = rows.map(r => ({ k: canon(r[spec.pk]), line: spec.cols.map(c => canon(r[c])).join('\x1f') }));
  lines.sort((a, b) => (a.k < b.k ? -1 : a.k > b.k ? 1 : 0)); // lexicographic pk, matches PHP ksort SORT_STRING
  return sha1Hex(lines.map(l => l.line).join('\x1e'));
}

// Once per session: if any table's cache hash disagrees with the server's, a row was stranded — full
// re-sync to repair, then report which rows/fields drifted so the offending incremental method can be
// found. Rate-limited to once/session so a serialisation mistake costs at most one extra full sync per
// client, never a storm. Purely diagnostic — any failure is swallowed and never breaks the sync.
let hashChecked = false;
async function runHashCheck(serverHashes: Record<string, string> | undefined): Promise<void> {
  if (hashChecked || !mem || !serverHashes) return;
  hashChecked = true;
  const mismatched: string[] = [];
  for (const t of Object.keys(HASH_COLS)) {
    if (!(t in serverHashes)) continue;
    if ((await hashTable(MEM_TABLE[t](mem), HASH_COLS[t])) !== serverHashes[t]) mismatched.push(t);
  }
  if (!mismatched.length) return;

  const before: Record<string, any[]> = {};
  for (const t of mismatched) before[t] = MEM_TABLE[t](mem).slice();

  // Full re-sync repairs the cache and gives the authoritative rows to diff against.
  await resetDatabase();
  const full = await fetchWithProgress(`/api/snapshot.php?pit=bare&${colonyQS()}&_=${Date.now()}`);
  await storeSnapshot(full, true);

  const diffs: any[] = [];
  for (const t of mismatched) {
    if (diffs.length >= 100) break;
    const spec = HASH_COLS[t];
    const now = new Map((MEM_TABLE[t](mem) as any[]).map(r => [canon(r[spec.pk]), r]));
    const old = new Map(before[t].map(r => [canon(r[spec.pk]), r]));
    for (const [k, o] of old) {
      if (diffs.length >= 100) break;
      const n = now.get(k);
      if (!n) { diffs.push({ table: t, pk: k, gone_on_server: true }); continue; }
      for (const c of spec.cols) if (canon(o[c]) !== canon(n[c])) diffs.push({ table: t, pk: k, col: c, local: canon(o[c]), server: canon(n[c]) });
    }
    for (const k of now.keys()) if (!old.has(k) && diffs.length < 100) diffs.push({ table: t, pk: k, missing_locally: true });
  }

  try {
    await fetch(`/api/hash-report.php?${colonyQS()}`, {
      method: 'POST', headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({ colony_id: getColonyId(), client_version: String(CACHE_VERSION), tables: mismatched, diffs }),
    });
  } catch { /* diagnostic only */ }
}

function fmtSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1048576) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${(bytes / 1048576).toFixed(1)} MB`;
}

async function fetchWithProgress(url: string, onProgress?: (pct: number, label: string) => void): Promise<any> {
  const resp = await fetch(url, { headers: authHeaders() });
  if (!resp.body) return resp.json();
  const total = parseInt(resp.headers.get('Content-Length') || '0', 10);
  const isGzip = resp.headers.get('Content-Type')?.includes('gzip');
  const reader = resp.body.getReader();
  const chunks: Uint8Array[] = [];
  let received = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    chunks.push(value);
    received += value.length;
    const pct = total > 0 ? Math.min(received / total, 0.99) : 0;
    onProgress?.(pct, total > 0 ? `${fmtSize(received)} / ${fmtSize(total)}` : fmtSize(received));
  }
  onProgress?.(1, fmtSize(received));
  console.log(`snapshot: ${fmtSize(received)} downloaded${isGzip ? ' (gzipped)' : ''}`);
  const merged = new Uint8Array(received);
  let offset = 0;
  for (const c of chunks) { merged.set(c, offset); offset += c.length; }
  // Decompress if server sent manual gzip
  if (isGzip) {
    console.time('decompress');
    const ds = new DecompressionStream('gzip');
    const writer = ds.writable.getWriter();
    writer.write(merged);
    writer.close();
    const decompressed = await new Response(ds.readable).text();
    console.timeEnd('decompress');
    console.log(`snapshot: ${fmtSize(decompressed.length)} decompressed`);
    return JSON.parse(decompressed);
  }
  return JSON.parse(new TextDecoder().decode(merged));
}

/**
 * Load already-cached data into memory if a prior snapshot exists, so the UI can
 * paint immediately while syncDatabase() checks the server in the background.
 * Returns true if cache was present (safe to render now), false on first-ever visit
 * or a stale cache format (caller must keep the spinner up for the full download).
 */
export async function primeFromCache(): Promise<boolean> {
  const db = await openDB();
  const cachedVersion = await getMeta(db, 'cache_version');
  if (cachedVersion !== CACHE_VERSION) return false; // stale format → full re-sync needed first
  const lastSync = await getMeta(db, 'snapshot_time');
  if (!lastSync) return false;
  console.time('primeFromCache');
  if (!mem) await loadMemFromIDB();
  console.timeEnd('primeFromCache');
  return true;
}

export async function syncDatabase(onProgress?: (msg: string, pct?: number) => void): Promise<void> {
  const db = await openDB(); // the active colony's own DB
  const cachedVersion = await getMeta(db, 'cache_version');
  if (cachedVersion !== CACHE_VERSION) {
    onProgress?.('Updating data format...');
    await resetDatabase();
    const freshDb = await openDB();
    await setMeta(freshDb, 'cache_version', CACHE_VERSION);
    // Fall through to full download below (lastSync will be null)
  }
  const lastSync = await getMeta(db, 'snapshot_time');

  if (lastSync) {
    // Load from IndexedDB into memory immediately (already up to date from last session)
    if (!mem) await loadMemFromIDB();

    // Then check for changes in background
    const resp = await fetch(`/api/snapshot.php?pit=bare&since=${encodeURIComponent(lastSync)}&${colonyQS()}&_=${Date.now()}`, { headers: authHeaders() });
    const data = await resp.json();
    // Verifications ride every snapshot in full, so "did any arrive" says nothing about change —
    // compare id:updated_at against what's cached. Without this a verdict-only edit (which moves no
    // observation/scan/penguin row) is dropped and the watermark advances past it, stranding the
    // cache on the pre-edit verification forever.
    const verSig = (rows: any[]) => (rows || []).map((r: any) => `${r.verification_id}:${r.updated_at}`).sort().join(',');
    const verChanged = verSig(data.verifications) !== verSig(mem?.verifications || []);
    // Day notes ride in full for the same reason, and need the same comparison: editing a note
    // moves no observation row, so "rows arrived" can't tell an edit from an unchanged resend.
    const noteSig = (rows: any[]) => (rows || []).map((r: any) => `${r.day_note_id}:${r.updated_at}`).sort().join(',');
    const notesChanged = noteSig(data.day_notes) !== noteSig(mem?.dayNotes || []);
    // Observers ride in full too. A rename (or a new account) moves no observation row, so the
    // same id:name comparison is what makes it land — and what stops it looking like a change.
    const obsrSig = (rows: any[]) => (rows || []).map((r: any) => `${r.observer_id}:${r.observer_name}`).sort().join(',');
    const observersChanged = obsrSig(data.observers) !== obsrSig(mem?.observers || []);
    const hasChanges = data.observations?.length > 0 || data.scans?.length > 0 || data.penguins?.length > 0 || data.chips?.length > 0 || data.locations?.length > 0 || data.biometrics?.length > 0 || verChanged || notesChanged || observersChanged;
    if (hasChanges) {
      onProgress?.('Syncing changes...');
      await storeSnapshot(data, false);
    } else {
      await setMeta(db, 'snapshot_time', data.snapshot_time);
      // FM-exclusion config comes back on every snapshot — apply an admin edit even
      // when no observation rows changed (its edit doesn't move the sync watermark).
      if (data.fm_excluded_boxes !== undefined && mem && data.fm_excluded_boxes !== mem.fmExcludedRaw) {
        await setMeta(db, 'fm_excluded_boxes', data.fm_excluded_boxes ?? null);
        mem.fmExcludedRaw = data.fm_excluded_boxes;
        mem.fmExcluded = parseFmExcluded(data.fm_excluded_boxes);
        buildDateStats(mem);
        notifySubscribers();
      }
    }

    // Verify counts match server
    if (data._counts && mem) {
      const local: Record<string, number> = {
        observations: mem.observations.length, scans: mem.scans.length,
        penguins: mem.penguins.length, chips: mem.chips.length,
        locations: mem.locations.length, biometrics: mem.biometrics.length,
      };
      const mismatch = Object.keys(data._counts).some(k => data._counts[k] !== local[k]);
      if (mismatch) {
        console.warn('Sync count mismatch, doing full re-sync', { server: data._counts, local });
        onProgress?.('Data mismatch — reloading...');
        await resetDatabase();
        const full = await fetchWithProgress(`/api/snapshot.php?pit=bare&${colonyQS()}&_=${Date.now()}`, (pct, label) => {
          onProgress?.(`Reloading colony data... ${label}`, pct);
        });
        await storeSnapshot(full, true);
        onProgress?.('Reload complete');
        return;
      }
    }
    // Integrity net: once per session, verify the cache against the server's per-table hashes and
    // repair+report any stranded rows. After a full re-sync above (count mismatch) the cache is
    // already authoritative, so only run this on the incremental path.
    try { await runHashCheck(data._hashes); } catch { /* diagnostic only — never break a sync */ }
    onProgress?.('Sync complete');
  } else {
    onProgress?.('Downloading colony data...', 0);
    console.time('fetch+parse');
    const data = await fetchWithProgress(`/api/snapshot.php?pit=bare&${colonyQS()}&_=${Date.now()}`, (pct, label) => {
      onProgress?.(`Downloading colony data... ${label}`, pct);
    });
    console.timeEnd('fetch+parse');
    onProgress?.('Storing data...', undefined);
    console.time('storeSnapshot');
    await storeSnapshot(data, true);
    console.timeEnd('storeSnapshot');
    onProgress?.(`Loaded ${data.observations.length} observations, ${data.penguins.length} penguins`);
  }

  // Integrity-check dismissals ride alongside the colony sync (separate tiny endpoint).
  await syncDismissals();
}

// Prevent concurrent syncs
let syncing = false;
let syncQueued = false;

export async function triggerSync(): Promise<void> {
  if (syncing) { syncQueued = true; return; }
  syncing = true;
  try {
    await syncDatabase();
  } finally {
    syncing = false;
    if (syncQueued) { syncQueued = false; triggerSync(); }
  }
}

// ============ Polling ============

let pollTimer: ReturnType<typeof setInterval> | null = null;
let lastWatermark = '';

export function startPolling(onChanged: () => void, intervalMs = 30000): void {
  stopPolling();
  pollTimer = setInterval(async () => {
    try {
      const resp = await fetch(`/api/events.php?wm=${encodeURIComponent(lastWatermark)}&_=${Date.now()}`, { headers: authHeaders() });
      const data = await resp.json();
      if (data.wm) lastWatermark = data.wm;
      if (data.changed) {
        await triggerSync();
        onChanged();
      }
    } catch {}
  }, intervalMs);
  // Also fetch initial watermark
  fetch(`/api/events.php?_=${Date.now()}`, { headers: authHeaders() }).then(r => r.json()).then(d => { if (d.wm) lastWatermark = d.wm; }).catch(() => {});
}

export function stopPolling(): void {
  if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
}

// ============ Query functions (pure in-memory) ============

function enrichScan(s: any, c: MemCache) {
  const chip = c.chipByPit.get(s.pit_id);
  const peng = chip ? c.pengByNum.get(chip.peng_num) : null;
  return {
    observation_id: s.observation_id, scan_id: s.scan_id, pit_id: s.pit_id,
    peng_num: chip?.peng_num || null, sex: peng?.sex || null,
    life_stage: peng?.life_stage || null, chipped_as_adult: peng?.chipped_as_adult || 0,
    chick_size_code: peng?.chick_size_code || null, chip_date: chip?.chip_date || null,
    hasReturned: peng?.hasReturned || false,
  };
}

/** Every chip made at one box, off the build-time indexes rather than a scan of the global
 *  chip list. Same either/or the rows were bucketed by: location_id identifies the box across
 *  colonies, the chip_box name covers legacy rows that never got one. */
function chipsAtBox(c: MemCache, locationId: number, boxName: string): any[] {
  const byLoc = c.chipsByLocId.get(locationId);
  const byName = c.chipsByBoxName.get(boxName);
  if (!byName) return byLoc || [];
  return byLoc ? [...byLoc, ...byName] : byName;
}

export function queryBoxDetailSync(boxName: string, includeDeleted?: boolean): any {
  return queryBoxDetailInner(boxName, includeDeleted);
}
export function queryBoxDetail(boxName: string, includeDeleted?: boolean): Promise<any> {
  return Promise.resolve(queryBoxDetailInner(boxName, includeDeleted));
}
function queryBoxDetailInner(boxName: string, includeDeleted?: boolean): any {
  if (!mem) return { location: null, observations: [], all_penguins: [], deleted_count: 0, deleted: [] };
  const c = mem;

  const location = c.locByName.get(boxName) || null;
  if (!location) return { location: null, observations: [], all_penguins: [], deleted_count: 0, deleted: [] };

  const boxObs = (c.obsByLocation.get(location.location_id) || [])
    .sort((a: any, b: any) => b.observation_time_utc.localeCompare(a.observation_time_utc));

  const active = boxObs.filter((o: any) => !o.is_deleted);
  const deleted = boxObs.filter((o: any) => o.is_deleted);

  const observations = active.map((o: any) => ({
    ...o,
    scans: (c.scansByObs.get(o.observation_id) || []).map((s: any) => enrichScan(s, c)),
  }));

  // All penguins seen in this box
  const seenPenguins = new Map<string, any>();
  for (const obs of observations) {
    for (const scan of obs.scans) {
      if (!scan.peng_num) continue;
      if (!seenPenguins.has(scan.peng_num)) {
        seenPenguins.set(scan.peng_num, {
          peng_num: scan.peng_num, pit_id: scan.pit_id, sex: scan.sex,
          life_stage: scan.life_stage, chipped_as_adult: scan.chipped_as_adult,
          chick_size_code: scan.chick_size_code, chip_date: scan.chip_date,
          hasReturned: scan.hasReturned || false,
          scan_count: 0, last_seen: obs.observation_time_utc, is_chipped_here: false,
        });
      }
      seenPenguins.get(scan.peng_num)!.scan_count++;
    }
  }
  // Birds chipped here. Match by location_id (unique across colonies) so a foreign
  // bird chipped in another colony's box of the SAME NAME (e.g. PT's "1" vs NI's "1")
  // doesn't leak in — chips are loaded globally to support visitor scans. Fall back to
  // the box name only for the rare legacy chip with no location_id.
  const boxChips = chipsAtBox(c, location.location_id, boxName);
  for (const chip of boxChips) {
    const peng = c.pengByNum.get(chip.peng_num);
    if (!seenPenguins.has(chip.peng_num)) {
      seenPenguins.set(chip.peng_num, {
        peng_num: chip.peng_num, pit_id: chip.pit_id, sex: peng?.sex || null,
        life_stage: peng?.life_stage || null, chipped_as_adult: peng?.chipped_as_adult || 0,
        chick_size_code: peng?.chick_size_code || null, chip_date: chip.chip_date,
        chip_by: chip.chip_by || null,
        hasReturned: peng?.hasReturned || false,
        scan_count: 0, last_seen: chip.chip_date, is_chipped_here: true,
      });
    } else {
      const sp = seenPenguins.get(chip.peng_num)!;
      sp.is_chipped_here = true;
      sp.chip_by = chip.chip_by || null;
      if (!sp.chip_date) sp.chip_date = chip.chip_date;
    }
  }

  // Every chip event at this box — one entry per CHIP (unlike all_penguins' one per
  // bird), so a rechip done here shows as its own sighting card in the box timeline.
  // is_rechip: any chip that isn't the bird's earliest.
  const chip_events: any[] = [];
  for (const chip of boxChips) {
    if (!chip.chip_date) continue;
    const peng = c.pengByNum.get(chip.peng_num);
    const all = c.chipsByPeng.get(chip.peng_num) || [];
    let first = all[0];
    for (const ch of all) if ((ch.chip_date || '') < (first?.chip_date || '')) first = ch;
    chip_events.push({
      peng_num: chip.peng_num, pit_id: chip.pit_id, sex: peng?.sex || null,
      life_stage: peng?.life_stage || null, chipped_as_adult: peng?.chipped_as_adult || 0,
      chick_size_code: peng?.chick_size_code || null, chip_date: chip.chip_date,
      chip_by: chip.chip_by || null, hasReturned: peng?.hasReturned || false,
      is_rechip: !!first && chip.pit_id !== first.pit_id,
    });
  }

  // Human-verified breeding truth for this box's clutches, keyed by the anchor observation.
  // chicks is already an inline array on each verification (from the snapshot's JSON column).
  // The anchor's date and deleted flag ride along so a verdict the algorithm has since
  // re-segmented away from can still name the day it was recorded against — including when
  // that observation has been deleted out from under it, and so is in neither list below.
  const verifications: any[] = [];
  for (const o of boxObs) {
    const v = c.verByObs.get(o.observation_id);
    if (v) verifications.push({ ...v, anchor_time: o.observation_time_utc, anchor_deleted: !!o.is_deleted });
  }

  return {
    location, observations,
    all_penguins: Array.from(seenPenguins.values()),
    chip_events,
    verifications,
    deleted_count: deleted.length,
    deleted: includeDeleted ? deleted : [],
  };
}

export function queryBirdDetailSync(pengNum: string): any {
  return queryBirdDetailInner(pengNum);
}
export function queryBirdDetail(pengNum: string): Promise<any> {
  return Promise.resolve(queryBirdDetailInner(pengNum));
}
function queryBirdDetailInner(key: string): any {
  if (!mem) return { error: 'not loaded' };
  const c = mem;

  // Keys arrive full from the app's own links, but an old bookmark, a typed route or the
  // nestcheck embed can still hand over the bare on-screen number.
  const pengNum = c.pengByNum.has(key) ? key : fullPengNum(key);
  const penguin = c.pengByNum.get(pengNum);
  if (!penguin) return { error: 'penguin not found' };

  const chips = (c.chipsByPeng.get(pengNum) || []).sort((a: any, b: any) => (a.chip_date || '').localeCompare(b.chip_date || ''));
  const result = { ...penguin, chips };

  const allBio = (c.bioByPeng.get(pengNum) || []).slice().sort((a: any, b: any) => (b.observation_date || '').localeCompare(a.observation_date || ''));
  const biometrics = allBio.filter((b: any) => !b.is_deleted);
  const biometricsDeleted = allBio.filter((b: any) => b.is_deleted);

  // Find this penguin's scans via pit_ids
  const myPitIds = new Set(chips.map((ch: any) => ch.pit_id));
  const myScans: any[] = [];
  for (const pit of myPitIds) {
    const scans = c.scansByPit.get(pit);
    if (scans) myScans.push(...scans);
  }
  // Build sightings
  const sightingMap = new Map<string, any>();
  for (const scan of myScans) {
    const obs = c.obsById.get(scan.observation_id);
    if (!obs || obs.is_deleted) continue;
    const loc = c.locById.get(obs.location_id);
    if (!loc) continue;
    const date = obs.observation_time_utc;
    const box = loc.location_name;
    const key = `${pengNum}|${date}|${box}`;

    if (!sightingMap.has(key)) {
      // Co-scanned birds
      const coScans = (c.scansByObs.get(scan.observation_id) || [])
        .filter((s: any) => !myPitIds.has(s.pit_id))
        .map((s: any) => {
          const ch = c.chipByPit.get(s.pit_id);
          const p = ch ? c.pengByNum.get(ch.peng_num) : null;
          return { peng_num: ch?.peng_num, pit_id: s.pit_id, sex: p?.sex, chipped_as_adult: p?.chipped_as_adult, chip_date: ch?.chip_date, chick_size_code: p?.chick_size_code, hasReturned: p?.hasReturned || false };
        })
        .filter((s: any) => s.peng_num);

      sightingMap.set(key, {
        peng_num: pengNum, date, box, source: 'scan',
        adults: obs.adults || 0, eggs: obs.eggs || 0, chicks: obs.chicks || 0,
        no_scan: obs.no_scan || 0,
        breeding_status: obs.breeding_status, notes: obs.notes, seen_with: coScans,
      });
    }
  }

  // Chip events — skipped when the bird was also scanned in that box on the chip
  // day (the observation sighting already shows it; don't list the day twice).
  const nzDay = (utc: string) => utcToNzDate(utc);
  for (const chip of chips) {
    if (chip.chip_box && chip.chip_date) {
      const key = `${pengNum}|${chip.chip_date}|${chip.chip_box}`;
      const scannedSameDay = Array.from(sightingMap.values()).some(
        (s: any) => s.source === 'scan' && s.box === chip.chip_box && nzDay(s.date) === chip.chip_date);
      if (!sightingMap.has(key) && !scannedSameDay) {
        sightingMap.set(key, {
          peng_num: pengNum, date: chip.chip_date, box: chip.chip_box, source: 'chip',
          adults: 0, eggs: 0, chicks: 0, breeding_status: null,
          // chips are sorted ascending by date, so any chip past the first is a rechip.
          pit_id: chip.pit_id, chip_by: chip.chip_by || null, is_rechip: chip.pit_id !== chips[0]?.pit_id,
          notes: (chip.pit_id !== chips[0]?.pit_id ? 'Rechipped by ' : 'Chipped by ') + (chip.chip_by || '?'), seen_with: [],
        });
      }
    }
  }

  const sightings = Array.from(sightingMap.values()).sort((a, b) => b.date.localeCompare(a.date));

  // Partners
  const partnerMap = new Map<string, any>();
  for (const s of sightings) {
    for (const sw of s.seen_with) {
      if (!partnerMap.has(sw.peng_num)) partnerMap.set(sw.peng_num, { ...sw, sightings: [] });
      const others = s.seen_with.filter((o: any) => o.peng_num !== sw.peng_num);
      partnerMap.get(sw.peng_num)!.sightings.push({
        box: s.box, date: s.date, adults: s.adults, eggs: s.eggs, chicks: s.chicks,
        no_scan: s.no_scan || 0, breeding_status: s.breeding_status, notes: s.notes, also_seen: others,
      });
    }
  }
  const partners = Array.from(partnerMap.values()).sort((a, b) => b.sightings.length - a.sightings.length);

  // Synthetic "no scan" partner: every sighting where unscanned adults were present,
  // grouped as a single stand-in bird (the observer knows what it means).
  const noScanSightings = sightings
    .filter((s: any) => s.source === 'scan' && (s.no_scan || 0) > 0)
    .map((s: any) => ({
      box: s.box, date: s.date, adults: s.adults, eggs: s.eggs, chicks: s.chicks,
      no_scan: s.no_scan, breeding_status: s.breeding_status, notes: s.notes, also_seen: s.seen_with,
    }));
  if (noScanSightings.length > 0) {
    partners.push({ peng_num: null, pit_id: null, sex: null, is_no_scan: true, sightings: noScanSightings });
  }

  // Breeding stats
  const bsMap = new Map<string, any>();
  for (const s of sightings) {
    const d = new Date(isoUtc(s.date));
    const m = d.getUTCMonth() + 1;
    const y = d.getUTCFullYear();
    const seasonYear = m >= 4 ? y : y - 1;
    const season = `${seasonYear}/${String(seasonYear + 1).slice(-2)}`;
    if (!bsMap.has(season)) bsMap.set(season, { season, scans: 0, boxes: [] as string[], max_eggs: 0, max_chicks: 0, statuses: [] as string[] });
    const bs = bsMap.get(season)!;
    bs.scans++;
    if (!bs.boxes.includes(s.box)) bs.boxes.push(s.box);
    if (s.eggs > bs.max_eggs) bs.max_eggs = s.eggs;
    if (s.chicks > bs.max_chicks) bs.max_chicks = s.chicks;
    if (s.breeding_status && !bs.statuses.includes(s.breeding_status)) bs.statuses.push(s.breeding_status);
  }
  // Chicks chipped in this bird's box(es) during each breeding season — shown with
  // a green ring in the breeding history; tooltip carries scan count + seasons seen.
  for (const bs of bsMap.values()) {
    const seasonYear = parseInt(bs.season);
    const seasonFrom = `${seasonYear}-04-01`, seasonTo = `${seasonYear + 1}-04-01`;
    const seen = new Set<string>();
    // Colony-safe box match: the location_ids of this bird's boxes in the viewing
    // colony (locByName is colony-scoped), so a foreign chip in a same-named box in
    // another colony can't match.
    const boxLocIds = new Set((bs.boxes as string[]).map((bn: string) => c.locByName.get(bn)?.location_id).filter(Boolean));
    bs.chipped_chicks = [];
    for (const chip of c.chips) {
      if (!chip.chip_box || !chip.chip_date || chip.peng_num === pengNum) continue;
      if (chip.location_id ? !boxLocIds.has(chip.location_id) : !bs.boxes.includes(chip.chip_box)) continue;
      if (chip.chip_date < seasonFrom || chip.chip_date >= seasonTo) continue;
      if (seen.has(chip.peng_num)) continue;
      const peng = c.pengByNum.get(chip.peng_num);
      if (!peng || peng.chipped_as_adult) continue; // only birds chipped as chicks
      seen.add(chip.peng_num);
      // Scan count + distinct seasons scanned, for the hover tooltip
      let scanCount = 0;
      const seasons = new Set<string>();
      for (const ch2 of (c.chipsByPeng.get(chip.peng_num) || [])) {
        for (const sc of (c.scansByPit.get(ch2.pit_id) || [])) {
          const obs = c.obsById.get(sc.observation_id);
          if (!obs || obs.is_deleted) continue;
          scanCount++;
          const d2 = new Date(isoUtc(obs.observation_time_utc));
          const sy = d2.getUTCMonth() + 1 >= 4 ? d2.getUTCFullYear() : d2.getUTCFullYear() - 1;
          seasons.add(`${sy}/${String(sy + 1).slice(-2)}`);
        }
      }
      bs.chipped_chicks.push({
        peng_num: chip.peng_num, pit_id: chip.pit_id, sex: peng.sex,
        chipped_as_adult: peng.chipped_as_adult, chick_size_code: peng.chick_size_code,
        chip_date: chip.chip_date, hasReturned: peng.hasReturned || false,
        scan_count: scanCount, seasons_scanned: Array.from(seasons).sort(),
      });
    }
  }
  const breedingStats = Array.from(bsMap.values()).sort((a, b) => b.season.localeCompare(a.season));

  return { penguin: result, sightings, biometrics, biometrics_deleted: biometricsDeleted, partners, breeding_stats: breedingStats };
}

/** Get all penguins with active chip info (for search/PenguinMini) */
export function queryAllPenguins(): any[] {
  if (!mem) return [];
  const result: any[] = [];
  for (const p of mem.penguins) {
    const chips = mem.chipsByPeng.get(p.peng_num) || [];
    const active = chips.find((c: any) => c.is_active == 1) || chips[0];
    result.push({
      peng_num: p.peng_num, sex: p.sex, life_stage: p.life_stage,
      chipped_as_adult: p.chipped_as_adult,
      chick_size_code: p.chick_size_code, hasReturned: p.hasReturned || false,
      pit_id: active?.pit_id || null, chip_date: active?.chip_date || null,
      chip_box: active?.chip_box || null,
    });
  }
  return result;
}

/** Biometric sex-guess tally for a penguin, split into male/female-leaning counts.
 *  observed_sex codes: PM/MM (and legacy M) → male; PF/MF (and legacy F) → female; U (unsure) ignored.
 *  Used by PenguinMini to label unsexed birds with their guess history. */
/** Rows for the All-penguins page, assembled entirely from the local cache — the snapshot's
 *  penguins/chips/biometrics are global (not colony-filtered), so this covers every colony.
 *  Same shape as penguins.php?all=1 minus colony_name (the caller derives a label from the
 *  peng_num prefix until the server refresh supplies real names). */
export function computeAllPenguinsRows(): any[] {
  if (!mem) return [];
  const chipsByPeng = new Map<string, any[]>();
  for (const ch of mem.chipByPit.values()) {
    if (!chipsByPeng.has(ch.peng_num)) chipsByPeng.set(ch.peng_num, []);
    chipsByPeng.get(ch.peng_num)!.push(ch);
  }
  const rows: any[] = [];
  for (const p of mem.penguins) {
    const chips = (chipsByPeng.get(p.peng_num) || []).sort((a, b) =>
      String(a.chip_date || '').localeCompare(String(b.chip_date || '')) || String(a.pit_id).localeCompare(String(b.pit_id)));
    const first = chips[0];
    const row: any = {
      peng_num: p.peng_num, sex: p.sex, is_dead: p.is_dead, death_date: p.death_date,
      colony_name: colonyNameForPeng(p.peng_num),
      chipped_as_adult: p.chipped_as_adult, chick_size_code: p.chick_size_code,
      first_chip_date: first?.chip_date ?? null, first_chip_box: first?.chip_box ?? null, first_chip_by: first?.chip_by ?? null,
      pits: chips.map(c => ({ pit_id: c.pit_id, is_active: c.is_active ? 1 : 0 })),
    };
    for (const b of (mem.bioByPeng.get(p.peng_num) || [])) {
      if (b.is_deleted) continue;
      const s = (b.observed_sex || '').toUpperCase();
      if (s === 'PM' || s === 'MM' || s === 'M') row.guess_m = (row.guess_m || 0) + 1;
      else if (s === 'PF' || s === 'MF' || s === 'F') row.guess_f = (row.guess_f || 0) + 1;
      if (first?.chip_date && String(b.observation_date || '').slice(0, 10) === String(first.chip_date).slice(0, 10)) {
        if (b.weight != null && row.chip_weight == null) row.chip_weight = b.weight;
        if (b.flipper_length != null && row.chip_flipper == null) row.chip_flipper = b.flipper_length;
      }
    }
    rows.push(row);
  }
  return rows;
}

/** BC/LC chick pairs chipped together (same box on the same day = one clutch's chipping), each
 *  with its chip-day weight and flipper. Feeds the "Little chick out-measures the big chick" data
 *  check: a Little Chick that weighs (or has a longer flipper than) its Big Chick nest-mate is a
 *  sign the BC/LC codes were swapped or mis-entered. Both metrics ride along so the check's
 *  weight/flipper toggle filters without recomputing. First BC and first LC per clutch (a box is
 *  rarely re-chipped the same day); a clutch missing either code is skipped. */
/**
 * Chipped birds with no weight and/or no flipper length recorded on their chip date. Chipping is
 * the one moment every bird is in the hand, so a gap here is a measurement that will never be
 * taken — unlike a later visit, there is no second chance at it.
 *
 * Derived from computeAllPenguinsRows, which reads both values off the biometric dated the same
 * day as the bird's first chip, so this reflects the data as it stands rather than any import.
 *
 * Every colony, deliberately: penguins, chips and biometrics ride the snapshot un-scoped, and a
 * missing chip-day measurement is a gap in the bird's record wherever it was caught.
 */
export function computeMissingChipMeasures(): any[] {
  return computeAllPenguinsRows()
    .filter(r => r.first_chip_date && (r.chip_weight == null || r.chip_flipper == null))
    .map(r => ({
      peng_num: r.peng_num,
      chip_date: String(r.first_chip_date).slice(0, 10),
      chip_box: r.first_chip_box ?? null,
      chip_by: r.first_chip_by ?? null,
      chip_weight: r.chip_weight ?? null,
      chip_flipper: r.chip_flipper ?? null,
      missing: [r.chip_weight == null ? 'weight' : null, r.chip_flipper == null ? 'flipper' : null].filter(Boolean).join(' + '),
    }))
    .sort((a, b) => b.chip_date.localeCompare(a.chip_date));
}

export function computeChickSizeMismatch(): any[] {
  const nests = new Map<string, { bc?: any; lc?: any }>();
  for (const r of computeAllPenguinsRows()) {
    if (r.chipped_as_adult) continue;
    if (r.chick_size_code !== 'BC' && r.chick_size_code !== 'LC') continue;
    if (!r.first_chip_box || !r.first_chip_date) continue;
    const key = `${r.first_chip_box}|${String(r.first_chip_date).slice(0, 10)}`;
    if (!nests.has(key)) nests.set(key, {});
    const n = nests.get(key)!;
    if (r.chick_size_code === 'BC') { if (!n.bc) n.bc = r; }
    else if (!n.lc) n.lc = r;
  }
  const out: any[] = [];
  for (const [key, n] of nests) {
    if (!n.bc || !n.lc) continue;
    const [box_name, chip_date] = key.split('|');
    out.push({
      box_name, chip_date,
      bc_peng: n.bc.peng_num, lc_peng: n.lc.peng_num,
      bc_weight: n.bc.chip_weight ?? null, lc_weight: n.lc.chip_weight ?? null,
      bc_flipper: n.bc.chip_flipper ?? null, lc_flipper: n.lc.chip_flipper ?? null,
      chipper: n.bc.first_chip_by ?? n.lc.first_chip_by ?? '',
      _href: dayBoxHref(chip_date, box_name),
    });
  }
  return out;
}

/** Distinct boxes each bird has been scanned in, most recent first, keyed by pit_id (any of the
 *  bird's chips maps to the same list) so callers whose peng_nums may be prefix-stripped for
 *  display can still look up. Sightings come from the cached (active-colony) observations. */
export function computeBoxesSeenByPit(): Map<string, string[]> {
  const out = new Map<string, string[]>();
  if (!mem) return out;
  for (const chips of mem.chipsByPeng.values()) {
    const seen: { box: string; t: string }[] = [];
    for (const ch of chips) {
      for (const sc of (mem.scansByPit.get(ch.pit_id) || [])) {
        const obs = mem.obsById.get(sc.observation_id);
        if (!obs || obs.is_deleted) continue;
        const box = mem.locById.get(obs.location_id)?.location_name;
        if (box) seen.push({ box: String(box), t: String(obs.observation_time_utc || '') });
      }
    }
    seen.sort((a, b) => b.t.localeCompare(a.t));
    const boxes: string[] = [];
    for (const s of seen) if (!boxes.includes(s.box)) boxes.push(s.box);
    for (const ch of chips) out.set(ch.pit_id, boxes);
  }
  return out;
}

export function observedSexGuess(pengNum: string | null | undefined): { m: number; f: number } {
  const out = { m: 0, f: 0 };
  if (!mem || !pengNum) return out;
  for (const b of (mem.bioByPeng.get(pengNum) || [])) {
    if (b.is_deleted) continue;
    const s = (b.observed_sex || '').toUpperCase();
    if (s === 'PM' || s === 'MM' || s === 'M') out.m++;
    else if (s === 'PF' || s === 'MF' || s === 'F') out.f++;
  }
  return out;
}

/** Weight of one observed_sex code towards its side: a "Probably" (or a legacy hard M/F) is
 *  worth two, a "Maybe" one, "Unsure" nothing. Two probablies or four maybes reach the
 *  SEX_CONFIRM_SCORE threshold at which the bird is worth sexing for real. */
export function observedSexWeight(code: string | null | undefined): number {
  const s = (code || '').toUpperCase();
  if (s === 'PM' || s === 'PF' || s === 'M' || s === 'F') return 2;
  if (s === 'MM' || s === 'MF') return 1;
  return 0;
}

/** Guesses agreeing on one side must total this before we ask anyone to commit penguins.sex. */
export const SEX_CONFIRM_SCORE = 4;

export type SexGuessRow = { biometric_id: any; observation_id: any; observation_date: string; observed_sex: string; weight: number;
                            /** Observer on the parent observation — the fallback when the day has no note naming who was out. */
                            obs_observer_id: number | null };

/** Weighted read of a bird's field sex guesses, for the "confirm this bird's sex" prompt.
 *  Each side is scored separately — four maybe-males and four maybe-females is not evidence,
 *  it's disagreement — so `side` is only set when exactly one side is over the line.
 *  `rows` is every scoring guess, newest first, for showing the working. */
export function observedSexScore(pengNum: string | null | undefined):
    { m: number; f: number; side: 'M' | 'F' | null; conflicted: boolean; rows: SexGuessRow[] } {
  const out: { m: number; f: number; side: 'M' | 'F' | null; conflicted: boolean; rows: SexGuessRow[] } =
    { m: 0, f: 0, side: null, conflicted: false, rows: [] };
  if (!mem || !pengNum) return out;
  for (const b of (mem.bioByPeng.get(pengNum) || [])) {
    if (b.is_deleted) continue;
    const s = (b.observed_sex || '').toUpperCase();
    const w = observedSexWeight(s);
    if (!w) continue;
    if (s === 'PM' || s === 'MM' || s === 'M') out.m += w;
    else if (s === 'PF' || s === 'MF' || s === 'F') out.f += w;
    else continue;
    out.rows.push({ biometric_id: b.biometric_id, observation_id: b.observation_id,
                    observation_date: b.observation_date, observed_sex: s, weight: w });
  }
  out.rows.sort((a, b) => String(b.observation_date || '').localeCompare(String(a.observation_date || '')));
  const mOver = out.m >= SEX_CONFIRM_SCORE, fOver = out.f >= SEX_CONFIRM_SCORE;
  out.conflicted = mOver && fOver;
  out.side = mOver && !fOver ? 'M' : fOver && !mOver ? 'F' : null;
  return out;
}

// ============ Reports (computed client-side from cache) ============
// These mirror the SQL in reports.php. Breeding season runs Apr–Mar; a date in
// Jan–Mar belongs to the season that started the previous April.

/** Season year (the April-start year) for a YYYY-MM-DD date string. */
function seasonYearFromDate(date: string): number {
  const y = parseInt(date.slice(0, 4), 10);
  const m = parseInt(date.slice(5, 7), 10);
  return m >= 4 ? y : y - 1;
}

/** "2024/25"-style label for a season year. */
function seasonLabel(sy: number): string {
  return `${sy}/${String((sy + 1) % 100).padStart(2, '0')}`;
}

/** Whole days between two YYYY-MM-DD dates (a - b), DST-safe via UTC. */
function dayDiff(a: string, b: string): number {
  return (Date.parse(a) - Date.parse(b)) / 86400000;
}

/** Total eggs across the colony over time, per season. */
export function computeEggArrival(): any[] {
  if (!mem) return [];
  const c = mem;
  const rows = c.observations
    .filter((o: any) => !o.is_deleted)
    .map((o: any) => ({ box: c.locById.get(o.location_id)?.location_name, date: utcToNzDate(o.observation_time_utc), eggs: o.eggs || 0, t: o.observation_time_utc }))
    .filter((r: any) => r.box)
    .sort((a: any, b: any) => a.t.localeCompare(b.t));

  const seasonData: Record<string, Record<string, number>> = {};   // season → box → latest eggs
  const seasonTimeline: Record<string, Record<string, number>> = {}; // season → date → total snapshot
  for (const row of rows) {
    const season = seasonLabel(seasonYearFromDate(row.date));
    if (!seasonData[season]) { seasonData[season] = {}; seasonTimeline[season] = {}; }
    seasonData[season][row.box] = row.eggs;
    seasonTimeline[season][row.date] = Object.values(seasonData[season]).reduce((s, v) => s + v, 0);
  }

  const result: any[] = [];
  for (const season of Object.keys(seasonTimeline)) {
    const sy = parseInt(season.split('/')[0], 10);
    const seasonStart = `${sy}-04-01`;
    const data = Object.keys(seasonTimeline[season]).map(date => ({
      day: Math.floor(dayDiff(date, seasonStart)), eggs: seasonTimeline[season][date], date,
    }));
    result.push({ season, max_eggs: Math.max(...data.map(d => d.eggs)), data });
  }
  result.sort((a, b) => a.season.localeCompare(b.season));
  return result;
}

/** The first egg seen in the colony each breeding season — the earliest egg DATE, and every box
 *  that recorded an egg on that date (there can be more than one). */
export function computeFirstEgg(): { season: string; date: string; boxes: { box: string; obs_time: string }[] }[] {
  if (!mem) return [];
  const c = mem;
  // season → NZ date → box → earliest obs_time on that box+date.
  const bySeason = new Map<string, Map<string, Map<string, string>>>();
  for (const o of c.observations) {
    if (o.is_deleted || (o.eggs || 0) < 1) continue;
    const box = c.locById.get(o.location_id)?.location_name;
    if (!box) continue;
    const nzDate = utcToNzDate(o.observation_time_utc);
    const season = seasonLabel(seasonYearFromDate(nzDate));
    if (!bySeason.has(season)) bySeason.set(season, new Map());
    const dateMap = bySeason.get(season)!;
    if (!dateMap.has(nzDate)) dateMap.set(nzDate, new Map());
    const boxMap = dateMap.get(nzDate)!;
    if (!boxMap.has(box) || o.observation_time_utc < boxMap.get(box)!) boxMap.set(box, o.observation_time_utc);
  }
  const result: { season: string; date: string; boxes: { box: string; obs_time: string }[] }[] = [];
  for (const [season, dateMap] of bySeason) {
    const earliest = Array.from(dateMap.keys()).sort()[0];
    const boxes = Array.from(dateMap.get(earliest)!.entries())
      .map(([box, obs_time]) => ({ box, obs_time }))
      .sort((a, b) => a.box.localeCompare(b.box, undefined, { numeric: true }));
    result.push({ season, date: earliest, boxes });
  }
  return result.sort((a, b) => b.season.localeCompare(a.season)); // newest season first
}

/** Count of distinct adult penguins scanned per season. */
export function computeDistinctAdults(): any[] {
  if (!mem) return [];
  const c = mem;
  const seasons: Record<string, Set<string>> = {};
  for (const s of c.scans) {
    if (s.scan_deleted) continue;
    const chip = c.chipByPit.get(s.pit_id);
    if (!chip || chip.is_active != 1) continue;
    const peng = c.pengByNum.get(chip.peng_num);
    if (!peng) continue;
    const obs = c.obsById.get(s.observation_id);
    if (!obs || obs.is_deleted) continue;
    const nzDate = utcToNzDate(obs.observation_time_utc);
    const isAdult = peng.chipped_as_adult || (chip.chip_date && dayDiff(nzDate, chip.chip_date) > 90);
    if (!isAdult) continue;
    const label = seasonLabel(seasonYearFromDate(nzDate));
    (seasons[label] ||= new Set()).add(s.pit_id);
  }
  return Object.keys(seasons).sort().map(season => ({ season, count: seasons[season].size }));
}

/** Highest total adults present on a single day, per season. */
export function computePeakAdults(): any[] {
  if (!mem) return [];
  const c = mem;
  const boxMax: Record<string, number> = {}; // "season|date|box" → max adults that day
  for (const o of c.observations) {
    if (o.is_deleted) continue;
    const box = c.locById.get(o.location_id)?.location_name;
    if (!box) continue;
    const nzDate = utcToNzDate(o.observation_time_utc);
    const key = `${seasonYearFromDate(nzDate)}|${nzDate}|${box}`;
    const a = o.adults || 0;
    if (boxMax[key] === undefined || a > boxMax[key]) boxMax[key] = a;
  }
  const dayTotal: Record<string, number> = {}; // "season|date" → summed adults
  for (const key of Object.keys(boxMax)) {
    const dk = key.slice(0, key.lastIndexOf('|'));
    dayTotal[dk] = (dayTotal[dk] || 0) + boxMax[key];
  }
  const peak: Record<string, { adults: number; date: string }> = {};
  for (const dk of Object.keys(dayTotal).sort()) { // sorted → earliest day wins ties
    const [sy, date] = dk.split('|');
    if (!peak[sy] || dayTotal[dk] > peak[sy].adults) peak[sy] = { adults: dayTotal[dk], date };
  }
  return Object.keys(peak).sort((a, b) => +a - +b).map(sy => ({
    season: seasonLabel(+sy), adults: peak[sy].adults, date: peak[sy].date,
  }));
}

/** First NZ date of the season in progress. Derived from today's NZ date rather than the
 *  browser's local one, so the window doesn't shift with where the viewer happens to be. */
function currentSeasonStartNz(): string {
  const [y, m, d] = utcToNzDate(new Date().toISOString()).split('-').map(Number);
  const started = m > SEASON_START_MONTH || (m === SEASON_START_MONTH && d >= SEASON_START_DAY);
  const pad2 = (n: number) => String(n).padStart(2, '0');
  return `${started ? y : y - 1}-${pad2(SEASON_START_MONTH)}-${pad2(SEASON_START_DAY)}`;
}

/** This season: boxes where fewer adults were scanned than recorded present.
 *  Per NZ day per box, adults = max recorded across the day's observations,
 *  scanned = max distinct adult scans in any single observation that day.
 *
 *  The window was the last 30 days, which was too short to be much use: a box visited
 *  fortnightly contributes two visits to it, so "3 of 3 visits" and "1 of 1" were the same
 *  claim at different confidence, and a box that missed all through spring read as clean by
 *  December. A season is the unit the monitoring is organised in, so it is the unit here. */
export function computeMissedScans(): any[] {
  if (!mem) return [];
  const c = mem;
  const cutoff = currentSeasonStartNz();

  // Birds chipped in each box within the window. Context for reading a missed scan:
  // adults unscanned in a box with no recent chipping are stronger unchipped candidates,
  // whereas a freshly-chipped box may just have a bird not yet scanned on a later visit.
  const chippedByBox: Record<string, Set<string>> = {};
  for (const ch of c.chips) {
    const cd = (ch.chip_date || '').slice(0, 10);
    if (!cd || cd < cutoff || !ch.peng_num) continue;
    const box = c.locById.get(ch.location_id)?.location_name ?? ch.chip_box;
    if (!box) continue;
    (chippedByBox[box] ||= new Set()).add(ch.peng_num);
  }

  // Distinct adult scans per observation (same adult rule as distinct-adults report)
  const scansByObs: Record<string, Set<string>> = {};
  for (const s of c.scans) {
    if (s.scan_deleted) continue;
    const obs = c.obsById.get(s.observation_id);
    if (!obs || obs.is_deleted) continue;
    const nzDate = utcToNzDate(obs.observation_time_utc);
    if (nzDate < cutoff) continue;
    const chip = c.chipByPit.get(s.pit_id);
    if (!chip) continue;
    const peng = c.pengByNum.get(chip.peng_num);
    if (!peng) continue;
    const isAdult = peng.chipped_as_adult || (chip.chip_date && dayDiff(nzDate, chip.chip_date) > 90);
    if (!isAdult) continue;
    (scansByObs[s.observation_id] ||= new Set()).add(s.pit_id);
  }

  const byKey: Record<string, { date: string; box: string; adults: number; scanned: number }> = {};
  for (const o of c.observations) {
    if (o.is_deleted) continue;
    const date = utcToNzDate(o.observation_time_utc);
    if (date < cutoff) continue;
    const box = c.locById.get(o.location_id)?.location_name;
    if (!box) continue;
    const adults = o.adults || 0;
    if (adults === 0) continue;
    const scanned = scansByObs[o.observation_id]?.size || 0;
    const e = (byKey[`${date}|${box}`] ||= { date, box, adults: 0, scanned: 0 });
    e.adults = Math.max(e.adults, adults);
    e.scanned = Math.max(e.scanned, scanned);
  }

  // Per box: how often was it seen with adults, and how often did scans fall short.
  // Boxes that repeatedly miss are the strongest unchipped-penguin candidates.
  const byBox: Record<string, { box: string; observedDays: number; missed: any[] }> = {};
  for (const r of Object.values(byKey)) {
    const e = (byBox[r.box] ||= { box: r.box, observedDays: 0, missed: [] });
    e.observedDays++;
    if (r.scanned < r.adults) e.missed.push({ date: r.date, adults: r.adults, scanned: r.scanned });
  }
  return Object.values(byBox)
    .filter(b => b.missed.length > 0)
    .map(b => ({ ...b, chipped: chippedByBox[b.box]?.size || 0, missed: b.missed.sort((a: any, x: any) => x.date.localeCompare(a.date)) }))
    .sort((a, b) => b.missed.length - a.missed.length
      || (b.missed.length / b.observedDays) - (a.missed.length / a.observedDays)
      || a.box.localeCompare(b.box, undefined, { numeric: true }));
}

/** Observations whose recorded adult count doesn't match the adults actually accounted
 *  for — scanned adult minis + "no scan" minis — a likely data-entry error. Newest first. */
export function computeMissingNoScans(): { total: number; rows: any[] } {
  if (!mem) return { total: 0, rows: [] };
  const c = mem;

  // Distinct adult scans per observation (same adult rule as the missed-scans report).
  const adultPitsByObs: Record<string, Set<string>> = {};
  for (const s of c.scans) {
    if (s.scan_deleted) continue;
    const obs = c.obsById.get(s.observation_id);
    if (!obs || obs.is_deleted) continue;
    const chip = c.chipByPit.get(s.pit_id);
    if (!chip) continue;
    const peng = c.pengByNum.get(chip.peng_num);
    if (!peng) continue;
    const nzDate = utcToNzDate(obs.observation_time_utc);
    const isAdult = peng.chipped_as_adult || (chip.chip_date && dayDiff(nzDate, chip.chip_date) > 90);
    if (!isAdult) continue;
    (adultPitsByObs[s.observation_id] ||= new Set()).add(s.pit_id);
  }

  const rows: any[] = [];
  for (const o of c.observations) {
    if (o.is_deleted) continue;
    const box = c.locById.get(o.location_id)?.location_name;
    if (!box) continue;
    const adults = o.adults || 0;
    const noScan = o.no_scan || 0;
    const adultScans = adultPitsByObs[o.observation_id]?.size || 0;
    if (adults === adultScans + noScan) continue;
    // Every bird scanned at this observation (chicks included), shaped for <PenguinMini>.
    const scans = (c.scansByObs.get(o.observation_id) || [])
      .filter((s: any) => !s.scan_deleted)
      .map((s: any) => {
        const ch = c.chipByPit.get(s.pit_id);
        const p = ch ? c.pengByNum.get(ch.peng_num) : null;
        return { peng_num: ch?.peng_num, pit_id: s.pit_id, sex: p?.sex, chipped_as_adult: p?.chipped_as_adult,
          chip_date: ch?.chip_date, chick_size_code: p?.chick_size_code, hasReturned: p?.hasReturned || false };
      })
      .filter((s: any) => s.peng_num);
    // Birds chipped in THIS box on THIS day that aren't on the observation. A chipping is a
    // bird in the hand, so it belongs on the visit's record — but nothing links the two, and a
    // chick chipped at the nest is the usual thing missing from a short count.
    const nz = utcToNzDate(o.observation_time_utc);
    const onObs = new Set((c.scansByObs.get(o.observation_id) || []).filter((s: any) => !s.scan_deleted).map((s: any) => s.pit_id));
    const newChips = c.chips
      .filter((ch: any) => ch.location_id === o.location_id && String(ch.chip_date || '').slice(0, 10) === nz && !onObs.has(ch.pit_id))
      .map((ch: any) => {
        const p = c.pengByNum.get(ch.peng_num);
        return { peng_num: ch.peng_num, pit_id: ch.pit_id, sex: p?.sex, chipped_as_adult: p?.chipped_as_adult,
          chip_date: ch.chip_date, chick_size_code: p?.chick_size_code, hasReturned: p?.hasReturned || false };
      });
    // `missing` is negative when more adults are accounted for than were recorded — those
    // rows are a different error, and adding a no-scan would make them worse.
    rows.push({ box, date: nz, time: o.observation_time_utc, adults, adultScans, noScan,
      obsId: o.observation_id, notes: o.notes || '', missing: adults - adultScans - noScan, scans, newChips });
  }
  rows.sort((a, b) => b.time.localeCompare(a.time));
  return { total: rows.length, rows };
}

// ============ Data-integrity checks (computed from the colony cache) ============
// Client-side versions of the admin SQL checks — instant, scoped to the active colony.

// ---- Dismissals: mark a specific integrity-check error as reviewed / valid ----
// Kept separate from the observation cache (its own tiny endpoint, no IndexedDB store,
// no CACHE_VERSION bump / full re-sync). A dismissal holds only while the flagged row's
// content is unchanged (content_hash) — edit any value the error shows and it re-surfaces.
export interface Dismissal {
  error_type: string; error_key: string; content_hash: string;
  reason?: string | null; dismissed_by?: number | null; dismissed_by_name?: string | null; dismissed_at?: string;
}
// Which computed-row fields identify one error instance, per check (stable identity, not content).
const ERROR_KEY_FIELDS: Record<string, string[]> = {
  duplicate_observations: ['obs_date', 'box_name'],
  duplicate_scans: ['obs_date', 'box_name', 'peng_num', 'dup_type'],
  same_gender_conflicts: ['obs_date', 'box_name', 'sex'],
  bird_two_boxes: ['peng_num', 'obs_date'],
  scan_before_chip: ['peng_num', 'obs_date'],
  dead_scanned: ['peng_num'],
  improbable_counts: ['obs_date', 'box_name'],
  future_observations: ['obs_date', 'box_name'],
  retired_tag_scans: ['peng_num', 'pit_id', 'obs_date'],
  chicks_no_scan: ['obs_date', 'box_name'],
};
let dismissals: Dismissal[] = [];
let dismissalIndex = new Map<string, Dismissal>(); // `${type}::${key}` -> dismissal

function fnv1a(str: string): string {
  let h = 0x811c9dc5;
  for (let i = 0; i < str.length; i++) { h ^= str.charCodeAt(i); h = Math.imul(h, 0x01000193); }
  return (h >>> 0).toString(16).padStart(8, '0');
}
/** Fingerprint of a computed error row (all non-internal fields, order-independent). */
function rowContentHash(row: any): string {
  const keys = Object.keys(row).filter(k => !k.startsWith('_')).sort();
  return fnv1a(JSON.stringify(keys.map(k => [k, row[k]])));
}
/** Stable identity string for one error instance. */
function rowErrorKey(errorType: string, row: any): string {
  const fields = ERROR_KEY_FIELDS[errorType] || Object.keys(row).filter(k => !k.startsWith('_')).sort();
  return fields.map(f => String(row[f] ?? '')).join('|');
}
const dmKey = (type: string, key: string) => type + '::' + key;

/** The active dismissal for a row — only if the row's content still matches what was approved. */
function dismissalFor(errorType: string, row: any): Dismissal | undefined {
  const d = dismissalIndex.get(dmKey(errorType, rowErrorKey(errorType, row)));
  return d && d.content_hash === rowContentHash(row) ? d : undefined;
}
/** Split a check's rows into still-active errors and reviewed/dismissed ones. */
export function splitDismissed(errorType: string, rows: any[]): { active: any[]; dismissed: any[] } {
  const active: any[] = [], dismissed: any[] = [];
  for (const r of rows) {
    const d = dismissalFor(errorType, r);
    if (d) dismissed.push({ ...r, _dismissal: d }); else active.push(r);
  }
  return { active, dismissed };
}

/** Pull the colony's dismissals; called at the end of each sync and after any change. */
export async function syncDismissals(): Promise<void> {
  try {
    const resp = await fetch(`/api/integrity.php?${colonyQS()}&_=${Date.now()}`, { headers: authHeaders() });
    if (!resp.ok) return;
    const data = await resp.json();
    dismissals = Array.isArray(data.dismissals) ? data.dismissals : [];
    dismissalIndex = new Map(dismissals.map((d): [string, Dismissal] => [dmKey(d.error_type, d.error_key), d]));
    notifySubscribers();
  } catch { /* offline / not authed — keep existing dismissals */ }
}
export async function dismissError(errorType: string, row: any, reason: string): Promise<void> {
  const resp = await fetch(`/api/integrity.php?action=dismiss&${colonyQS()}`, {
    method: 'POST', headers: { ...authHeaders(), 'Content-Type': 'application/json' },
    body: JSON.stringify({ error_type: errorType, error_key: rowErrorKey(errorType, row), content_hash: rowContentHash(row), reason }),
  });
  if (!resp.ok) throw new Error((await resp.json().catch(() => ({}))).error || 'Could not dismiss');
  await syncDismissals();
}
export async function undismissError(errorType: string, row: any): Promise<void> {
  const resp = await fetch(`/api/integrity.php?action=undismiss&${colonyQS()}`, {
    method: 'POST', headers: { ...authHeaders(), 'Content-Type': 'application/json' },
    body: JSON.stringify({ error_type: errorType, error_key: rowErrorKey(errorType, row) }),
  });
  if (!resp.ok) throw new Error('Could not restore');
  await syncDismissals();
}

const byDateDesc = (a: any, b: any) => b.obs_date.localeCompare(a.obs_date);
/** Deep-link that opens a box and highlights one observation (obsAnchor). */
const obsHref = (box: string, time: string) => `/?box=${encodeURIComponent(box)}&obs=${encodeURIComponent(time)}`;
/** Deep-link to a whole day (all boxes) — for issues that span boxes, not one observation. */
const dayHref = (date: string) => `/?day=${encodeURIComponent(date)}`;
/** Day view scrolled to + highlighting one box (its observation) — for date-level, box-anchored issues. */
const dayBoxHref = (date: string, box: string) => `${dayHref(date)}&box=${encodeURIComponent(box)}`;
/** Like obsHref, but also opens the bird panel — for bird-specific checks. */
const obsBirdHref = (box: string, time: string, peng: string) => `${obsHref(box, time)}&bird=${encodeURIComponent(peng)}`;

/** A penguin scanned at two different boxes on the same NZ day. */
export function computeBirdTwoBoxes(): any[] {
  if (!mem) return [];
  const c = mem;
  const map = new Map<string, { boxes: Set<string>; firstTime: string; firstBox: string }>(); // peng|date
  for (const s of c.scans) {
    if (s.scan_deleted) continue;
    const obs = c.obsById.get(s.observation_id);
    if (!obs || obs.is_deleted) continue;
    const chip = c.chipByPit.get(s.pit_id);
    const box = c.locById.get(obs.location_id)?.location_name;
    if (!chip || !box) continue;
    const t = obs.observation_time_utc;
    const key = chip.peng_num + '|' + utcToNzDate(t);
    let e = map.get(key);
    if (!e) { e = { boxes: new Set(), firstTime: t, firstBox: box }; map.set(key, e); }
    e.boxes.add(box);
    if (t < e.firstTime) { e.firstTime = t; e.firstBox = box; }
  }
  const rows: any[] = [];
  for (const [key, e] of map) {
    if (e.boxes.size < 2) continue;
    const [peng_num, obs_date] = key.split('|');
    rows.push({ peng_num, obs_date, box_count: e.boxes.size, boxes: [...e.boxes].sort(compareBoxNames).join(', '), _href: dayBoxHref(obs_date, e.firstBox) });
  }
  return rows.sort(byDateDesc);
}

/** A scan dated before the bird's chip was fitted. */
export function computeScanBeforeChip(): any[] {
  if (!mem) return [];
  const c = mem;
  const rows: any[] = [];
  for (const s of c.scans) {
    if (s.scan_deleted) continue;
    const chip = c.chipByPit.get(s.pit_id);
    if (!chip || !chip.chip_date) continue;
    const obs = c.obsById.get(s.observation_id);
    if (!obs || obs.is_deleted) continue;
    const obs_date = utcToNzDate(obs.observation_time_utc);
    if (obs_date < chip.chip_date.slice(0, 10)) {
      const box_name = c.locById.get(obs.location_id)?.location_name || '';
      rows.push({ obs_date, chip_date: chip.chip_date.slice(0, 10), box_name, peng_num: chip.peng_num, _href: obsBirdHref(box_name, obs.observation_time_utc, chip.peng_num) });
    }
  }
  return rows.sort(byDateDesc);
}

/** Birds marked dead that were still scanned in the last year. */
export function computeDeadScanned(): any[] {
  if (!mem) return [];
  const c = mem;
  // Parse a UTC datetime string ("YYYY-MM-DD HH:MM:SS") to epoch ms, same normalisation
  // as utcToNzDate. death_date is stamped at 2pm NZ (02:00 UTC) on the death date.
  const toMs = (u: string) => new Date(u.includes('T') || u.includes('Z') ? u : u.replace(' ', 'T') + 'Z').getTime();
  const rows: any[] = [];
  for (const p of c.penguins) {
    if (!p.death_date) continue;
    const deathMs = toMs(p.death_date);
    let count = 0, lastTime = '', lastBox = '';
    for (const ch of (c.chipsByPeng.get(p.peng_num) || [])) {
      for (const s of (c.scansByPit.get(ch.pit_id) || [])) {
        if (s.scan_deleted) continue;
        const obs = c.obsById.get(s.observation_id);
        if (!obs || obs.is_deleted) continue;
        const t = obs.observation_time_utc;
        if (toMs(t) <= deathMs) continue; // only scans AFTER the bird died
        count++;
        if (t > lastTime) { lastTime = t; lastBox = c.locById.get(obs.location_id)?.location_name || ''; }
      }
    }
    if (count > 0) rows.push({ peng_num: p.peng_num, death_date: utcToNzDate(p.death_date), last_scan: utcToNzDate(lastTime), scan_count: count, _href: obsBirdHref(lastBox, lastTime, p.peng_num) });
  }
  return rows.sort((a, b) => b.last_scan.localeCompare(a.last_scan));
}

/** Observations with adults > 2 or eggs + chicks > 2. */
export function computeImprobableCounts(): any[] {
  if (!mem) return [];
  const c = mem;
  const rows: any[] = [];
  for (const o of c.observations) {
    if (o.is_deleted) continue;
    const adults = o.adults || 0, eggs = o.eggs || 0, chicks = o.chicks || 0;
    if (adults <= 2 && eggs + chicks <= 2) continue;
    const box_name = c.locById.get(o.location_id)?.location_name;
    if (!box_name) continue;
    rows.push({ obs_date: utcToNzDate(o.observation_time_utc), box_name, adults, eggs, chicks, _href: obsHref(box_name, o.observation_time_utc) });
  }
  return rows.sort(byDateDesc);
}

/** Observations dated after today (NZ). */
export function computeFutureObservations(): any[] {
  if (!mem) return [];
  const c = mem;
  const today = utcToNzDate(new Date().toISOString());
  const rows: any[] = [];
  for (const o of c.observations) {
    if (o.is_deleted) continue;
    const obs_date = utcToNzDate(o.observation_time_utc);
    if (obs_date > today) {
      const box_name = c.locById.get(o.location_id)?.location_name || '';
      rows.push({ obs_date, box_name, monitor: c.noteByDate.get(obs_date) || '', _href: obsHref(box_name, o.observation_time_utc) });
    }
  }
  return rows.sort(byDateDesc);
}

/** Scans via a previous chip after the bird was rechipped. Judged purely by chip dates —
 *  the bird's newest chip is current, and a scan on any older chip after the newest chip's
 *  date is flagged. Chips with equal dates can't be ordered, so those birds are skipped. */
export function computeRetiredTagScans(): any[] {
  if (!mem) return [];
  const c = mem;
  const rows: any[] = [];
  for (const s of c.scans) {
    if (s.scan_deleted) continue;
    const chip = c.chipByPit.get(s.pit_id);
    if (!chip || !chip.chip_date) continue;
    const chips = c.chipsByPeng.get(chip.peng_num) || [];
    if (chips.length < 2) continue;
    let newest = chips[0];
    for (const ch of chips) if ((ch.chip_date || '') > (newest.chip_date || '')) newest = ch;
    if (!newest.chip_date || newest.pit_id === chip.pit_id) continue; // scanned the current chip
    const rechip_date = newest.chip_date.slice(0, 10);
    if (chip.chip_date.slice(0, 10) >= rechip_date) continue; // same-day chips — order unknowable
    const obs = c.obsById.get(s.observation_id);
    if (!obs || obs.is_deleted) continue;
    const obs_date = utcToNzDate(obs.observation_time_utc);
    if (obs_date > rechip_date) {
      const box_name = c.locById.get(obs.location_id)?.location_name || '';
      rows.push({ obs_date, box_name, peng_num: chip.peng_num, pit_id: chip.pit_id, rechip_date, _href: obsBirdHref(box_name, obs.observation_time_utc, chip.peng_num) });
    }
  }
  return rows.sort(byDateDesc);
}

/** A box where >=2 chicks were chipped in the prior month, then chicks recorded with no scans. */
export function computeChicksNoScan(): any[] {
  if (!mem) return [];
  const c = mem;
  const rows: any[] = [];
  for (const o of c.observations) {
    if (o.is_deleted || (o.chicks || 0) <= 0) continue;
    if ((c.scansByObs.get(o.observation_id) || []).some((s: any) => !s.scan_deleted)) continue; // has a scan
    const loc = c.locById.get(o.location_id);
    if (!loc) continue;
    const obs_date = utcToNzDate(o.observation_time_utc);
    const from = new Date(Date.parse(obs_date) - 31 * 86400000).toISOString().slice(0, 10);
    const chipped = new Set<string>();
    for (const ch of c.chips) {
      if (!ch.chip_date) continue;
      const cd = ch.chip_date.slice(0, 10);
      if (cd >= obs_date || cd < from) continue;
      const inBox = ch.location_id ? ch.location_id === o.location_id : ch.chip_box === loc.location_name;
      if (!inBox) continue;
      const peng = c.pengByNum.get(ch.peng_num);
      if (!peng || peng.chipped_as_adult) continue;
      chipped.add(ch.peng_num);
    }
    if (chipped.size >= 2) rows.push({ obs_date, box_name: loc.location_name, chicks: o.chicks || 0, chicks_chipped: chipped.size, _href: obsHref(loc.location_name, o.observation_time_utc) });
  }
  return rows.sort(byDateDesc);
}

/** More than one non-deleted observation for a box on the same day. */
export function computeDuplicateObservations(): any[] {
  if (!mem) return [];
  const c = mem;
  const map = new Map<string, any[]>(); // box|date -> observations
  for (const o of c.observations) {
    if (o.is_deleted) continue;
    const box = c.locById.get(o.location_id)?.location_name;
    if (!box) continue;
    const key = box + '|' + utcToNzDate(o.observation_time_utc);
    (map.get(key) || map.set(key, []).get(key)!).push(o);
  }
  const rows: any[] = [];
  for (const [key, obs] of map) {
    if (obs.length < 2) continue;
    const [box_name, obs_date] = key.split('|');
    // The two rows came from the same day, so they share a note — it names the day rather than
    // telling the duplicates apart, which the per-observation monitor_filename used to do.
    const monitors = c.noteByDate.get(obs_date) || '';
    const first = obs.reduce((a: any, b: any) => a.observation_time_utc < b.observation_time_utc ? a : b);
    rows.push({ obs_date, box_name, cnt: obs.length, monitors, _href: obsHref(box_name, first.observation_time_utc) });
  }
  return rows.sort(byDateDesc);
}

/** The same bird scanned more than once within one observation (exact, or via multiple chips). */
export function computeDuplicateScans(): any[] {
  if (!mem) return [];
  const c = mem;
  const rows: any[] = [];
  for (const [obsId, allScans] of c.scansByObs) {
    const obs = c.obsById.get(obsId);
    if (!obs || obs.is_deleted) continue;
    const scans = allScans.filter((s: any) => !s.scan_deleted);
    if (scans.length < 2) continue;
    const box_name = c.locById.get(obs.location_id)?.location_name || '';
    const obs_date = utcToNzDate(obs.observation_time_utc);
    const pitCount = new Map<string, number>();
    const pengPits = new Map<string, Set<string>>();
    for (const s of scans) {
      pitCount.set(s.pit_id, (pitCount.get(s.pit_id) || 0) + 1);
      const peng = c.chipByPit.get(s.pit_id)?.peng_num;
      if (peng) (pengPits.get(peng) || pengPits.set(peng, new Set()).get(peng)!).add(s.pit_id);
    }
    const href = obsHref(box_name, obs.observation_time_utc);
    for (const [pit, cnt] of pitCount) if (cnt > 1)
      rows.push({ obs_date, box_name, peng_num: c.chipByPit.get(pit)?.peng_num || pit, cnt, dup_type: 'exact', _href: href });
    for (const [peng, pits] of pengPits) if (pits.size > 1)
      rows.push({ obs_date, box_name, peng_num: peng, cnt: pits.size, dup_type: 'multi-chip', _href: href });
  }
  return rows.sort(byDateDesc);
}

/** Two or more penguins of the same sex scanned at the same box on the same day. */
export function computeSameGenderConflicts(): any[] {
  if (!mem) return [];
  const c = mem;
  const map = new Map<string, { pengs: Set<string>; firstTime: string }>(); // box|date|sex
  for (const s of c.scans) {
    if (s.scan_deleted) continue;
    const obs = c.obsById.get(s.observation_id);
    if (!obs || obs.is_deleted) continue;
    const chip = c.chipByPit.get(s.pit_id);
    const sex = (chip ? c.pengByNum.get(chip.peng_num)?.sex : '')?.toUpperCase();
    if (sex !== 'M' && sex !== 'F') continue;
    const box = c.locById.get(obs.location_id)?.location_name;
    if (!box) continue;
    const t = obs.observation_time_utc;
    const key = box + '|' + utcToNzDate(t) + '|' + sex;
    let e = map.get(key);
    if (!e) { e = { pengs: new Set(), firstTime: t }; map.set(key, e); }
    e.pengs.add(chip!.peng_num);
    if (t < e.firstTime) e.firstTime = t;
  }
  const rows: any[] = [];
  for (const [key, e] of map) {
    if (e.pengs.size < 2) continue;
    const [box_name, obs_date, sex] = key.split('|');
    rows.push({ obs_date, box_name, sex, cnt: e.pengs.size, peng_nums: [...e.pengs].join(', '), _href: obsHref(box_name, e.firstTime) });
  }
  return rows.sort(byDateDesc);
}

/** Chick return rates by size, with return-age points. */
export function computeChickReturn(): any {
  if (!mem) return { by_season: {}, totals: {}, points: [] };
  const c = mem;
  const curSeasonYear = seasonYearFromDate(utcToNzDate(new Date().toISOString()));
  const excludeFromYear = curSeasonYear - 1; // chicks from the last two seasons can't have returned yet

  const bySeason: Record<string, Record<string, { chipped: number; returned: number }>> = {};
  const totals: Record<string, { chipped: number; returned: number; ages: number[] }> = {
    LC: { chipped: 0, returned: 0, ages: [] }, BC: { chipped: 0, returned: 0, ages: [] }, SC: { chipped: 0, returned: 0, ages: [] },
  };
  const points: any[] = [];

  for (const p of c.penguins) {
    if (p.chipped_as_adult) continue;
    const size = p.chick_size_code;
    if (size !== 'LC' && size !== 'BC' && size !== 'SC') continue;
    const chips = c.chipsByPeng.get(p.peng_num) || [];
    const active = chips.find((ch: any) => ch.is_active == 1) || chips[0];
    const chipDate = active?.chip_date;
    if (!chipDate) continue;
    const chipSeasonYear = seasonYearFromDate(chipDate);
    const chipSeason = seasonLabel(chipSeasonYear);

    // First scan in any season after the chip season, across all of this bird's chips.
    let firstReturn: string | null = null;
    for (const ch of chips) {
      for (const s of c.scansByPit.get(ch.pit_id) || []) {
        if (s.scan_deleted) continue;
        const obs = c.obsById.get(s.observation_id);
        if (!obs || obs.is_deleted) continue;
        const nzDate = utcToNzDate(obs.observation_time_utc);
        if (seasonYearFromDate(nzDate) > chipSeasonYear && (firstReturn === null || nzDate < firstReturn)) firstReturn = nzDate;
      }
    }
    const returned = !!firstReturn;

    (bySeason[chipSeason] ||= {})[size] ||= { chipped: 0, returned: 0 };
    bySeason[chipSeason][size].chipped++;
    if (returned) bySeason[chipSeason][size].returned++;

    if (chipSeasonYear < excludeFromYear) {
      totals[size].chipped++;
      if (returned && firstReturn) {
        totals[size].returned++;
        const age = Math.round((dayDiff(firstReturn, chipDate) / 365.25) * 10) / 10;
        totals[size].ages.push(age);
        points.push({ size, age, peng_num: p.peng_num });
      }
    }
  }

  const summary: Record<string, any> = {};
  for (const size of ['LC', 'BC', 'SC']) {
    const ages = totals[size].ages.slice().sort((a, b) => a - b);
    let median: number | null = null;
    if (ages.length > 0) {
      const mid = Math.floor(ages.length / 2);
      median = ages.length % 2 === 0 ? Math.round(((ages[mid - 1] + ages[mid]) / 2) * 10) / 10 : ages[mid];
    }
    summary[size] = {
      chipped: totals[size].chipped,
      returned: totals[size].returned,
      avg_return_age: ages.length > 0 ? Math.round((ages.reduce((s, v) => s + v, 0) / ages.length) * 10) / 10 : null,
      median_return_age: median,
    };
  }
  return { by_season: bySeason, totals: summary, points };
}

/** Get all observation locations */
export function queryAllLocations(): any[] {
  return mem?.locations || [];
}

/**
 * Nest-grid tile data per box: the colour (breeding status) and the 🐧/🥚/🐣 counts.
 *
 * The same thing dashboard.php's handleOverview builds and ships as `box_info`, computed from
 * the local cache so the grid paints its colours with its numbers instead of a round-trip
 * later. Deliberately mirrors that SQL's semantics, including the odd one: the counts always
 * come from the newest live observation, but a newest observation with no breeding_status
 * falls back to the most recent one that has a status, rather than showing none.
 *
 * One deliberate departure: eggs or chicks in the box report as BR whatever pre-breeding
 * assessment was last recorded — the same rule the observation badges run on (App.tsx's
 * displayStatus). Without it a monitor who logged "No" on the visit they entered two eggs
 * leaves the tile grey, so a box that is plainly breeding doesn't read as one.
 */
type BoxInfo = Record<string, { s: string; a: number; e: number; c: number; m?: number }>;
/** Pre-breeding assessments: a monitor's read on a nest that has nothing in it yet. Eggs or
 *  chicks overrule any of them (see the tile rule below); ABN/DCM/IGN don't give way, since
 *  each says something about the nest that outranks its contents. */
const PRE_BREEDING = new Set(['NO', 'UNL', 'POT', 'CON', '']);
// The grid reads this and so does computeOverview, which would otherwise walk every location's
// history (and every biometric) twice per sync. Held against the store version, so a sync — the
// only thing that can change the answer — is what rebuilds it.
let _boxInfoCache: { version: number; info: BoxInfo } | null = null;
export function computeBoxInfo(): BoxInfo {
  if (_boxInfoCache && _boxInfoCache.version === storeVersion) return _boxInfoCache.info;
  const info = computeBoxInfoUncached();
  _boxInfoCache = { version: storeVersion, info };
  return info;
}
function computeBoxInfoUncached(): BoxInfo {
  const cache = mem;
  if (!cache) return {};
  const out: Record<string, { s: string; a: number; e: number; c: number; m?: number }> = {};
  // Moulting birds, indexed both ways a biometric can be tied to a visit: nestcheck records
  // one against the observation, while wildwatch's per-bird form knows only the penguin and
  // the date. Matching on either is what makes the tile work whoever entered it.
  const moultObs = new Set<number>();
  const moultBirdDay = new Set<string>();
  for (const b of cache.biometrics) {
    if (b.is_deleted || Number(b.is_moulting) !== 1) continue;
    if (b.observation_id != null) moultObs.add(b.observation_id);
    const day = String(b.observation_date || '').slice(0, 10);
    if (b.peng_num && day) moultBirdDay.add(`${b.peng_num}|${day}`);
  }
  // A moult is a state of the box's current occupant rather than of the nest, so it colours the
  // tile only while it is the latest thing known — the next observation without one clears it.
  const isMoulting = (o: any) => {
    if (moultObs.has(o.observation_id)) return true;
    if (moultBirdDay.size === 0) return false;
    const day = utcToNzDate(o.observation_time_utc);
    return (cache.scansByObs.get(o.observation_id) || []).some((sc: any) => {
      const chip = cache.chipByPit.get(sc.pit_id);
      return !!chip && moultBirdDay.has(`${chip.peng_num}|${day}`);
    });
  };
  for (const loc of cache.locations) {
    const obs = (cache.obsByLocation.get(loc.location_id) || [])
      .filter((o: any) => !o.is_deleted)
      .sort((a: any, b: any) => String(b.observation_time_utc).localeCompare(String(a.observation_time_utc)));
    if (obs.length === 0) continue;   // never observed — dashboard.php omits it too
    const latest = obs[0];
    const recorded = latest.breeding_status || obs.find((o: any) => o.breeding_status)?.breeding_status || '';
    const eggs = latest.eggs || 0, chicks = latest.chicks || 0;
    out[loc.location_name] = {
      s: (eggs > 0 || chicks > 0) && PRE_BREEDING.has(recorded) ? 'BR' : recorded,
      a: latest.adults || 0,
      e: eggs,
      c: chicks,
      m: isMoulting(latest) ? 1 : 0,
    };
  }
  return out;
}

/**
 * The colony overview — box tiles, season counts, status tallies and the list of dates with
 * activity. Identical in shape and rule to what dashboard.php?view=overview returned, computed
 * from the cache instead of fetched: every input (observations, scans, chips, locations)
 * already rides the snapshot, so the round trip only bought latency. It was the app's heaviest
 * server query and ran on load, on every 30s poll and on every tab resume.
 *
 * Deliberately keeps `box_info`'s moulting flag, which the server answer never had — the grid
 * used to lay the local flag back over the fetched tiles to get it.
 */
export function computeOverview(): any | null {
  if (!mem) return null;
  const c = mem;
  const now = new Date();
  const year = now.getMonth() + 1 >= SEASON_START_MONTH ? now.getFullYear() : now.getFullYear() - 1;
  const pad2 = (n: number) => String(n).padStart(2, '0');
  // Compared against observation_time_utc, which is a 'YYYY-MM-DD HH:MM:SS' string — same
  // string comparison the SQL did against its own '<year>-04-01 00:00:00' bound.
  const seasonStart = `${year}-${pad2(SEASON_START_MONTH)}-${pad2(SEASON_START_DAY)} 00:00:00`;

  let seasonObs = 0;
  const seasonPengs = new Set<string>();
  for (const o of c.observations) {
    if (o.is_deleted) continue;
    if (String(o.observation_time_utc) < seasonStart) continue;
    seasonObs++;
    for (const s of c.scansByObs.get(o.observation_id) || []) {
      const peng = c.chipByPit.get(s.pit_id)?.peng_num;
      if (peng) seasonPengs.add(peng);
    }
  }

  // Tiles carry the same latest-observation-per-box rule the tallies below count, so the two
  // can't disagree — computeBoxInfo is the single source for both.
  const boxInfo = computeBoxInfo();
  const statusCounts: Record<string, number> = { BR: 0, CON: 0, POT: 0, UNL: 0, NO: 0, ABN: 0, DCM: 0 };
  let totalEggs = 0, totalChicks = 0;
  for (const info of Object.values(boxInfo)) {
    // Only the seven statuses the panel has cards for — a box parked on IGN was never a tally.
    if (info.s && info.s in statusCounts) statusCounts[info.s]++;
    totalEggs += info.e || 0;
    totalChicks += info.c || 0;
  }

  return {
    total_boxes: c.locations.length,
    season_observations: seasonObs,
    season_penguins: seasonPengs.size,
    season_start: seasonStart,
    status_counts: statusCounts,
    total_eggs: totalEggs,
    total_chicks: totalChicks,
    box_info: boxInfo,
    // Every date with activity, newest first — as the server sent it, which means chipping
    // days count even with no box observation. locById is colony-scoped, so it is also what
    // keeps another colony's chippings out.
    observation_dates: (() => {
      const days = new Set(c.observationDates);
      for (const ch of c.chips) if (ch.chip_date && c.locById.has(ch.location_id)) days.add(String(ch.chip_date).slice(0, 10));
      return [...days].sort().reverse();
    })(),
  };
}

/** Box tags (tag id + GPS fix) for the map and grid, keyed by box name.
 *
 *  These five columns ride the snapshot's locations rows precisely so they don't need fetching
 *  — snapshot_columns.php calls them out as "the box tag", and nestcheck already builds its
 *  store this way. Same filter boxtags.php applied: a box with neither a tag nor a fix isn't
 *  one. Writes still go through boxtags.php; they come back on the next snapshot. */
export function queryBoxTags(): Record<string, any> {
  if (!mem) return {};
  const out: Record<string, any> = {};
  for (const l of mem.locations) {
    if (!l.pit_id && l.latitude == null) continue;
    out[l.location_name] = {
      BoxID: l.location_name,
      TagNumber: l.pit_id ?? '',
      // The server substituted "now" for a missing scan time; keep that so the panel's
      // formatter never renders an Invalid Date.
      ScanTimeUTC: l.scan_time_utc ? new Date(isoUtc(String(l.scan_time_utc))).toISOString() : new Date().toISOString(),
      Latitude: l.latitude !== null && l.latitude !== undefined ? Number(l.latitude) : 0,
      Longitude: l.longitude !== null && l.longitude !== undefined ? Number(l.longitude) : 0,
      Accuracy: l.accuracy !== null && l.accuracy !== undefined ? Number(l.accuracy) : -1,
    };
  }
  return out;
}

/**
 * Split a search query into alternatives on top-level `|`.
 *
 * A "quoted" alternative keeps its whitespace and punctuation ("BS, MV" matches that exact
 * run of characters); a bare one is trimmed. Both then match as a case-insensitive substring,
 * so quoting is what lets a term contain spaces, commas or a literal `|`.
 */
export function parseSearchTerms(q: string): string[] {
  const out: string[] = [];
  let buf = '', inQuote = false, quoted = false;
  const push = () => {
    const t = quoted ? buf : buf.trim();
    if (t.trim()) out.push(t);
    buf = ''; quoted = false;
  };
  for (const ch of q) {
    if (ch === '"') { inQuote = !inQuote; if (!inQuote) quoted = true; continue; }
    if (ch === '|' && !inQuote) { push(); continue; }
    buf += ch;
  }
  push();
  return out;
}

export interface LocalSearchResults {
  boxes: string[];           // the box you named, exactly — a partial box name is noise
  pengs: any[];              // peng# hit — PenguinMini-shaped
  pits: any[];               // PIT-ID hit
  pengNotes: { peng: any; note: string; from?: string }[];
  obsNotes: any[];           // ObsCard-shaped, plus `box`
  dateNotes: { date: string; note: string }[];
}

/**
 * Everything the unified search can find in the local cache, grouped by kind so the caller can
 * render each in its own way and keep them in precedence order. Dates aren't here: matching
 * human date input ("28 dec", "FM 3 24") belongs with the date search in the UI layer.
 *
 * Boxes match by exact name only. Matching a partial name buries the real answer — "832" is a
 * penguin, not an invitation to list every box starting with 8.
 */
export function searchLocal(query: string, limit = 8): LocalSearchResults {
  const empty: LocalSearchResults = { boxes: [], pengs: [], pits: [], pengNotes: [], obsNotes: [], dateNotes: [] };
  const c = mem;
  const terms = parseSearchTerms(query).map(t => t.toLowerCase()).filter(Boolean);
  if (!c || terms.length === 0) return empty;
  const hits = (v: any) => { const s = String(v ?? '').toLowerCase(); return !!s && terms.some(t => s.includes(t)); };
  const tail = (n: any) => String(n ?? '').replace(/^[A-Z]+/i, '').toLowerCase();
  const fullTerms = new Set(terms.map(t => fullPengNum(t)));

  // "box 2" names box 2 as surely as "2" does, so the word is stripped before matching.
  const boxTerms = terms.map(t => t.replace(/^box[\s#]*/, '')).filter(Boolean);
  const boxes: string[] = [];
  for (const loc of c.locations) {
    if (boxTerms.some(t => String(loc.location_name).toLowerCase() === t)) boxes.push(loc.location_name);
  }
  const byNum = (a: string, b: string) => {
    const na = parseInt(a), nb = parseInt(b);
    return (!isNaN(na) && !isNaN(nb)) ? na - nb : a.localeCompare(b);
  };

  const mini = (p: any) => {
    const chips = c.chipsByPeng.get(p.peng_num) || [];
    const active = chips.find((ch: any) => ch.is_active == 1) || chips[0];
    return {
      peng_num: p.peng_num, sex: p.sex, life_stage: p.life_stage, chipped_as_adult: p.chipped_as_adult,
      chick_size_code: p.chick_size_code, hasReturned: p.hasReturned || false,
      pit_id: active?.pit_id || null, chip_date: active?.chip_date || null, notes: p.notes || null,
    };
  };

  const pengs: any[] = [], pits: any[] = [], pengNotes: { peng: any; note: string; from?: string }[] = [];
  for (const p of c.penguins) {
    // A bird already shown as a peng# hit isn't repeated further down the list.
    if (terms.some(t => tail(p.peng_num) === t || String(p.peng_num).toLowerCase() === t)) { pengs.push(mini(p)); continue; }
    const chips = c.chipsByPeng.get(p.peng_num) || [];
    if (chips.some((ch: any) => hits(ch.pit_id))) { pits.push(mini(p)); continue; }
    if (hits(p.notes)) { pengNotes.push({ peng: mini(p), note: p.notes }); continue; }
    // A measurement's note is about the bird as much as the bird's own note is — "heavy tick
    // burden" is written on the biometric, and searching for it should still find the penguin.
    const bio = (c.bioByPeng.get(p.peng_num) || []).find((b: any) => !b.is_deleted && hits(b.notes));
    if (bio) pengNotes.push({ peng: mini(p), note: bio.notes, from: `biometric ${String(bio.observation_date || '').slice(0, 10)}` });
  }

  const obsNotes = c.observations
    .filter((o: any) => !o.is_deleted && hits(o.notes))
    .sort((a: any, b: any) => String(b.observation_time_utc).localeCompare(String(a.observation_time_utc)))
    .slice(0, limit)
    .map((o: any) => ({
      ...o,
      box: c.locById.get(o.location_id)?.location_name || '?',
      scans: (c.scansByObs.get(o.observation_id) || []).map((s: any) => enrichScan(s, c)),
    }));

  const dateNotes: { date: string; note: string }[] = [];
  for (const [date, note] of c.noteByDate) if (hits(note)) dateNotes.push({ date, note });
  dateNotes.sort((a, b) => b.date.localeCompare(a.date));

  return {
    boxes: boxes.sort(byNum),
    // "7" names this colony's #7 as it reads on screen; a visitor's NI7 still shows, after it.
    pengs: pengs.sort((a, b) => Number(fullTerms.has(b.peng_num)) - Number(fullTerms.has(a.peng_num))).slice(0, limit),
    pits: pits.slice(0, limit),
    pengNotes: pengNotes.slice(0, limit),
    obsNotes,
    dateNotes: dateNotes.slice(0, limit),
  };
}

/** Locations excluded from Full Monitor detection for the active colony. */
export function getFmExcluded(): Set<string> {
  return mem?.fmExcluded || new Set(DEFAULT_FM_EXCLUDED);
}

/** Get precomputed date stats (instant) */
export function getDateStats(): Map<string, any> {
  return mem?.dateStats || new Map();
}

/** Get sorted list of NZ dates with observations */
export function getObservationDates(): string[] {
  return mem?.observationDates || [];
}

/** For boxes not observed on a given NZ date, get their most recent observation before that date */
export function queryCarryForward(nzDate: string, observedBoxes: Set<string>): any[] {
  if (!mem) return [];
  const c = mem;
  const utcCutoff = nzDate + ' 12:00:00';
  const results: any[] = [];
  for (const loc of c.locations) {
    if (observedBoxes.has(loc.location_name)) continue;
    // Find most recent non-deleted observation before this date
    const locObs = (c.obsByLocation.get(loc.location_id) || [])
      .filter((o: any) => !o.is_deleted && o.observation_time_utc < utcCutoff)
      .sort((a: any, b: any) => b.observation_time_utc.localeCompare(a.observation_time_utc));
    if (locObs.length === 0) continue;
    const latest = locObs[0];
    const scans = (c.scansByObs.get(latest.observation_id) || []).map((s: any) => enrichScan(s, c));
    results.push({
      box_name: loc.location_name,
      observation_id: latest.observation_id,
      observation_time_utc: latest.observation_time_utc,
      adults: latest.adults || 0,
      eggs: latest.eggs || 0,
      chicks: latest.chicks || 0,
      breeding_status: latest.breeding_status,
      gate_status: latest.gate_status,
      notes: latest.notes,
      scans,
      carried_forward: true,
    });
  }
  return results;
}

/** For each given box, its most recent non-deleted observation strictly before the NZ date.
 *  Numeric fields (adults/eggs/chicks) come from that latest prior observation. breeding_status
 *  is carried forward from the most recent observation that actually recorded one, which may be
 *  older than the numeric snapshot. Returns box_name -> prev summary (boxes with no prior omitted). */
export function queryPreviousObservations(nzDate: string, boxNames: string[]): Record<string, any> {
  const out: Record<string, any> = {};
  if (!mem) return out;
  const c = mem;
  // NZ date D starts at UTC (D-1)T12:00:00 — fixed +12, same window math as queryDay
  const d = new Date(nzDate + 'T00:00:00Z');
  const utcStart = new Date(d.getTime() - 86400000 + 12 * 3600000).toISOString().replace('T', ' ').slice(0, 19);
  for (const box of boxNames) {
    const loc = c.locByName.get(box);
    if (!loc) continue;
    const locObs = (c.obsByLocation.get(loc.location_id) || [])
      .filter((o: any) => !o.is_deleted && o.observation_time_utc < utcStart)
      .sort((a: any, b: any) => b.observation_time_utc.localeCompare(a.observation_time_utc));
    if (locObs.length === 0) continue;
    const latest = locObs[0];
    const lastStatus = locObs.find((o: any) => o.breeding_status)?.breeding_status ?? null;
    out[box] = {
      adults: latest.adults || 0,
      eggs: latest.eggs || 0,
      chicks: latest.chicks || 0,
      breeding_status: lastStatus,
      observation_time_utc: latest.observation_time_utc,
    };
  }
  return out;
}

/** Boxes whose most recent breeding_status before a date is one of `statuses`. */
function boxesWithLatestStatus(beforeNzDate: string, statuses: Set<string>): Set<string> {
  const out = new Set<string>();
  if (!mem) return out;
  const utcCutoff = beforeNzDate + ' 12:00:00';
  for (const loc of mem.locations) {
    const locObs = (mem.obsByLocation.get(loc.location_id) || [])
      .filter((o: any) => !o.is_deleted && o.breeding_status && o.observation_time_utc < utcCutoff)
      .sort((a: any, b: any) => b.observation_time_utc.localeCompare(a.observation_time_utc));
    if (locObs.length > 0 && statuses.has(locObs[0].breeding_status)) out.add(loc.location_name);
  }
  return out;
}

/** Get boxes whose most recent breeding_status before a date is DCM (for the Hide-DCM filter). */
export function getDcmBoxes(beforeNzDate: string): Set<string> {
  return boxesWithLatestStatus(beforeNzDate, new Set(['DCM']));
}

/** Boxes excused from Full Monitor (latest status DCM or IGN) — mirrors the statsCache calc. */
export function getFmExcusedBoxes(beforeNzDate: string): Set<string> {
  return boxesWithLatestStatus(beforeNzDate, FM_EXCUSED_STATUSES);
}

/** The box's most recent observation before `beforeTimeUtc` that carries an actual
 *  (non-blank, non-IGN) breeding status — used to show the pre-IGN status in read-only views. */
export function prevNonIgnObs(boxName: string, beforeTimeUtc: string): any | null {
  if (!mem || !boxName || !beforeTimeUtc) return null;
  const loc = mem.locByName.get(boxName);
  if (!loc) return null;
  const prior = (mem.obsByLocation.get(loc.location_id) || [])
    .filter((o: any) => !o.is_deleted && o.observation_time_utc < beforeTimeUtc
      && (o.breeding_status || '').trim() && (o.breeding_status || '').trim() !== 'IGN')
    .sort((a: any, b: any) => b.observation_time_utc.localeCompare(a.observation_time_utc));
  return prior[0] || null;
}

/** Boxes with a bird chipped there in the last `days` NZ days where that bird has NOT
 *  been scanned again on a later NZ day (anywhere in the colony). Chips in other
 *  colonies drop out via locById (colony-scoped). Feeds NestCheck's "Chip only"
 *  overview filter through the embed bridge (window.wwChipOnlyBoxes). */
export function queryChipOnlyBoxes(days = 30): string[] {
  if (!mem) return [];
  const c = mem;
  const today = utcToNzDate(new Date().toISOString());
  const cutoff = new Date(Date.parse(today + 'T00:00:00Z') - days * 86400000).toISOString().slice(0, 10);
  // Latest NZ day each bird was scanned
  const lastSeen = new Map<string, string>();
  for (const s of c.scans) {
    if (s.scan_deleted) continue;
    const obs = c.obsById.get(s.observation_id);
    if (!obs || obs.is_deleted) continue;
    const peng = c.chipByPit.get(s.pit_id)?.peng_num;
    if (!peng) continue;
    const day = utcToNzDate(obs.observation_time_utc);
    if ((lastSeen.get(peng) || '') < day) lastSeen.set(peng, day);
  }
  const out = new Set<string>();
  for (const chip of c.chips) {
    if (!chip.chip_date) continue;
    const chipDay = chip.chip_date.slice(0, 10);
    if (chipDay < cutoff) continue;
    if ((lastSeen.get(chip.peng_num) || '') > chipDay) continue; // seen on a later day
    const box = chip.location_id ? c.locById.get(chip.location_id)?.location_name
      : c.locByName.has(chip.chip_box) ? chip.chip_box : null;
    if (box) out.add(box);
  }
  return [...out].sort();
}

/** Get observations for a NZ date (same logic as day.php) */
export function queryDay(date: string): any {
  if (!mem) return { date, day_note: null, observations: [], chippings: [] };
  const c = mem;

  // NZ date D covers UTC range: (D-1)T12:00:00 to DT12:00:00 — fixed +12 (NZST), matching
  // utcToNzDate's bucketing exactly. A wider NZDT-aware window would put late-evening
  // observations (11:00–12:00 UTC) on two consecutive day views.
  const d = new Date(date + 'T00:00:00Z');
  const utcStart = new Date(d.getTime() - 86400000 + 12 * 3600000).toISOString().replace('T', ' ').slice(0, 19);
  const utcEnd = new Date(d.getTime() + 12 * 3600000).toISOString().replace('T', ' ').slice(0, 19);

  const dayObs = c.observations
    .filter(o => !o.is_deleted && o.observation_time_utc >= utcStart && o.observation_time_utc < utcEnd)
    .sort((a: any, b: any) => {
      const locA = c.locById.get(a.location_id);
      const locB = c.locById.get(b.location_id);
      const numA = parseInt(locA?.location_name) || 999;
      const numB = parseInt(locB?.location_name) || 999;
      return numA - numB || a.observation_time_utc.localeCompare(b.observation_time_utc);
    });

  const observations = dayObs.map((o: any) => {
    const loc = c.locById.get(o.location_id);
    return {
      ...o,
      box_name: loc?.location_name || '',
      scans: (c.scansByObs.get(o.observation_id) || []).map((s: any) => enrichScan(s, c)),
    };
  });

  // Chippings on this date — only birds chipped at a location in the current colony
  const chippings = c.chips
    .filter((ch: any) => ch.chip_date === date && c.locById.has(ch.location_id))
    .map((ch: any) => {
      const p = c.pengByNum.get(ch.peng_num);
      return {
        pit_id: ch.pit_id, peng_num: ch.peng_num, chip_box: ch.chip_box, chip_by: ch.chip_by, chip_date: ch.chip_date,
        sex: p?.sex, chipped_as_adult: p?.chipped_as_adult, chick_size_code: p?.chick_size_code, hasReturned: p?.hasReturned || false,
      };
    })
    .sort((a: any, b: any) => (parseInt(a.chip_box) || 999) - (parseInt(b.chip_box) || 999));

  return { date, day_note: c.noteByDate.get(date) || null, observations, chippings };
}

/** The name behind an observations.observer_id, or null when the id is unknown to the cache
 *  (an account removed since, or a row that predates the id being recorded). */
export function getObserverName(id: number | string | null | undefined): string | null {
  if (id === null || id === undefined || id === '') return null;
  return mem?.observerById.get(Number(id)) || null;
}

/** The note filed against an NZ date for the active colony, or null. Reads straight from the
 *  cache so it also answers for a day with no observations (a note written ahead of the visit). */
export function getDayNote(date: string): string | null {
  return mem?.noteByDate.get(date) || null;
}

/** Who was observing and who was recording on an NZ date, for the active colony — user ids. */
export function getDayPeople(date: string): { observer_id: number | null; scribe_id: number | null } {
  return mem?.dayPeopleByDate.get(date) || { observer_id: null, scribe_id: null };
}

/** People who can be picked as the day's observer or scribe. Inactive people are included
 *  and flagged — a day is often written up long after the fact, and whoever worked it may
 *  since have left. Service accounts (the API login) are not people and are left out; getUserName
 *  still resolves them, so an old row referencing one is not left blank. */
export function getUsers(): { id: number; name: string; active: boolean }[] {
  return (mem?.observers || [])
    .filter((o: any) => (o.role || '') !== 'api' && !(o.deleted == 1))
    .map((o: any) => ({
      id: Number(o.observer_id),
      name: [o.observer_name, o.surname].filter(Boolean).join(' '),
      active: o.active == null ? true : o.active == 1,
    // Active first, each group alphabetical — the people you are likely to pick sit at the top,
    // while past volunteers stay reachable below rather than scattered through the list.
    })).sort((a, b) => Number(b.active) - Number(a.active) || a.name.localeCompare(b.name));
}

/** A user's display name from their id — "Britta Steude". Null when the id is unknown. */
export function getUserName(id: number | null | undefined): string | null {
  if (!id) return null;
  const u = (mem?.observers || []).find((o: any) => Number(o.observer_id) === Number(id));
  return u ? [u.observer_name, u.surname].filter(Boolean).join(' ') : null;
}

/** Save the day's note and/or who was out, then fold the result into the cache so the day view
 *  and calendar update without waiting for the next snapshot. Only the fields passed are
 *  touched; '' clears one, and clearing all three deletes the day's row server-side. */
export async function saveDayNote(token: string, date: string,
                                  fields: { note?: string; observer_id?: number | null; scribe_id?: number | null }): Promise<void> {
  const resp = await fetch(`/api/crud.php?action=save_day_note&${colonyQS()}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
    // note is always sent: the endpoint treats a missing note as blank, so omitting it on a
    // people-only edit would wipe the note.
    body: JSON.stringify({ date, colony_id: getColonyId(), note: fields.note ?? (getDayNote(date) || ''),
      ...(fields.observer_id !== undefined ? { observer_id: fields.observer_id } : {}),
      ...(fields.scribe_id !== undefined ? { scribe_id: fields.scribe_id } : {}) }),
  });
  const data = await resp.json();
  if (!resp.ok || data.error) throw new Error(data.error || 'Failed to save note');
  if (mem) {
    const saved = (data.note ?? '') as string;
    if (saved) mem.noteByDate.set(date, saved); else mem.noteByDate.delete(date);
    const obs = (data.observer_id ?? null) as number | null, rec = (data.scribe_id ?? null) as number | null;
    if (obs || rec) mem.dayPeopleByDate.set(date, { observer_id: obs || null, scribe_id: rec || null });
    else mem.dayPeopleByDate.delete(date);
    const stats = mem.dateStats.get(date);
    if (stats) stats.label = saved || null;
    notifySubscribers();
  }
  triggerSync();
}

/**
 * Registered FM/PM dates (the server's date_mappings), cached verbatim in `meta`.
 *
 * These are NOT part of the sync snapshot — the app refetches them each load — so without a
 * cache every date paints bare and gains its "(FM 3)" tag a round-trip later, which reads as
 * a correction rather than a load. The cache is per colony because the whole database is, and
 * it survives a re-sync (clearAll skips `meta`) but not a resetDatabase, where the fetch
 * refills it. Failures are swallowed: this is only ever an optimisation over the network.
 */
export async function getCachedFmDates(): Promise<any[] | null> {
  try {
    const rows = await getMeta(await openDB(), 'fm_dates');
    return Array.isArray(rows) ? rows : null;
  } catch { return null; }
}

export async function setCachedFmDates(rows: any[]): Promise<void> {
  try { await setMeta(await openDB(), 'fm_dates', rows); } catch { /* cache only */ }
}

// ============ Lifecycle ============

export async function isLoaded(): Promise<boolean> {
  try {
    const db = await openDB();
    return !!(await getMeta(db, 'snapshot_time'));
  } catch { return false; }
}

export async function resetDatabase(): Promise<void> {
  mem = null;
  const db = await openDB();
  await clearAll(db);
  await new Promise<void>((resolve, reject) => {
    const tx = db.transaction('meta', 'readwrite');
    tx.objectStore('meta').clear();
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
  });
}
