// Editing commands of the Library, ported from LibraryViewModel.cs so books are edited the same way in the
// browser as in the apps. Files are uploaded to the organization's media store as soon as they are added;
// the book itself is stored when the manager presses Save.

import {
  newId, totalPages, firstPageOf, songEnd, songAtPage, songStartingAt, normalizeSongs, firstUnassignedRun,
  unassignedHint, unassignedCount, uniqueEntryName, isRecordingAvailable
} from './model.js';
import { upload } from './api.js';
import * as dialogs from './dialogs.js';
import { pickFiles, fileBaseName, isPdfName, isAudioName, isBookName } from './util.js';

const AUDIO_ACCEPT = 'audio/*,.mp3,.m4a,.aac,.wav,.ogg,.oga,.opus,.flac,.wma';

export class BookEditing {
  constructor(view) {
    this.view = view;
  }

  get ctx() { return this.view.ctx; }
  get doc() { return this.ctx.doc; }
  get book() { return this.ctx.doc.book; }

  changed(status) {
    normalizeSongs(this.book);
    this.ctx.markDirty();
    if (status) this.ctx.setStatus(status);
  }

  /** Uploads one file to the organization; resolves with the stored media info (or null after showing the error). */
  async uploadMedia(file, kind) {
    const p = dialogs.progress('Uploading', `${file.name}…`);
    try {
      const info = await upload(`/api/orgs/${this.doc.orgId}/media`, file, (f) => p.set(f));
      if (kind && info.kind !== kind) throw new Error(`“${file.name}” is not ${kind === 'pdf' ? 'a PDF' : 'an audio file'}.`);
      return info;
    } catch (e) {
      p.close();
      await dialogs.message(kind === 'pdf' ? 'Could not add the PDF' : 'Could not add the recording', e.message);
      return null;
    } finally {
      p.close();
    }
  }

  embeddedSource(file, info) {
    const entryName = uniqueEntryName(this.doc, file.name);
    this.doc.media[entryName] = info;
    return { kind: 'embedded', entryName, fileName: file.name, sizeBytes: info.sizeBytes };
  }

  // ------------------------------------------------------------------ step 1: PDFs

  async addPdf() {
    const files = await pickFiles({ accept: '.pdf,application/pdf', multiple: true });
    for (const f of files) await this.addPdfFile(f);
  }

  async addPdfFile(file) {
    const info = await this.uploadMedia(file, 'pdf');
    if (!info) return;
    const doc = { id: newId(), name: fileBaseName(file.name), source: this.embeddedSource(file, info), pageCount: info.pageCount || 0 };
    const book = this.book;
    book.documents.push(doc);
    if (book.songs.length === 0 && book.documents.length === 1 && (!book.title || book.title === 'Untitled Book' || book.title === 'My Songs'))
      book.title = doc.name;
    this.changed(`Added ${doc.name} (${doc.pageCount} pages). Next: identify the songs.`);
  }

  async removeDocument(doc) {
    if (!(await dialogs.confirm('Remove PDF', `Remove “${doc.name}” and its ${doc.pageCount} pages from the book? Songs on those pages are removed too.`, 'Remove', 'Keep', true))) return;
    const book = this.book;
    const first = firstPageOf(book, doc);
    if (first < 0) return;
    const last = first + doc.pageCount - 1;
    book.songs = book.songs.filter((s) => !(s.startPage >= first && s.startPage <= last));
    for (const s of book.songs) {
      if (s.startPage > last) s.startPage -= doc.pageCount;
      else if (s.startPage < first && songEnd(s) >= first) s.pageCount = first - s.startPage;
    }
    book.documents = book.documents.filter((d) => d !== doc);
    this.view.selPage = null;
    this.changed();
  }

  moveDocument(doc, delta) {
    const book = this.book;
    const index = book.documents.indexOf(doc);
    const target = index + delta;
    if (index < 0 || target < 0 || target >= book.documents.length) return;
    // songs travel with their pages: remember (document, offset inside it)
    const positions = book.songs.map((s) => {
      let remaining = s.startPage;
      for (const d of book.documents) {
        if (remaining < d.pageCount) return { song: s, doc: d, offset: remaining };
        remaining -= d.pageCount;
      }
      return { song: s, doc: null, offset: 0 };
    });
    book.documents.splice(index, 1);
    book.documents.splice(target, 0, doc);
    for (const { song, doc: d, offset } of positions) if (d) song.startPage = firstPageOf(book, d) + offset;
    this.view.selPage = null;
    this.changed();
  }

  // ------------------------------------------------------------------ step 2: songs

