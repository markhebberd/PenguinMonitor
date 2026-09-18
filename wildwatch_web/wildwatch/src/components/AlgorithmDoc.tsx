/**
 * Plain-English explanation of the breeding-detection (family composition) algorithm,
 * shown on the admin page's Algorithm tab.
 *
 * ── How this stays true ──────────────────────────────────────────────────────────
 * Numbers are never written out here. Every tunable value is imported from
 * src/breedingConstants.ts — the same module the algorithm itself runs on — so
 * changing an offset updates this page automatically. If you find yourself typing a
 * number into this file, add it to breedingConstants.ts instead.
 *
 * The prose is written by hand, and hand-written prose rots. scripts/check-algorithm-doc.mjs
 * fingerprints the source of every function described below; the deploy workflow fails
 * if that fingerprint moved without this explanation being re-reviewed. So: change the
 * algorithm, re-read this page, fix what the change made untrue, then run
 *   node scripts/check-algorithm-doc.mjs --update
 * ─────────────────────────────────────────────────────────────────────────────────
 */
import {
  BREEDING_OFFSETS, SECOND_EGG_LAG_DAYS, COURTSHIP_LEAD_DAYS,
  MAX_OFFSPRING_SHOWN, PAIR_WEIGHTS, IMPLIED_SHARE_CONFIDENCE, PRE_BREEDING_SIGHTINGS_CAP,
  CHICK_START_MIN_GAP_DAYS, CHIPPED_CHICK_START_MIN_GAP_DAYS,
} from '../breedingConstants';
import { ALGORITHM_DOC } from '../algorithmFingerprint';

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

const CSS = `
.algo-doc { max-width: 46em; line-height: 1.6; color: #222; }
.algo-doc h3 { margin: 32px 0 8px; font-size: 17px; color: #1a6b8f; }
.algo-doc h3:first-of-type { margin-top: 16px; }
.algo-doc h4 { margin: 20px 0 4px; font-size: 14px; }
.algo-doc p { margin: 0 0 12px; }
.algo-doc ul { margin: 0 0 12px; padding-left: 22px; }
.algo-doc li { margin-bottom: 6px; }
.algo-doc code { background: #f0f3f5; padding: 1px 5px; border-radius: 3px; font-size: 12.5px; }
.algo-doc .lede { font-size: 15px; color: #444; }
.algo-doc .stamp { background: #f7f9fa; border: 1px solid #dde4e8; border-left: 3px solid #1a6b8f;
  padding: 10px 14px; border-radius: 3px; font-size: 12.5px; color: #555; margin-bottom: 20px; }
.algo-doc .stamp b { color: #222; }
.algo-doc .eg { background: #fbfaf5; border: 1px solid #e6e0cc; border-radius: 3px;
  padding: 10px 14px; margin: 0 0 12px; font-size: 13px; }
.algo-doc .eg-title { font-weight: 600; display: block; margin-bottom: 4px; }
.algo-doc table.stages { border-collapse: collapse; margin: 0 0 12px; font-size: 13px; }
.algo-doc table.stages td, .algo-doc table.stages th { border-bottom: 1px solid #e5e5e5; padding: 5px 16px 5px 0; text-align: left; }
.algo-doc table.stages th { font-weight: 600; color: #666; font-size: 12px; }
.algo-doc .caveats li { margin-bottom: 8px; }
.algo-doc .src { margin-top: 32px; padding-top: 14px; border-top: 1px solid #e5e5e5;
  font-size: 12.5px; color: #666; }
.algo-doc .src code { background: none; padding: 0; color: #444; }
@media print { .algo-doc { max-width: none; } }
`;

/** A day-offset from the laid estimate, written the way the rest of the app writes it. */
const d = (n: number) => `${n} days`;

