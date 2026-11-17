// Performance mode (port of PlayerViewModel.cs): one page in portrait, two in landscape, pages turn with the
// recording, the line being played is highlighted, taps seek, and playback can lead or follow others in sync.

import { el, clear, button, formatTime, clamp, now } from './util.js';
import { songsInPageOrder, pageImageUrl, recordingUrl, isRecordingAvailable } from './model.js';
import * as T from './timing.js';
import { planSync, START_LEAD_MS } from './sync.js';
import { PageView, preload } from './pageview.js';

const DRIFT_SEEK_MS = 180;   // like the apps: a larger difference is corrected with a seek
const DRIFT_RATE_MS = 40;    // smaller differences are absorbed by playing 3% faster/slower until the next update

export class PlayerView {
  constructor(ctx) {
    this.ctx = ctx;
    this.songId = null;
    this.recId = null;
    this.pageOffset = 0;
    this.active = false;
    this.loaded = false;
    this.showMarkers = false;
    this.fill = localStorage.getItem('pm.fillDisplay') === '1';
    this.scheduled = null;
    this.applying = false;
    this.overlayTimer = null;
    this.lastPublish = 0;
    this.statusText = '';
    this.build();

    ctx.bus.on('book-replaced', () => { this.songId = null; this.recId = null; this.pageOffset = 0; this.loaded = false; if (this.active) this.refreshSong(); });
    ctx.bus.on('book-saved', () => { if (this.active) this.refreshSong(); });
    ctx.bus.on('book-changed', () => { if (this.active) this.refreshSong(); });
    const audio = ctx.audio;
    audio.on('state', () => { if (this.active) this.onAudioState(); });
    audio.on('ended', () => { if (this.active) this.publish(false); });
    audio.on('duration', () => { if (this.active) this.updateTransport(); });
    audio.on('blocked', () => { if (this.active) this.showTapToPlay(); });
    ctx.sync.on('state', (state, localAt) => this.onRemoteState(state, localAt));
    ctx.sync.on('changed', () => this.updateSyncText());
    new ResizeObserver(() => this.layoutPages()).observe(this.pages);
  }

  get book() { return this.ctx.doc?.book || null; }
  get songs() { return this.book ? songsInPageOrder(this.book) : []; }
  get song() { return this.book?.songs.find((s) => s.id === this.songId) || null; }
  get recording() { return this.song?.recordings.find((r) => r.id === this.recId) || null; }
  get twoPages() { return this.pages.clientWidth > this.pages.clientHeight && (this.song?.pageCount || 0) > 1; }

  // ------------------------------------------------------------------ layout