  /** Title + page range dialog; the song before the range is shortened, a song that starts inside it is a conflict. */
  async addSongWithDialog(dialogTitle, initialTitle, start, end) {
    const book = this.book;
    const total = totalPages(book);
    if (total === 0) return null;
    let title = initialTitle;
    const hint = unassignedCount(book) === 0
      ? 'Every page already belongs to a song. The song before the new range is shortened to end just before it.'
      : unassignedHint(book);
    for (;;) {
      const r = await dialogs.promptRange({
        title: dialogTitle, message: 'Enter the title of the song and the pages (of the whole book) that belong to it.',
        inputLabel: 'Song title', initial: title, fromLabel: 'First page', toLabel: 'Last page',
        from: start + 1, to: end + 1, min: 1, max: total, ok: 'Add song', hint
      });
      if (!r) return null;
      title = r.text.trim() || `Song ${book.songs.length + 1}`;
      start = r.from - 1;
      end = r.to - 1;
      const conflict = book.songs.find((s) => s.startPage >= start && s.startPage <= end);
      if (conflict) {
        await dialogs.message('Pages already taken', `Page ${conflict.startPage + 1} is the first page of “${conflict.title}” (pages ${conflict.startPage + 1}–${songEnd(conflict) + 1}). Choose a different range, or select that song and change its pages first.`);
        continue;
      }
      const song = { id: newId(), title, startPage: start, pageCount: end - start + 1, recordings: [], highlightHeight: 0.08 };
      for (const prev of book.songs) if (prev.startPage < start && songEnd(prev) >= start) prev.pageCount = start - prev.startPage;
      book.songs.push(song);
      this.view.selectSongSilently(song);
      this.changed(`Added “${song.title}” (pages ${song.startPage + 1}–${songEnd(song) + 1}). Next: add its recording.`);
      return song;
    }
  }

  async addSong() {
    const book = this.book;
    if (totalPages(book) === 0) {
      await dialogs.message('Add a PDF first', 'Songs are ranges of pages, so the book needs a PDF before songs can be added.');
      return;
    }
    const run = firstUnassignedRun(book);
    const last = totalPages(book) - 1;
    await this.addSongWithDialog('Add song', `Song ${book.songs.length + 1}`, run?.start ?? last, run?.end ?? last);
  }

  async addSongAtPage(page) {
    const book = this.book;
    const existing = songStartingAt(book, page);
    if (existing) { this.view.selectSong(existing); return; }
    const next = book.songs.filter((s) => s.startPage > page).sort((a, b) => a.startPage - b.startPage)[0];
    const end = next ? next.startPage - 1 : totalPages(book) - 1;
    await this.addSongWithDialog(`New song starting on page ${page + 1}`, `Song ${book.songs.length + 1}`, page, end);
  }

  async editSongPages() {
    const song = this.view.song;
    if (!song) return;
    const book = this.book;
    let title = song.title;
    let start = song.startPage;
    let end = songEnd(song);
    for (;;) {
      const r = await dialogs.promptRange({
        title: 'Song pages', message: 'Change the title or the pages (of the whole book) that belong to this song.',
        inputLabel: 'Song title', initial: title, fromLabel: 'First page', toLabel: 'Last page',
        from: start + 1, to: end + 1, min: 1, max: totalPages(book), ok: 'Apply', hint: unassignedHint(book)
      });
      if (!r) return;
      title = r.text.trim() || song.title;
      start = r.from - 1;
      end = r.to - 1;
      const conflict = book.songs.find((s) => s !== song && s.startPage >= start && s.startPage <= end);
      if (conflict) {
        await dialogs.message('Pages already taken', `Page ${conflict.startPage + 1} is the first page of “${conflict.title}” (pages ${conflict.startPage + 1}–${songEnd(conflict) + 1}). Choose a different range, or change that song's pages first.`);
        continue;
      }
      const hasTiming = song.recordings.some((rec) => (rec.markers || []).length > 0 || (rec.pageTimings || []).some((t) => t.pageOffset > 0));
      if (start !== song.startPage && hasTiming &&
        !(await dialogs.confirm('First page changes', 'Page times and markers are counted from the first page of the song. Moving the first page shifts them; you may need to redo the timing. Continue?', 'Continue', 'Cancel')))
        return;
      song.title = title;
      song.startPage = start;
      song.pageCount = end - start + 1;
      for (const prev of book.songs) if (prev !== song && prev.startPage < start && songEnd(prev) >= start) prev.pageCount = start - prev.startPage;
      this.changed();
      return;
    }
  }