// The worked example in section 6, taken from one box's real records and computed with the
// same constants and the same arithmetic as the algorithm — so a changed offset moves the
// example too, rather than leaving a plausible-looking sum that is quietly wrong.
const DAY_MS = 86400000;
const EG = { empty: '2023-07-03', egg: '2023-07-17', noChicks: '2023-08-22', chicks: '2023-08-29' };
const egT = (s: string) => new Date(s + 'T00:00:00Z').getTime();
const egDate = (ms: number) => new Date(ms).toLocaleDateString('en-NZ', { day: 'numeric', month: 'long', timeZone: 'UTC' });
const egHalve = (lo: number, hi: number) => ({
  laid: Math.min(hi, lo + Math.ceil((hi - lo) / 2 / DAY_MS) * DAY_MS),
  unc: Math.floor((hi - lo) / 2 / DAY_MS),
});
const egBoxOnly = egHalve(egT(EG.empty), egT(EG.egg));
const egChickLo = egT(EG.noChicks) - BREEDING_OFFSETS.hatch * DAY_MS;
const egChickHi = egT(EG.chicks) - BREEDING_OFFSETS.hatch * DAY_MS;
const egLo = Math.max(egT(EG.empty), egChickLo), egHi = Math.min(egT(EG.egg), egChickHi);
const egBoth = egHalve(egLo, egHi);
const egPlural = (n: number) => `${n} day${n === 1 ? '' : 's'}`;

