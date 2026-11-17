// One sheet-music page with the playback highlight and the time markers - the browser counterpart of the
// apps' PageCanvas. Taps are reported in normalized page coordinates (0..1), so they work at any size.

import { el, formatTime } from './util.js';

const HIGHLIGHT_FILL = 'rgba(255, 200, 0, 0.35)';
const HIGHLIGHT_LINE = 'rgba(230, 170, 0, 0.85)';
const MARKER = 'rgba(30, 120, 255, 0.9)';
const MARKER_SELECTED = 'rgb(255, 69, 0)';

export class PageView {
  /**
   * @param {{onTap?: (t:{x:number,y:number,markerId?:string})=>void, onMarkerMoved?: (id:string,x:number,y:number)=>void, allowDrag?: boolean}} opts
   */
  constructor(opts = {}) {
    this.opts = opts;
    this.root = el('div.page-view');
    this.img = el('img.page-img', { alt: '', draggable: false, decoding: 'async' });
    this.canvas = el('canvas.page-overlay');
    this.labelEl = el('div.page-label');
    this.placeholder = el('div.page-placeholder');
    this.root.append(this.img, this.canvas, this.labelEl, this.placeholder);
    this.url = null;
    this.highlight = null;
    this.markers = [];
    this.showMarkers = false;
    this.selectedId = null;
    this.rect = { x: 0, y: 0, w: 0, h: 0 };
    this.drag = null;
    this.press = null;
    this.img.hidden = true;
    if (opts.allowDrag) this.root.classList.add('editing');

    new ResizeObserver(() => this.layout()).observe(this.root);
    this.root.addEventListener('pointerdown', (e) => this.#down(e));
    this.root.addEventListener('pointermove', (e) => this.#move(e));
    this.root.addEventListener('pointerup', (e) => this.#up(e));
    this.root.addEventListener('pointercancel', () => { this.drag = null; this.press = null; this.draw(); });
  }

  /** Device pixels wanted for the page image at the current size. */
  desiredWidth() {
    const w = this.root.clientWidth || 800;
    const h = this.root.clientHeight || 1000;
    // a letter/A4 page is about 1.3-1.41 times as tall as wide: the height can limit the width
    return Math.round(Math.min(w, h / 1.29) * (window.devicePixelRatio || 1));
  }

  setLabel(text) {
    this.labelEl.textContent = text || '';
    this.labelEl.hidden = !text;
  }

  /** Shows a page image (null = empty, with an optional message). The previous image stays until the new one has loaded. */
  setPage(url, emptyText = '') {
    if (url === this.url) return;
    this.url = url;
    if (!url) {
      this.img.hidden = true;
      this.img.removeAttribute('src');
      this.placeholder.textContent = emptyText;
      this.placeholder.hidden = !emptyText;
      this.draw();
      return;
    }
    this.placeholder.hidden = true;
    const loader = new Image();
    loader.decoding = 'async';
    loader.onload = () => {
      if (this.url !== url) return;
      this.img.src = url;
      this.img.hidden = false;
      this.natural = { w: loader.naturalWidth, h: loader.naturalHeight };
      this.layout();
    };
    loader.onerror = () => {
      if (this.url !== url) return;
      this.img.hidden = true;
      this.placeholder.textContent = 'This page could not be loaded.';
      this.placeholder.hidden = false;
    };
    loader.src = url;
  }

  setHighlight(region) {
    const same = (a, b) => (!a && !b) || (a && b && a.x1 === b.x1 && a.y1 === b.y1 && a.x2 === b.x2 && a.y2 === b.y2);
    if (same(region, this.highlight)) return;
    this.highlight = region;
    this.draw();
  }

  setMarkers(markers, show, selectedId = null) {
    this.markers = markers || [];
    this.showMarkers = !!show;
    this.selectedId = selectedId;
    this.draw();
  }

  layout() {
    const W = this.root.clientWidth;
    const H = this.root.clientHeight;
    if (!this.natural || W <= 0 || H <= 0) {
      this.rect = { x: 0, y: 0, w: W, h: H };
      this.draw();
      return;
    }
    const scale = Math.min(W / this.natural.w, H / this.natural.h);
    const w = this.natural.w * scale;
    const h = this.natural.h * scale;
    this.rect = { x: (W - w) / 2, y: (H - h) / 2, w, h };
    for (const node of [this.img, this.canvas]) {
      Object.assign(node.style, { left: this.rect.x + 'px', top: this.rect.y + 'px', width: w + 'px', height: h + 'px' });
    }
    this.labelEl.style.left = this.rect.x + 6 + 'px';
    this.labelEl.style.top = this.rect.y + 4 + 'px';
    this.draw();
  }

  draw() {
    const { w, h } = this.rect;
    const dpr = window.devicePixelRatio || 1;
    const c = this.canvas;
    if (!this.natural || this.img.hidden || w <= 0 || h <= 0) {
      c.width = 0;
      return;
    }
    if (c.width !== Math.round(w * dpr) || c.height !== Math.round(h * dpr)) {
      c.width = Math.round(w * dpr);
      c.height = Math.round(h * dpr);
    }
    const g = c.getContext('2d');
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);

    if (this.highlight) {
      const r = this.highlight;
      const x = r.x1 * w, y = r.y1 * h;
      const bw = Math.max(2, (r.x2 - r.x1) * w), bh = Math.max(2, (r.y2 - r.y1) * h);
      g.fillStyle = HIGHLIGHT_FILL;
      g.strokeStyle = HIGHLIGHT_LINE;
      g.lineWidth = 2;
      roundRect(g, x, y, bw, bh, 4);
      g.fill();
      g.stroke();
    }

    if (this.showMarkers) {
      g.font = '11px system-ui, -apple-system, Segoe UI, Roboto, sans-serif';
      g.textBaseline = 'middle';
      for (const m of this.markers) {
        const dragged = this.drag?.moved && this.drag.id === m.id;
        const px = (dragged ? this.drag.x : m.x) * w;
        const py = (dragged ? this.drag.y : m.y) * h;
        const selected = m.id === this.selectedId || dragged;
        const radius = selected ? 9 : 7;
        g.beginPath();
        g.arc(px, py, radius, 0, Math.PI * 2);
        g.fillStyle = selected ? MARKER_SELECTED : MARKER;
        g.fill();
        g.lineWidth = 2;
        g.strokeStyle = '#fff';
        g.stroke();
        const text = m.label || formatTime(m.timeMs);
        const tw = g.measureText(text).width;
        g.fillStyle = 'rgba(255,255,255,0.8)';
        g.fillRect(px + radius + 1, py - 8, tw + 4, 16);
        g.fillStyle = selected ? MARKER_SELECTED : MARKER;
        g.fillText(text, px + radius + 3, py);
      }
    }
  }

  #toPage(e) {
    const b = this.root.getBoundingClientRect();
    const x = (e.clientX - b.left - this.rect.x) / this.rect.w;
    const y = (e.clientY - b.top - this.rect.y) / this.rect.h;
    return { x, y, px: e.clientX, py: e.clientY };
  }

  #markerAt(p) {
    if (!this.showMarkers || !this.opts.allowDrag) return null;
    let best = null;
    let bestDist = 16;
    for (const m of this.markers) {
      const d = Math.hypot((m.x - p.x) * this.rect.w, (m.y - p.y) * this.rect.h);
      if (d <= bestDist) { best = m; bestDist = d; }
    }
    return best;
  }