  build() {
    // on phones only the icons are shown (CSS hides .lbl), so the bar fits on one line
    const lbl = (text) => el('span.lbl', { text });
    this.prevSong = button(['⏮', lbl(' Prev song')], () => this.stepSong(-1), { title: 'Previous song (P)' });
    this.nextSong = button([lbl('Next song '), '⏭'], () => this.stepSong(1), { title: 'Next song (N)' });
    this.titleEl = el('b.song-title');
    this.recSelect = el('select.rec-select', { 'aria-label': 'Recording' });
    this.recSelect.addEventListener('change', () => { this.selectSong(this.song, this.recSelect.value); this.publish(false); });
    this.syncEl = el('span.muted.small.sync-text');
    this.statusEl = el('span.warn-text.small');
    this.markersToggle = button(['📍', lbl(' Markers')], () => { this.showMarkers = !this.showMarkers; this.markersToggle.classList.toggle('on', this.showMarkers); this.render(); }, { class: 'toggle', title: 'Show the time markers' });
    this.fillToggle = button(['⛶', lbl(' Full display')], () => this.setFill(!this.fill), { class: 'toggle', title: 'The pages fill the whole screen (key F). Tap to show the controls.' });
    this.songBar = el('div.player-bar.top', this.prevSong, el('div.song-center', this.titleEl, this.recSelect, this.syncEl, this.statusEl), this.markersToggle, this.fillToggle, this.nextSong);

    this.left = new PageView({ onTap: (t) => this.onTap(t, 'L') });
    this.right = new PageView({ onTap: (t) => this.onTap(t, 'R') });
    this.pages = el('div.player-pages', this.left.root, this.right.root);
    this.tapToPlay = el('div.tap-to-play', { hidden: true }, button('▶ Tap to allow playback', () => { this.ctx.audio.unlock(); this.tapToPlay.hidden = true; this.reapplyRemote(); }, { class: 'accent big' }));

    this.posText = el('span.time');
    this.durText = el('span.time');
    this.slider = el('input.seek', { type: 'range', min: 0, max: 1000, step: 1, value: 0, 'aria-label': 'Position' });
    this.slider.addEventListener('input', () => { this.dragging = true; this.posText.textContent = formatTime(Number(this.slider.value)); });
    this.slider.addEventListener('change', () => { this.dragging = false; this.seekTo(Number(this.slider.value), true); });
    this.playBtn = button('▶', () => this.playPause(), { class: 'big accent', title: 'Play / pause (Space)' });
    this.pageText = el('span.page-text');
    const transport = () => [
      button('⏹', () => this.stop(), { class: 'icon', title: 'Stop and rewind (S)' }),
      this.playBtn,
      button('−5s', () => this.nudge(-5000)),
      button('+5s', () => this.nudge(5000))
    ];
    this.transport = el('div.player-bar.bottom', ...transport(), this.posText, this.slider, this.durText,
      button('◀', () => this.turnPage(-1), { class: 'icon', title: 'Previous page (PageUp)' }), this.pageText, button('▶', () => this.turnPage(1), { class: 'icon', title: 'Next page (PageDown)' }));

    // floating controls in full-display mode
    this.overlayPlay = button('▶', () => this.playPause(), { class: 'big accent' });
    this.overlayPos = el('span.time');
    this.overlayPage = el('span.page-text');
    this.overlay = el('div.player-overlay', { hidden: true },
      button('⏹', () => this.stop(), { class: 'icon' }), this.overlayPlay, button('−5s', () => this.nudge(-5000)), button('+5s', () => this.nudge(5000)),
      this.overlayPos, button('◀', () => this.turnPage(-1), { class: 'icon' }), this.overlayPage, button('▶', () => this.turnPage(1), { class: 'icon' }),
      button('⤢ Exit full display', () => this.setFill(false)));

    this.root = el('div.player', this.songBar, el('div.player-stage', this.pages, this.overlay, this.tapToPlay), this.transport);
    this.root.addEventListener('pointermove', (e) => {
      if (Math.abs(e.movementX) + Math.abs(e.movementY) < 3) return;
      this.pokeOverlay();
    });
    this.root.addEventListener('pointerdown', () => this.pokeOverlay());
  }

  activate(params = {}) {
    this.active = true;
    if (params.songId) {
      const song = this.book?.songs.find((s) => s.id === params.songId);
      if (song) this.selectSong(song, params.recordingId);
    } else if (!this.song) {
      this.selectSong(this.songs[0] || null);
    } else {
      this.loadRecording();
    }
    this.applyFill();
    this.refreshSong();
    this.timer = setInterval(() => this.tick(), 40);
    this.heartbeat = setInterval(() => { if (this.ctx.sync.leading && this.ctx.audio.playing) this.publish(false); }, 1000);
    this.updateSyncText();
  }

  deactivate() {
    this.active = false;
    clearInterval(this.timer);
    clearInterval(this.heartbeat);
    clearTimeout(this.scheduled);
    this.ctx.audio.pause();
    this.ctx.audio.rate = 1;
    this.ctx.wake.set(false);
    document.body.classList.remove('fill-display');
    if (document.fullscreenElement) document.exitFullscreen?.().catch(() => {});
  }

  // ------------------------------------------------------------------ song / recording

