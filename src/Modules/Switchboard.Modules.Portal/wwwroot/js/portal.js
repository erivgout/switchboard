// Requester portal behaviors: attachments, the people picker and live request updates.
// No inline scripts (CSP): everything hangs off data- attributes.
(function () {
  "use strict";

  var doc = document;

  function el(tag, cls, text) {
    var node = doc.createElement(tag);
    if (cls) node.className = cls;
    if (text != null) node.textContent = text;
    return node;
  }

  function icon(name) {
    var i = el("i", "bi " + name);
    i.setAttribute("aria-hidden", "true");
    return i;
  }

  function fileSize(bytes) {
    if (bytes < 1024) return bytes + " B";
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1).replace(/\.0$/, "") + " KB";
    return (bytes / (1024 * 1024)).toFixed(1).replace(/\.0$/, "") + " MB";
  }

  // ---------------------------------------------------------------- attachments
  // Each picked file goes to POST /uploads right away; success adds a hidden AttachmentIds input.

  function initUploads(root) {
    root.querySelectorAll("[data-prt-upload]").forEach(function (box) {
      if (box.dataset.prtReady) return;
      box.dataset.prtReady = "1";
      box.hidden = false;
      var input = box.querySelector("[data-prt-upload-input]");
      var list = box.querySelector("[data-prt-upload-list]");
      var form = box.closest("form");
      var pending = 0;

      function syncSubmit() {
        if (!form) return;
        form.querySelectorAll('button[type="submit"]').forEach(function (b) { b.disabled = pending > 0; });
      }

      function removeButton(item, name) {
        var b = el("button", "btn btn-ghost btn-icon btn-sm");
        b.type = "button";
        b.setAttribute("data-prt-upload-remove", "");
        b.setAttribute("aria-label", "Remove " + name);
        b.appendChild(icon("bi-x-lg"));
        return b;
      }

      function upload(file) {
        var item = el("li", "prt-file is-uploading");
        item.appendChild(icon("bi-arrow-up-circle"));
        item.appendChild(el("span", "prt-file-name", file.name));
        var status = el("span", "prt-file-size", "Uploading");
        item.appendChild(status);
        list.appendChild(item);
        pending++;
        syncSubmit();

        var data = new FormData();
        data.append("file", file, file.name);
        window.sb.fetch("/uploads", { method: "POST", body: data })
          .then(function (r) {
            return r.json().catch(function () { return {}; }).then(function (body) { return { ok: r.ok, body: body }; });
          })
          .then(function (res) {
            if (!res.ok) throw new Error(res.body && res.body.error ? res.body.error : "That file couldn't be uploaded. Try again.");
            item.className = "prt-file";
            item.firstChild.replaceWith(icon("bi-file-earmark"));
            status.textContent = fileSize(res.body.size || file.size);
            var hidden = el("input");
            hidden.type = "hidden";
            hidden.name = "AttachmentIds";
            hidden.value = res.body.id;
            item.appendChild(hidden);
            item.appendChild(removeButton(item, file.name));
          })
          .catch(function (err) {
            item.className = "prt-file is-failed";
            item.setAttribute("role", "alert");
            item.firstChild.replaceWith(icon("bi-exclamation-circle"));
            status.textContent = err && err.message ? err.message : "That file couldn't be uploaded.";
            item.appendChild(removeButton(item, file.name));
          })
          .finally(function () {
            pending--;
            syncSubmit();
          });
      }

      input.addEventListener("change", function () {
        Array.prototype.forEach.call(input.files || [], upload);
        input.value = "";
      });

      list.addEventListener("click", function (e) {
        var btn = e.target.closest("[data-prt-upload-remove]");
        if (!btn) return;
        btn.closest("li").remove();
        input.focus();
      });
    });
  }

  // ---------------------------------------------------------------- people picker
  // Multiple mode (data-prt-people-name set): picked people become chips with hidden inputs.
  // Single mode: picking a person fills the input with their email.

  var emailPattern = /^[^\s@,;]+@[^\s@,;]+\.[^\s@,;]+$/;
  var pickerId = 0;

  function initPeople(root) {
    root.querySelectorAll("[data-prt-people]").forEach(function (box) {
      if (box.dataset.prtReady) return;
      box.dataset.prtReady = "1";
      var input = box.querySelector(".prt-people-input");
      var menu = box.querySelector(".prt-people-menu");
      var chips = box.querySelector("[data-prt-people-chips]");
      var chipName = box.getAttribute("data-prt-people-name");
      var timer = null;
      var seq = 0;
      var options = [];
      var active = -1;
      var prefix = "prtPerson" + (++pickerId) + "_";

      box.querySelector(".prt-people-box").addEventListener("click", function (e) {
        if (e.target === e.currentTarget) input.focus();
      });

      function close() {
        menu.hidden = true;
        menu.textContent = "";
        options = [];
        active = -1;
        input.setAttribute("aria-expanded", "false");
        input.removeAttribute("aria-activedescendant");
      }

      function setActive(i) {
        active = i;
        options.forEach(function (o, n) { o.el.setAttribute("aria-selected", n === i ? "true" : "false"); });
        if (i >= 0) input.setAttribute("aria-activedescendant", options[i].el.id);
        else input.removeAttribute("aria-activedescendant");
      }

      function hasChip(email) {
        if (!chips) return false;
        var lower = email.toLowerCase();
        return Array.prototype.some.call(chips.querySelectorAll('input[type="hidden"]'), function (h) { return h.value.toLowerCase() === lower; });
      }

      function addChip(email, label) {
        if (hasChip(email)) return;
        var li = el("li", "prt-chip");
        li.appendChild(el("span", null, label || email));
        if (label && label !== email) li.title = email;
        var hidden = el("input");
        hidden.type = "hidden";
        hidden.name = chipName;
        hidden.value = email;
        li.appendChild(hidden);
        var remove = el("button", "prt-chip-remove");
        remove.type = "button";
        remove.setAttribute("data-prt-chip-remove", "");
        remove.setAttribute("aria-label", "Remove " + (label || email));
        remove.appendChild(icon("bi-x"));
        li.appendChild(remove);
        chips.appendChild(li);
      }

      function choose(person) {
        if (chipName) {
          addChip(person.email, person.name);
          input.value = "";
        } else {
          input.value = person.email;
        }
        close();
        input.focus();
      }

      function render(people) {
        menu.textContent = "";
        options = [];
        if (people.length === 0) {
          menu.appendChild(el("li", "prt-people-empty", emailPattern.test(input.value.trim()) ? "Press Enter to add this address" : "No one matches that name"));
        }
        people.forEach(function (p, n) {
          var li = el("li", "prt-people-option");
          li.id = prefix + n;
          li.setAttribute("role", "option");
          li.setAttribute("aria-selected", "false");
          li.appendChild(el("span", null, p.name));
          li.appendChild(el("small", null, p.email));
          li.addEventListener("mousedown", function (e) { e.preventDefault(); choose(p); });
          menu.appendChild(li);
          options.push({ el: li, person: p });
        });
        menu.hidden = false;
        input.setAttribute("aria-expanded", "true");
        setActive(options.length ? 0 : -1);
      }

      function lookup() {
        var q = input.value.trim();
        if (q.length < 2) { close(); return; }
        var mine = ++seq;
        window.sb.fetch("/portal/people?q=" + encodeURIComponent(q), { headers: { Accept: "application/json" } })
          .then(function (r) { return r.ok ? r.json() : []; })
          .then(function (people) { if (mine === seq && doc.activeElement === input) render(people || []); })
          .catch(function () { close(); });
      }

      function commitTyped() {
        var value = input.value.trim().replace(/[,;]+$/, "");
        if (!chipName || !emailPattern.test(value)) return false;
        addChip(value);
        input.value = "";
        close();
        return true;
      }

      input.addEventListener("input", function () {
        clearTimeout(timer);
        timer = setTimeout(lookup, 180);
      });

      input.addEventListener("keydown", function (e) {
        if (e.key === "ArrowDown" && options.length) { e.preventDefault(); setActive((active + 1) % options.length); }
        else if (e.key === "ArrowUp" && options.length) { e.preventDefault(); setActive((active - 1 + options.length) % options.length); }
        else if (e.key === "Escape" && !menu.hidden) { e.preventDefault(); close(); }
        else if (e.key === "Enter") {
          if (active >= 0 && options[active]) { e.preventDefault(); choose(options[active].person); }
          else if (chipName && input.value.trim()) { e.preventDefault(); commitTyped(); }
        }
        else if ((e.key === "," || e.key === ";") && chipName) {
          if (commitTyped()) e.preventDefault();
        }
        else if (e.key === "Backspace" && chipName && input.value === "" && chips.lastElementChild) {
          chips.lastElementChild.remove();
        }
      });

      input.addEventListener("blur", function () {
        setTimeout(close, 120);
        commitTyped();
      });

      if (chips) {
        chips.addEventListener("click", function (e) {
          var btn = e.target.closest("[data-prt-chip-remove]");
          if (!btn) return;
          btn.closest("li").remove();
          input.focus();
        });
      }
    });
  }

  // ---------------------------------------------------------------- live request updates
  // The status panel and conversation reload in place when the service desk replies or changes
  // the request. Private activity is ignored; the server only ever renders public content anyway.

  function initLive() {
    var page = doc.querySelector("[data-prt-ticket]");
    if (!page) return;
    var ticketId = (page.getAttribute("data-prt-ticket") || "").toLowerCase();
    var me = (page.getAttribute("data-prt-me") || "").toLowerCase();

    if (window.sbLive && !doc.body.getAttribute("data-sb-watch-ticket")) window.sbLive.watchTicket(ticketId);

    // The reply box sits outside the refreshed region so drafts survive. When a status change alters
    // what replying does (reply, reopen, follow-up, none), reload, unless that would lose a draft.
    function syncReplyMode() {
      var live = doc.querySelector("[data-prt-live]");
      if (!live || live.getAttribute("data-prt-reply-mode") === page.getAttribute("data-prt-reply-mode")) return;
      var body = doc.getElementById("prtBody");
      var hasDraft = (body && body.value.trim()) || doc.querySelector("#reply .prt-file");
      if (!hasDraft) { window.location.reload(); return; }
      page.setAttribute("data-prt-reply-mode", live.getAttribute("data-prt-reply-mode"));
      if (window.sb && window.sb.toast) window.sb.toast("This request was updated. Copy your message and reload the page before sending it.", "error");
    }

    var timer = null;
    function refresh() {
      clearTimeout(timer);
      timer = setTimeout(function () {
        var live = doc.querySelector("[data-prt-live]");
        if (!live || !window.htmx) return;
        window.htmx.ajax("GET", live.getAttribute("data-prt-live"), { target: live, swap: "outerHTML" }).then(function () { setTimeout(syncReplyMode, 50); });
      }, 250);
    }

    doc.addEventListener("sb:live", function (e) {
      var detail = e.detail || {};
      var p = detail.payload || {};
      var id = String(p.ticketId || p.TicketId || "").toLowerCase();
      if (id !== ticketId) return;
      if (detail.type === "ticket.conversation") {
        if (p.isPrivate || p.IsPrivate) return;
        var by = String(p.byUserId || p.ByUserId || "").toLowerCase();
        if (by && by === me) return;
        refresh();
        if (window.sb && window.sb.toast) window.sb.toast("There's a new message on this request");
      } else if (detail.type === "ticket.updated") {
        refresh();
      }
    });
  }

  // ---------------------------------------------------------------- small things

  doc.addEventListener("click", function (e) {
    var link = e.target.closest("[data-prt-focus-reply]");
    if (!link) return;
    var body = doc.getElementById("prtBody");
    if (body) setTimeout(function () { body.focus(); }, 0);
  });

  function focusErrors() {
    var summary = doc.querySelector("[data-prt-focus]");
    if (summary) summary.focus();
  }

  function init(root) {
    initUploads(root);
    initPeople(root);
  }

  doc.addEventListener("DOMContentLoaded", function () {
    init(doc);
    initLive();
    focusErrors();
  });
  doc.addEventListener("htmx:afterSettle", function (e) { init(e.target); });
})();
