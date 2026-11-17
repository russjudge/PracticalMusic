// Links a recording to its pages (port of TimingEditorViewModel.cs): play the audio, press "Page N starts
// now" when the music reaches the next page, tap the page where the music is to drop time markers, drag
// markers to adjust them. Changes stay in the open book until the manager saves it.

import { el, clear, button, formatTime, clamp } from './util.js';
import { pageImageUrl, recordingUrl, newId } from './model.js';
import * as T from './timing.js';
import { PageView, preload } from './pageview.js';
import * as dialogs from './dialogs.js';

export class TimingEditorView {
  constructor(ctx) {
    this.ctx = ctx;
    this.songId = null;
    this.recId = null;
    this.pageOffset = 0;
    this.tapAddsMarker = true;
    this.followPlayback = true;
    this.selectedId = null;
    this.loaded = false;
    this.build();
    ctx.audio.on('state', () => { if (this.active) this.updateTransport(); });
    ctx.audio.on('duration', () => { if (this.active) this.updateTransport(); });
    ctx.bus.on('book-replaced', () => { if (this.active) ctx.navigate('library'); });
    ctx.bus.on('book-saved', () => { if (this.active) { this.rebuildLists(); this.render(); } });
    ctx.bus.on('dirty', () => this.updateSave());
    new ResizeObserver(() => { if (this.active) this.render(); }).observe(this.pages);
  }

  get song() { return this.ctx.doc?.book.songs.find((s) => s.id === this.songId) || null; }
  get recording() { return this.song?.recordings.find((r) => r.id === this.recId) || null; }
  get twoPages() { return this.pages.clientWidth > this.pages.clientHeight && (this.song?.pageCount || 0) > 1; }

  build() {
    this.heading = el('div.te-heading');
    this.status = el('div.muted.small');
    this.saveBtn = button('💾 Save', () => this.ctx.saveBook());
    const tapToggle = el('input', { type: 'checkbox', checked: true });
    tapToggle.addEventListener('change', () => {
      this.tapAddsMarker = tapToggle.checked;
      this.setStatus(this.tapAddsMarker ? 'Tap the page where the music currently is to add a marker.' : 'Tap the page to seek to that spot.');
    });
    const followToggle = el('input', { type: 'checkbox', checked: true });
    followToggle.addEventListener('change', () => { this.followPlayback = followToggle.checked; });
    this.header = el('div.te-header',
      button('← Done', () => this.ctx.navigate('library'), { title: 'Back to the Library (changes stay in the book; Save stores them)' }),
      el('div.te-title', this.heading, this.status),
      el('label.check', tapToggle, ' Tap adds marker'),
      el('label.check', followToggle, ' Pages follow audio'),
      this.saveBtn);

    this.left = new PageView({ allowDrag: true, onTap: (t) => this.onTap(t, 'L'), onMarkerMoved: (id, x, y) => this.markerMoved(id, x, y) });
    this.right = new PageView({ allowDrag: true, onTap: (t) => this.onTap(t, 'R'), onMarkerMoved: (id, x, y) => this.markerMoved(id, x, y) });
    this.pages = el('div.te-pages', this.left.root, this.right.root);

    this.posText = el('span.time');
    this.durText = el('span.time');
    this.slider = el('input.seek', { type: 'range', min: 0, max: 1000, step: 1, value: 0, 'aria-label': 'Position' });
    this.slider.addEventListener('input', () => { this.dragging = true; this.posText.textContent = formatTime(Number(this.slider.value)); });
    this.slider.addEventListener('change', () => { this.dragging = false; this.seek(Number(this.slider.value)); });
    this.playBtn = button('▶', () => this.playPause(), { class: 'big accent', title: 'Play / pause (Space)' });
    this.pageText = el('span.page-text');
    this.prevPageBtn = button('◀ Page', () => this.setPage(this.pageOffset - 1));
    this.nextPageBtn = button('Page ▶', () => this.setPage(this.pageOffset + 1));
    const seekbar = el('div.te-seek', this.posText, this.slider, this.durText);
    const transport = el('div.te-transport',
      button('⏹', () => this.stop(), { class: 'icon' }), this.playBtn,
      button('−5s', () => this.nudge(-5000)), button('−1s', () => this.nudge(-1000)), button('+1s', () => this.nudge(1000)), button('+5s', () => this.nudge(5000)),
      this.prevPageBtn, this.pageText, this.nextPageBtn);

    this.intro = el('p.muted.small');
    this.nextNowBtn = button('', () => this.nextPageNow(), { class: 'accent huge', title: 'Press when the music reaches the top of the next page (key N)' });
    this.timingList = el('div.te-list');
    this.markerCount = el('h3');
    this.markerList = el('div.te-list.markers');
    this.hlSlider = el('input', { type: 'range', min: 0.02, max: 0.3, step: 0.01, 'aria-label': 'Highlight height' });
    this.hlSlider.addEventListener('input', () => {
      const song = this.song;
      if (!song) return;
      song.highlightHeight = clamp(Number(this.hlSlider.value), 0.02, 0.3);
      this.ctx.markDirty();
      this.updateHighlight();
    });
    const side = el('div.te-side',
      this.intro, this.nextNowBtn,
      el('h3', { text: 'Page start times' }), this.timingList,
      el('div.section-head', this.markerCount, button('Clear all', () => this.clearMarkers(), { class: 'small' })),
      el('p.muted.small', { text: 'Markers link a spot on the page to a time. The highlight runs from a marker to the next marker on the same line (or to the end of the line). Drag a marker to move it.' }),
      this.markerList,
      el('h3', { text: 'Highlight height' }), this.hlSlider);

    this.root = el('div.timing-editor', this.header, el('div.te-body', el('div.te-main', this.pages, seekbar, transport), side));
  }

