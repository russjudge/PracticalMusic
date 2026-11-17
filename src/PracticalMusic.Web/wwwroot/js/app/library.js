// The Library: the organization's books, and the book editor organised as the same guided flow as the
// apps (1 add the PDF, 2 identify the songs, 3 add recordings, 4 set the timing). Members see it read only.

import { el, clear, button, formatBytes, formatDate, pickFiles } from './util.js';
import { totalPages, resolvePage, songEnd, songAtPage, songStartingAt, songsInPageOrder, unassignedCount, pageImageUrl, isRecordingAvailable } from './model.js';
import { BookEditing } from './library-edit.js';

export class LibraryView {
  constructor(ctx) {
    this.ctx = ctx;
    this.root = el('div.library');
    this.selSongId = null;
    this.selRecId = null;
    this.selPage = null;
    this.active = false;
    this.edit = new BookEditing(this);
    this.thumbs = new Map(); // global page → element (reused while the PDFs stay the same)
    this.thumbKey = '';

    const rerender = () => { if (this.active) this.render(); };
    ctx.bus.on('book-replaced', () => { this.selSongId = this.selRecId = null; this.selPage = null; this.thumbKey = ''; rerender(); });
    ctx.bus.on('book-saved', () => { this.thumbKey = ''; rerender(); });
    ctx.bus.on('book-changed', rerender);
    ctx.bus.on('books', () => { if (this.active && !ctx.doc) this.render(); });
    ctx.bus.on('dirty', () => this.updateSaveButton());

    this.root.addEventListener('dragover', (e) => {
      if (![...(e.dataTransfer?.types || [])].includes('Files') || !ctx.canEdit) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = 'copy';
      this.root.classList.add('dropping');
    });
    this.root.addEventListener('dragleave', (e) => { if (e.target === this.root) this.root.classList.remove('dropping'); });
    this.root.addEventListener('drop', (e) => {
      this.root.classList.remove('dropping');
      if (!e.dataTransfer?.files?.length || !ctx.canEdit) return;
      e.preventDefault();
      const target = e.target.closest?.('[data-song-id], [data-page]');
      this.edit.handleDrop([...e.dataTransfer.files], target?.dataset.page != null ? Number(target.dataset.page) : null, target?.dataset.songId || null);
    });
  }

  get doc() { return this.ctx.doc; }
  get book() { return this.ctx.doc?.book; }
  get song() { return this.book?.songs.find((s) => s.id === this.selSongId) || null; }
  get recording() { return this.song?.recordings.find((r) => r.id === this.selRecId) || null; }

  activate() {
    this.active = true;
    this.render();
  }

  deactivate() {
    this.active = false;
  }

  selectSong(song, recording = null) {
    this.selSongId = song?.id || null;
    const rec = recording && song?.recordings.includes(recording) ? recording : song?.recordings[0] || null;
    this.selRecId = rec?.id || null;
    this.render();
  }

  // ------------------------------------------------------------------ rendering

  render() {
    if (!this.active) return;
    clear(this.root);
    if (!this.ctx.orgId) return;
    if (!this.doc) this.renderBookList();
    else this.renderEditor();
  }

