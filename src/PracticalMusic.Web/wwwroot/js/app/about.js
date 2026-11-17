// Short help inside the music app (the full About page is at /About).

import { el } from './util.js';

export class AboutView {
  constructor(ctx) {
    this.ctx = ctx;
    const steps = [
      'Add the PDF: in the Library press “Add PDF…” (or drop the PDF file in). A PDF can hold many songs.',
      'Identify the songs: press “Add song”, give it a title and the first and last page. Repeat for every song. (Or click the first page of a song in the page overview and choose “New song starts here”.)',
      'Add recordings: select a song, then press “Add MP3…” or drop MP3 files onto the song. A song can have several recordings (vocals, piano, …); one plays at a time.',
      'Edit timing: select the song and press “Edit timing…”. Play the recording; when the music reaches the top of the next page press “Page N starts now” (key N). Tap the page where the music is to add markers, which drive the highlight and tap-to-seek.',
      'Save: managers press “Save changes” so everyone in the organization gets the book. Each save is a new revision; the apps can download it from their Online page.',
      'Play: pages turn by themselves and the current line is highlighted; tap a spot on the page to jump there. Landscape shows two pages, portrait one. “Full display” (key F) makes the pages fill the screen; tap to show the controls.',
      'Play together: on the Share tab one person leads, everybody else follows - in browsers and in the apps.'
    ];
    this.root = el('div.about',
      el('h1', { text: 'Practical Music' }),
      el('p', { text: 'Sheet music that follows the recording. Books belong to your organization; managers maintain them, members play them.' }),
      ctx.orgId ? null : el('div.alert.warn', el('p', { text: 'You do not have access to an organization yet. Ask to join one on your account page: ' }), el('a', { href: '/Account/Organizations', text: 'My organizations' })),
      el('h2', { text: 'Quick start' }),
      el('ol', steps.map((s) => el('li', { text: s }))),
      el('h2', { text: 'Keys' }),
      el('p.muted', { text: 'Player: Space play/pause · ←/→ ±5 s · PageUp/PageDown turn pages · N/P next/previous song · S stop · F full display · Esc leave full display. Timing editor: N page starts now · ←/→ ±2 s · Delete removes the selected marker.' }),
      el('h2', { text: 'iPhone and iPad' }),
      el('p', { text: 'In Safari tap Share → Add to Home Screen: Practical Music then opens full screen like an app. Before following a leader, tap the page once so the browser allows the music to start by itself. Keep the screen on while playing (the website asks the browser to).' }),
      el('p', el('a', { href: '/About', text: 'More about Practical Music, credits and the prompts it was made from →' })));
  }

  activate() {}
}
