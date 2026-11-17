// Small behaviors of the account, organization and administration pages. (No inline scripts: the
// Content-Security-Policy only allows script files from this website.)
(function () {
  'use strict';

  // <form data-confirm="Question?"> asks before submitting
  document.addEventListener('submit', function (e) {
    var form = e.target;
    var question = form && form.getAttribute && form.getAttribute('data-confirm');
    if (question && !window.confirm(question)) e.preventDefault();
  }, true);

  // <select class="autosubmit"> submits its form when changed
  document.addEventListener('change', function (e) {
    var t = e.target;
    if (t && t.classList && t.classList.contains('autosubmit') && t.form) {
      var question = t.form.getAttribute('data-confirm');
      if (!question || window.confirm(question)) t.form.submit();
    }
  });

  // close the mobile menu after choosing an entry
  document.addEventListener('click', function (e) {
    var toggle = document.getElementById('nav-toggle');
    if (toggle && toggle.checked && e.target && e.target.closest && e.target.closest('.topnav a')) toggle.checked = false;
  });

  // Organization page: upload a .pmbook to the API with progress, and ask before replacing a changed book
  var upload = document.getElementById('pmbook-upload');
  if (upload) {
    var status = upload.querySelector('.upload-status');
    var progress = upload.querySelector('progress');
    var button = upload.querySelector('button[type=submit]');

    var show = function (text, isError) {
      status.textContent = text;
      status.className = 'upload-status' + (isError ? ' error' : '');
    };

    var send = function (file, query) {
      return new Promise(function (resolve) {
        var xhr = new XMLHttpRequest();
        xhr.open('POST', '/api/orgs/' + encodeURIComponent(upload.getAttribute('data-org')) + '/books/package' + query);
        var token = upload.querySelector('input[name=__RequestVerificationToken]');
        if (token) xhr.setRequestHeader('X-CSRF-TOKEN', token.value);
        xhr.setRequestHeader('Content-Type', 'application/octet-stream');
        xhr.upload.onprogress = function (e) {
          if (e.lengthComputable) progress.value = e.loaded / e.total;
        };
        xhr.onload = function () {
          var body = null;
          try { body = JSON.parse(xhr.responseText); } catch (err) { /* not JSON */ }
          resolve({ status: xhr.status, body: body || {} });
        };
        xhr.onerror = function () { resolve({ status: 0, body: { error: 'The upload failed (network error).' } }); };
        xhr.send(file);
      });
    };

    upload.addEventListener('submit', function (e) {
      e.preventDefault();
      var file = upload.querySelector('input[type=file]').files[0];
      if (!file) { show('Choose a .pmbook file.', true); return; }
      var copy = upload.querySelector('input[name=mode][value=copy]').checked;
      button.disabled = true;
      progress.hidden = false;
      progress.value = 0;
      show('Uploading ' + file.name + '…', false);

      var attempt = function (query) {
        return send(file, query).then(function (r) {
          if (r.status === 409 && !copy && query.indexOf('force=true') < 0) {
            // the book was changed on the website since this copy was made, or the copy has no link to it
            var question = (r.body.error || 'This organization already has this book.') +
              '\n\nReplace it with the uploaded file (a new revision)? To keep both, cancel and choose “keep both”.';
            if (window.confirm(question)) {
              progress.value = 0;
              show('Uploading ' + file.name + ' again…', false);
              return attempt('?force=true');
            }
            return { status: -1, body: {} };
          }
          return r;
        });
      };

      attempt(copy ? '?asNew=true' : '').then(function (r) {
        button.disabled = false;
        progress.hidden = true;
        if (r.status === -1) { show('Nothing was changed.', false); return; }
        if (r.status >= 200 && r.status < 300) {
          var b = r.body;
          var what = b.created ? 'added' : b.unchanged ? 'already up to date' : 'updated to revision ' + b.revision;
          show('“' + (b.title || file.name) + '” ' + what + '. Refreshing the list…', false);
          window.setTimeout(function () { window.location.reload(); }, 1200);
          return;
        }
        show(r.body.error || ('The upload failed (HTTP ' + r.status + ').'), true);
      });
    });
  }
})();
