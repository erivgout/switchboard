// Knowledge pages and slots: copy links, insert article links into a reply, and portal deflection.
// Loaded by pages and by slot components, so it guards against running twice.
(function () {
  "use strict";
  if (window.sbKnowledge) return;
  window.sbKnowledge = true;

  var doc = document;

  function toast(message, kind) {
    if (window.sb && window.sb.toast) window.sb.toast(message, kind);
  }

  function copy(text) {
    if (navigator.clipboard && window.isSecureContext) return navigator.clipboard.writeText(text);
    var area = doc.createElement("textarea");
    area.value = text;
    area.setAttribute("readonly", "");
    area.style.position = "fixed";
    area.style.opacity = "0";
    doc.body.appendChild(area);
    area.select();
    var ok = doc.execCommand("copy");
    area.remove();
    return ok ? Promise.resolve() : Promise.reject(new Error("copy failed"));
  }

  doc.addEventListener("click", function (e) {
    var btn = e.target.closest("[data-kb-copy]");
    if (btn) {
      copy(btn.getAttribute("data-kb-copy"))
        .then(function () { toast("Link copied"); })
        .catch(function () { toast("The link couldn't be copied. Open the article and copy it from the address bar.", "error"); });
      return;
    }

    // Ticket sidebar: put "Title: link" into the reply composer when there is one, else copy it.
    var insert = e.target.closest("[data-kb-insert]");
    if (insert) {
      var text = insert.getAttribute("data-kb-insert");
      var target = doc.querySelector(".sb-composer textarea, .sb-composer [contenteditable='true']");
      if (!target) {
        copy(text).then(function () { toast("Link copied"); }).catch(function () { toast("The link couldn't be copied.", "error"); });
        return;
      }
      if (target.tagName === "TEXTAREA") {
        var start = target.selectionStart || target.value.length;
        var before = target.value.slice(0, start);
        var spacer = before && !/\s$/.test(before) ? "\n" : "";
        target.value = before + spacer + text + target.value.slice(target.selectionEnd || start);
        target.dispatchEvent(new Event("input", { bubbles: true }));
      } else {
        target.focus();
        doc.execCommand("insertText", false, text);
      }
      target.focus();
      toast("Link added to your reply");
    }
  });

  // ------------------------------------------------------------ deflection (portal "Report a problem")
  function initDeflection(box) {
    var source = doc.querySelector("[data-sb-deflect-source]");
    var list = box.querySelector("[data-kb-deflect-list]");
    if (!source || !list) return;
    var timer, last = "", controller = null;

    function render(items) {
      list.innerHTML = "";
      items.forEach(function (item) {
        var li = doc.createElement("li");
        var a = doc.createElement("a");
        a.href = item.url;
        a.target = "_blank";
        a.rel = "noopener";
        a.textContent = item.title;
        var hint = doc.createElement("span");
        hint.className = "visually-hidden";
        hint.textContent = " (opens in a new tab)";
        a.appendChild(hint);
        var icon = doc.createElement("i");
        icon.className = "bi bi-box-arrow-up-right sb-meta";
        icon.setAttribute("aria-hidden", "true");
        var p = doc.createElement("p");
        p.className = "kb-snippet";
        p.textContent = item.snippet;
        li.append(a, icon, p);
        list.appendChild(li);
      });
      box.hidden = items.length === 0;
    }

    function lookup() {
      var q = source.value.trim();
      if (q === last) return;
      last = q;
      if (q.length < 4) { render([]); return; }
      if (controller) controller.abort();
      controller = "AbortController" in window ? new AbortController() : null;
      fetch("/portal/kb/suggest?q=" + encodeURIComponent(q), { credentials: "same-origin", signal: controller ? controller.signal : undefined, headers: { Accept: "application/json" } })
        .then(function (r) { return r.ok ? r.json() : []; })
        .then(render)
        .catch(function () { /* typing on; the next lookup replaces this one */ });
    }

    source.addEventListener("input", function () {
      clearTimeout(timer);
      timer = setTimeout(lookup, 350);
    });
    if (source.value) lookup();
  }

  // ------------------------------------------------------------ local times in forms
  function pad(n) { return (n < 10 ? "0" : "") + n; }

  function initTimes() {
    doc.querySelectorAll("[data-kb-tz]").forEach(function (input) {
      input.value = String(new Date().getTimezoneOffset());
    });
    doc.querySelectorAll("input[data-kb-utc]").forEach(function (input) {
      var iso = input.getAttribute("data-kb-utc");
      if (!iso || input.value) return;
      var d = new Date(iso);
      if (isNaN(d)) return;
      input.value = d.getFullYear() + "-" + pad(d.getMonth() + 1) + "-" + pad(d.getDate()) + "T" + pad(d.getHours()) + ":" + pad(d.getMinutes());
    });
  }

  // ------------------------------------------------------------ show a section only for one radio choice
  // data-kb-show-when="Audience=Specific" shows the element while that radio is checked.
  function initShowWhen() {
    doc.querySelectorAll("[data-kb-show-when]").forEach(function (el) {
      var spec = el.getAttribute("data-kb-show-when");
      var i = spec.indexOf("=");
      var name = spec.slice(0, i), value = spec.slice(i + 1);
      var scope = el.closest("form") || doc;
      function sync() {
        var checked = scope.querySelector('input[type="radio"][name="' + name + '"]:checked');
        el.hidden = !checked || checked.value !== value;
      }
      scope.addEventListener("change", function (e) { if (e.target.name === name) sync(); });
      sync();
    });
  }

  function init() {
    doc.querySelectorAll("[data-kb-deflect]").forEach(initDeflection);
    initTimes();
    initShowWhen();
  }

  if (doc.readyState === "loading") doc.addEventListener("DOMContentLoaded", init);
  else init();
})();