  renderBookList() {
    const ctx = this.ctx;
    const head = el('div.lib-head',
      el('h1', { text: `Books of ${ctx.org?.name || ''}` }),
      ctx.org?.canEdit ? el('div.actions',
        button('➕ New book', () => ctx.newBook(), { class: 'accent' }),
        button('⬆ Upload .pmbook…', async () => {
          const [file] = await pickFiles({ accept: '.pmbook' });
          if (file) ctx.importPackage(file);
        }, { title: 'A book saved in the app with “Save (with media)”' })) : null);
    this.root.append(head);
    if (ctx.books.length === 0) {
      this.root.append(el('div.empty',
        el('p', { text: ctx.org?.canEdit ? 'No books yet. Press “New book” to create one, or upload a .pmbook made with the app.' : 'Your organization has no books yet.' })));
      return;
    }
    const list = el('div.book-list');
    for (const b of ctx.books) {
      list.append(el('div.book-row',
        el('button.book-title', { type: 'button', onclick: () => ctx.openBook(b.id).then((ok) => ok && this.render()), text: b.title }),
        el('span.muted.small', { text: `${b.songCount} song(s) · ${formatBytes(b.mediaBytes)} · revision ${b.revision} · ${formatDate(b.updatedAt)}${b.updatedBy ? ' · ' + b.updatedBy : ''}` }),
        el('span.row-actions',
          button('▶ Play', async () => { if (await ctx.openBook(b.id)) ctx.navigate('player'); }, { class: 'accent small' }),
          button(ctx.org?.canEdit ? 'Edit' : 'Open', () => ctx.openBook(b.id), { class: 'small' }),
          el('a.button.small.secondary', { href: `/api/books/${b.id}/package`, title: 'Download as .pmbook for the app', text: '⬇ .pmbook' }))));
    }
    this.root.append(list);
    this.root.append(el('p.muted.small', { text: 'Tip: drop a .pmbook file here to upload it (managers).' }));
  }

  renderEditor() {
    const ctx = this.ctx;
    const book = this.book;
    const editable = ctx.canEdit;
    if (this.selSongId && !this.song) this.selSongId = this.selRecId = null;
    if (!this.selSongId && book.songs.length) this.selectSongSilently(songsInPageOrder(book)[0]);

    // toolbar
    const title = editable
      ? el('input.title-input', { type: 'text', value: book.title, maxlength: 200, 'aria-label': 'Book title' })
      : el('b', { text: book.title });
    if (editable) title.addEventListener('change', () => { book.title = title.value.trim() || 'Untitled Book'; ctx.markDirty(); });
    this.saveButton = editable ? button('💾 Save', () => ctx.saveBook(), { title: 'Save the book on the website (a new revision)' }) : null;
    const toolbar = el('div.toolbar',
      button('← Books', async () => { if (await ctx.confirmDiscard()) { ctx.closeBook(); this.render(); } }, { class: 'secondary' }),
      this.saveButton,
      this.doc.id ? el('a.button.secondary', { href: `/api/books/${this.doc.id}/package`, title: 'The last saved revision as a .pmbook for the apps', text: '⬇ .pmbook' }) : null,
      el('span.toolbar-sep'),
      el('label.inline-label', 'Book: ', title),
      el('span.muted.small', { text: this.doc.id ? `revision ${this.doc.revision}${this.doc.updatedBy ? ' · ' + this.doc.updatedBy : ''}` : 'not saved yet' }));
    this.updateSaveButton();

    const left = el('div.lib-left', this.renderSteps(), this.renderDocuments(editable), this.renderSongs(editable));
    const right = el('div.lib-right', el('div.hintbar', { text: this.stepHint() }), this.renderPageToolbar(editable), this.renderPages(editable));
    this.root.append(toolbar, el('div.lib-body', left, right));
  }

  selectSongSilently(song) {
    this.selSongId = song?.id || null;
    this.selRecId = song?.recordings[0]?.id || null;
  }

  updateSaveButton() {
    if (!this.saveButton) return;
    this.saveButton.classList.toggle('accent', this.ctx.dirty);
    this.saveButton.textContent = this.ctx.dirty ? '💾 Save changes' : '💾 Saved';
  }

  // ------------------------------------------------------------------ guided flow

  flow() {
    const book = this.book;
    const s1 = totalPages(book) > 0;
    const s2 = book.songs.length > 0;
    const withRec = book.songs.filter((s) => s.recordings.length > 0);
    const s3 = withRec.length > 0;
    const s4 = s3 && withRec.every((s) => this.isSongTimed(s));
    const current = !s1 ? 1 : !s2 ? 2 : !s3 ? 3 : !s4 ? 4 : 5;
    return { done: [s1, s2, s3, s4], current };
  }

  pageTurnsSet(song, rec) {
    return (rec.pageTimings || []).filter((t) => t.pageOffset > 0 && t.pageOffset < song.pageCount).length;
  }

