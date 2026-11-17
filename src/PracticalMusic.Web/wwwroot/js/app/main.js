// The music app: library, player, timing editor and synchronized playback in the browser - the same
// features as the apps for Windows, Linux and Android, for every device with a browser (iPhone and iPad too).

import { el, clear, Emitter } from './util.js';
import { get, put, post, upload, ApiError } from './api.js';
import { emptyBook, normalizeSongs, mediaRefs } from './model.js';
import { AudioPlayer, WakeLock } from './audio.js';
import { OnlineSync } from './sync.js';
import * as dialogs from './dialogs.js';
import { LibraryView } from './library.js';
import { PlayerView } from './player.js';
import { TimingEditorView } from './timing-editor.js';
import { ShareView } from './share.js';
import { AboutView } from './about.js';

const boot = JSON.parse(document.getElementById('app-data').textContent);

function defaultDeviceName() {
  const ua = navigator.userAgent;
  const device = /iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1) ? 'iPad'
    : /iPhone/.test(ua) ? 'iPhone' : /Android/.test(ua) ? 'Android' : /Windows/.test(ua) ? 'Windows' : /Mac/.test(ua) ? 'Mac' : /Linux/.test(ua) ? 'Linux' : 'browser';
  return `${boot.user.name.split(' ')[0]} (${device})`;
}

