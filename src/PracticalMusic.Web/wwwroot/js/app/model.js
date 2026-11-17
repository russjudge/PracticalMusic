// The book model, the same JSON as book.json inside a .pmbook (see PracticalMusic.Core/Models/Book.cs).
// These helpers mirror the C# Book methods so the website edits books exactly like the apps do.
//
// A "document" here is the server's BookDocument: { id, orgId, revision, canEdit, book, media } where
// media maps a package entry name (MediaSource.entryName) to the stored file { id, kind, pageCount, ... }.

export function newId() {
  if (globalThis.crypto?.randomUUID) return crypto.randomUUID().replaceAll('-', '');
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return [...bytes].map((b) => b.toString(16).padStart(2, '0')).join('');
}

export function emptyBook(title) {
  return {
    formatVersion: 1,
    id: newId(),
    title: title || 'My Songs',
    modified: new Date().toISOString(),
    documents: [],
    songs: []
  };
}

export const totalPages = (book) => book.documents.reduce((n, d) => n + (d.pageCount || 0), 0);

/** Global page index → { doc, pageIndex } (or null). */
export function resolvePage(book, globalPage) {
  if (globalPage < 0) return null;
  let remaining = globalPage;
  for (const doc of book.documents) {
    if (remaining < doc.pageCount) return { doc, pageIndex: remaining };
    remaining -= doc.pageCount;
  }
  return null;
}

export function firstPageOf(book, doc) {
  let p = 0;
  for (const d of book.documents) {
    if (d === doc || d.id === doc.id) return p;
    p += d.pageCount;
  }
  return -1;
}

export const songEnd = (song) => song.startPage + song.pageCount - 1;
export const songContains = (song, p) => p >= song.startPage && p <= songEnd(song);
export const songAtPage = (book, p) => book.songs.find((s) => songContains(s, p)) || null;
export const songStartingAt = (book, p) => book.songs.find((s) => s.startPage === p) || null;
export const songsInPageOrder = (book) => [...book.songs].sort((a, b) => a.startPage - b.startPage);

export function* allMediaSources(book) {
  for (const d of book.documents) yield d.source;
  for (const s of book.songs) for (const r of s.recordings || []) yield r.source;
}

/** Same rules as Book.NormalizeSongs: songs inside the book, no overlaps, timings/markers on existing pages. */
export function normalizeSongs(book) {
  book.songs.sort((a, b) => a.startPage - b.startPage);
  const total = totalPages(book);
  for (let i = 0; i < book.songs.length; i++) {
    const s = book.songs[i];
    s.recordings ||= [];
    if (total === 0) { s.startPage = 0; s.pageCount = 1; continue; }
    s.startPage = Math.min(Math.max(s.startPage, 0), total - 1);
    let maxEnd = i + 1 < book.songs.length ? book.songs[i + 1].startPage - 1 : total - 1;
    if (maxEnd < s.startPage) maxEnd = s.startPage;
    s.pageCount = Math.min(Math.max(s.pageCount, 1), maxEnd - s.startPage + 1);
    for (const r of s.recordings) {
      r.pageTimings = (r.pageTimings || []).filter((t) => t.pageOffset >= 0 && t.pageOffset < s.pageCount);
      r.markers = (r.markers || []).filter((m) => m.pageOffset >= 0 && m.pageOffset < s.pageCount);
      r.markers.sort((a, b) => a.timeMs - b.timeMs);
      r.pageTimings.sort((a, b) => a.pageOffset - b.pageOffset);
    }
  }
}

/** First run of pages that belong to no song ({ start, end } 0-based) or null. */
export function firstUnassignedRun(book) {
  const total = totalPages(book);
  for (let p = 0; p < total; p++) {
    if (songAtPage(book, p)) continue;
    let end = p;
    while (end + 1 < total && !songAtPage(book, end + 1)) end++;
    return { start: p, end };
  }
  return null;
}

export function unassignedHint(book) {
  const runs = [];
  const total = totalPages(book);
  for (let p = 0; p < total; p++) {
    if (songAtPage(book, p)) continue;
    let end = p;
    while (end + 1 < total && !songAtPage(book, end + 1)) end++;
    runs.push(end === p ? `${p + 1}` : `${p + 1}–${end + 1}`);
    p = end;
  }
  return runs.length ? 'Pages not in any song yet: ' + runs.join(', ') + '.' : '';
}

export const unassignedCount = (book) => {
  let n = 0;
  const total = totalPages(book);
  for (let p = 0; p < total; p++) if (!songAtPage(book, p)) n++;
  return n;
};

// ------------------------------------------------------------------ media on the server

export const PAGE_WIDTHS = [160, 320, 480, 640, 800, 1000, 1200, 1500, 1800, 2200, 2600];
export const bucketWidth = (w) => PAGE_WIDTHS.find((x) => x >= w) || PAGE_WIDTHS[PAGE_WIDTHS.length - 1];

export function mediaOf(doc, source) {
  if (!source || source.kind !== 'embedded' || !source.entryName) return null;
  return doc.media[source.entryName] || null;
}

/** Image of a page (global page index) at about the given device-pixel width, or null when unavailable. */
export function pageImageUrl(doc, globalPage, width) {
  const located = resolvePage(doc.book, globalPage);
  if (!located) return null;
  const m = mediaOf(doc, located.doc.source);
  if (!m) return null;
  return `/api/media/${m.id}/pages/${located.pageIndex}?w=${bucketWidth(Math.max(100, Math.round(width)))}`;
}

/** Where the browser can play a recording from. */
export function recordingUrl(doc, recording) {
  const src = recording?.source;
  if (!src) return null;
  if (src.kind === 'url') return src.url || null;
  const m = mediaOf(doc, src);
  return m ? `/api/media/${m.id}/file` : null;
}

export function isRecordingAvailable(doc, recording) {
  return !!recordingUrl(doc, recording);
}

/** Entry name for a new upload, unique within the book (same scheme as the server's canonical names). */
export function uniqueEntryName(doc, fileName) {
  const clean = String(fileName || 'file').replace(/[\\/:*?"<>|\u0000-\u001f]/g, '_').trim() || 'file';
  const used = new Set(Object.keys(doc.media).map((k) => k.toLowerCase()));
  for (const s of allMediaSources(doc.book)) if (s?.entryName) used.add(s.entryName.toLowerCase());
  const dot = clean.lastIndexOf('.');
  const stem = dot > 0 ? clean.slice(0, dot) : clean;
  const ext = dot > 0 ? clean.slice(dot) : '';
  let name = 'media/' + clean;
  for (let n = 2; used.has(name.toLowerCase()); n++) name = `media/${stem}_${n}${ext}`;
  return name;
}

/** The map the server expects on save: entry name → media id, for every embedded source in use. */
export function mediaRefs(doc) {
  const refs = {};
  for (const s of allMediaSources(doc.book)) {
    if (s?.kind === 'embedded' && s.entryName && doc.media[s.entryName]) refs[s.entryName] = doc.media[s.entryName].id;
  }
  return refs;
}

export function deepClone(value) {
  return typeof structuredClone === 'function' ? structuredClone(value) : JSON.parse(JSON.stringify(value));
}