  isRecTimed(song, rec) {
    return this.pageTurnsSet(song, rec) >= Math.max(0, song.pageCount - 1);
  }

  isSongTimed(song) {
    return song.recordings.length > 0 && song.recordings.some((r) => this.isRecTimed(song, r));
  }

  stepHint() {
    const book = this.book;
    const { current } = this.flow();
    if (!this.ctx.canEdit) return 'Select a song and press ▶ Play. Only the organization\'s managers can change this book.';
    switch (current) {
      case 1: return 'Step 1 of 4 · Add the PDF that holds your sheet music. Press “Add PDF…” or drop a PDF file here.';
      case 2: return 'Step 2 of 4 · Tell Practical Music which pages make up each song: press “Add song” and enter the title and the first and last page. Repeat for every song. Pages that belong to no song cannot be played.';
      case 3: return 'Step 3 of 4 · Add the recordings: select a song, then press “Add MP3…” or drop an MP3 file onto the song.';
      case 4: return 'Step 4 of 4 · Set the page turns: select a song and press “Edit timing…”. When that is done, press ▶ Play - and Save.';
      default: {
        const n = unassignedCount(book);
        return n > 0
          ? `Ready to play. ${n} page(s) are not part of any song yet - add a song for them if they are needed.`
          : this.ctx.dirty ? 'Ready to play. Press “Save changes” so everyone in your organization gets this version.' : 'Ready to play. You can add more recordings, refine the timing, or play.';
      }
    }
  }

  renderSteps() {
    const { done, current } = this.flow();
    const names = ['1 · PDF', '2 · Songs', '3 · Recordings', '4 · Timing'];
    return el('div.steps', names.map((n, i) => el('span.step', { class: 'step ' + (done[i] ? 'done' : current === i + 1 ? 'current' : ''), text: n })));
  }

  renderDocuments(editable) {
    const book = this.book;
    const section = el('section.section', el('h2', { text: '1 · Sheet music (PDF)' }),
      el('p.muted.small', { text: totalPages(book) === 0 ? 'No PDF yet.' : `${totalPages(book)} pages in ${book.documents.length} PDF file(s)` }));
    book.documents.forEach((d, i) => {
      section.append(el('div.doc-row',
        el('div', el('b', { text: d.name }), el('div.muted.small', { text: `${d.pageCount} pages` })),
        editable ? el('span.row-actions',
          button('↑', () => this.edit.moveDocument(d, -1), { class: 'icon', title: 'Move up', disabled: i === 0 }),
          button('↓', () => this.edit.moveDocument(d, +1), { class: 'icon', title: 'Move down', disabled: i === book.documents.length - 1 }),
          button('✕', () => this.edit.removeDocument(d), { class: 'icon', title: 'Remove this PDF from the book' })) : null));
    });
    if (editable) section.append(button('📄 Add PDF…', () => this.edit.addPdf(), { class: this.flow().current === 1 ? 'accent wide' : 'wide' }));
    return section;
  }

  renderSongs(editable) {
    const book = this.book;
    if (totalPages(book) === 0) return el('div');
    const { current } = this.flow();
    const section = el('section.section',
      el('div.section-head', el('h2', { text: '2 · Songs' }),
        editable ? button('➕ Add song', () => this.edit.addSong(), { class: current === 2 ? 'accent' : '' }) : null));
    const unassigned = unassignedCount(book);
    section.append(el('p.muted.small', {
      text: book.songs.length === 0 ? 'No songs yet. Each song is a range of pages; press “Add song” to define the first one.'
        : unassigned > 0 ? `${unassigned} page(s) do not belong to a song yet. Select a song to play it, rename it, change its pages, or add recordings.`
          : 'Select a song to play it' + (editable ? ', rename it, change its pages, or add recordings.' : '.')
    }));

    const list = el('div.song-list', { role: 'listbox' });
    for (const s of songsInPageOrder(book)) {
      const recs = s.recordings.length;
      const missing = s.recordings.some((r) => !isRecordingAvailable(this.doc, r));
      const pages = s.pageCount === 1 ? `Page ${s.startPage + 1}` : `Pages ${s.startPage + 1}–${songEnd(s) + 1}`;
      list.append(el('button.song-item', {
        type: 'button', role: 'option', class: 'song-item' + (s.id === this.selSongId ? ' selected' : ''), dataset: { songId: s.id },
        onclick: () => this.selectSong(s)
      },
      el('b', { text: s.title }),
      el('span.muted.small', { text: `${pages} · ${recs === 0 ? 'no recording yet' : recs === 1 ? '1 recording' : recs + ' recordings'}${missing ? ' · ⚠ file missing' : ''}` })));
    }
    section.append(list);

    const song = this.song;
    if (song) section.append(this.renderSelectedSong(song, editable));
    return section;
  }

