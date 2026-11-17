// Synchronized playback through the website: everyone connected to the organization - browsers and the
// Windows, Linux and Android apps - appears here; anyone can lead and the others follow.

import { el, clear, button } from './util.js';

export class ShareView {
  constructor(ctx) {
    this.ctx = ctx;
    this.log = [];
    const sync = ctx.sync;
    this.nameInput = el('input', { type: 'text', value: ctx.deviceName, maxlength: 60, 'aria-label': 'Device name' });
    this.connection = el('p.muted.small');
    this.peerList = el('div.peer-list');
    this.syncStatus = el('p');
    this.controls = el('div.button-row');
    this.logList = el('ul.log');
    this.root = el('div.share',
      el('h1', { text: 'Play together' }),
      el('div.card',
        el('div.button-row', el('label.inline-label', 'This device is called: ', this.nameInput), button('Apply', () => this.applyName())),
        this.connection,
        el('p.muted.small', { text: 'Everyone signed in to this organization appears below - in a browser or in the Practical Music app. The website relays the playback, so this works across different networks too; it only needs the normal web connection (HTTPS).' })),
      el('div.section-head', el('h2', { text: 'Connected now' })),
      this.peerList,
      el('h2', { text: 'Synchronized playback' }),
      el('div.card', this.syncStatus,
        el('p.muted.small', { text: 'One device leads: when the leader presses play, every follower starts the same song at the same moment and the highlight moves together. Followers open the leader\'s book automatically when it is in this organization\'s library.' }),
        this.controls),
      el('div.section-head', el('h2', { text: 'Activity' }), button('Clear', () => { this.log = []; this.renderLog(); }, { class: 'small' })),
      this.logList);
    sync.on('peers', () => { if (this.active) this.render(); });
    sync.on('changed', () => { if (this.active) this.render(); });
    sync.on('log', (text) => this.addLog(text));
  }

  activate() {
    this.active = true;
    this.render();
  }

  deactivate() {
    this.active = false;
  }

  applyName() {
    const name = this.nameInput.value.trim() || this.ctx.deviceName;
    this.ctx.deviceName = name;
    this.nameInput.value = name;
    localStorage.setItem('pm.deviceName', name);
    this.ctx.sync.setName(name);
    this.addLog(`This device is now called “${name}”.`);
  }

  addLog(text) {
    this.log.unshift(`${new Date().toLocaleTimeString()}  ${text}`);
    this.log.length = Math.min(this.log.length, 200);
    if (this.active) this.renderLog();
  }

  renderLog() {
    clear(this.logList);
    for (const line of this.log) this.logList.append(el('li', { text: line }));
  }

  render() {
    const ctx = this.ctx;
    const sync = ctx.sync;
    this.connection.textContent = `${ctx.org?.name || ''} · ${sync.status}` + (sync.clock.synced ? ` · clock offset measured (round trip ${Math.round(sync.clock.rttMs)} ms)` : '');

    clear(this.peerList);
    if (sync.peers.length === 0) {
      this.peerList.append(el('p.muted', { text: sync.connected ? 'Nobody else is connected right now. Open Practical Music on another device - in a browser or the app (Online page) - with an account of this organization.' : 'Connecting to the website…' }));
    }
    for (const p of sync.peers) {
      const following = sync.followingId === p.id;
      this.peerList.append(el('div.card.peer',
        el('div',
          el('div.peer-name', el('b', { text: p.name }), p.leading ? el('span.badge.accent', { text: 'LEADING' }) : null, following ? el('span.badge', { text: 'you follow' }) : null),
          el('div.muted.small', { text: `${p.userName} · ${p.platform}${p.bookTitle ? ` · “${p.bookTitle}”` : ''}` })),
        el('div.button-row',
          p.leading && !following ? button('🔗 Follow', () => this.follow(p), { class: 'accent' }) : null,
          following ? button('Stop following', () => { sync.stopFollowing(); this.addLog('Stopped following.'); }) : null)));
    }

    this.syncStatus.textContent = sync.leading
      ? 'You are leading. Play, pause, seek and song changes in the Player are mirrored on every follower.'
      : sync.followingId
        ? `Following ${sync.leaderName || 'the leader'}` + (sync.clock.synced ? ` · clock offset ${Math.round(sync.clock.offsetMs)} ms · round trip ${Math.round(sync.clock.rttMs)} ms` : ' · measuring the clock…')
        : 'Not synchronized. Lead a session, or follow someone who is leading.';
    clear(this.controls);
    if (!sync.leading) {
      this.controls.append(button('🎼 Lead a session', () => {
        ctx.audio.unlock();
        sync.startLeading();
        this.addLog('Started leading a sync session.');
        ctx.navigate('player');
      }, { class: 'accent' }));
    } else {
      this.controls.append(button('Stop leading', () => { sync.stopLeading(); this.addLog('Stopped leading.'); }));
    }
    if (sync.followingId) this.controls.append(button('Stop following', () => { sync.stopFollowing(); this.addLog('Stopped following.'); }));
    this.renderLog();
  }

  async follow(peer) {
    const ctx = this.ctx;
    ctx.audio.unlock(); // this tap allows the browser to start the music when the leader does
    // open the leader's book first when it is in the library
    if (peer.serverBookId && ctx.doc?.id !== peer.serverBookId && ctx.books.some((b) => b.id === peer.serverBookId)) {
      if (!(await ctx.openBook(peer.serverBookId))) return;
    }
    ctx.sync.follow(peer.id);
    this.addLog(`Following ${peer.name}.`);
    ctx.navigate('player');
  }
}
