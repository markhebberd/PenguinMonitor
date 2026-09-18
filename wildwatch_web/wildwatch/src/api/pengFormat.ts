/**
 * Every request to our own /api/ says which peng# form it speaks.
 *
 * The API used to strip the viewing colony's prefix from every peng# it sent and re-add it on
 * every write; it now always speaks the full stored form ("PT1039") and the client strips only
 * for display. A bundle built before that switch would store, compare and send bare numbers, so
 * the server refuses peng-carrying requests that don't carry `X-Peng-Format: full` with a 426 —
 * and this module is both halves of that handshake: the header on the way out, and a one-time
 * reload on a 426 so a stale tab picks up the current bundle.
 *
 * Done as a guarded patch of window.fetch rather than a wrapper because ~80 call sites across
 * the app talk to /api/ directly; a wrapper would have to be threaded through every one of
 * them, and the one that got missed is exactly the bug this contract exists to prevent. Only
 * same-origin /api/ URLs are touched — anything else goes out exactly as it was asked for.
 *
 * Imported first thing in main.tsx so it is in place before any module makes a request.
 */

const HEADER = 'X-Peng-Format';
const FORMAT = 'full';
// When the last 426 reload happened. A reload that comes straight back to a 426 means the
// server and the freshly loaded bundle still disagree (a deploy half-way out, a cached
// index.html) — reloading again would just spin, so it waits this long before trying again.
const RELOAD_KEY = 'ww_peng_format_reload';
const RELOAD_COOLDOWN_MS = 5 * 60 * 1000;

function isOwnApi(url: string): boolean {
  try {
    const u = new URL(url, window.location.href);
    return u.origin === window.location.origin && u.pathname.startsWith('/api/');
  } catch { return false; }
}

function reloadOnce(): void {
  let last = 0;
  try { last = Number(sessionStorage.getItem(RELOAD_KEY)) || 0; } catch { /* storage blocked — still reload once */ }
  if (Date.now() - last < RELOAD_COOLDOWN_MS) return;
  try { sessionStorage.setItem(RELOAD_KEY, String(Date.now())); } catch { /* see above */ }
  window.location.reload();
}

const nativeFetch = window.fetch.bind(window);

window.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
  if (!isOwnApi(url)) return nativeFetch(input, init);
  // Built from whatever headers the caller gave — a plain object, a Headers, an array, or those
  // of a Request passed as input — so the caller's Authorization/Content-Type survive intact.
  const headers = new Headers(init?.headers ?? (input instanceof Request ? input.headers : undefined));
  headers.set(HEADER, FORMAT);
  const resp = await nativeFetch(input, { ...init, headers });
  if (resp.status === 426) reloadOnce();
  return resp;
};

export {};
