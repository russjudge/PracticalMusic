// Talks to the website's JSON API (/api). Changes carry the anti-forgery token that the page was
// rendered with (header X-CSRF-TOKEN); errors arrive as { error, code, currentRevision }.

const csrf = () => document.querySelector('meta[name="csrf-token"]')?.content || '';

export class ApiError extends Error {
  constructor(message, status, code, currentRevision) {
    super(message);
    this.status = status;
    this.code = code;
    this.currentRevision = currentRevision;
  }
}

async function errorFrom(response) {
  let body = null;
  try { body = await response.json(); } catch { /* not JSON */ }
  const fallback = response.status === 401 ? 'You are signed out. Reload the page and sign in again.'
    : response.status === 403 ? 'You do not have permission for this.'
      : response.status === 413 ? 'The file is larger than this website accepts.'
        : `The website answered ${response.status}.`;
  return new ApiError(body?.error || fallback, response.status, body?.code, body?.currentRevision);
}

export async function api(method, url, body) {
  const init = { method, headers: { Accept: 'application/json' }, credentials: 'same-origin' };
  if (method !== 'GET') init.headers['X-CSRF-TOKEN'] = csrf();
  if (body !== undefined) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body);
  }
  let response;
  try {
    response = await fetch(url, init);
  } catch (e) {
    throw new ApiError('The website cannot be reached. Check the connection and try again.', 0, 'network');
  }
  if (!response.ok) throw await errorFrom(response);
  if (response.status === 204) return null;
  return response.json();
}

export const get = (url) => api('GET', url);
export const post = (url, body) => api('POST', url, body);
export const put = (url, body) => api('PUT', url, body);
export const del = (url) => api('DELETE', url);

/** Uploads one file as multipart/form-data with progress (0..1). Resolves with the JSON answer. */
export function upload(url, file, onProgress, fieldName = 'file') {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', url);
    xhr.setRequestHeader('X-CSRF-TOKEN', csrf());
    xhr.setRequestHeader('Accept', 'application/json');
    xhr.responseType = 'json';
    xhr.upload.onprogress = (e) => { if (e.lengthComputable && onProgress) onProgress(e.loaded / e.total); };
    xhr.onload = () => {
      const body = xhr.response;
      if (xhr.status >= 200 && xhr.status < 300) resolve(body);
      else reject(new ApiError(body?.error || `The website answered ${xhr.status}.`, xhr.status, body?.code, body?.currentRevision));
    };
    xhr.onerror = () => reject(new ApiError('The upload failed. Check the connection and try again.', 0, 'network'));
    const form = new FormData();
    form.append(fieldName, file, file.name);
    xhr.send(form);
  });
}