  #down(e) {
    if (e.button !== undefined && e.button !== 0) return;
    if (!this.natural) return;
    const p = this.#toPage(e);
    this.press = p;
    const m = this.#markerAt(p);
    if (m) {
      this.drag = { id: m.id, x: m.x, y: m.y, moved: false };
      this.root.setPointerCapture?.(e.pointerId);
      e.preventDefault();
    }
  }

  #move(e) {
    if (!this.drag || !this.press) return;
    const p = this.#toPage(e);
    if (!this.drag.moved && Math.hypot(p.px - this.press.px, p.py - this.press.py) < 6) return;
    this.drag.moved = true;
    this.drag.x = Math.min(1, Math.max(0, p.x));
    this.drag.y = Math.min(1, Math.max(0, p.y));
    this.draw();
    e.preventDefault();
  }

  #up(e) {
    const press = this.press;
    this.press = null;
    if (!press) return;
    const p = this.#toPage(e);
    if (this.drag) {
      const d = this.drag;
      this.drag = null;
      if (d.moved) this.opts.onMarkerMoved?.(d.id, d.x, d.y);
      else {
        const m = this.markers.find((x) => x.id === d.id);
        if (m) this.opts.onTap?.({ x: m.x, y: m.y, markerId: m.id });
      }
      this.draw();
      return;
    }
    if (Math.hypot(p.px - press.px, p.py - press.py) > 12) return; // a scroll or swipe, not a tap
    if (p.x < 0 || p.x > 1 || p.y < 0 || p.y > 1) return;
    this.opts.onTap?.({ x: p.x, y: p.y });
  }
}

function roundRect(g, x, y, w, h, r) {
  g.beginPath();
  g.moveTo(x + r, y);
  g.arcTo(x + w, y, x + w, y + h, r);
  g.arcTo(x + w, y + h, x, y + h, r);
  g.arcTo(x, y + h, x, y, r);
  g.arcTo(x, y, x + w, y, r);
  g.closePath();
}

/** Warms the browser cache for upcoming pages so page turns are instant. */
export function preload(urls) {
  for (const u of urls) if (u) { const i = new Image(); i.decoding = 'async'; i.src = u; }
}
