// Tests of the browser code of the website. See run-tests.html. Static imports: the whole module graph
// (and therefore every syntax or import error) is checked before the page has finished loading.
import * as util from '../../src/PracticalMusic.Web/wwwroot/js/app/util.js';
import * as api from '../../src/PracticalMusic.Web/wwwroot/js/app/api.js';
import * as model from '../../src/PracticalMusic.Web/wwwroot/js/app/model.js';
import * as timing from '../../src/PracticalMusic.Web/wwwroot/js/app/timing.js';
import * as sync from '../../src/PracticalMusic.Web/wwwroot/js/app/sync.js';
import * as audio from '../../src/PracticalMusic.Web/wwwroot/js/app/audio.js';
import * as pageview from '../../src/PracticalMusic.Web/wwwroot/js/app/pageview.js';
import * as dialogs from '../../src/PracticalMusic.Web/wwwroot/js/app/dialogs.js';
import * as libraryEdit from '../../src/PracticalMusic.Web/wwwroot/js/app/library-edit.js';
import * as library from '../../src/PracticalMusic.Web/wwwroot/js/app/library.js';
import * as player from '../../src/PracticalMusic.Web/wwwroot/js/app/player.js';
import * as timingEditor from '../../src/PracticalMusic.Web/wwwroot/js/app/timing-editor.js';
import * as share from '../../src/PracticalMusic.Web/wwwroot/js/app/share.js';
import * as about from '../../src/PracticalMusic.Web/wwwroot/js/app/about.js';
import '../../src/PracticalMusic.Web/wwwroot/js/app/main.js';

const out = document.getElementById('out');
const results = [];
let failed = 0;

function check(name, cond, detail = '') {
  results.push(`${cond ? 'ok  ' : 'FAIL'} ${name}${cond ? '' : ' ' + detail}`);
  if (!cond) failed++;
}
const near = (a, b, eps = 1e-6) => Math.abs(a - b) <= eps;
const eq = (a, b) => JSON.stringify(a) === JSON.stringify(b);