  refreshSong() {
    if (!this.book) {
      this.titleEl.textContent = 'No book open - choose one in the Library';
      this.left.setPage(null, 'Open a book in the Library');
      this.right.root.hidden = true;
      this.recSelect.hidden = true;
      this.prevSong.disabled = this.nextSong.disabled = true;
      return;
    }
    if (this.songId && !this.song) this.songId = null;
    if (!this.song && this.songs.length) this.selectSong(this.songs[0]);
    const song = this.song;
    this.titleEl.textContent = song ? song.title : 'This book has no songs yet';
    clear(this.recSelect);
    for (const r of song?.recordings || []) this.recSelect.append(el('option', { value: r.id, text: r.name }));
    this.recSelect.value = this.recId || '';
    this.recSelect.hidden = (song?.recordings.length || 0) < 2;
    const i = this.songs.indexOf(song);
    this.prevSong.disabled = !(i > 0);
    this.nextSong.disabled = !(i >= 0 && i < this.songs.length - 1);
    this.render();
  }

  selectSong(song, recordingId = null) {
    const same = song && song.id === this.songId;
    this.songId = song?.id || null;
    const rec = song?.recordings.find((r) => r.id === recordingId) || (same ? this.recording : null) || song?.recordings[0] || null;
    if (!same) {
      clearTimeout(this.scheduled);
      this.ctx.audio.pause();
      this.pageOffset = 0;
    }
    if (rec?.id !== this.recId || !this.loaded) {
      this.recId = rec?.id || null;
      this.loadRecording();
    }
    if (this.active) this.refreshSong();
  }

  async loadRecording() {
    const rec = this.recording;
    const audio = this.ctx.audio;
    this.loaded = false;
    this.setStatus('');
    if (!rec) {
      audio.unload();
      if (this.song) this.setStatus('This song has no recording yet.');
      this.updateTransport();
      return;
    }
    const url = recordingUrl(this.ctx.doc, rec);
    if (!url) { this.setStatus('The audio file of this recording is missing.'); return; }
    const wanted = rec.id;
    try {
      this.setStatus('Loading ' + rec.name + '…');
      await audio.load(url);
      if (this.recId !== wanted) return;
      this.loaded = true;
      this.setStatus('');
      if (audio.durationMs > 0 && !rec.durationMs) rec.durationMs = audio.durationMs;
      this.pageOffset = clamp(T.pageOffsetAt(rec, audio.positionMs), 0, Math.max(0, (this.song?.pageCount || 1) - 1));
      this.render();
    } catch (e) {
      if (this.recId === wanted) this.setStatus('Could not load the audio: ' + e.message);
    }
  }

  stepSong(delta) {
    const i = this.songs.indexOf(this.song);
    const target = this.songs[i + delta];
    if (target) {
      this.selectSong(target);
      this.publish(false);
    }
  }

  // ------------------------------------------------------------------ transport

  playPause() {
    const audio = this.ctx.audio;
    audio.unlock();
    if (!this.loaded) return;
    if (audio.playing) {
      clearTimeout(this.scheduled);
      audio.pause();
      this.publish(false);
    } else if (this.ctx.sync.leading) {
      // scheduled start so every follower begins at the same instant
      const at = now() + START_LEAD_MS;
      this.publish(true, at, true);
      this.schedulePlay(at - now());
    } else {
      audio.play();
    }
  }

  schedulePlay(delayMs) {
    clearTimeout(this.scheduled);
    this.scheduled = setTimeout(() => { if (this.loaded) this.ctx.audio.play(); }, Math.max(0, delayMs));
  }

  stop() {
    if (!this.loaded) return;
    clearTimeout(this.scheduled);
    this.ctx.audio.pause();
    this.ctx.audio.seek(0);
    this.pageOffset = 0;
    this.render();
    this.publish(false);
  }

  nudge(ms) {
    this.seekTo(this.ctx.audio.positionMs + ms, true);
  }

  seekTo(ms, publish) {
    if (!this.loaded) return;
    const audio = this.ctx.audio;
    const d = audio.durationMs;
    ms = clamp(ms, 0, d > 0 ? d : Math.max(0, ms));
    audio.seek(ms);
    if (this.recording) this.pageOffset = clamp(T.pageOffsetAt(this.recording, ms), 0, this.song.pageCount - 1);
    this.render();
    if (publish) this.publish(false);
  }

