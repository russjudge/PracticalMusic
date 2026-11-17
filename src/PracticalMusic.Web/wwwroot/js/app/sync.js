// Synchronized playback through the website (protocol: PracticalMusic.Core/Online/SyncWire.cs).
// Browsers cannot use the apps' LAN protocol, so every browser and app connected to the same
// organization meets on the server's WebSocket: anyone can lead, the others follow.

import { Emitter, now } from './util.js';

/** Port of SyncMath.cs - decides what a follower does with a leader state, with the same safety bounds. */
export const START_LEAD_MS = 700;
const MAX_CATCH_UP_MS = 15_000;
const MAX_SCHEDULE_AHEAD_MS = 5_000;

export function planSync(state, localAtMs, nowMs, durationMs) {
  const clampPos = (p) => (durationMs > 0 ? Math.min(Math.max(p, 0), durationMs) : Math.max(0, p));
  const position = clampPos(state.positionMs);
  if (!state.isPlaying) return { action: 'pause', positionMs: position, delayMs: 0 };
  let lead = localAtMs - nowMs;
  if (lead > 0) {
    if (lead > MAX_SCHEDULE_AHEAD_MS) lead = START_LEAD_MS;
    return { action: 'startAt', positionMs: position, delayMs: lead };
  }
  let elapsed = -lead;
  if (elapsed > MAX_CATCH_UP_MS) elapsed = 0;
  return { action: 'playFrom', positionMs: clampPos(state.positionMs + elapsed), delayMs: 0 };
}

/** NTP-style estimate of "server clock minus local clock" from ping/pong round trips (same as ClockOffsetEstimator.cs). */
export class ClockEstimator {
  samples = [];
  coarse = null;
  offsetMs = 0;
  rttMs = 0;
  get synced() { return this.samples.length > 0; }
  add(t0, t1, t2) {
    const rtt = t2 - t0;
    if (rtt < 0 || rtt > 5000) return false;
    this.samples.push({ offset: t1 - (t0 + t2) / 2, rtt });
    if (this.samples.length > 8) this.samples.shift();
    const best = this.samples.reduce((a, b) => (b.rtt < a.rtt ? b : a));
    this.offsetMs = best.offset;
    this.rttMs = best.rtt;
    return true;
  }
  addCoarse(serverNow, localNow) { this.coarse = serverNow - localNow; }
  reset() { this.samples = []; this.coarse = null; this.offsetMs = 0; this.rttMs = 0; }
  get current() { return this.samples.length ? this.offsetMs : this.coarse; }
  toLocal(serverMs, localNow) { const o = this.current; return o == null ? localNow : serverMs - o; }
  toServer(localMs) { const o = this.current; return o == null ? null : localMs + o; }
}

/**
 * The WebSocket connection. Events: 'peers', 'changed' (connection/role/clock), 'state' (leader state, localAtMs),
 * 'log' (text). Reconnects by itself; leading/following survive a reconnect.
 */
export class OnlineSync extends Emitter {
  ws = null;
  orgId = null;
  connected = false;
  connectionId = null;
  peers = [];
  leading = false;
  followingId = null;
  leaderName = null;
  followingSince = 0;
  lastState = null;
  status = 'Not connected';
  clock = new ClockEstimator();
  #retry = 1000;
  #timer = null;
  #pingTimer = null;
  #stopped = true;
  #bookInfo = () => ({});

  constructor({ name, platform = 'Browser', bookInfo }) {
    super();
    this.name = name;
    this.platform = platform;
    if (bookInfo) this.#bookInfo = bookInfo;
  }

  start(orgId) {
    if (!orgId) return;
    const switching = this.orgId !== orgId;
    this.orgId = orgId;
    if (!this.#stopped) {
      if (switching && this.ws?.readyState === WebSocket.OPEN) {
        this.stopFollowing();
        this.peers = [];
        this.emit('peers', this.peers);
        this.#send(this.#hello());
      }
      return;
    }
    this.#stopped = false;
    this.#connect();
  }