  async activate(params = {}) {
    if (params.songId) { this.songId = params.songId; this.recId = params.recordingId; this.pageOffset = 0; this.selectedId = null; }
    const song = this.song;
    const rec = this.recording;
    if (!song || !rec) { this.ctx.navigate('library'); return; }
    this.active = true;
    this.heading.textContent = `Edit timing: ${song.title} - ${rec.name}`;
    this.hlSlider.value = String(song.highlightHeight ?? 0.08);
    this.intro.textContent = song.pageCount > 1
      ? 'Play the recording. When the music reaches the top of the next page, press the big button (or key N). Then tap the page where the music is to add markers.'
      : 'This song has a single page, so no page turns are needed. Tap the page where the music is to add markers (optional); they drive the highlight and tap-to-seek.';
    this.rebuildLists();
    this.render();
    this.updateSave();
    this.timer = setInterval(() => this.tick(), 50);
    this.ctx.wake.set(true);
    this.loaded = false;
    const url = recordingUrl(this.ctx.doc, rec);
    try {
      this.setStatus('Loading audio…');
      await this.ctx.audio.load(url);
      if (!this.active || this.recId !== rec.id) return;
      this.loaded = true;
      if (this.ctx.audio.durationMs > 0) rec.durationMs = this.ctx.audio.durationMs;
      this.pageOffset = clamp(T.pageOffsetAt(rec, this.ctx.audio.positionMs), 0, song.pageCount - 1);
      this.setStatus(this.intro.textContent);
      this.render();
    } catch (e) {
      this.setStatus('Could not load the audio: ' + e.message);
    }
  }

  deactivate() {
    this.active = false;
    clearInterval(this.timer);
    this.ctx.audio.pause();
    this.ctx.wake.set(false);
  }

  setStatus(text) { this.status.textContent = text; }

  updateSave() {
    this.saveBtn.hidden = !this.ctx.canEdit;
    this.saveBtn.classList.toggle('accent', this.ctx.dirty);
    this.saveBtn.textContent = this.ctx.dirty ? '💾 Save changes' : '💾 Saved';
  }

  changed() {
    this.ctx.markDirty();
    this.rebuildLists();
    this.render();
  }

  // ------------------------------------------------------------------ transport

  playPause() {
    const a = this.ctx.audio;
    a.unlock();
    if (!this.loaded) return;
    if (a.playing) a.pause(); else a.play();
  }

  stop() {
    if (!this.loaded) return;
    this.ctx.audio.pause();
    this.ctx.audio.seek(0);
    if (this.followPlayback) this.setPage(0);
  }

  nudge(ms) { this.seek(this.ctx.audio.positionMs + ms); }

  seek(ms) {
    if (!this.loaded) return;
    const d = this.ctx.audio.durationMs;
    ms = clamp(ms, 0, d > 0 ? d : Math.max(0, ms));
    this.ctx.audio.seek(ms);
    if (this.followPlayback) this.setPage(clamp(T.pageOffsetAt(this.recording, ms), 0, this.song.pageCount - 1));
    this.tick();
  }

  setPage(offset) {
    const song = this.song;
    if (!song) return;
    offset = clamp(offset, 0, song.pageCount - 1);
    if (offset === this.pageOffset) return;
    this.pageOffset = offset;
    this.render();
  }

