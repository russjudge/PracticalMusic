// One <audio> element shared by the player and the timing editor (like the apps' single IAudioPlayer).
// iPhones and iPads only let a page start audio from a tap; once this element has played from a tap it
// may be started by code later (needed when following a leader), so unlock() runs on the first tap.

import { Emitter } from './util.js';

// 0.05 s of silence (8 kHz mono WAV) used to unlock playback without a sound.
const SILENT_WAV = 'data:audio/wav;base64,UklGRrQBAABXQVZFZm10IBAAAAABAAEAQB8AAEAfAAABAAgAZGF0YZABAACAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICA';

/**
 * Events: 'state' (playing/paused changed), 'ended', 'duration', 'error' (message), 'blocked' (the browser refused
 * to start without a tap).
 */
export class AudioPlayer extends Emitter {
  el = new Audio();
  url = null;
  loaded = false;
  unlocked = false;
  #loadToken = 0;

  constructor() {
    super();
    this.el.preload = 'auto';
    this.el.setAttribute('playsinline', '');
    this.el.addEventListener('play', () => this.emit('state'));
    this.el.addEventListener('pause', () => this.emit('state'));
    this.el.addEventListener('ended', () => { this.emit('state'); this.emit('ended'); });
    this.el.addEventListener('durationchange', () => this.emit('duration'));
    this.el.addEventListener('error', () => {
      if (this.el.src && this.el.src !== SILENT_WAV && this.url) this.emit('error', 'The recording could not be played.');
    });
    try { this.el.preservesPitch = true; } catch { /* older browsers */ }
  }

  get playing() { return !this.el.paused && !this.el.ended; }
  get positionMs() { return (this.el.currentTime || 0) * 1000; }
  get durationMs() { const d = this.el.duration; return Number.isFinite(d) ? d * 1000 : 0; }

  /** Call from a tap/click: afterwards code may start playback (iOS Safari). */
  unlock() {
    if (this.unlocked) return;
    this.unlocked = true;
    const el = this.el;
    if (!el.src) {
      el.src = SILENT_WAV;
      el.play()?.then(() => { if (el.src === SILENT_WAV) el.pause(); }).catch(() => { this.unlocked = false; });
    } else if (el.paused) {
      const t = el.currentTime;
      el.muted = true;
      el.play()?.then(() => { el.pause(); el.currentTime = t; el.muted = false; }).catch(() => { el.muted = false; this.unlocked = false; });
    }
  }

  /** Loads a recording; resolves when its length is known (or after a timeout, best effort). */
  async load(url) {
    if (url === this.url && this.loaded) return;
    const token = ++this.#loadToken;
    this.loaded = false;
    this.url = url;
    this.el.pause();
    this.el.playbackRate = 1;
    this.el.src = url;
    this.el.load();
    await new Promise((resolve, reject) => {
      const done = (ok, message) => {
        clearTimeout(timer);
        this.el.removeEventListener('loadedmetadata', onMeta);
        this.el.removeEventListener('error', onError);
        ok ? resolve() : reject(new Error(message));
      };
      const onMeta = () => done(true);
      const onError = () => done(false, 'The recording could not be loaded.');
      const timer = setTimeout(() => done(true), 15000); // iOS sometimes delays metadata until play
      this.el.addEventListener('loadedmetadata', onMeta);
      this.el.addEventListener('error', onError);
    });
    if (token === this.#loadToken) this.loaded = true;
  }

  unload() {
    this.#loadToken++;
    this.el.pause();
    this.url = null;
    this.loaded = false;
  }

  play() {
    const p = this.el.play();
    if (p?.catch) {
      p.catch((e) => {
        if (e?.name === 'NotAllowedError') { this.unlocked = false; this.emit('blocked'); }
        else if (e?.name !== 'AbortError') this.emit('error', e?.message || 'Playback failed.');
      });
    }
    return p;
  }

  pause() { this.el.pause(); }

  seek(ms) {
    const d = this.durationMs;
    const target = Math.max(0, d > 0 ? Math.min(ms, d) : ms);
    try { this.el.currentTime = target / 1000; } catch { /* not seekable yet */ }
  }

  set rate(r) { this.el.playbackRate = r; }
  get rate() { return this.el.playbackRate; }
  set volume(v) { this.el.volume = Math.min(1, Math.max(0, v)); }
}

/** Keeps the screen on while music plays (Screen Wake Lock API; iOS 16.4+, Chrome, Edge). */
export class WakeLock {
  #lock = null;
  #wanted = false;

  constructor() {
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible' && this.#wanted) this.#acquire();
    });
  }

  set(on) {
    this.#wanted = on;
    if (on) this.#acquire();
    else { this.#lock?.release().catch(() => {}); this.#lock = null; }
  }

  async #acquire() {
    if (this.#lock || !('wakeLock' in navigator)) return;
    try {
      this.#lock = await navigator.wakeLock.request('screen');
      this.#lock.addEventListener('release', () => { this.#lock = null; });
    } catch { /* not allowed right now */ }
  }
}