  renderSelectedSong(song, editable) {
    const { current } = this.flow();
    const hasRec = song.recordings.length > 0;
    const box = el('div.selected-song',
      el('div.muted.small', { text: 'Selected song' }),
      el('div.selected-title', { text: song.title }),
      el('div.muted.small', { text: song.pageCount === 1 ? `Page ${song.startPage + 1}` : `Pages ${song.startPage + 1}–${songEnd(song) + 1}` }),
      el('div.button-row',
        button('▶ Play', () => this.ctx.navigate('player', { songId: song.id, recordingId: this.selRecId }), { class: 'accent', disabled: !hasRec }),
        editable ? button('⏱ Edit timing…', () => this.edit.editTiming(), { class: current === 4 ? 'accent' : '', disabled: !hasRec, title: 'Mark when each page starts in the recording and add markers' }) : null,
        editable ? button('Rename', () => this.edit.renameSong()) : null,
        editable ? button('Pages…', () => this.edit.editSongPages(), { title: 'Change the first and last page of this song' }) : null,
        editable ? button('Remove', () => this.edit.removeSong(), { class: 'danger-text' }) : null));

    box.append(el('div.subhead', { text: '3 · Recordings of this song' }));
    if (editable) {
      box.append(el('div.muted.small', { text: 'Drop MP3 files onto the song, or:' }),
        el('div.button-row',
          button('🎵 Add MP3…', () => this.edit.addRecordings(), { class: current === 3 ? 'accent' : '' }),
          button('🌐 Add from URL…', () => this.edit.addRecordingUrl(), { title: 'Direct link to an audio file on the web (https://…/song.mp3)' })));
    }
    const recList = el('div.rec-list');
    for (const r of song.recordings) {
      recList.append(el('button.rec-item', {
        type: 'button', class: 'rec-item' + (r.id === this.selRecId ? ' selected' : ''),
        onclick: () => { this.selRecId = r.id; this.render(); }
      },
      el('b', { text: r.name }),
      el('span.muted.small', { text: r.source?.fileName || r.source?.url || '' }),
      el('span.small', { class: 'small ' + (isRecordingAvailable(this.doc, r) ? 'muted' : 'warn-text'), text: this.recordingStatus(song, r) })));
    }
    if (hasRec) box.append(recList);
    const rec = this.recording;
    if (rec && editable) {
      box.append(el('div.button-row.small',
        el('span.muted.small', { text: 'Selected recording:' }),
        button('Rename', () => this.edit.renameRecording(), { class: 'small' }),
        button('Replace file…', () => this.edit.replaceRecordingFile(), { class: 'small', title: 'Upload a different audio file for this recording (its timing is kept)' }),
        button('Remove', () => this.edit.removeRecording(), { class: 'small danger-text' })));
    }
    return box;
  }

  recordingStatus(song, r) {
    if (!isRecordingAvailable(this.doc, r)) return '⚠ file missing - press Replace file…';
    if (r.source?.kind === 'url') return 'web address (played straight from it)';
    const needed = Math.max(0, song.pageCount - 1);
    const markers = (r.markers || []).length;
    if (needed === 0) return markers === 0 ? 'single page · no markers yet (optional)' : `single page · ${markers} markers`;
    const set = this.pageTurnsSet(song, r);
    return `${set} of ${needed} page turns set · ${markers} markers` + (set >= needed ? '' : ' - press Edit timing…');
  }