  tick() {
    if (!this.active) return;
    const a = this.ctx.audio;
    const rec = this.recording;
    const song = this.song;
    if (!rec || !song) return;
    const pos = this.loaded ? a.positionMs : 0;
    if (this.followPlayback && a.playing) {
      const p = clamp(T.pageOffsetAt(rec, pos), 0, song.pageCount - 1);
      if (p !== this.pageOffset) this.setPage(p);
    }
    this.updateHighlight();
    const current = T.currentMarker(rec, pos);
    for (const row of this.markerList.children) row.classList.toggle('current', !!current && row.dataset.id === current.id);
    const page = T.pageOffsetAt(rec, pos);
    for (const row of this.timingList.children) row.classList.toggle('current', Number(row.dataset.page) === page);
    this.updateTransport();
  }

  updateHighlight() {
    const song = this.song;
    const rec = this.recording;
    if (!song || !rec) return;
    const hl = T.highlightAt(song, rec, this.loaded ? this.ctx.audio.positionMs : 0);
    this.left.setHighlight(hl && hl.pageOffset === this.leftOffset ? hl : null);
    this.right.setHighlight(hl && this.rightOffset >= 0 && hl.pageOffset === this.rightOffset ? hl : null);
  }

  updateTransport() {
    const a = this.ctx.audio;
    const pos = this.loaded ? a.positionMs : 0;
    const dur = this.loaded ? a.durationMs : 0;
    this.posText.textContent = formatTime(pos);
    this.durText.textContent = formatTime(dur);
    if (!this.dragging) {
      this.slider.max = String(Math.max(1, Math.round(dur)));
      this.slider.value = String(Math.round(pos));
    }
    this.playBtn.textContent = a.playing ? '⏸' : '▶';
  }

  // ------------------------------------------------------------------ pages and markers

  render() {
    const song = this.song;
    const rec = this.recording;
    if (!song || !rec) return;
    const two = this.twoPages;
    const visible = T.visiblePages(this.pageOffset, song.pageCount, two);
    this.leftOffset = visible[0];
    this.rightOffset = visible.length > 1 ? visible[1] : -1;
    this.right.root.hidden = this.rightOffset < 0;
    this.pages.classList.toggle('two', this.rightOffset >= 0);
    const doc = this.ctx.doc;
    const width = (this.rightOffset >= 0 ? this.right : this.left).desiredWidth();
    this.left.setPage(pageImageUrl(doc, song.startPage + this.leftOffset, width), 'Page not available');
    this.left.setLabel((this.leftOffset === this.pageOffset ? '▶ ' : '') + `p.${this.leftOffset + 1}`);
    if (this.rightOffset >= 0) {
      this.right.setPage(pageImageUrl(doc, song.startPage + this.rightOffset, width), 'Page not available');
      this.right.setLabel((this.rightOffset === this.pageOffset ? '▶ ' : '') + `p.${this.rightOffset + 1}`);
    }
    const markers = rec.markers || [];
    this.left.setMarkers(markers.filter((m) => m.pageOffset === this.leftOffset), true, this.selectedId);
    this.right.setMarkers(markers.filter((m) => m.pageOffset === this.rightOffset), true, this.selectedId);
    this.pageText.textContent = `Page ${this.pageOffset + 1} of ${song.pageCount}`;
    this.prevPageBtn.disabled = this.pageOffset <= 0;
    this.nextPageBtn.disabled = this.pageOffset >= song.pageCount - 1;
    this.nextNowBtn.textContent = this.pageOffset < song.pageCount - 1 ? `Page ${this.pageOffset + 2} starts now` : 'Last page';
    this.nextNowBtn.disabled = this.pageOffset >= song.pageCount - 1;
    this.updateHighlight();
    const next = two ? (T.spreadIndexForPage(this.pageOffset) + 1) * 2 : this.pageOffset + 1;
    const urls = [];
    for (let k = 0; k < (two ? 2 : 1); k++) if (next + k < song.pageCount) urls.push(pageImageUrl(doc, song.startPage + next + k, width));
    preload(urls);
  }