/** Everything the views share. */
const ctx = {
  boot,
  bus: new Emitter(),
  audio: new AudioPlayer(),
  wake: new WakeLock(),
  orgId: null,
  org: null,
  books: [],
  doc: null,
  dirty: false,
  view: null,
  deviceName: localStorage.getItem('pm.deviceName') || defaultDeviceName(),

  get canEdit() { return !!(this.doc ? this.doc.canEdit : this.org?.canEdit); },

  setStatus(text) {
    document.getElementById('status-bar').textContent = text || '';
  },

  markDirty() {
    if (!this.doc) return;
    this.doc.book.modified = new Date().toISOString();
    const was = this.dirty;
    this.dirty = true;
    if (!was) this.bus.emit('dirty');
    this.bus.emit('book-changed');
    updateTitle();
  },

  async loadBooks() {
    if (!this.orgId) { this.books = []; return; }
    try {
      this.books = await get(`/api/orgs/${this.orgId}/books`);
    } catch (e) {
      this.books = [];
      this.setStatus(e.message);
    }
    this.bus.emit('books');
  },

  /** True when there are no unsaved changes, or the user agreed to lose/save them. */
  async confirmDiscard() {
    if (!this.dirty || !this.doc) return true;
    const choice = await dialogs.choose('Unsaved changes', `“${this.doc.book.title}” has changes that are not saved on the website.`, [
      { text: 'Save', value: 'save' }, { text: 'Discard', value: 'discard', danger: true }, { text: 'Cancel', value: null }]);
    if (choice === 'save') return await this.saveBook();
    return choice === 'discard';
  },

  async openBook(bookId, { skipConfirm = false, quiet = false } = {}) {
    if (!skipConfirm && !(await this.confirmDiscard())) return false;
    const p = quiet ? null : dialogs.progress('Opening', 'Loading the book…');
    try {
      const doc = await get(`/api/books/${bookId}`);
      normalizeSongs(doc.book);
      this.replaceDoc(doc);
      this.setStatus(`Opened “${doc.book.title}” (revision ${doc.revision}).`);
      return true;
    } catch (e) {
      if (!quiet) await dialogs.message('Could not open the book', e.message);
      return false;
    } finally {
      p?.close();
    }
  },

  async newBook() {
    if (!this.org?.canEdit) return;
    if (!(await this.confirmDiscard())) return;
    const title = await dialogs.prompt('New book', 'Name for the new book (for example the date of the service or the set list):', 'My Songs');
    if (title === null) return;
    this.replaceDoc({ id: null, orgId: this.orgId, orgName: this.org.name, revision: 0, fingerprint: '', canEdit: true, media: {}, book: emptyBook(title.trim() || 'My Songs') });
    this.dirty = true;
    this.bus.emit('dirty');
    updateTitle();
  },

  closeBook() {
    this.replaceDoc(null);
  },

  replaceDoc(doc) {
    this.audio.unload();
    this.doc = doc;
    this.dirty = false;
    this.bus.emit('book-replaced');
    this.bus.emit('dirty');
    updateTitle();
    updateUrl();
    sync.announceBook();
  },

  /** Saves the open book on the website (managers). Resolves true when saved. */
  async saveBook({ force = false } = {}) {
    const doc = this.doc;
    if (!doc || !doc.canEdit) return false;
    if (doc.book.documents.length === 0) {
      await dialogs.message('Nothing to save', 'Add a PDF first.');
      return false;
    }
    normalizeSongs(doc.book);
    const body = { book: doc.book, media: mediaRefs(doc), baseRevision: doc.id ? doc.revision : null, force };
    const p = dialogs.progress('Saving', 'Saving the book on the website…');
    try {
      const result = doc.id ? await put(`/api/books/${doc.id}`, body) : await post(`/api/orgs/${doc.orgId}/books`, body);
      const fresh = await get(`/api/books/${result.bookId}`);
      normalizeSongs(fresh.book);
      // keep the same objects alive for the views where possible: swap in the canonical book
      doc.id = fresh.id;
      doc.revision = fresh.revision;
      doc.fingerprint = fresh.fingerprint;
      doc.updatedAt = fresh.updatedAt;
      doc.updatedBy = fresh.updatedBy;
      doc.media = fresh.media;
      doc.book = fresh.book;
      this.dirty = false;
      this.bus.emit('dirty');
      this.bus.emit('book-saved');
      updateTitle();
      updateUrl();
      sync.announceBook();
      this.setStatus(result.unchanged ? 'No changes to save.' : `Saved “${doc.book.title}” - revision ${doc.revision}.`);
      this.loadBooks();
      return true;
    } catch (e) {
      p.close();
      if (e instanceof ApiError && e.status === 409) {
        const choice = await dialogs.choose('Changed on the website', e.message, [
          { text: 'Keep my version', value: 'overwrite' },
          { text: 'Load the website version (lose my changes)', value: 'reload', danger: true },
          { text: 'Cancel', value: null }]);
        if (choice === 'overwrite') return this.saveBook({ force: true });
        if (choice === 'reload' && doc.id) await this.openBook(doc.id, { skipConfirm: true });
        return false;
      }
      await dialogs.message('Could not save', e.message);
      return false;
    } finally {
      p.close();
    }
  },

  /** Uploads a .pmbook made by the app (managers) and opens it. */
  async importPackage(file) {
    if (!this.org?.canEdit) return;
    if (!(await this.confirmDiscard())) return;
    const send = async (query) => {
      const p = dialogs.progress('Uploading', `${file.name}`);
      try {
        return await upload(`/api/orgs/${this.orgId}/books/package${query}`, file, (f) => p.set(f));
      } finally {
        p.close();
      }
    };
    try {
      let result;
      try {
        result = await send('');
      } catch (e) {
        if (!(e instanceof ApiError) || e.status !== 409) throw e;
        const choice = await dialogs.choose('Book already here', e.message, [
          { text: 'Replace it (new revision)', value: '?force=true' }, { text: 'Keep both', value: '?asNew=true' }, { text: 'Cancel', value: null }]);
        if (!choice) return;
        result = await send(choice);
      }
      await this.loadBooks();
      await this.openBook(result.bookId, { skipConfirm: true });
      this.setStatus(`“${result.title}” ${result.created ? 'added' : result.unchanged ? 'is already up to date' : 'updated to revision ' + result.revision}.`);
    } catch (e) {
      await dialogs.message('Could not upload the book', e.message);
    }
  },

  navigate(view, params = {}) {
    showView(view, params);
  }
};

const sync = new OnlineSync({
  name: ctx.deviceName,
  platform: 'Browser',
  bookInfo: () => ({ serverBookId: ctx.doc?.id || null, bookUid: ctx.doc?.book.id || null, bookTitle: ctx.doc?.book.title || null })
});
ctx.sync = sync;

// ------------------------------------------------------------------ views

const main = document.getElementById('app-main');
const views = {};
let active = null;

function viewFor(name) {
  if (!views[name]) {
    const View = { library: LibraryView, player: PlayerView, timing: TimingEditorView, share: ShareView, about: AboutView }[name];
    views[name] = new View(ctx);
  }
  return views[name];
}