  // ------------------------------------------------------------------ page overview

  renderPageToolbar(editable) {
    const book = this.book;
    const bar = el('div.page-toolbar');
    if (this.selPage == null || this.selPage >= totalPages(book)) { bar.hidden = true; return bar; }
    const p = this.selPage;
    const owner = songAtPage(book, p);
    bar.append(el('b', { text: owner ? `Page ${p + 1} · “${owner.title}”` : `Page ${p + 1} · not in a song` }));
    if (editable) {
      const song = this.song;
      if (!songStartingAt(book, p)) bar.append(button('🎶 New song starts here', () => this.edit.addSongAtPage(p)));
      if (song && p !== song.startPage) bar.append(button(`“${song.title}” starts here`, () => this.edit.setSongStart(p)));
      if (song && p >= song.startPage && p !== songEnd(song)) bar.append(button(`“${song.title}” ends here`, () => this.edit.setSongEnd(p)));
    }
    return bar;
  }

  renderPages(editable) {
    const book = this.book;
    const total = totalPages(book);
    if (total === 0) {
      return el('div.empty-pages',
        el('div.big-icon', { text: '📄' }),
        el('h2', { text: editable ? 'Start with your sheet music' : 'This book has no pages yet' }),
        editable ? el('p.muted', { text: 'Add the PDF that contains the songs. One PDF can hold many songs; you will mark where each song starts and ends in the next step.' }) : null,
        editable ? button('📄 Add PDF…', () => this.edit.addPdf(), { class: 'accent big' }) : null,
        editable ? el('p.muted.small', { text: '…or drop a PDF file anywhere here.' }) : null);
    }
    const key = book.documents.map((d) => d.id + ':' + d.pageCount + ':' + (d.source?.entryName || '')).join('|');
    if (key !== this.thumbKey) {
      this.thumbs.clear();
      this.thumbKey = key;
    }
    const grid = el('div.page-grid');
    const width = 170 * (window.devicePixelRatio || 1);
    const selected = this.song;
    for (let p = 0; p < total; p++) {
      let thumb = this.thumbs.get(p);
      if (!thumb) {
        const located = resolvePage(book, p);
        const url = pageImageUrl(this.doc, p, width);
        thumb = {
          node: el('button.thumb', { type: 'button', dataset: { page: String(p) } }),
          img: url ? el('img', { src: url, alt: `Page ${p + 1}`, loading: 'lazy', decoding: 'async' }) : el('div.thumb-missing', { text: 'PDF missing' }),
          caption: el('span.thumb-caption', { text: `Page ${p + 1}` }),
          sub: el('span.thumb-sub', { text: `${located?.doc.name || ''} p.${(located?.pageIndex ?? 0) + 1}` }),
          songLine: el('span.thumb-song')
        };
        thumb.node.append(el('div.thumb-img', thumb.img), thumb.caption, thumb.sub, thumb.songLine);
        thumb.node.addEventListener('click', () => {
          this.selPage = p;
          const owner = songAtPage(this.book, p);
          if (owner && owner.id !== this.selSongId) this.selectSongSilently(owner);
          this.render();
        });
        this.thumbs.set(p, thumb);
      }
      const owner = songAtPage(book, p);
      thumb.songLine.textContent = owner ? (owner.startPage === p ? '▶ ' : '') + owner.title : 'not in a song yet';
      thumb.songLine.classList.toggle('unassigned', !owner);
      thumb.node.classList.toggle('in-selected-song', !!selected && p >= selected.startPage && p <= songEnd(selected));
      thumb.node.classList.toggle('selected', p === this.selPage);
      if (owner) thumb.node.dataset.songId = owner.id; else delete thumb.node.dataset.songId;
      grid.append(thumb.node);
    }
    return grid;
  }

}
