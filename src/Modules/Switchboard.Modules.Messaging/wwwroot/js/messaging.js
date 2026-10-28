// Messaging module behaviors: the notification bell, form reveals, placeholder insertion, copy buttons.
// Loaded from the top bar on every agent page, so everything is idempotent and delegated.
(function () {
  "use strict";
  if (window.sbMessaging) return;
  window.sbMessaging = true;
  var doc = document;

  // ---------------------------------------------------------------- bell
  function setBellCount(unread) {
    var n = Number(unread) || 0;
    doc.querySelectorAll("[data-sb-bell-count]").forEach(function (el) {
      el.textContent = n > 99 ? "99+" : String(n);
      el.hidden = n === 0;
    });
    doc.querySelectorAll("[data-sb-bell-button]").forEach(function (btn) {
      btn.setAttribute("aria-label", n === 0 ? "Notifications" : "Notifications, " + n + " unread");
    });
  }

  // Live: a new notification for me (pushed by the server over SignalR).
  doc.addEventListener("sb:live", function (e) {
    var d = e.detail || {};
    if (d.type !== "notification.created") return;
    var payload = d.payload || {};
    setBellCount(payload.unread);
    // If the menu is open, refresh it so the new one shows.
    var menu = doc.getElementById("sbBellMenu");
    if (menu && menu.classList.contains("show") && window.htmx) {
      window.htmx.ajax("GET", "/notifications/menu", { target: "#sbBellMenu", swap: "innerHTML" });
    }
  });

  // After htmx actions (mark read, mark all read) the server sends HX-Trigger: {"sb:bell-count": {"unread": n}}.
  doc.addEventListener("sb:bell-count", function (e) {
    var d = e.detail || {};
    setBellCount(d.unread);
  });

  // ---------------------------------------------------------------- reveal: radios show matching sections
  function syncReveal(name) {
    var checked = doc.querySelector('input[data-sb-reveal="' + name + '"]:checked');
    if (!checked) return;
    doc.querySelectorAll('[data-sb-reveal-for="' + name + '"]').forEach(function (section) {
      section.hidden = section.getAttribute("data-sb-reveal-value") !== checked.value;
    });
  }

  doc.addEventListener("change", function (e) {
    var input = e.target.closest ? e.target.closest("input[data-sb-reveal]") : null;
    if (input) syncReveal(input.getAttribute("data-sb-reveal"));

    var auto = e.target.closest ? e.target.closest("select[data-sb-autosubmit]") : null;
    if (auto && auto.form) auto.form.submit();
  });

  // ---------------------------------------------------------------- placeholders: insert at the cursor
  var lastField = null;
  doc.addEventListener("focusin", function (e) {
    if (e.target.matches && e.target.matches("[data-sb-template-field]")) lastField = e.target;
  });

  doc.addEventListener("click", function (e) {
    var button = e.target.closest ? e.target.closest("[data-sb-insert]") : null;
    if (button) {
      var field = lastField || doc.querySelector("textarea[data-sb-template-field]");
      if (!field) return;
      var text = button.getAttribute("data-sb-insert");
      var start = field.selectionStart == null ? field.value.length : field.selectionStart;
      var end = field.selectionEnd == null ? field.value.length : field.selectionEnd;
      field.value = field.value.slice(0, start) + text + field.value.slice(end);
      field.focus();
      field.setSelectionRange(start + text.length, start + text.length);
      field.dispatchEvent(new Event("input", { bubbles: true }));
      return;
    }

    var copy = e.target.closest ? e.target.closest("[data-sb-copy]") : null;
    if (copy) {
      var source = doc.querySelector(copy.getAttribute("data-sb-copy"));
      if (!source) return;
      var value = source.value || source.textContent || "";
      var done = function () {
        var original = copy.textContent;
        copy.textContent = "Copied";
        setTimeout(function () { copy.textContent = original; }, 2000);
      };
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(value).then(done, function () { source.select && source.select(); });
      } else if (source.select) {
        source.select();
      }
    }
  });
})();