  setSongStart(page) {
    const song = this.view.song;
    if (!song) return;
    const book = this.book;
    if (book.songs.some((s) => s !== song && s.startPage === page)) return;
    const end = Math.max(songEnd(song), page);
    song.startPage = page;
    song.pageCount = Math.max(1, end - song.startPage + 1);
    for (const other of book.songs) {
      if (other === song) continue;
      if (other.startPage < song.startPage && songEnd(other) >= song.startPage) other.pageCount = Math.max(1, song.startPage - other.startPage);
      if (other.startPage > song.startPage && songEnd(song) >= other.startPage) song.pageCount = Math.max(1, other.startPage - song.startPage);
    }
    this.changed();
  }

  setSongEnd(page) {
    const song = this.view.song;
    if (!song || page < song.startPage) return;
    const book = this.book;
    const next = book.songs.filter((s) => s !== song && s.startPage > song.startPage).sort((a, b) => a.startPage - b.startPage)[0];
    const maxEnd = next ? next.startPage - 1 : totalPages(book) - 1;
    song.pageCount = Math.min(Math.max(page, song.startPage), maxEnd) - song.startPage + 1;
    this.changed();
  }

  async renameSong() {
    const song = this.view.song;
    if (!song) return;
    const title = await dialogs.prompt('Rename song', 'Title:', song.title);
    if (title && title.trim()) { song.title = title.trim(); this.changed(); }
  }

  async removeSong() {
    const song = this.view.song;
    if (!song) return;
    if (!(await dialogs.confirm('Remove song', `Remove “${song.title}” and its ${song.recordings.length} recording link(s)? The PDF pages stay in the book.`, 'Remove', 'Keep', true))) return;
    this.book.songs = this.book.songs.filter((s) => s !== song);
    this.view.selSongId = this.view.selRecId = null;
    this.changed();
  }

  // ------------------------------------------------------------------ step 3: recordings

  async addRecordings() {
    const song = this.view.song;
    if (!song) {
      await dialogs.message('Select a song', 'Select the song in the Songs list first, then add its recording.');
      return;
    }
    const files = await pickFiles({ accept: AUDIO_ACCEPT, multiple: true });
    for (const f of files) await this.addRecordingFile(song, f);
  }

  async addRecordingFile(song, file) {
    const info = await this.uploadMedia(file, 'audio');
    if (!info) return;
    this.addRecordingSource(song, this.embeddedSource(file, info), fileBaseName(file.name));
  }

  addRecordingSource(song, source, name) {
    const rec = { id: newId(), name: name || 'Recording', source, durationMs: 0, pageTimings: [{ pageOffset: 0, timeMs: 0 }], markers: [] };
    song.recordings.push(rec);
    this.view.selSongId = song.id;
    this.view.selRecId = rec.id;
    this.changed(song.pageCount > 1
      ? `Added “${rec.name}” to “${song.title}”. Next: press Edit timing… to set the page turns.`
      : `Added “${rec.name}” to “${song.title}”.`);
  }

  async addRecordingUrl() {
    const song = this.view.song;
    if (!song) return;
    let url = await dialogs.prompt('Add recording from a web address',
      'Enter a direct link to an audio file (for example https://example.com/song.mp3). Links to web pages such as Soundslice or YouTube cannot be played.', 'https://', 'Add', 'url');
    if (!url) return;
    url = url.trim();
    let parsed;
    try { parsed = new URL(url); } catch { parsed = null; }
    if (!parsed || (parsed.protocol !== 'https:' && parsed.protocol !== 'http:')) {
      await dialogs.message('Not a web address', 'Enter a complete http:// or https:// address.');
      return;
    }
    if (/(^|\.)soundslice\.com$/i.test(parsed.hostname)) {
      await dialogs.message('Not an audio file', 'Soundslice plays its audio inside the Soundslice website only; a slice link does not give access to the recording. If you have the rights to the audio, download or export it from Soundslice and add the MP3 file instead.');
      return;
    }
    const p = dialogs.progress('Checking address', url);
    const durationMs = await probeAudio(url);
    p.close();
    if (durationMs == null) {
      await dialogs.message('Not an audio file', 'This address does not play as audio here. Practical Music needs a direct link to the audio file itself (for example https://example.com/song.mp3).');
      return;
    }
    const fileName = decodeURIComponent(parsed.pathname.split('/').pop() || '') || 'recording';
    this.addRecordingSource(song, { kind: 'url', url, fileName, sizeBytes: 0 }, fileBaseName(fileName) || 'Recording');
    const rec = this.view.recording;
    if (rec && durationMs > 0) rec.durationMs = durationMs;
  }