  onTap(t, side) {
    this.ctx.audio.unlock();
    if (this.fill && this.overlay.hidden) { this.pokeOverlay(); return; }
    const song = this.song;
    const rec = this.recording;
    if (!song || !rec || !this.loaded) return;
    const offset = side === 'R' ? this.rightOffset : this.leftOffset;
    if (offset == null || offset < 0) return;
    this.seekTo(T.timeForTap(song, rec, offset, t.x, t.y), true);
  }

  turnPage(direction) {
    const song = this.song;
    if (!song) return;
    const step = this.twoPages ? 2 : 1;
    const target = clamp(this.pageOffset + direction * step, 0, song.pageCount - 1);
    const rec = this.recording;
    if (rec && this.loaded && T.hasPageTiming(rec, target)) this.seekTo(T.pageStartTime(rec, target), true);
    else { this.pageOffset = target; this.render(); }
  }

  // ------------------------------------------------------------------ ticking / drawing

  tick() {
    if (!this.active) return;
    const audio = this.ctx.audio;
    const song = this.song;
    const rec = this.recording;
    const pos = this.loaded ? audio.positionMs : 0;
    if (song && rec && this.loaded && audio.playing) {
      const p = clamp(T.pageOffsetAt(rec, pos), 0, song.pageCount - 1);
      if (p !== this.pageOffset) { this.pageOffset = p; this.render(); }
    }
    if (song && rec) {
      const hl = T.highlightAt(song, rec, pos);
      this.left.setHighlight(hl && hl.pageOffset === this.leftOffset ? hl : null);
      this.right.setHighlight(hl && hl.pageOffset === this.rightOffset ? hl : null);
    }
    this.updateTransport();
  }

  updateTransport() {
    const audio = this.ctx.audio;
    const pos = this.loaded ? audio.positionMs : 0;
    const dur = this.loaded ? audio.durationMs : 0;
    this.posText.textContent = this.overlayPos.textContent = formatTime(pos);
    this.durText.textContent = formatTime(dur);
    if (!this.dragging) {
      this.slider.max = String(Math.max(1, Math.round(dur)));
      this.slider.value = String(Math.round(pos));
    }
    this.slider.disabled = !this.loaded;
    const glyph = audio.playing ? '⏸' : '▶';
    if (this.playBtn.textContent !== glyph) this.playBtn.textContent = this.overlayPlay.textContent = glyph;
    this.playBtn.disabled = !this.recording;
  }

  render() {
    const song = this.song;
    if (!song) {
      this.left.setPage(null, this.book ? 'Add songs to this book in the Library' : 'Open a book in the Library');
      this.right.root.hidden = true;
      this.leftOffset = this.rightOffset = -1;
      this.pageText.textContent = this.overlayPage.textContent = '';
      return;
    }
    const two = this.twoPages;
    const visible = T.visiblePages(this.pageOffset, song.pageCount, two);
    this.leftOffset = visible[0];
    this.rightOffset = visible.length > 1 ? visible[1] : -1;
    this.right.root.hidden = this.rightOffset < 0;
    this.pages.classList.toggle('two', this.rightOffset >= 0);
    const doc = this.ctx.doc;
    const width = (this.rightOffset >= 0 ? this.right : this.left).desiredWidth();
    this.left.setPage(pageImageUrl(doc, song.startPage + this.leftOffset, width), 'Page not available');
    this.left.setLabel(this.fill ? '' : `p.${this.leftOffset + 1}`);
    if (this.rightOffset >= 0) {
      this.right.setPage(pageImageUrl(doc, song.startPage + this.rightOffset, width), 'Page not available');
      this.right.setLabel(this.fill ? '' : `p.${this.rightOffset + 1}`);
    }
    const markers = this.recording?.markers || [];
    this.left.setMarkers(markers.filter((m) => m.pageOffset === this.leftOffset), this.showMarkers);
    this.right.setMarkers(markers.filter((m) => m.pageOffset === this.rightOffset), this.showMarkers);
    const text = this.rightOffset >= 0 ? `Pages ${this.leftOffset + 1}–${this.rightOffset + 1} of ${song.pageCount}` : `Page ${this.leftOffset + 1} of ${song.pageCount}`;
    this.pageText.textContent = this.overlayPage.textContent = text;
    // warm the cache for the next spread so the page turn is instant
    const next = two ? (T.spreadIndexForPage(this.pageOffset) + 1) * 2 : this.pageOffset + 1;
    const urls = [];
    for (let k = 0; k < (two ? 2 : 1); k++) if (next + k < song.pageCount) urls.push(pageImageUrl(doc, song.startPage + next + k, width));
    preload(urls);
  }