export default function AlgorithmDoc({ seasonStartMonth, seasonStartDay }: { seasonStartMonth: number; seasonStartDay: number }) {
  const seasonStart = `${seasonStartDay} ${MONTHS[seasonStartMonth - 1]}`;
  return (
    <div className="algo-doc">
      <style>{CSS}</style>

      <p className="lede">
        Nobody records which two penguins bred together. Monitors record what was in a box on a
        day — adults, eggs, chicks — and which microchipped birds were scanned there. Everything
        the app shows about families and breeding attempts is <em>inferred</em> from those two
        records. This page describes exactly how, in the order the code does it.
      </p>

      <h3>1. What it produces</h3>
      <p>
        For every nest box, for every season, the algorithm produces a list of <b>breeding
        attempts</b> (clutches). Each attempt carries: a date window, up to two parents, the
        chicks microchipped in that nest, and a tally of the eggs and chicks that didn’t make it.
      </p>

      <h3>2. What it reads</h3>
      <ul>
        <li><b>Observations</b> — one box, one day: adults, eggs, chicks, breeding status, notes.</li>
        <li><b>Scans</b> — the microchipped birds read inside an observation.</li>
        <li><b>Chipping records</b> — a bird given its chip at this box on a date.</li>
        <li><b>Penguin records</b> — confirmed sex where known, and the observed-sex calls a monitor recorded in the bird’s traits when handling it.</li>
      </ul>

      <h3>3. Seasons</h3>
      <p>
        A season runs from {seasonStart} to the day before the next {seasonStart}, and is labelled by
        the year it starts in — “2026” means 2026/27. Everything below runs per box, per season,
        independently.
      </p>

      <h3>4. Sightings: what counts as “this bird was here”</h3>
      <p>
        A <b>sighting</b> is one bird, at one box, on one day — a bird can only be counted once
        per nest per day, however many times it was recorded there. Two things create one: a scan
        inside an observation, and a chipping record at that box. Both are merged into a single
        chronological list before anything else happens; where a bird was scanned <em>and</em>
        chipped at the box on the same day, the scan wins, because it carries the box contents
        with it.
      </p>
      <p>
        Counting chippings as sightings matters: an adult chipped while guarding chicks was
        plainly attending that nest, even if no observation was filed that day. Without this,
        such a bird would be invisible to parent detection.
      </p>

      <h3>5. Splitting a season into breeding attempts</h3>
      <p>The season’s observations are walked in date order:</p>
      <ul>
        <li>Eggs or chicks appearing in a box that was last seen empty <b>starts</b> an attempt. That observation is the <em>discovery</em>.</li>
        <li>
          Eggs always start one. Chicks with <em>no</em> egg phase only start one if the box went
          unchecked long enough for the eggs to have come and gone unseen:
          {' '}<b>{CHICK_START_MIN_GAP_DAYS} days</b> for downy chicks, and
          {' '}<b>{CHIPPED_CHICK_START_MIN_GAP_DAYS} days</b> where the chicks found are already
          microchipped — a chipped chick is close to fledging, so the whole cycle would have had
          to pass unseen. Chicks turning up in a box checked more recently than that can’t be
          newly hatched; that’s stale or carried-forward data, and it’s ignored.
        </li>
        <li>The gap measured is the box’s real monitoring gap — the last check of any kind, including one in the previous season. A season boundary is not a gap.</li>
        <li>An attempt <b>ends</b> at the first check that finds the box empty (offspring removed or dead), or at an observation marked <code>ABN</code> (abandoned) — whichever comes first.</li>
        <li>After an <code>ABN</code>, doomed eggs often linger in later checks. Curently A new attempt is not detected until an empty check has actually been seen, but this needs further analysis.</li>
        <li>
          The highest egg count and highest chick count seen anywhere in the attempt are kept —
          they drive the offspring tally in step 11. A chick a monitor found <em>dead</em> at a
          check counts alongside the live ones at that same check: a death doesn’t change how
          many live chicks are in the nest, so the two together are how many chicks it had.
        </li>
      </ul>

      <h3>6. Estimating when the eggs were laid</h3>
      <p>
        Laying is never observed, so it’s bracketed and then halved. The laid date is the
        <b> midpoint</b> of the range laying must fall in, and the <code>± n days</code> quoted
        beside it is half that range — which makes it a direct measure of how often the box was
        visited.
      </p>
      <p>
        Four things bound that range, and <em>all</em> of them apply — the range is where they
        agree, not whichever one looks best. Two come from the box:
      </p>
      <ul>
        <li><b>Laying came after</b> the last check that found the box empty.</li>
        <li>
          <b>Laying came before</b> the check that found eggs — less {SECOND_EGG_LAG_DAYS} days
          when {SECOND_EGG_LAG_DAYS}+ were already there, since the second egg comes about
          {' '}{SECOND_EGG_LAG_DAYS} days after the first and it’s the <em>first</em> being dated.
        </li>
      </ul>
      <p>And two come from the hatch, which is usually what sharpens the answer:</p>
      <ul>
        <li><b>Laying was at least {BREEDING_OFFSETS.hatch} days before the first check with chicks</b> — the time from first egg to first chick.</li>
        <li>
          <b>And no more than {BREEDING_OFFSETS.hatch} days before the last check that still had
          no chicks</b>, which is the bound that does the real work: it turns “somewhere in a
          fortnight” into “within a day or two”.
        </li>
      </ul>
      <p>
        Those are the <em>only</em> two the chicks contribute, and both need an actual hatch to
        have been observed — no chicks on one visit, chicks on the next. What a chick’s own stage
        implies is deliberately kept out: that an already-microchipped chick must be near
        fledging, or that a chick still in the nest can’t have fledged, are inferences about the
        bird’s age rather than dated events. They’re sound enough to rule a breeding window
        <em> out</em> — the chipped-chick one does exactly that back in step 5 — but not to date
        one, and an estimate is only as honest as its weakest input.
      </p>
      <div className="eg">
        <span className="eg-title">Example — the hatch doing the work</span>
        A box was empty on {egDate(egT(EG.empty))} and held an egg on {egDate(egT(EG.egg))}. On
        the box alone that puts laying somewhere in a fortnight:
        {' '}<b>{egDate(egBoxOnly.laid)} ± {egPlural(egBoxOnly.unc)}</b>. But the same box still had
        no chicks on {egDate(egT(EG.noChicks))} and had them on {egDate(egT(EG.chicks))}. Stepped
        back {BREEDING_OFFSETS.hatch} days, that puts laying between {egDate(egChickLo)} and
        {' '}{egDate(egChickHi)} — so the only dates satisfying everything run
        {' '}{egDate(egLo)}–{egDate(egHi)}. Laid <b>{egDate(egBoth.laid)}, ± {egPlural(egBoth.unc)}</b>,
        out of exactly the same records.
      </div>
      <p>
        Where the two disagree outright — an empty box fewer than {BREEDING_OFFSETS.hatch} days before a
        chick that needs a whole incubation — one of the records is simply wrong. The box’s own
        contents were seen directly, so the chick evidence is dropped rather than averaged in, and
        the estimate falls back to the laying window alone.
      </p>

      <h3>7. The dates that follow from it</h3>
      <p>
        Every other breeding date is the laid estimate plus a fixed offset — the same offsets the
        nestcheck app’s Next Breeding Dates card uses. These are <b>predictions</b>, never
        observations, and they’re shown only while an attempt is still running.
      </p>
      <table className="stages">
        <thead><tr><th>Stage</th><th>After laying</th><th>Meaning</th></tr></thead>
        <tbody>
          <tr><td>Hatch</td><td>{d(BREEDING_OFFSETS.hatch)}</td><td>Eggs hatch (shown only while the clutch is still eggs)</td></tr>
          <tr><td>Guard ends</td><td>{d(BREEDING_OFFSETS.pg)}</td><td>Parents stop attending the nest; both feed at sea</td></tr>
          <tr><td>Chip window</td><td>{BREEDING_OFFSETS.chip}–{BREEDING_OFFSETS.fledge} days</td><td>Chicks big enough to microchip</td></tr>
          <tr><td>Fledge</td><td>{d(BREEDING_OFFSETS.fledge)}</td><td>Chicks leave; the attempt is over</td></tr>
        </tbody>
      </table>

      <h4>What the status badge says</h4>
      <p>
        The badge on an observation is not simply the status a monitor ticked. What is in the box
        outranks a pre-breeding assessment, because the assessment was a guess about what
        {' '}<em>would</em> happen and the contents are what did: eggs in the box read as
        {' '}<b>Incubation</b> and chicks as <b>Guard</b>, whatever <code>NO</code>,
        {' '}<code>UNL</code>, <code>POT</code>, <code>CON</code> or <code>BR</code> was last
        recorded. A status that names a stage or raises an alarm — <code>PG</code>,
        {' '}<code>MOULT</code>, <code>ABN</code>, <code>DCM</code> — is always shown as stored;
        a monitor writing one of those is stating a fact, not predicting.
      </p>
      <p>
        Two smaller rules follow the same principle. <code>BR</code> on an empty box reads as
        {' '}<code>NO</code>: breeding was expected and the box says it didn’t happen. And an
        observation marked <code>IGN</code> — excused from a full monitor — shows the box’s
        previous real status instead of <code>IGN</code>, so excusing a nest never hides what is
        in it. (The editable card still shows <code>IGN</code>, because there you are looking at
        the record rather than at the nest.)
      </p>

      <h4>When guard actually ends</h4>
      <p>
        Guard is the one predicted date the app also checks against what was seen. The
        prediction — laid + {d(BREEDING_OFFSETS.pg)} — says when the parents <em>should</em> stop
        attending the nest. The status badge on an observation waits for the evidence: it reads
        post-guard from the first check that actually found the chicks with neither parent there.
      </p>
      <p>
        A minimum age is what keeps that honest. No check before laid + {d(BREEDING_OFFSETS.pg)} —
        {' '}{d(BREEDING_OFFSETS.pg - BREEDING_OFFSETS.hatch)} after the estimated hatch — can start
        post-guard, because a nest found without an adult earlier than that caught a parent briefly
        off it, not the end of guard. Nor is post-guard ever given back. Going post-guard is fluid:
        a pair commonly leaves the chicks for a day and one of them is back the next, and the two
        parents don’t stop together. So from that first check the attempt reads post-guard until it
        ends, whatever the checks in between find.
      </p>
      <p>
        The consequence is worth knowing: an attempt where every check after the mark happened to
        catch a parent in the box never reads post-guard. Nothing was seen that says guard ended,
        so the badge doesn’t claim it — the timeline still draws the predicted phase, which is a
        prediction and says so.
      </p>
      <p>
        The same mark decides the timeline’s ⚠ marker. Offspring in the box with no adult present is
        worth flagging before it, and ordinary after it.
      </p>

      <h4>When the chicks leave</h4>
      <p>
        An empty box says nothing by itself: it looks the same after a successful fledging as
        after a predation. What separates them is how far the chicks had got when they were
        <em> last seen</em>. Chicks last recorded at or past the chip window opening (laid +
        {' '}{d(BREEDING_OFFSETS.chip)}) were big enough to microchip, and a chick big enough to
        chip is big enough to go — so the timeline marks that disappearance as a fledging rather
        than a loss. Earlier than that, it stays a loss.
      </p>
      <p>
        The test deliberately reads the last sighting, not the check that found the box empty.
        Those can be months apart, and a nest nobody looked at for half a season should not be
        credited with a fledging no one was near enough to infer. Eggs are never fledged: a
        clutch that lost its eggs at the same check is a loss whatever the dates say.
      </p>
      <p>
        Two things outrank that arithmetic, because they were witnessed rather than inferred.
        A <b>chick microchipped in the nest means the nest fledged</b> — nobody chips a bird
        that then dies in the nest, and the chipping is someone’s record of having held it,
        grown. The window for accepting a chipping is deliberately loose, running from the
        start of the attempt to the predicted fledge or the check that ended it, whichever is
        later: a chipping entered a few days late is a filing detail, not evidence against the
        chick. And a monitor who records unchipped chicks as presumed fledged settles it
        outright — that is a person stating the outcome.
      </p>
      <p>
        This matters more than it sounds, because a chipping is not a nest check. A chick can
        be chipped on a visit that recorded no box contents at all, so the chipping can fall
        after the last observation that saw chicks — which is exactly the case where the dates
        alone would call a successful nest a loss.
      </p>

      <h4>How a season is scored</h4>
      <p>
        Beside each season on a box or a bird sits one word for how it went:
      </p>
      <table className="stages">
        <thead><tr><th>Word</th><th>Means</th></tr></thead>
        <tbody>
          <tr><td>None</td><td>Nothing was laid</td></tr>
          <tr><td>Bred</td><td>The season produced a chick that got away</td></tr>
          <tr><td>Active</td><td>An attempt is still running</td></tr>
          <tr><td>Failed</td><td>Eggs were laid and nothing came of them</td></tr>
        </tbody>
      </table>
      <p>
        “A chick that got away” means what the record <em>establishes</em>, by either of the two
        ways it can say so: a chick microchipped in the nest, or unchipped chicks a monitor
        recorded as presumed fledged. Both count. Counting only the chipped ones would read a
        nest whose brood nobody happened to catch as a failure, which is the opposite of what
        happened.
      </p>
      <p>
        What deliberately does <em>not</em> count is the timeline’s date-based inference — that
        chicks last seen past the chip window must have fledged. That is a fair reading of a
        single marker, but this word is a stronger claim, and it is what breeding success gets
        counted from. Inferring success from an offset would inflate the figures with attempts
        nobody saw finish. So a nest can show a fledgling on its bar and still read “Failed”
        for the season: the bar is reporting what the last check looked like, the word is
        reporting what the record can prove. Chipping the chicks, or ticking the unchipped ones
        as fledged, is what turns one into the other.
      </p>

      <h3>8. Two different windows</h3>
      <p>The algorithm uses two date ranges per attempt, and they are not the same range:</p>
      <ul>
        <li>
          <b>The breeding window</b> — from laying to the check that ended the attempt: the
          offspring gone, or <code>ABN</code>. Not from the discovery, which only records when
          someone happened to look, and not to a predicted fledge, which would close the window
          while the box plainly still held chicks. An attempt still running has no observed end
          yet, so there the predicted fledge (laid + {BREEDING_OFFSETS.fledge} days) stands in —
          it’s what eventually stops a box nobody has revisited reading as “current”. This is the
          window drawn on the box page, and the one chicks are matched against.
        </li>
        <li>
          <b>The attendance window</b> — from {COURTSHIP_LEAD_DAYS} days before the <em>earliest
          plausible</em> laid date (the estimate minus its uncertainty) through the end of guard
          (laid + {BREEDING_OFFSETS.pg} days). This is when a bird in the box means something for
          parentage.
        </li>
      </ul>
      <p>
        Both ends of the attendance window are deliberate. It opens early because courtship and
        nest-building run for about a month before laying, so a male seen only before the eggs
        appeared is still a parent. It closes at the end of guard because after that both parents
        are at sea and the nest is unattended — a bird recorded in the box past then is weak
        evidence.
      </p>
      <p>
        Weak, though, is not worthless. The attendance window <em>ranks</em> candidates rather than
        excluding them (step 9): a bird seen only outside it can still be named a parent when
        nothing better exists, which is the difference between a thin answer and no answer at all
        on a nest that was rarely visited.
      </p>

      <h3>9. Choosing the pair</h3>
      <p>
        <b>Every</b> bird sighted at the box this season is a candidate, excluding birds chipped as
        chicks in this same season (they’re the offspring, not the parents). Where in the attempt a
        bird was seen is weight, not a gate: a bird seen only outside the attendance window can
        still take a slot, but only once no better-evidenced bird exists. That way a poorly watched
        nest still gets its best available answer instead of none.
      </p>
      <h4>Sex</h4>
      <p>
        A bird’s sex is its confirmed <code>M</code>/<code>F</code> if the penguin record has one.
        Otherwise it’s the majority of the biometric observed-sex calls recorded when the bird was
        handled (probable/measured male vs probable/measured female); an even split leaves the bird
        unsexed. A valid pair is <b>one male and one female where at least one of the two sexes is
        confirmed</b> — two guesses never form a pair, however strong each guess is on its own.
      </p>
      <h4>Scoring the candidates</h4>
      <p>
        Every possible pair scores each kind of evidence, times its weight, added up. Two phases
        matter: <b>I&amp;G</b> is incubation and guard, from laying to the end of guard, when the
        parents are genuinely attending the nest; <b>pre-breeding</b> is earlier the same season,
        during courtship and nest-building.
      </p>
      <table className="stages">
        <thead><tr><th>Evidence</th><th>Weight</th><th>Counting</th></tr></thead>
        <tbody>
          <tr><td>Shared sighting in I&amp;G</td><td>×{PAIR_WEIGHTS.sharedIg}</td><td>Both birds recorded together: one observation, or chipped at the box the same day</td></tr>
          <tr><td>Sighting in I&amp;G</td><td>×{PAIR_WEIGHTS.ig}</td><td>Either bird, per sighting</td></tr>
          <tr><td>Shared sighting pre-breeding</td><td>×{PAIR_WEIGHTS.sharedPre}</td><td>Both birds together, before laying — at most {PRE_BREEDING_SIGHTINGS_CAP}</td></tr>
          <tr><td>Sighting pre-breeding</td><td>×{PAIR_WEIGHTS.pre}</td><td>Either bird, since the start of this season — at most {PRE_BREEDING_SIGHTINGS_CAP} per bird</td></tr>
          <tr><td>Bred at this box before</td><td>×{PAIR_WEIGHTS.bred}</td><td>Per bird, from an earlier season</td></tr>
          <tr><td>After guard</td><td>—</td><td>Scores nothing: both parents are at sea. Still makes a bird a candidate</td></tr>
        </tbody>
      </table>
      <p>
        Because the terms add, plenty of weak evidence can outweigh a little strong evidence — a
        pair seen together once but never again can lose to a pair recorded through the whole of
        incubation. Pairs that score exactly alike are separated by whichever was seen closest to
        laying, so the answer never depends on which bird happened to be scanned first.
      </p>
      <p>
        Pre-breeding is capped at {PRE_BREEDING_SIGHTINGS_CAP} sightings per bird for a reason
        worth stating. Courtship visits are many and brief where nest attendance is sparse, so
        uncapped they simply outvote it: a bird seen a dozen times around the box in spring and
        never once during incubation would be named a parent over the bird that actually sat the
        eggs. Past the first visit or two, more courtship says nothing new about who bred — it
        shows interest in a box, where incubation shows a parent.
      </p>
      <h4>Unidentified adults</h4>
      <p>
        An unchipped bird can perfectly well be a parent; it just can’t be named, and the algorithm
        never invents one. But where a monitor recorded an adult they couldn’t identify beside one
        half of a pair already known to breed together, that unnamed bird was most likely the
        partner — so the observation counts as a shared sighting at {IMPLIED_SHARE_CONFIDENCE * 100}%
        of the weight, in whichever phase it fell. A good inference is never worth as much as
        reading the chip. It only applies to a pair that already has a real shared sighting: with no
        established partner, an unnamed adult points at nobody in particular.
      </p>
      <h4>When there’s no pair</h4>
      <p>
        If no valid male–female pair exists, the best-evidenced single candidate becomes a
        <b> lone parent</b>, ranked the same way. Its sex needn’t be known; it fills the male slot
        unless something says female. The family then shows one adult and the offspring. If not one
        candidate bird was sighted, the attempt has no parents at all, which is normal for a box of
        unchipped birds.
      </p>

      <h3>10. Matching chicks to an attempt</h3>
      <p>
        A bird counts as this season’s chick if it was chipped <em>as a chick</em> (not as an adult),
        its chip date falls inside this season, and it was chipped <em>at this box</em>. A returning
        adult that happens to have been chick-chipped years ago is a visitor, not a chick; so is a
        chick chipped in another nest that was merely scanned here — it belongs to a single family,
        the one in its own chip box, and is never pulled into a second nest’s clutch.
      </p>
      <p>
        Each such chick is assigned to the attempt whose breeding window contains its chip date.
        Chipping happens near the end of an attempt, so a chick that lands outside every window
        defaults to the last attempt that has a detected pair.
      </p>

      <h3>11. What didn’t make it</h3>
      <p>Offspring are counted at their <b>final</b> life stage, once the attempt has closed:</p>
      <ul>
        <li><b>Failed eggs</b> = highest egg count − highest chick count. Eggs that never became a chick.</li>
        <li><b>Unchipped chicks</b> = highest chick count − chicks actually chipped in the nest.</li>
        <li>Of those unchipped chicks, any a monitor explicitly logged as <em>presumed fledged</em> are shown as fledged. The rest are assumed to have died.</li>
        <li>All of these are capped at {MAX_OFFSPRING_SHOWN} per attempt. A little penguin clutch is two eggs; anything beyond the cap is a data-entry error, and the cap stops one bad number drawing a nest full of chicks.</li>
      </ul>
      <p>
        While an attempt is still running none of this is marked as failure — an egg in the nest is
        just an egg in the nest. The failure marks appear only once the window has closed. The
        exception is a chick a monitor found dead and recorded: that is settled the moment it is
        written down, and is marked as a failure straight away.
      </p>

      <h3>12. Human verification of calculated breeding data</h3>
      <p>
        Each breeding attempt carries a tick with two halves — the <b>adults</b> and the
        <b> offspring</b> — that a monitor can accept or reject. Accepting snapshots the detected
        answer as ground truth, stored separately from anything the algorithm computes.
      </p>
      <ul>
        <li><b>Grey</b> — not yet reviewed.</li>
        <li><b>Green</b> — reviewed, accepted, and the algorithm still produces the same answer.</li>
        <li><b>Red</b> — rejected, <em>or</em> accepted-then-drifted: the code now says something different from what a human confirmed. That’s the case worth looking at, and it’s why the drift is surfaced rather than silently resolved.</li>
      </ul>
      <p>
        The offspring half can only be verified once the attempt’s window has closed — until then
        there is nothing final to confirm. Verified truth is never overwritten by the algorithm.
      </p>

      <h3>13. What it cannot do</h3>
      <ul className="caveats">
        <li><b>Unchipped adults are invisible.</b> A pair that was never scanned cannot be detected, no matter how faithfully the box was monitored.</li>
        <li><b>Two guessed sexes never pair.</b> Deliberate: it would rather show one parent than invent a pair from two guesses.</li>
        <li><b>Laid dates are only as good as the visit interval.</b> A three-week gap between checks produces a ±10-day estimate, and every predicted stage inherits that error.</li>
        <li><b>A visitor can take a slot.</b> Where the real parent was never scanned, a bird that merely passed through during the attendance window can win by default.</li>
        <li><b>Stale or carried-forward entries distort attempts.</b> Duplicate observations and impossible counts can create or hide an attempt; the Data validation tab exists to catch them.</li>
        <li><b>Chicks in a well-monitored box with no eggs ever recorded show no attempt at all.</b> The gap test in step 5 reads them as carried-forward data. If a real brood was simply never recorded at the egg stage, the fix is at the source — the eggs belong in the record.</li>
        <li><b>It is an inference, not a record.</b> Where it’s wrong, the verification tick is the fix — human truth wins and is stored as such.</li>
      </ul>

      <div className="src">
        The code this describes: <code>segmentClutches</code> and <code>estimateLaidFrom</code> (steps 5–6),
        {' '}<code>postGuardRanges</code>, <code>looksFledged</code> and <code>predictedDates</code> (step 7) in
        {' '}<code>src/breeding.ts</code>; <code>displayStatus</code>, <code>isPostGuard</code> and
        {' '}<code>displayStatusOrPrev</code> (the badge, step 7), <code>seasonOutcome</code> (step 7),
        {' '}<code>boxSightings</code> (step 4),
        {' '}<code>detectClutchPair</code> (step 9), <code>computeBoxFamilies</code> (steps 3, 10–11) and
        {' '}<code>computeClutchVerify</code> (step 12) in <code>src/App.tsx</code>; all numbers in
        {' '}<code>src/breedingConstants.ts</code>. Both the box breeding overview and the bird panel’s
        family view consume the same functions, so what you read here is what both screens show —
        and so does nestcheck’s Next breeding dates card, which the server answers by running
        {' '}<code>src/breeding.ts</code> under node rather than keeping a second copy of the rules.
      </div>
      {/* Maintenance detail, not something a monitor needs — last thing on the page. */}
      <div className="stamp">
        {ALGORITHM_DOC.reviewed
          ? <>This explanation was last checked against the code on <b>{ALGORITHM_DOC.reviewed}</b> (algorithm fingerprint <code>{ALGORITHM_DOC.fingerprint}</code>).</>
          : <>This explanation has not yet been stamped against the code.</>}
          {' '}The deploy checks the two still match: if the algorithm changes and this page
          isn’t re-reviewed, the deploy fails. Every number above is read live from the code.
      </div>
    </div>
  );
}