  async renameRecording() {
    const rec = this.view.recording;
    if (!rec) return;
    const name = await dialogs.prompt('Rename recording', 'Name (e.g. Vocals, Piano, Click track):', rec.name);
    if (name && name.trim()) { rec.name = name.trim(); this.changed(); }
  }

  async removeRecording() {
    const song = this.view.song;
    const rec = this.view.recording;
    if (!song || !rec) return;
    if (!(await dialogs.confirm('Remove recording', `Remove “${rec.name}” from “${song.title}”? Its page times and markers are lost.`, 'Remove', 'Keep', true))) return;
    song.recordings = song.recordings.filter((r) => r !== rec);
    this.view.selRecId = song.recordings[0]?.id || null;
    this.changed();
  }

  /** The browser's "Locate file…": upload another audio file for the recording, keeping its timing. */
  async replaceRecordingFile() {
    const rec = this.view.recording;
    if (!rec) return;
    const [file] = await pickFiles({ accept: AUDIO_ACCEPT });
    if (!file) return;
    const info = await this.uploadMedia(file, 'audio');
    if (!info) return;
    rec.source = this.embeddedSource(file, info);
    rec.durationMs = 0;
    this.changed(`“${rec.name}” now plays ${file.name}.`);
  }

  // ------------------------------------------------------------------ step 4: timing

  async editTiming() {
    const song = this.view.song;
    if (!song) return;
    const rec = this.view.recording || song.recordings[0];
    if (!rec) {
      await dialogs.message('No recording yet', 'Add an MP3 to this song first; the timing links the recording to the pages.');
      return;
    }
    if (!isRecordingAvailable(this.doc, rec)) {
      await dialogs.message('File missing', `The audio file for “${rec.name}” is not available. Press “Replace file…” to upload it.`);
      return;
    }
    this.ctx.navigate('timing', { songId: song.id, recordingId: rec.id });
  }

  // ------------------------------------------------------------------ drag & drop

  /** Same rules as the app: PDFs are appended; audio goes to the song (or the page's song) it was dropped on. */
  async handleDrop(files, page, songId) {
    const pmbook = files.find((f) => isBookName(f.name));
    if (pmbook) { await this.ctx.importPackage(pmbook); return; }
    if (!this.doc) {
      await dialogs.message('Open a book first', 'Open a book (or create a new one), then drop its PDF and recordings here. A .pmbook file can be dropped right away.');
      return;
    }
    for (const f of files.filter((x) => isPdfName(x.name))) await this.addPdfFile(f);
    const audio = files.filter((x) => isAudioName(x.name));
    if (audio.length === 0) return;
    const book = this.book;
    if (totalPages(book) === 0) {
      await dialogs.message('Add the PDF first', 'Step 1 is the PDF with the sheet music. Recordings are added to songs in step 3.');
      return;
    }
    let song = songId ? book.songs.find((s) => s.id === songId) : null;
    if (!song && page != null) {
      song = songAtPage(book, page);
      if (!song) {
        const next = book.songs.filter((s) => s.startPage > page).sort((a, b) => a.startPage - b.startPage)[0];
        const end = next ? next.startPage - 1 : totalPages(book) - 1;
        song = await this.addSongWithDialog(`New song starting on page ${page + 1}`, fileBaseName(audio[0].name), page, end);
        if (!song) return;
      }
    }
    if (!song) {
      if (book.songs.length === 0) {
        await dialogs.message('Identify the songs first', 'Step 2 is to define the songs (which pages belong to which song). Press “Add song”, then drop the MP3 onto the song.');
        return;
      }
      song = this.view.song;
      if (!song) {
        await dialogs.message('Drop onto a song', 'Drop the MP3 onto a song in the Songs list (or onto one of its pages).');
        return;
      }
    }
    for (const f of audio) await this.addRecordingFile(song, f);
  }
}

/** Resolves with the duration (ms, 0 if unknown) when the browser can play the address as audio, otherwise null. */
function probeAudio(url) {
  return new Promise((resolve) => {
    const a = new Audio();
    a.preload = 'metadata';
    const done = (v) => { clearTimeout(timer); a.removeAttribute('src'); a.load(); resolve(v); };
    const timer = setTimeout(() => done(null), 15000);
    a.addEventListener('loadedmetadata', () => done(Number.isFinite(a.duration) ? a.duration * 1000 : 0), { once: true });
    a.addEventListener('error', () => done(null), { once: true });
    a.src = url;
  });
}