function run() {
  const m = { util, api, model, timing, sync, audio, pageview, dialogs, 'library-edit': libraryEdit, library, player, 'timing-editor': timingEditor, share, about };
  for (const [name, mod] of Object.entries(m)) check(`module ${name} loads`, Object.keys(mod).length > 0);

  const T = m.timing;
  const makeSong = () => {
    const rec = { name: 'Piano', durationMs: 60000, pageTimings: [{ pageOffset: 0, timeMs: 0 }, { pageOffset: 1, timeMs: 20000 }, { pageOffset: 2, timeMs: 40000 }], markers: [] };
    const song = { title: 'Test', startPage: 2, pageCount: 3, highlightHeight: 0.08, recordings: [rec] };
    return { song, rec };
  };

  if (T) {
    const { rec } = makeSong();
    for (const [ms, expected] of [[0, 0], [19999, 0], [20000, 1], [39999, 1], [40000, 2], [99999, 2]])
      check(`pageOffsetAt(${ms}) = ${expected}`, T.pageOffsetAt(rec, ms) === expected, `got ${T.pageOffsetAt(rec, ms)}`);

    const empty = { pageTimings: [], markers: [] };
    check('first page starts at zero', T.pageOffsetAt(empty, 12345) === 0 && T.pageStartTime(empty, 0) === 0);
    T.setPageStart(empty, 0, 5000);
    check('setPageStart(0) ignored', empty.pageTimings.length === 0);

    const r2 = makeSong().rec;
    T.setPageStart(r2, 1, 45000);
    check('later page pushed', T.pageStartTime(r2, 1) === 45000 && T.pageStartTime(r2, 2) === 45000);
    T.setPageStart(r2, 2, 10000);
    check('earlier page pulled back', T.pageStartTime(r2, 1) === 10000 && T.pageStartTime(r2, 2) === 10000);

    const { song, rec: r3 } = makeSong();
    const a = T.addMarker(r3, 0, 0.1, 0.2, 1000, 'a');
    const b = T.addMarker(r3, 0, 0.5, 0.2, 5000, 'b');
    const c = T.addMarker(r3, 0, 0.1, 0.4, 9000, 'c');
    check('no marker before the first', T.currentMarker(r3, 500) === null);
    check('current marker a', T.currentMarker(r3, 1000) === a && T.currentMarker(r3, 4999) === a);
    check('current marker b', T.currentMarker(r3, 5000) === b);
    check('marker after a/b/c', T.markerAfter(r3, a) === b && T.markerAfter(r3, b) === c && T.markerAfter(r3, c) === null);
    let hl = T.highlightAt(song, r3, 2000);
    check('highlight on the line a→b', hl && hl.pageOffset === 0 && near(hl.x1, 0.1) && near(hl.x2, 0.5) && near(hl.y2 - hl.y1, 0.08), JSON.stringify(hl));
    hl = T.highlightAt(song, r3, 6000);
    check('highlight to the right edge', hl && near(hl.x1, 0.5) && near(hl.x2, 1.0), JSON.stringify(hl));

    check('tap halfway interpolates', near(T.timeForTap(song, r3, 0, 0.3, 0.2), 3000, 1));
    check('tap before the line', near(T.timeForTap(song, r3, 0, 0.05, 0.2), 1000, 1));
    check('tap after the line', near(T.timeForTap(song, r3, 0, 0.9, 0.2), 5000, 1));
    check('tap on the second line', near(T.timeForTap(song, r3, 0, 0.3, 0.41), 9000, 1));
    check('tap on a page without markers', near(T.timeForTap(song, r3, 1, 0.3, 0.3), 20000, 1));

    check('spreads', eq(T.visiblePages(0, 3, false), [0]) && eq(T.visiblePages(2, 3, false), [2]) && eq(T.visiblePages(0, 3, true), [0, 1])
      && eq(T.visiblePages(1, 3, true), [0, 1]) && eq(T.visiblePages(2, 3, true), [2]) && eq(T.visiblePages(3, 4, true), [2, 3]));
  }

  if (m.util) {
    const f = m.util.formatTime;
    check('formatTime', f(0) === '0:00.0' && f(65300) === '1:05.3' && f(3661000) === '1:01:01', `${f(0)} ${f(65300)} ${f(3661000)}`);
  }

  if (m.sync) {
    const plan = m.sync.planSync;
    let p = plan({ isPlaying: false, positionMs: 12000 }, 1000, 1000, 200000);
    check('follower pauses at the leader position', p.action === 'pause' && p.positionMs === 12000);
    p = plan({ isPlaying: true, positionMs: 5000 }, 10650, 10000, 200000);
    check('scheduled start', p.action === 'startAt' && p.positionMs === 5000 && p.delayMs === 650, JSON.stringify(p));
    p = plan({ isPlaying: true, positionMs: 5000 }, 10000, 10400, 200000);
    check('catch up by the elapsed time', p.action === 'playFrom' && near(p.positionMs, 5400), JSON.stringify(p));
    p = plan({ isPlaying: true, positionMs: 5000 }, 0, 100000, 200000);
    check('stale timestamps are not extrapolated', p.action === 'playFrom' && p.positionMs === 5000, JSON.stringify(p));
    p = plan({ isPlaying: true, positionMs: 5000 }, 1e9, 0, 200000);
    check('absurd schedule uses the normal lead', p.action === 'startAt' && p.delayMs === m.sync.START_LEAD_MS, JSON.stringify(p));
    p = plan({ isPlaying: true, positionMs: 999999 }, 0, 0, 200000);
    check('position clamped to the recording', p.positionMs === 200000, JSON.stringify(p));

    const c = new m.sync.ClockEstimator();
    check('no estimate → now', c.toLocal(123456, 42) === 42 && c.toServer(5) === null);
    c.addCoarse(1000, 100);
    check('coarse estimate', c.toLocal(1500, 0) === 600);
    c.add(100, 1100, 120); // rtt 20, offset 990
    c.add(200, 1300, 300); // rtt 100, offset 1050 (worse)
    check('best round trip wins', c.synced && c.offsetMs === 990 && c.rttMs === 20, `${c.offsetMs} ${c.rttMs}`);
    check('server ↔ local', c.toLocal(2000, 0) === 1010 && c.toServer(10) === 1000);
    check('implausible sample rejected', c.add(100, 0, 99) === false && c.add(0, 0, 6000) === false);
  }

  if (m.model) {
    const M = m.model;
    const book = M.emptyBook('B');
    book.documents.push({ id: 'd1', name: 'A', pageCount: 4, source: { kind: 'embedded', entryName: 'media/a.pdf', fileName: 'a.pdf' } });
    book.documents.push({ id: 'd2', name: 'B', pageCount: 3, source: { kind: 'embedded', entryName: 'media/b.pdf', fileName: 'b.pdf' } });
    check('totalPages', M.totalPages(book) === 7);
    const r = M.resolvePage(book, 4);
    check('resolvePage across documents', r && r.doc.name === 'B' && r.pageIndex === 0 && M.resolvePage(book, 7) === null);
    check('firstPageOf', M.firstPageOf(book, book.documents[1]) === 4);
    book.songs.push({ id: 's1', title: 'One', startPage: 0, pageCount: 9, recordings: [] }, { id: 's2', title: 'Two', startPage: 4, pageCount: 9, recordings: [] });
    M.normalizeSongs(book);
    check('normalizeSongs trims overlaps', book.songs[0].pageCount === 4 && book.songs[1].pageCount === 3, JSON.stringify(book.songs.map((s) => s.pageCount)));
    check('unassigned run', M.firstUnassignedRun(book) === null);
    const doc = { id: 1, orgId: 1, book, media: { 'media/a.pdf': { id: 10, kind: 'pdf', pageCount: 4 }, 'media/b.pdf': { id: 11, kind: 'pdf', pageCount: 3 } } };
    check('page image url', M.pageImageUrl(doc, 5, 700) === '/api/media/11/pages/1?w=800', M.pageImageUrl(doc, 5, 700));
    check('unique entry names', M.uniqueEntryName(doc, 'a.pdf') === 'media/a_2.pdf' && M.uniqueEntryName(doc, 'c:d?.mp3') === 'media/c_d_.mp3', M.uniqueEntryName(doc, 'c:d?.mp3'));
    check('media refs only for used entries', eq(M.mediaRefs(doc), { 'media/a.pdf': 10, 'media/b.pdf': 11 }));
    check('new ids are 32 hex', /^[0-9a-f]{32}$/.test(M.newId()));
  }

  if (m.util) {
    const div = m.util.el('div.a.b#x', { text: '<b>not markup</b>', dataset: { k: 'v' } });
    check('el() escapes text', div.innerHTML === '&lt;b&gt;not markup&lt;/b&gt;' && div.className === 'a b' && div.id === 'x' && div.dataset.k === 'v');
  }

  // main.js starts asynchronously: look once it had a moment
  setTimeout(() => {
    check('main.js started the app (About view without an organization)', document.body.dataset.view === 'about', `view=${document.body.dataset.view}`);
    for (const err of window.__errors || []) check('no uncaught errors', false, err);
    out.textContent = results.join('\n') + `\n\nRESULT: ${failed === 0 ? 'PASS' : 'FAIL'} (${results.length - failed} passed, ${failed} failed)`;
    document.title = failed === 0 ? 'PASS' : 'FAIL';
  }, 200);
}

try { run(); } catch (e) { out.textContent = 'RESULT: FAIL (harness) ' + (e && e.stack || e); document.title = 'FAIL'; }
