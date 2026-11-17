// Small DOM and formatting helpers shared by the views. User-supplied text is only ever inserted with
// textContent (never innerHTML), so names of books, songs and people cannot inject markup.

/** el('div.cls#id', {attrs/on...}, children...) */
export function el(spec, props, ...children) {
  const m = /^([a-z0-9-]+)?((?:[.#][\w-]+)*)$/i.exec(spec) || [null, 'div', ''];
  const node = document.createElement(m[1] || 'div');
  for (const part of (m[2] || '').match(/[.#][\w-]+/g) || []) {
    if (part[0] === '.') node.classList.add(part.slice(1));
    else node.id = part.slice(1);
  }
  if (props && (typeof props !== 'object' || props instanceof Node || Array.isArray(props))) {
    children.unshift(props);
    props = null;
  }
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v === undefined || v === null || v === false) continue;
      if (k.startsWith('on') && typeof v === 'function') node.addEventListener(k.slice(2).toLowerCase(), v);
      else if (k === 'text') node.textContent = v;
      else if (k === 'class') node.className = v;
      else if (k === 'style' && typeof v === 'object') Object.assign(node.style, v);
      else if (k === 'dataset') Object.assign(node.dataset, v);
      else if (v === true) node.setAttribute(k, '');
      else if (k in node && typeof v !== 'string') node[k] = v;
      else node.setAttribute(k, v);
    }
  }
  append(node, children);
  return node;
}

function append(node, children) {
  for (const c of children) {
    if (c === null || c === undefined || c === false) continue;
    if (Array.isArray(c)) append(node, c);
    else node.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
}

export function clear(node) {
  while (node.firstChild) node.removeChild(node.firstChild);
  return node;
}

export function button(label, onClick, opts = {}) {
  const b = el('button', { type: 'button', class: opts.class || '', title: opts.title, disabled: opts.disabled }, label);
  if (onClick) b.addEventListener('click', (e) => { e.preventDefault(); onClick(e); });
  return b;
}

export function show(node, visible) {
  if (node) node.hidden = !visible;
}

export function formatTime(ms) {
  if (!Number.isFinite(ms) || ms < 0) ms = 0;
  const total = Math.floor(ms / 1000);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const tenth = Math.floor((ms % 1000) / 100);
  return h >= 1 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}.${tenth}`;
}

const pad = (n) => String(n).padStart(2, '0');

export function formatBytes(b) {
  if (b >= 1 << 30) return (b / (1 << 30)).toFixed(1) + ' GB';
  if (b >= 1 << 20) return (b / (1 << 20)).toFixed(1) + ' MB';
  if (b >= 1 << 10) return Math.round(b / 1024) + ' KB';
  return b + ' B';
}

export function formatDate(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }) + ' ' +
    d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
}

export function clamp(v, lo, hi) {
  return Math.min(hi, Math.max(lo, v));
}

/** Monotonic milliseconds (the local clock used for synchronized playback). */
export const now = () => performance.now();

/** Simple event hub. */
export class Emitter {
  #handlers = new Map();
  on(name, fn) {
    if (!this.#handlers.has(name)) this.#handlers.set(name, new Set());
    this.#handlers.get(name).add(fn);
    return () => this.#handlers.get(name)?.delete(fn);
  }
  emit(name, ...args) {
    for (const fn of [...(this.#handlers.get(name) || [])]) {
      try { fn(...args); } catch (e) { console.error(e); }
    }
  }
}

/** True on phones/tablets where the screen is touch only (no hover). */
export const isTouch = () => window.matchMedia('(hover: none)').matches;

export function fileBaseName(name) {
  const n = String(name || '').split(/[\\/]/).pop();
  const dot = n.lastIndexOf('.');
  return dot > 0 ? n.slice(0, dot) : n;
}

export const AUDIO_EXTENSIONS = ['.mp3', '.m4a', '.mp4', '.aac', '.wav', '.ogg', '.oga', '.opus', '.flac', '.wma'];
export const isPdfName = (n) => /\.pdf$/i.test(n || '');
export const isAudioName = (n) => AUDIO_EXTENSIONS.some((e) => String(n || '').toLowerCase().endsWith(e));
export const isBookName = (n) => /\.pmbook$/i.test(n || '');

/** Opens the file chooser; resolves with the picked files (possibly empty). */
export function pickFiles({ accept = '', multiple = false } = {}) {
  return new Promise((resolve) => {
    const input = el('input', { type: 'file', accept, style: { display: 'none' } });
    input.multiple = multiple;
    let done = false;
    const finish = (files) => { if (!done) { done = true; resolve(files); input.remove(); } };
    input.addEventListener('change', () => finish([...(input.files || [])]));
    input.addEventListener('cancel', () => finish([]));
    document.body.append(input);
    input.click();
  });
}
