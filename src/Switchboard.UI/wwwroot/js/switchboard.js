// Switchboard shared behaviors. No inline scripts anywhere (strict CSP): pages opt in with data- attributes.
(function () {
  "use strict";

  var sb = (window.sb = window.sb || {});
  var doc = document;

  // ---------------------------------------------------------------- antiforgery for htmx and fetch
  function antiforgeryToken() {
    var meta = doc.querySelector('meta[name="sb-antiforgery"]');
    return meta ? meta.getAttribute("content") : null;
  }

  doc.addEventListener("htmx:configRequest", function (e) {
    var token = antiforgeryToken();
    if (token) e.detail.headers["RequestVerificationToken"] = token;
  });

  /** fetch() with the antiforgery header and same-origin credentials. */
  sb.fetch = function (url, options) {
    options = options || {};
    options.credentials = "same-origin";
    options.headers = new Headers(options.headers || {});
    var token = antiforgeryToken();
    if (token) options.headers.set("RequestVerificationToken", token);
    return fetch(url, options);
  };

  // ---------------------------------------------------------------- toasts
  sb.toast = function (message, kind) {
    var host = doc.querySelector(".sb-toasts");
    if (!host || !message) return;
    var el = doc.createElement("div");
    el.className = "sb-toast" + (kind === "error" ? " sb-toast--error" : "");
    el.setAttribute("role", kind === "error" ? "alert" : "status");
    var icon = doc.createElement("i");
    icon.className = "bi " + (kind === "error" ? "bi-exclamation-octagon" : "bi-check2-circle");
    icon.setAttribute("aria-hidden", "true");
    var text = doc.createElement("div");
    text.textContent = message;
    var close = doc.createElement("button");
    close.type = "button";
    close.className = "btn-close btn-close-sm";
    close.setAttribute("aria-label", "Dismiss");
    close.addEventListener("click", function () { el.remove(); });
    el.append(icon, text, close);
    host.appendChild(el);
    setTimeout(function () { el.remove(); }, kind === "error" ? 9000 : 4500);
  };

  // Server-sent toasts: HX-Trigger: {"sb:toast": {"message": "...", "kind": "success"}}
  doc.addEventListener("sb:toast", function (e) {
    var d = e.detail || {};
    sb.toast(d.message || d.value, d.kind);
  });

  doc.addEventListener("htmx:responseError", function (e) {
    var xhr = e.detail.xhr;
    var message = "Something went wrong. Try again.";
    if (xhr && xhr.status === 403) message = "You don't have permission to do that.";
    if (xhr && xhr.status === 404) message = "That record doesn't exist or you can't see it.";
    if (xhr && xhr.status === 409) message = "Someone else changed this. Reload to see their changes.";
    try {
      var problem = JSON.parse(xhr.responseText);
      if (problem && (problem.detail || problem.title)) message = problem.detail || problem.title;
    } catch (_) { /* not JSON */ }
    sb.toast(message, "error");
  });

  function initToasts(root) {
    root.querySelectorAll(".sb-toast[data-sb-autohide]").forEach(function (el) {
      el.removeAttribute("data-sb-autohide");
      setTimeout(function () { el.remove(); }, 4500);
      var close = el.querySelector(".btn-close");
      if (close) close.addEventListener("click", function () { el.remove(); });
    });
  }

  // ---------------------------------------------------------------- times
  var rtf = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
  var absolute = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });
  var shortDate = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric" });

  function relative(date) {
    var diff = (date.getTime() - Date.now()) / 1000;
    var abs = Math.abs(diff);
    if (abs < 45) return "just now";
    if (abs < 3600) return rtf.format(Math.round(diff / 60), "minute");
    if (abs < 86400) return rtf.format(Math.round(diff / 3600), "hour");
    if (abs < 86400 * 7) return rtf.format(Math.round(diff / 86400), "day");
    return shortDate.format(date);
  }

  function duration(seconds) {
    var s = Math.abs(seconds);
    var d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
    if (d > 0) return d + "d " + h + "h";
    if (h > 0) return h + "h " + m + "m";
    return Math.max(m, 1) + "m";
  }

  function renderTimes(root) {
    root.querySelectorAll("time[data-sb-time]").forEach(function (el) {
      var date = new Date(el.getAttribute("datetime"));
      if (isNaN(date)) return;
      var mode = el.getAttribute("data-sb-time");
      el.title = absolute.format(date);
      el.textContent = mode === "relative" ? relative(date) : absolute.format(date);
    });

    // SLA due chips: <span class="sb-due" data-sb-due="ISO" data-sb-due-state="running|paused|met|missed">
    root.querySelectorAll("[data-sb-due]").forEach(function (el) {
      var state = el.getAttribute("data-sb-due-state");
      if (state !== "running") return;
      var due = new Date(el.getAttribute("data-sb-due"));
      if (isNaN(due)) return;
      var left = (due.getTime() - Date.now()) / 1000;
      var label = el.querySelector("[data-sb-due-text]");
      el.classList.toggle("is-overdue", left < 0);
      el.classList.toggle("is-soon", left >= 0 && left < 2 * 3600);
      var icon = el.querySelector(".bi");
      if (icon) icon.className = "bi " + (left < 0 ? "bi-exclamation-circle" : "bi-clock");
      if (label) label.textContent = left < 0 ? "Overdue by " + duration(left) : "Due in " + duration(left);
      el.title = "Due " + absolute.format(due);
    });
  }

  // ---------------------------------------------------------------- selection and bulk bars
  function initSelection(root) {
    root.querySelectorAll("[data-sb-select-scope]").forEach(function (scope) {
      if (scope.dataset.sbSelectInit) return;
      scope.dataset.sbSelectInit = "1";
      var all = scope.querySelector("[data-sb-select-all]");
      var bar = doc.querySelector(scope.getAttribute("data-sb-select-scope"));
      function boxes() { return scope.querySelectorAll("input[data-sb-select]"); }
      function sync() {
        var checked = 0;
        boxes().forEach(function (b) {
          var row = b.closest("tr");
          if (row) row.classList.toggle("is-selected", b.checked);
          if (b.checked) checked++;
        });
        if (all) {
          all.checked = checked > 0 && checked === boxes().length;
          all.indeterminate = checked > 0 && checked < boxes().length;
        }
        if (bar) {
          bar.hidden = checked === 0;
          var count = bar.querySelector("[data-sb-selected-count]");
          if (count) count.textContent = checked + (checked === 1 ? " selected" : " selected");
        }
      }
      scope.addEventListener("change", function (e) {
        if (e.target === all) boxes().forEach(function (b) { b.checked = all.checked; });
        sync();
      });
      sync();
    });
  }

  // ---------------------------------------------------------------- clickable rows
  doc.addEventListener("click", function (e) {
    var row = e.target.closest("[data-sb-href]");
    if (!row) return;
    if (e.target.closest("a, button, input, select, textarea, label, [data-sb-no-row-link]")) return;
    var href = row.getAttribute("data-sb-href");
    if (e.ctrlKey || e.metaKey) window.open(href, "_blank");
    else window.location.href = href;
  });

  // ---------------------------------------------------------------- confirm
  doc.addEventListener("submit", function (e) {
    var form = e.target;
    var message = form.getAttribute("data-sb-confirm") || (e.submitter && e.submitter.getAttribute("data-sb-confirm"));
    if (message && !window.confirm(message)) e.preventDefault();
  }, true);

  // ---------------------------------------------------------------- rail toggle (small screens)
  doc.addEventListener("click", function (e) {
    var toggle = e.target.closest("[data-sb-rail-toggle]");
    var rail = doc.getElementById("sbRail");
    if (!rail) return;
    if (toggle) {
      var open = rail.classList.toggle("is-open");
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
      if (open) { var first = rail.querySelector("a"); if (first) first.focus(); }
      return;
    }
    if (rail.classList.contains("is-open") && !rail.contains(e.target)) rail.classList.remove("is-open");
  });

  // ---------------------------------------------------------------- theme
  doc.addEventListener("click", function (e) {
    var btn = e.target.closest("[data-sb-theme-set]");
    if (!btn) return;
    var pref = btn.getAttribute("data-sb-theme-set");
    try { localStorage.setItem("sb-theme", pref); } catch (_) { /* ignore */ }
    var dark = pref === "dark" || (pref === "auto" && window.matchMedia("(prefers-color-scheme: dark)").matches);
    doc.documentElement.setAttribute("data-bs-theme", dark ? "dark" : "light");
    doc.documentElement.setAttribute("data-sb-theme-pref", pref);
  });

  // ---------------------------------------------------------------- keyboard
  doc.addEventListener("keydown", function (e) {
    var tag = (e.target.tagName || "").toLowerCase();
    var typing = tag === "input" || tag === "textarea" || tag === "select" || e.target.isContentEditable;
    if (e.key === "/" && !typing && !e.ctrlKey && !e.metaKey) {
      var search = doc.querySelector("[data-sb-search]");
      if (search) { e.preventDefault(); search.focus(); search.select(); }
    }
    if (e.key === "Escape" && typing && e.target.matches("[data-sb-search]")) e.target.blur();
  });

  // ---------------------------------------------------------------- autosize
  doc.addEventListener("input", function (e) {
    if (!e.target.matches("textarea[data-sb-autosize]")) return;
    e.target.style.height = "auto";
    e.target.style.height = Math.min(e.target.scrollHeight + 2, 640) + "px";
  });

  // ---------------------------------------------------------------- init
  // Disclosures marked data-sb-open-desktop (e.g. the admin menu) start open on wide screens.
  function openOnDesktop(root) {
    if (!window.matchMedia("(min-width: 992px)").matches) return;
    root.querySelectorAll("details[data-sb-open-desktop]").forEach(function (d) { d.open = true; });
  }

  function init(root) {
    openOnDesktop(root);
    renderTimes(root);
    initSelection(root);
    initToasts(root);
  }

  sb.init = init;
  doc.addEventListener("DOMContentLoaded", function () { init(doc); });
  doc.addEventListener("htmx:afterSettle", function (e) { init(e.target); });
  setInterval(function () { renderTimes(doc); }, 60000);

  if ("serviceWorker" in navigator && doc.querySelector('meta[name="sb-pwa"]')) {
    window.addEventListener("load", function () {
      navigator.serviceWorker.register("/sw.js", { scope: "/" }).catch(function () { /* optional */ });
    });
  }
})();