function showView(name, params = {}) {
  if (!ctx.orgId && name !== 'about') name = 'about';
  const next = viewFor(name);
  if (active && active !== next) active.deactivate?.();
  if (active !== next) {
    clear(main);
    main.append(next.root);
  }
  active = next;
  ctx.view = name;
  document.body.dataset.view = name;
  for (const b of document.querySelectorAll('#app-tabs .tab')) {
    const v = b.dataset.view;
    b.classList.toggle('active', v === name || (v === 'library' && name === 'timing'));
  }
  next.activate?.(params);
  updateUrl();
}

for (const b of document.querySelectorAll('#app-tabs .tab')) {
  b.addEventListener('click', () => {
    ctx.audio.unlock();
    showView(b.dataset.view);
  });
}

// the first tap anywhere unlocks audio for later automatic starts (iOS)
document.addEventListener('pointerdown', () => ctx.audio.unlock(), { once: true, capture: true });

document.addEventListener('keydown', (e) => {
  if (dialogs.isOpen()) return;
  const t = e.target;
  if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable)) return;
  if (e.ctrlKey || e.metaKey || e.altKey) return;
  active?.onKey?.(e);
});

window.addEventListener('beforeunload', (e) => {
  if (ctx.dirty) { e.preventDefault(); e.returnValue = ''; }
});

// ------------------------------------------------------------------ organization, title, URL

const orgSelect = document.getElementById('org-select');

function fillOrgs() {
  clear(orgSelect);
  for (const o of boot.orgs) orgSelect.append(el('option', { value: String(o.id), text: o.name }));
  orgSelect.hidden = boot.orgs.length <= 1;
}

async function selectOrg(orgId) {
  ctx.orgId = orgId;
  ctx.org = boot.orgs.find((o) => o.id === orgId) || null;
  orgSelect.value = String(orgId ?? '');
  ctx.closeBook();
  ctx.bus.emit('org');
  if (orgId) sync.start(orgId);
  else sync.stop();
  await ctx.loadBooks();
}

orgSelect.addEventListener('change', async () => {
  const id = Number(orgSelect.value);
  if (id === ctx.orgId) return;
  if (!(await ctx.confirmDiscard())) { orgSelect.value = String(ctx.orgId); return; }
  ctx.dirty = false;
  await selectOrg(id);
  showView(ctx.view === 'share' ? 'share' : 'library');
});

function updateTitle() {
  const title = ctx.doc ? `${ctx.dirty ? '* ' : ''}${ctx.doc.book.title}` : (ctx.org?.name || '');
  document.getElementById('app-title').textContent = title;
  document.title = (ctx.doc ? title + ' - ' : '') + 'Practical Music';
}

function updateUrl() {
  const q = new URLSearchParams();
  if (ctx.orgId) q.set('org', ctx.orgId);
  if (ctx.doc?.id) q.set('book', ctx.doc.id);
  if (ctx.view && ctx.view !== 'timing') q.set('view', ctx.view);
  try { history.replaceState(null, '', '/App?' + q.toString()); } catch { /* only cosmetic (e.g. pages opened from a file) */ }
}

function updateSyncDot() {
  const dot = document.getElementById('sync-dot');
  dot.className = 'sync-dot' + (sync.connected ? ' on' : '') + (sync.leading ? ' leading' : '') + (sync.followingId ? ' following' : '');
  dot.title = sync.leading ? 'Leading synchronized playback' : sync.followingId ? `Following ${sync.leaderName || 'the leader'}` : sync.status;
}
sync.on('changed', updateSyncDot);
sync.on('peers', updateSyncDot);
ctx.bus.on('dirty', updateTitle);

// ------------------------------------------------------------------ start

fillOrgs();
(async () => {
  const initial = boot.initial || {};
  await selectOrg(initial.org ?? null);
  if (!ctx.orgId) {
    showView('about');
    ctx.setStatus('You do not have access to an organization yet. Ask to join one on your account page.');
    return;
  }
  if (initial.book && await ctx.openBook(initial.book, { skipConfirm: true, quiet: true })) {
    showView(initial.view && initial.view !== 'timing' ? initial.view : 'player');
  } else if (initial.newBook && ctx.org?.canEdit) {
    showView('library');
    await ctx.newBook();
  } else {
    showView(initial.view && initial.view !== 'timing' ? initial.view : 'library');
  }
})();