  layoutPages() {
    if (this.active) this.render();
  }

  onAudioState() {
    const playing = this.ctx.audio.playing;
    this.ctx.wake.set(playing);
    if (!playing) this.ctx.audio.rate = 1;
    this.updateTransport();
    this.pokeOverlay();
  }

  setStatus(text) {
    this.statusText = text;
    this.statusEl.textContent = text;
  }

  // ------------------------------------------------------------------ full display

  setFill(on) {
    this.fill = on;
    localStorage.setItem('pm.fillDisplay', on ? '1' : '0');
    this.applyFill();
    this.render();
  }

  applyFill() {
    document.body.classList.toggle('fill-display', this.fill && this.active);
    this.fillToggle.classList.toggle('on', this.fill);
    const docEl = document.documentElement;
    if (this.fill && this.active) {
      const request = docEl.requestFullscreen || docEl.webkitRequestFullscreen;
      if (request && !document.fullscreenElement && !document.webkitFullscreenElement) {
        try { const p = request.call(docEl); p?.catch?.(() => {}); } catch { /* iPhone: the page still fills the window */ }
      }
    } else if (document.fullscreenElement || document.webkitFullscreenElement) {
      (document.exitFullscreen || document.webkitExitFullscreen)?.call(document)?.catch?.(() => {});
    }
    this.pokeOverlay();
  }

  pokeOverlay() {
    if (!this.fill) { this.overlay.hidden = true; return; }
    this.overlay.hidden = false;
    this.root.classList.remove('hide-cursor');
    clearTimeout(this.overlayTimer);
    if (this.ctx.audio.playing) {
      this.overlayTimer = setTimeout(() => {
        if (this.ctx.audio.playing) { this.overlay.hidden = true; this.root.classList.add('hide-cursor'); }
      }, 3000);
    }
  }

  // ------------------------------------------------------------------ keys

  onKey(e) {
    const k = e.key;
    let handled = true;
    if (k === ' ' || k === 'k') this.playPause();
    else if (k === 'ArrowLeft') this.nudge(-5000);
    else if (k === 'ArrowRight') this.nudge(5000);
    else if (k === 'PageUp' || k === 'ArrowUp') this.turnPage(-1);
    else if (k === 'PageDown' || k === 'ArrowDown') this.turnPage(1);
    else if (k === 'n' || k === 'N') this.stepSong(1);
    else if (k === 'p' || k === 'P') this.stepSong(-1);
    else if (k === 'Home') this.seekTo(0, true);
    else if (k === 's' || k === 'S') this.stop();
    else if (k === 'f' || k === 'F' || k === 'F11') this.setFill(!this.fill);
    else if (k === 'Escape' && this.fill) this.setFill(false);
    else handled = false;
    if (handled) { e.preventDefault(); this.pokeOverlay(); }
  }

  // ------------------------------------------------------------------ sync

  updateSyncText() {
    const sync = this.ctx.sync;
    let text = '';
    if (sync.leading) text = 'Leading sync';
    else if (sync.followingId) {
      const who = sync.leaderName || 'leader';
      text = sync.clock.synced ? `Following ${who} (±${Math.round(sync.clock.rttMs / 2)} ms)`
        : Date.now() - sync.followingSince > 6000 ? `Following ${who} - no answer from the website yet` : `Following ${who} (syncing clock…)`;
    }
    this.syncEl.textContent = text;
  }

