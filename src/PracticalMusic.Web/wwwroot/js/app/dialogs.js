// In-page dialogs, the counterpart of the apps' DialogService: one at a time, Enter = first button, Esc = cancel.

import { el, clear } from './util.js';

const queue = [];
let current = null;

function root() {
  return document.getElementById('dialog-root');
}

function showNext() {
  if (current || queue.length === 0) return;
  current = queue.shift();
  const r = root();
  clear(r);
  r.append(current.node);
  r.classList.add('open');
  const focus = current.node.querySelector('input, textarea, select') || current.node.querySelector('button.accent') || current.node.querySelector('button');
  setTimeout(() => focus?.focus(), 30);
}

function close(result) {
  if (!current) return;
  const c = current;
  current = null;
  const r = root();
  clear(r);
  r.classList.remove('open');
  c.resolve(result);
  showNext();
}

document.addEventListener('keydown', (e) => {
  if (!current) return;
  if (e.key === 'Escape') {
    e.preventDefault();
    if (current.cancelValue !== undefined) close(current.cancelValue); // progress dialogs cannot be dismissed
  }
  else if (e.key === 'Enter' && !e.shiftKey && e.target?.tagName !== 'TEXTAREA' && current.defaultValue !== undefined) {
    e.preventDefault();
    close(current.collect ? current.collect(current.defaultValue) : current.defaultValue);
  }
}, true);

export const isOpen = () => current !== null;

/**
 * @param {{title:string, message?:string, body?:Node, buttons:{text:string,value:any,accent?:boolean,danger?:boolean}[], cancelValue?:any, collect?:(v:any)=>any}} spec
 */
export function dialog(spec) {
  return new Promise((resolve) => {
    const buttons = el('div.dialog-buttons');
    const box = el('div.dialog', { role: 'dialog', 'aria-modal': 'true', 'aria-label': spec.title },
      el('h2', { text: spec.title }),
      spec.message ? el('p.dialog-message', { text: spec.message }) : null,
      spec.body || null,
      buttons);
    const entry = { node: el('div.dialog-backdrop', box), resolve, cancelValue: spec.cancelValue ?? null, collect: spec.collect, defaultValue: spec.buttons[0]?.value };
    for (const b of spec.buttons) {
      buttons.append(el('button', {
        type: 'button', class: b.accent ? 'accent' : b.danger ? 'danger' : 'secondary', text: b.text,
        onclick: () => close(spec.collect ? spec.collect(b.value) : b.value)
      }));
    }
    entry.node.addEventListener('pointerdown', (e) => { if (e.target === entry.node && spec.cancelValue !== undefined) close(spec.cancelValue); });
    queue.push(entry);
    showNext();
  });
}

export const message = (title, text) => dialog({ title, message: text, buttons: [{ text: 'OK', value: true, accent: true }], cancelValue: true });

export const confirm = async (title, text, yes = 'Yes', no = 'No', danger = false) =>
  (await dialog({ title, message: text, buttons: [{ text: yes, value: true, accent: !danger, danger }, { text: no, value: false }], cancelValue: false })) === true;

/** Several choices; resolves with the chosen value or null. */
export const choose = (title, text, choices) =>
  dialog({ title, message: text, buttons: choices.map((c, i) => ({ ...c, accent: i === 0 && !c.danger })), cancelValue: null });

export async function prompt(title, text, initial = '', ok = 'OK', inputType = 'text') {
  const input = el('input', { type: inputType, value: initial });
  const result = await dialog({
    title, message: text, body: el('div.dialog-field', input),
    buttons: [{ text: ok, value: 'ok', accent: true }, { text: 'Cancel', value: 'cancel' }], cancelValue: 'cancel'
  });
  return result === 'ok' ? input.value : null;
}

/** Title + first/last page (1-based). Resolves { text, from, to } or null. */
export async function promptRange({ title, message: text, inputLabel, initial, fromLabel, toLabel, from, to, min, max, ok = 'OK', hint = '' }) {
  max = Math.max(min, max);
  const input = el('input', { type: 'text', value: initial ?? '' });
  const a = el('input', { type: 'number', min, max, step: 1, value: Math.min(Math.max(from, min), max), inputmode: 'numeric' });
  const b = el('input', { type: 'number', min, max, step: 1, value: Math.min(Math.max(to, min), max), inputmode: 'numeric' });
  const body = el('div',
    el('label.dialog-field', inputLabel, input),
    el('div.dialog-range', el('label', fromLabel, a), el('label', toLabel, b)),
    hint ? el('p.muted.small', { text: hint }) : null);
  const result = await dialog({
    title, message: text, body,
    buttons: [{ text: ok, value: 'ok', accent: true }, { text: 'Cancel', value: 'cancel' }], cancelValue: 'cancel'
  });
  if (result !== 'ok') return null;
  const clampInt = (v, fallback) => {
    const n = parseInt(v, 10);
    return Number.isFinite(n) ? Math.min(Math.max(n, min), max) : fallback;
  };
  const x = clampInt(a.value, min);
  const y = clampInt(b.value, x);
  return { text: input.value, from: Math.min(x, y), to: Math.max(x, y) };
}

/** A progress dialog; call set(fraction), text(message) and close(). */
export function progress(title, text) {
  const bar = el('progress', { max: 1, value: 0 });
  const msg = el('p.dialog-message', { text });
  const backdrop = el('div.dialog-backdrop', el('div.dialog', { role: 'dialog', 'aria-label': title }, el('h2', { text: title }), msg, bar));
  let closed = false;
  let resolveFn = () => {};
  const entry = { node: backdrop, resolve: () => resolveFn(), cancelValue: undefined, defaultValue: undefined };
  queue.push(entry);
  showNext();
  return {
    set(fraction) { bar.value = Math.min(1, Math.max(0, fraction)); },
    text(t) { msg.textContent = t; },
    close() {
      if (closed) return;
      closed = true;
      if (current === entry) close(null);
      else {
        const i = queue.indexOf(entry);
        if (i >= 0) queue.splice(i, 1);
      }
    }
  };
}
