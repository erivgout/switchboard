// API keys and webhooks: copy-once secrets and the scope picker. Delegated, so htmx swaps need no re-init.
(function () {
  "use strict";
  if (window.sbApiInit) return;
  window.sbApiInit = true;
  var doc = document;

  function copy(text) {
    if (navigator.clipboard && window.isSecureContext) return navigator.clipboard.writeText(text);
    return new Promise(function (resolve, reject) {
      var area = doc.createElement("textarea");
      area.value = text;
      area.setAttribute("readonly", "");
      area.style.position = "fixed";
      area.style.opacity = "0";
      doc.body.appendChild(area);
      area.select();
      var ok = false;
      try { ok = doc.execCommand("copy"); } catch (_) { ok = false; }
      area.remove();
      if (ok) { resolve(); } else { reject(); }
    });
  }

  doc.addEventListener("click", function (e) {
    var btn = e.target.closest("[data-sb-copy]");
    if (!btn) return;
    var source = doc.querySelector(btn.getAttribute("data-sb-copy"));
    if (!source) return;
    var label = btn.querySelector("span");
    copy(source.textContent.trim()).then(function () {
      if (label) label.textContent = btn.getAttribute("data-sb-copied") || "Copied";
      if (window.sb && sb.toast) sb.toast("Copied to the clipboard");
    }, function () {
      var range = doc.createRange();
      range.selectNodeContents(source);
      var sel = window.getSelection();
      sel.removeAllRanges();
      sel.addRange(range);
      if (window.sb && sb.toast) sb.toast("Press Ctrl+C to copy the selected text.", "error");
    });
  });

  // "Only what I choose" reveals the scope list.
  doc.addEventListener("change", function (e) {
    if (!e.target.matches("[data-sb-scope-mode]")) return;
    var form = e.target.closest("form");
    var list = form && form.querySelector("[data-sb-scope-list]");
    if (list) list.hidden = e.target.value !== "limited";
  });
})();