  /** Leader: tells the followers what is playing. atLeaderTime is on the local clock (performance.now). */
  publish(scheduled, atLeaderTime = 0, playing = null) {
    const sync = this.ctx.sync;
    if (!sync.leading || this.applying || !this.ctx.doc) return;
    const song = this.song;
    const rec = this.recording;
    const audio = this.ctx.audio;
    sync.publish({
      bookId: this.book.id,
      bookTitle: this.book.title,
      songId: song?.id || null,
      songTitle: song?.title || null,
      songIndex: song ? this.songs.indexOf(song) : -1,
      recordingId: rec?.id || null,
      recordingName: rec?.name || null,
      isPlaying: playing ?? audio.playing,
      positionMs: this.loaded ? audio.positionMs : 0,
      atLeaderTimeMs: scheduled ? atLeaderTime : now()
    });
  }

  showTapToPlay() {
    if (this.ctx.sync.followingId) this.tapToPlay.hidden = false;
    else this.setStatus('Tap ▶ to start playback.');
  }

  reapplyRemote() {
    const s = this.ctx.sync;
    if (s.followingId && s.lastState) this.onRemoteState(s.lastState, s.clock.toLocal(s.lastState.atLeaderTimeMs, now()));
  }

  async onRemoteState(state, localAt) {
    const sync = this.ctx.sync;
    if (!sync.followingId) return;
    if (this.ctx.view !== 'player') this.ctx.navigate('player');
    if (this.applying) { this.pending = [state, localAt]; return; }
    this.applying = true;
    try {
      // the leader's book: open it from the organization's library when another one is open here
      if (state.bookId && this.ctx.doc?.book.id !== state.bookId) {
        const summary = this.ctx.books.find((b) => b.uid === state.bookId);
        if (!summary) { this.setStatus(`The leader plays “${state.bookTitle}”, which is not in this organization's library.`); return; }
        if (!(await this.ctx.openBook(summary.id, { quiet: true }))) return;
      }
      const songs = this.songs;
      const song = songs.find((s) => s.id === state.songId)
        || songs.find((s) => s.title.toLowerCase() === (state.songTitle || '').toLowerCase())
        || (state.songIndex >= 0 && state.songIndex < songs.length ? songs[state.songIndex] : null);
      if (!song) { this.setStatus(`The leader plays “${state.songTitle}”, which is not in this book.`); return; }
      const rec = song.recordings.find((r) => r.id === state.recordingId)
        || song.recordings.find((r) => r.name.toLowerCase() === (state.recordingName || '').toLowerCase())
        || song.recordings[0];
      if (!rec || !isRecordingAvailable(this.ctx.doc, rec)) { this.setStatus('The leader\'s recording is not available here.'); return; }
      if (song.id !== this.songId || rec.id !== this.recId || !this.loaded) {
        this.selectSong(song, rec.id);
        const started = Date.now();
        while (!this.loaded && Date.now() - started < 10000 && this.recId === rec.id) await new Promise((r) => setTimeout(r, 50));
        if (!this.loaded) return;
      }
      this.setStatus('');
      const audio = this.ctx.audio;
      clearTimeout(this.scheduled);
      const plan = planSync(state, localAt, now(), audio.durationMs);
      if (plan.action === 'pause') {
        if (audio.playing) audio.pause();
        audio.rate = 1;
        if (Math.abs(audio.positionMs - plan.positionMs) > DRIFT_SEEK_MS) this.seekTo(plan.positionMs, false);
      } else if (plan.action === 'startAt') {
        if (audio.playing) audio.pause();
        audio.rate = 1;
        this.seekTo(plan.positionMs, false);
        this.schedulePlay(plan.delayMs);
      } else {
        const drift = audio.positionMs - plan.positionMs;
        if (!audio.playing) {
          this.seekTo(plan.positionMs + 30, false); // allowance for the start-up latency of play()
          audio.play();
        } else if (Math.abs(drift) > DRIFT_SEEK_MS) {
          audio.rate = 1;
          this.seekTo(plan.positionMs + 30, false);
        } else {
          // small drift: play slightly faster or slower until the next heartbeat instead of jumping
          audio.rate = Math.abs(drift) > DRIFT_RATE_MS ? (drift > 0 ? 0.97 : 1.03) : 1;
        }
      }
    } finally {
      this.applying = false;
      if (this.pending) {
        const [s, t] = this.pending;
        this.pending = null;
        this.onRemoteState(s, t);
      }
    }
  }
}