  rebuildLists() {
    const song = this.song;
    const rec = this.recording;
    if (!song || !rec) return;
    clear(this.timingList);
    for (let i = 0; i < song.pageCount; i++) {
      const t = (rec.pageTimings || []).find((x) => x.pageOffset === i);
      const has = i === 0 || !!t;
      this.timingList.append(el('div.te-row', { dataset: { page: String(i) } },
        el('span.te-label', { text: `Page ${i + 1}` }),
        el('span.time', { text: i === 0 ? '0:00.0 (start)' : has ? formatTime(t.timeMs) : 'not set' }),
        button('Go', () => { if (has) this.seek(T.pageStartTime(rec, i)); this.setPage(i); }, { class: 'small', title: 'Show this page / seek to its start' }),
        i > 0 ? button('Now', () => { T.setPageStart(rec, i, this.ctx.audio.positionMs); this.changed(); }, { class: 'small', title: 'The page starts at the current time' }) : null,
        i > 0 && has ? button('✕', () => { T.clearPageStart(rec, i); this.changed(); }, { class: 'small', title: 'Clear' }) : null));
    }
    clear(this.markerList);
    const markers = [...(rec.markers || [])].sort((a, b) => a.timeMs - b.timeMs);
    this.markerCount.textContent = `${markers.length} marker(s)`;
    for (const m of markers) {
      this.markerList.append(el('div.te-row', { class: 'te-row' + (m.id === this.selectedId ? ' selected' : ''), dataset: { id: m.id } },
        el('span.time', { text: formatTime(m.timeMs) }),
        el('span.muted', { text: `p.${m.pageOffset + 1}` }),
        el('span.muted.small', { text: `(${m.x.toFixed(2)}, ${m.y.toFixed(2)})` }),
        button('Go', () => this.goToMarker(m), { class: 'small' }),
        button('Now', () => { m.timeMs = this.ctx.audio.positionMs; T.sortMarkers(rec); this.changed(); }, { class: 'small', title: 'Set the marker time to the current time' }),
        button('✕', () => this.deleteMarker(m), { class: 'small' })));
    }
  }

  /** The music just reached the top of the next page: stamp the time and show that page. */
  nextPageNow() {
    const song = this.song;
    const rec = this.recording;
    if (!song || !rec || this.pageOffset >= song.pageCount - 1) return;
    const next = this.pageOffset + 1;
    const t = this.ctx.audio.positionMs;
    T.setPageStart(rec, next, t);
    this.pageOffset = next;
    this.changed();
    this.setStatus(next < song.pageCount - 1
      ? `Page ${next + 1} starts at ${formatTime(t)}. Keep playing; press again at the top of page ${next + 2}.`
      : `Page ${next + 1} starts at ${formatTime(t)}. All page turns are set - press Save when you are finished.`);
  }

  onTap(tap, side) {
    this.ctx.audio.unlock();
    const song = this.song;
    const rec = this.recording;
    if (!song || !rec) return;
    const offset = side === 'R' ? this.rightOffset : this.leftOffset;
    if (offset < 0) return;
    if (tap.markerId) {
      const m = (rec.markers || []).find((x) => x.id === tap.markerId);
      if (m) this.goToMarker(m);
      return;
    }
    if (this.tapAddsMarker) {
      const t = this.ctx.audio.positionMs;
      const m = T.addMarker(rec, offset, tap.x, tap.y, t, newId());
      // a marker on a later page means that page starts no later than this time
      if (offset > 0 && !T.hasPageTiming(rec, offset)) T.setPageStart(rec, offset, t);
      if (offset !== this.pageOffset) this.pageOffset = offset;
      this.selectedId = m.id;
      this.changed();
      this.setStatus(`Marker added on page ${offset + 1} at ${formatTime(m.timeMs)}.`);
    } else {
      this.seek(T.timeForTap(song, rec, offset, tap.x, tap.y));
    }
  }

  markerMoved(id, x, y) {
    const m = (this.recording?.markers || []).find((k) => k.id === id);
    if (!m) return;
    m.x = clamp(x, 0, 1);
    m.y = clamp(y, 0, 1);
    this.selectedId = id;
    this.changed();
  }

  goToMarker(m) {
    this.selectedId = m.id;
    this.pageOffset = m.pageOffset;
    this.seek(m.timeMs);
    this.rebuildLists();
    this.render();
  }

  deleteMarker(m) {
    const rec = this.recording;
    if (!rec || !m) return;
    rec.markers = rec.markers.filter((x) => x !== m);
    if (this.selectedId === m.id) this.selectedId = null;
    this.changed();
  }

  async clearMarkers() {
    const rec = this.recording;
    if (!rec || !(rec.markers || []).length) return;
    if (!(await dialogs.confirm('Clear markers', `Delete all ${rec.markers.length} markers of this recording?`, 'Delete', 'Keep', true))) return;
    rec.markers = [];
    this.selectedId = null;
    this.changed();
  }

  onKey(e) {
    const k = e.key;
    let handled = true;
    if (k === ' ') this.playPause();
    else if (k === 'ArrowLeft') this.nudge(-2000);
    else if (k === 'ArrowRight') this.nudge(2000);
    else if (k === 'PageUp') this.setPage(this.pageOffset - 1);
    else if (k === 'PageDown') this.setPage(this.pageOffset + 1);
    else if (k === 'n' || k === 'N') this.nextPageNow();
    else if (k === 'Delete' || k === 'Backspace') this.deleteMarker((this.recording?.markers || []).find((m) => m.id === this.selectedId));
    else if (k === 'Escape') this.ctx.navigate('library');
    else handled = false;
    if (handled) e.preventDefault();
  }
}
