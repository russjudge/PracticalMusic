// Port of PracticalMusic.Core/Timing/TimingEngine.cs: pure functions that map between playback time,
// pages and marker positions. Keep the two in step - the apps and the website must turn pages and
// highlight lines identically for the same book.

/** Page offset (0-based within the song) that should be shown at timeMs. */
export function pageOffsetAt(recording, timeMs) {
  let page = 0;
  let bestTime = -Infinity;
  for (const t of recording.pageTimings || []) {
    if (t.pageOffset === 0) continue;
    if (t.timeMs <= timeMs && t.timeMs >= bestTime) {
      bestTime = t.timeMs;
      page = Math.max(page, t.pageOffset);
    }
  }
  return page;
}

/** Time at which pageOffset starts. Page 0 always starts at 0. */
export function pageStartTime(recording, pageOffset) {
  if (pageOffset <= 0) return 0;
  const timings = recording.pageTimings || [];
  const exact = timings.find((t) => t.pageOffset === pageOffset);
  if (exact) return exact.timeMs;
  const earlier = timings.filter((t) => t.pageOffset < pageOffset).sort((a, b) => b.pageOffset - a.pageOffset)[0];
  return earlier ? earlier.timeMs : 0;
}

export const hasPageTiming = (recording, pageOffset) =>
  pageOffset === 0 || (recording.pageTimings || []).some((t) => t.pageOffset === pageOffset);

/** Sets the start time of a page and keeps all page timings monotonic. */
export function setPageStart(recording, pageOffset, timeMs) {
  if (pageOffset <= 0) return;
  timeMs = Math.max(0, timeMs);
  recording.pageTimings ||= [];
  const existing = recording.pageTimings.find((t) => t.pageOffset === pageOffset);
  if (!existing) recording.pageTimings.push({ pageOffset, timeMs });
  else existing.timeMs = timeMs;
  for (const t of recording.pageTimings) {
    if (t.pageOffset < pageOffset && t.timeMs > timeMs) t.timeMs = timeMs;
    if (t.pageOffset > pageOffset && t.timeMs < timeMs) t.timeMs = timeMs;
  }
  recording.pageTimings.sort((a, b) => a.pageOffset - b.pageOffset);
}

export function clearPageStart(recording, pageOffset) {
  recording.pageTimings = (recording.pageTimings || []).filter((t) => t.pageOffset !== pageOffset);
}

/** The marker at or before timeMs (null if none yet). */
export function currentMarker(recording, timeMs) {
  let best = null;
  for (const m of recording.markers || []) if (m.timeMs <= timeMs && (!best || m.timeMs >= best.timeMs)) best = m;
  return best;
}

/** The marker that directly follows `marker` in time order (ties broken by id, like the C# code). */
export function markerAfter(recording, marker) {
  let best = null;
  for (const m of recording.markers || []) {
    if (m === marker || m.id === marker.id) continue;
    if (m.timeMs < marker.timeMs) continue;
    if (m.timeMs === marker.timeMs && compareOrdinal(m.id, marker.id) < 0) continue;
    if (!best || m.timeMs < best.timeMs) best = m;
  }
  return best;
}

function compareOrdinal(a, b) {
  return a < b ? -1 : a > b ? 1 : 0;
}

export const onSameLine = (song, a, b) => a.pageOffset === b.pageOffset && Math.abs(a.y - b.y) < song.highlightHeight * 0.75;

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

/**
 * The region being played: { pageOffset, x1, y1, x2, y2 } in 0..1 page coordinates, or null. The band starts at
 * the current marker and ends at the next marker when it is on the same line, otherwise at the right edge.
 */
export function highlightAt(song, recording, timeMs) {
  const current = currentMarker(recording, timeMs);
  if (!current) return null;
  const next = markerAfter(recording, current);
  const h = clamp(song.highlightHeight ?? 0.08, 0.01, 0.5);
  const y1 = clamp(current.y - h / 2, 0, 1);
  const y2 = clamp(current.y + h / 2, 0, 1);
  if (next && onSameLine(song, current, next) && next.x > current.x)
    return { pageOffset: current.pageOffset, x1: current.x, y1, x2: next.x, y2 };
  return { pageOffset: current.pageOffset, x1: current.x, y1, x2: 1, y2 };
}

/** A tap on a page → playback time (markers on that page, interpolated on a line; otherwise the page start). */
export function timeForTap(song, recording, pageOffset, x, y) {
  const onPage = (recording.markers || []).filter((m) => m.pageOffset === pageOffset);
  if (onPage.length === 0) return pageStartTime(recording, pageOffset);
  const tolerance = clamp(song.highlightHeight ?? 0.08, 0.01, 0.5) * 0.75;
  const sameLine = onPage.filter((m) => Math.abs(m.y - y) <= tolerance);
  let chosen;
  if (sameLine.length > 0) {
    const leftOf = sameLine.filter((m) => m.x <= x).sort((a, b) => b.x - a.x)[0];
    chosen = leftOf || [...sameLine].sort((a, b) => a.x - b.x)[0];
  } else {
    chosen = [...onPage].sort((a, b) => (Math.abs(a.y - y) * 4 + Math.abs(a.x - x)) - (Math.abs(b.y - y) * 4 + Math.abs(b.x - x)))[0];
  }
  const next = markerAfter(recording, chosen);
  if (next && onSameLine(song, chosen, next) && next.x > chosen.x && x > chosen.x && x < next.x) {
    const f = (x - chosen.x) / (next.x - chosen.x);
    return chosen.timeMs + f * (next.timeMs - chosen.timeMs);
  }
  return chosen.timeMs;
}

export function addMarker(recording, pageOffset, x, y, timeMs, id, label) {
  const m = { id, timeMs: Math.max(0, timeMs), pageOffset, x: clamp(x, 0, 1), y: clamp(y, 0, 1) };
  if (label) m.label = label;
  recording.markers ||= [];
  recording.markers.push(m);
  sortMarkers(recording);
  return m;
}

export const sortMarkers = (recording) => (recording.markers || []).sort((a, b) => a.timeMs - b.timeMs);

/** Index of the two-page spread (0-based, counted from the first page of the song). */
export const spreadIndexForPage = (pageOffset) => Math.floor(Math.max(0, pageOffset) / 2);

/** Page offsets visible for a spread: one page in portrait, two in landscape. */
export function visiblePages(pageOffset, songPageCount, twoPages) {
  if (songPageCount <= 0) return [];
  pageOffset = clamp(pageOffset, 0, songPageCount - 1);
  if (!twoPages) return [pageOffset];
  const first = spreadIndexForPage(pageOffset) * 2;
  return first + 1 < songPageCount ? [first, first + 1] : [first];
}