  stop() {
    this.#stopped = true;
    clearTimeout(this.#timer);
    clearInterval(this.#pingTimer);
    try { this.ws?.close(); } catch { /* ignore */ }
    this.ws = null;
    this.connected = false;
    this.peers = [];
    this.emit('peers', this.peers);
    this.#setStatus('Not connected');
  }

  #connect() {
    if (this.#stopped) return;
    const url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/ws/sync';
    this.#setStatus('Connecting…');
    let ws;
    try { ws = new WebSocket(url); } catch { this.#scheduleReconnect(); return; }
    this.ws = ws;
    ws.onopen = () => {
      this.clock.reset();
      this.#send(this.#hello());
      if (this.leading) this.#send({ type: 'lead' });
      if (this.followingId) this.#send({ type: 'follow', leaderId: this.followingId });
      this.#retry = 1000;
      let burst = 6;
      const ping = () => this.#send({ type: 'ping', t0: now() });
      ping();
      clearInterval(this.#pingTimer);
      const fast = setInterval(() => {
        ping();
        if (--burst <= 0) {
          clearInterval(fast);
          this.#pingTimer = setInterval(ping, 3000);
        }
      }, 300);
      this.#pingTimer = fast;
    };
    ws.onmessage = (e) => {
      let msg;
      try { msg = JSON.parse(e.data); } catch { return; }
      this.#handle(msg);
    };
    ws.onclose = () => {
      clearInterval(this.#pingTimer);
      if (this.ws === ws) this.ws = null;
      const was = this.connected;
      this.connected = false;
      this.peers = [];
      this.emit('peers', this.peers);
      if (was) this.emit('changed');
      this.#scheduleReconnect();
    };
    ws.onerror = () => { /* onclose follows */ };
  }

  #scheduleReconnect() {
    if (this.#stopped) return;
    this.#setStatus('Connection lost - reconnecting…');
    clearTimeout(this.#timer);
    this.#timer = setTimeout(() => this.#connect(), this.#retry);
    this.#retry = Math.min(30_000, this.#retry * 2);
  }

  #hello() {
    const b = this.#bookInfo() || {};
    return { type: 'hello', protocol: 1, orgId: this.orgId, name: this.name, platform: this.platform, serverBookId: b.serverBookId, bookUid: b.bookUid, bookTitle: b.bookTitle };
  }

  #handle(msg) {
    const t = now();
    if (msg.serverTimeMs != null && !this.clock.synced) this.clock.addCoarse(msg.serverTimeMs, t);
    switch (msg.type) {
      case 'welcome':
        this.connectionId = msg.connectionId;
        this.connected = true;
        this.#setStatus('Connected');
        break;
      case 'peers':
        this.peers = (msg.peers || []).filter((p) => p.id !== this.connectionId);
        if (this.followingId) {
          const leader = this.peers.find((p) => p.id === this.followingId);
          if (leader) this.leaderName = leader.name;
        }
        this.emit('peers', this.peers);
        break;
      case 'pong':
        if (msg.t0 != null && msg.t1 != null && this.clock.add(msg.t0, msg.t1, t)) this.emit('changed');
        break;
      case 'state':
        if (msg.state && this.followingId && msg.leaderId === this.followingId) {
          this.lastState = msg.state;
          this.emit('state', msg.state, this.clock.toLocal(msg.state.atLeaderTimeMs, t));
        }
        break;
      case 'leaderGone':
        if (this.followingId && msg.leaderId === this.followingId) {
          this.emit('log', `${this.leaderName || 'The leader'} stopped leading.`);
          this.followingId = null;
          this.leaderName = null;
          this.lastState = null;
          this.emit('changed');
        }
        break;
      case 'error':
        this.emit('log', msg.message || 'error');
        break;
    }
  }

  #setStatus(s) {
    if (this.status === s) return;
    this.status = s;
    this.emit('changed');
  }

  #send(obj) {
    if (this.ws?.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(obj));
  }

  setName(name) {
    this.name = name;
    this.#send(this.#hello());
  }

  announceBook() {
    const b = this.#bookInfo() || {};
    this.#send({ type: 'book', serverBookId: b.serverBookId, bookUid: b.bookUid, bookTitle: b.bookTitle });
  }

  startLeading() {
    this.stopFollowing();
    if (this.leading) return;
    this.leading = true;
    this.#send({ type: 'lead' });
    this.emit('changed');
  }

  stopLeading() {
    if (!this.leading) return;
    this.leading = false;
    this.#send({ type: 'unlead' });
    this.emit('changed');
  }

  follow(leaderId) {
    this.stopLeading();
    this.followingId = leaderId;
    this.leaderName = this.peers.find((p) => p.id === leaderId)?.name || null;
    this.followingSince = Date.now();
    this.lastState = null;
    this.#send({ type: 'follow', leaderId });
    this.emit('changed');
  }

  stopFollowing() {
    if (!this.followingId) return;
    this.followingId = null;
    this.leaderName = null;
    this.lastState = null;
    this.#send({ type: 'unfollow' });
    this.emit('changed');
  }

  /** Leader: state.atLeaderTimeMs is on the local clock (performance.now) and is converted to server time. */
  publish(state) {
    if (!this.leading || !this.connected) return;
    const serverAt = this.clock.toServer(state.atLeaderTimeMs);
    if (serverAt == null) return;
    this.#send({ type: 'state', state: { ...state, atLeaderTimeMs: serverAt } });
  }
}
