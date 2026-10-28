// Agent ticket page: composer (modes, drafts, mentions, canned responses, collision checks),
// presence, live changes, inline subject edit, dialogs and tabs.
(function () {
  "use strict";

  var doc = document;
  var page = doc.querySelector("[data-sd-ticket]");
  if (!page) return;

  var ticketId = (page.getAttribute("data-sd-ticket-id") || "").toLowerCase();
  var me = (page.getAttribute("data-sd-me") || "").toLowerCase();
  var url = page.getAttribute("data-sd-url");
  var conflict = doc.getElementById("sdConflict");

  function same(a, b) { return String(a || "").toLowerCase() === String(b || "").toLowerCase(); }
  function typing(el) {
    var tag = (el.tagName || "").toLowerCase();
    return tag === "input" || tag === "textarea" || tag === "select" || el.isContentEditable;
  }

  function showConflict(message) {
    if (!conflict) return;
    conflict.querySelector("[data-sd-conflict-text]").textContent = message;
    conflict.hidden = false;
  }

  // ---------------------------------------------------------------- time helpers for forms

  function initTime(root) {
    var offset = String(new Date().getTimezoneOffset());
    root.querySelectorAll("[data-sd-tz]").forEach(function (el) { el.value = offset; });
    root.querySelectorAll("[data-sd-local-value]").forEach(function (el) {
      var iso = el.getAttribute("data-sd-local-value");
      if (!iso || el.value) return;
      var d = new Date(iso);
      if (isNaN(d)) return;
      var local = new Date(d.getTime() - d.getTimezoneOffset() * 60000);
      el.value = local.toISOString().slice(0, 16);
    });
  }
  initTime(doc);
  doc.addEventListener("htmx:afterSettle", function (e) { initTime(e.target); });

  // ---------------------------------------------------------------- composer

  var composer = doc.querySelector("[data-sd-composer]");
  var editorWrap = composer && composer.querySelector("[data-sd-editor]");
  var area = editorWrap && editorWrap.querySelector("[data-sd-editor-area]");
  var thread = doc.getElementById("sdThread");
  var mentions = new Map();
  var composingTimer = null;
  var composing = false;
  var draftTimer = null;

  var labels = { reply: "Send reply", note: "Add private note", public: "Add public note", forward: "Forward" };

  function mode() { return composer.getAttribute("data-sd-mode"); }

  function draftKey(m) { return composer.getAttribute("data-sd-draft-key") + ":" + (m || mode()); }

  function draftFields() { return composer.querySelectorAll("[data-sd-draft-field]"); }

  function saveDraft() {
    if (!composer) return;
    var data = { html: sdEditor.getHtml(editorWrap), fields: {}, mentions: Array.from(mentions.entries()), at: Date.now() };
    var any = data.html !== "";
    draftFields().forEach(function (f) { data.fields[f.name] = f.value; if (f.value) any = true; });
    try {
      if (any) localStorage.setItem(draftKey(), JSON.stringify(data));
      else localStorage.removeItem(draftKey());
    } catch (_) { /* storage full or blocked */ }
    setDraftStatus(any ? "Draft saved" : "");
  }

  function setDraftStatus(text, withDiscard) {
    var status = composer.querySelector("[data-sd-draft-status]");
    if (!status) return;
    status.textContent = text;
    if (withDiscard) {
      var btn = doc.createElement("button");
      btn.type = "button";
      btn.className = "btn btn-link btn-sm p-0 ms-1";
      btn.textContent = "Discard";
      btn.setAttribute("data-sd-discard-draft", "");
      status.appendChild(btn);
    }
  }

  function loadDraft(m) {
    var raw = null;
    try { raw = localStorage.getItem(draftKey(m)); } catch (_) { /* ignore */ }
    if (!raw) { sdEditor.setHtml(editorWrap, ""); setDraftStatus(""); return; }
    try {
      var data = JSON.parse(raw);
      sdEditor.setHtml(editorWrap, data.html || "");
      (data.mentions || []).forEach(function (m) { mentions.set(m[0], m[1]); });
      draftFields().forEach(function (f) { if (data.fields && data.fields[f.name] != null) f.value = data.fields[f.name]; });
      if (data.fields && (data.fields.Cc || data.fields.Bcc)) toggleCc(true);
      setDraftStatus("Draft restored", true);
    } catch (_) { /* corrupt draft */ }
  }

  function clearDraft(m) {
    try { localStorage.removeItem(draftKey(m)); } catch (_) { /* ignore */ }
  }

  function toggleCc(show) {
    var extra = composer.querySelector('[data-sd-banner-extra="reply"]');
    var btn = composer.querySelector("[data-sd-toggle-cc]");
    if (!extra) return;
    extra.setAttribute("data-sd-cc-open", show ? "1" : "0");
    extra.hidden = !show;
    extra.querySelectorAll("input").forEach(function (i) { i.disabled = !show; });
    if (btn) btn.setAttribute("aria-expanded", show ? "true" : "false");
  }

  function setMode(next, focus) {
    if (!composer.querySelector('[data-sd-mode-btn="' + next + '"]')) return;
    if (next !== mode()) saveDraft();
    composer.setAttribute("data-sd-mode", next);
    composer.classList.toggle("is-note", next === "note");
    composer.querySelector("[data-sd-mode-input]").value = next;
    composer.querySelectorAll("[data-sd-mode-btn]").forEach(function (b) {
      b.setAttribute("aria-pressed", b.getAttribute("data-sd-mode-btn") === next ? "true" : "false");
    });
    composer.querySelectorAll("[data-sd-banner]").forEach(function (b) { b.hidden = b.getAttribute("data-sd-banner") !== next; });
    composer.querySelectorAll("[data-sd-banner-extra]").forEach(function (b) {
      var on = b.getAttribute("data-sd-banner-extra") === next && (next !== "reply" || b.getAttribute("data-sd-cc-open") === "1");
      b.hidden = !on;
      b.querySelectorAll("input").forEach(function (i) { i.disabled = !on; });
    });
    var submit = composer.querySelector("[data-sd-submit]");
    if (submit) submit.textContent = labels[next];
    var split = composer.querySelector("[data-sd-split]");
    if (split) split.hidden = next === "forward";
    var header = composer.querySelector("[data-sd-split-header]");
    if (header) header.textContent = next === "reply" ? "Send and set status to" : "Add the note and set status to";
    if (area) {
      area.setAttribute("data-placeholder", area.getAttribute("data-sd-placeholder-" + next) || "");
      area.setAttribute("aria-label", labels[next].replace(/^(Send|Add) /, "").replace(/^./, function (c) { return c.toUpperCase(); }));
    }
    hideCollision();
    loadDraft(next);
    if (focus && area) area.focus();
  }

  function setComposing(on) {
    if (!window.sbLive || composing === on) return;
    composing = on;
    window.sbLive.setComposing(ticketId, on);
  }

  function hideCollision() {
    var box = composer && composer.querySelector("[data-sd-collision]");
    if (box) box.hidden = true;
    var force = composer && composer.querySelector("[data-sd-force]");
    if (force) force.value = "false";
  }

  function mentionIds() {
    var text = area.textContent;
    var ids = [];
    mentions.forEach(function (id, name) { if (text.indexOf("@" + name) >= 0) ids.push(id); });
    return ids;
  }

  function send() {
    var m = mode();
    sdEditor.sync(editorWrap);
    if (sdEditor.isEmpty(area) && m !== "forward") { sb.toast("Write a message first.", "error"); area.focus(); return; }
    if (m === "forward" && !composer.querySelector("#sdTo").value.trim()) {
      sb.toast("Add who to forward this to.", "error");
      composer.querySelector("#sdTo").focus();
      return;
    }
    if (composer.querySelector(".sd-upload.is-uploading")) {
      sb.toast("Wait for the files to finish uploading.", "error");
      return;
    }
    var seen = composer.querySelector("[data-sd-seen-until]");
    // Anything posted by someone else after this page loaded triggers the collision check on the server.
    seen.value = page.getAttribute("data-sd-loaded") || "";
    var body = new FormData(composer);
    if (m === "note" || m === "public") mentionIds().forEach(function (id) { body.append("Mentions", id); });

    var buttons = composer.querySelectorAll("[data-sd-send-group] button");
    buttons.forEach(function (b) { b.disabled = true; });
    sb.fetch(composer.getAttribute("action"), { method: "POST", body: body, headers: { Accept: "application/json", "HX-Request": "true" } })
      .then(function (r) {
        if (r.ok) {
          clearDraft(m);
          setComposing(false);
          try { sessionStorage.setItem("sd-scroll-latest", ticketId); } catch (_) { /* ignore */ }
          window.location.reload();
          return;
        }
        return r.json().catch(function () { return {}; }).then(function (problem) {
          if (r.status === 409 && problem.kind === "messages") {
            var box = composer.querySelector("[data-sd-collision]");
            box.querySelector("[data-sd-collision-text]").textContent = problem.detail;
            box.hidden = false;
            box.querySelector("[data-sd-send-anyway]").focus();
          } else {
            sb.toast(problem.detail || problem.title || "That didn't send. Try again.", "error");
          }
        });
      })
      .catch(function () { sb.toast("That didn't send. Check your connection and try again.", "error"); })
      .finally(function () {
        buttons.forEach(function (b) { b.disabled = false; });
        composer.querySelector("[data-sd-status-input]").value = "";
      });
  }

  if (composer) {
    var initialMode = mode();
    setMode(initialMode, false);

    composer.addEventListener("click", function (e) {
      var modeBtn = e.target.closest("[data-sd-mode-btn]");
      if (modeBtn) { setMode(modeBtn.getAttribute("data-sd-mode-btn"), true); return; }
      if (e.target.closest("[data-sd-toggle-cc]")) {
        var extra = composer.querySelector('[data-sd-banner-extra="reply"]');
        var open = extra.hidden;
        toggleCc(open);
        if (open) extra.querySelector("input").focus();
        return;
      }
      var statusBtn = e.target.closest("[data-sd-send-status]");
      if (statusBtn) {
        composer.querySelector("[data-sd-status-input]").value = statusBtn.getAttribute("data-sd-send-status");
        send();
        return;
      }
      if (e.target.closest("[data-sd-send-anyway]")) {
        composer.querySelector("[data-sd-force]").value = "true";
        send();
        return;
      }
      if (e.target.closest("[data-sd-show-new]")) {
        saveDraft();
        window.location.reload();
        return;
      }
      if (e.target.closest("[data-sd-discard-draft]")) {
        clearDraft();
        sdEditor.setHtml(editorWrap, "");
        draftFields().forEach(function (f) { f.value = ""; });
        setDraftStatus("Draft discarded");
        area.focus();
        return;
      }
      var cannedItem = e.target.closest("[data-sd-canned-id]");
      if (cannedItem) {
        sb.fetch(url + "?handler=CannedBody&id=" + encodeURIComponent(cannedItem.getAttribute("data-sd-canned-id")), { headers: { Accept: "application/json" } })
          .then(function (r) { if (!r.ok) throw new Error(); return r.json(); })
          .then(function (data) {
            var toggle = composer.querySelector("[data-sd-canned] [data-bs-toggle]");
            if (toggle) bootstrap.Dropdown.getOrCreateInstance(toggle).hide();
            sdEditor.insertHtml(editorWrap, data.html);
          })
          .catch(function () { sb.toast("That response couldn't be inserted.", "error"); });
      }
    });

    composer.addEventListener("submit", function (e) { e.preventDefault(); send(); });

    composer.addEventListener("sd:editor-change", function () {
      clearTimeout(draftTimer);
      draftTimer = setTimeout(saveDraft, 400);
      if (!sdEditor.isEmpty(area)) {
        setComposing(true);
        clearTimeout(composingTimer);
        composingTimer = setTimeout(function () { setComposing(false); }, 10000);
      }
    });
    composer.addEventListener("input", function (e) {
      if (e.target.matches("[data-sd-draft-field]")) { clearTimeout(draftTimer); draftTimer = setTimeout(saveDraft, 400); }
    });

    area.addEventListener("keydown", function (e) {
      if ((e.ctrlKey || e.metaKey) && e.key === "Enter") { e.preventDefault(); send(); }
    });

    var canned = composer.querySelector("[data-sd-canned]");
    if (canned) canned.addEventListener("shown.bs.dropdown", function () {
      var input = canned.querySelector("input[type=search]");
      if (input) input.focus();
    });

    // ---------------------------------------------------------------- @mentions in notes
    var menu = composer.querySelector("[data-sd-mention-menu]");
    var agents = [];
    try { agents = JSON.parse(composer.getAttribute("data-sd-agents") || "[]"); } catch (_) { agents = []; }
    var active = 0;
    var matches = [];

    function mentionQuery() {
      var sel = window.getSelection();
      if (!sel || sel.rangeCount === 0 || !sel.isCollapsed) return null;
      var node = sel.anchorNode;
      if (!node || node.nodeType !== 3 || !area.contains(node)) return null;
      var before = node.textContent.slice(0, sel.anchorOffset);
      var m = /(^|\s)@([^\s@]{0,30})$/.exec(before);
      return m ? { node: node, offset: sel.anchorOffset, query: m[2] } : null;
    }

    function closeMenu() { menu.classList.remove("show"); menu.textContent = ""; matches = []; }

    function renderMenu(q) {
      var term = q.toLowerCase();
      matches = agents.filter(function (a) { return a.name.toLowerCase().indexOf(term) >= 0; }).slice(0, 6);
      menu.textContent = "";
      if (matches.length === 0) { closeMenu(); return; }
      active = Math.min(active, matches.length - 1);
      matches.forEach(function (a, i) {
        var item = doc.createElement("button");
        item.type = "button";
        item.className = "dropdown-item" + (i === active ? " active" : "");
        item.setAttribute("role", "option");
        item.setAttribute("aria-selected", i === active ? "true" : "false");
        item.textContent = a.name;
        item.addEventListener("mousedown", function (e) { e.preventDefault(); pick(i); });
        menu.appendChild(item);
      });
      menu.classList.add("show");
    }

    function pick(i) {
      var agent = matches[i];
      var q = mentionQuery();
      if (!agent || !q) { closeMenu(); return; }
      var range = doc.createRange();
      range.setStart(q.node, q.offset - q.query.length - 1);
      range.setEnd(q.node, q.offset);
      var sel = window.getSelection();
      sel.removeAllRanges();
      sel.addRange(range);
      doc.execCommand("insertText", false, "@" + agent.name + " ");
      mentions.set(agent.name, agent.id);
      closeMenu();
      sdEditor.sync(editorWrap);
    }

    area.addEventListener("input", function () {
      var m = mode();
      if (m !== "note" && m !== "public") return;
      var q = mentionQuery();
      if (q) renderMenu(q.query); else closeMenu();
    });
    area.addEventListener("keydown", function (e) {
      if (!menu.classList.contains("show")) return;
      if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        e.preventDefault();
        active = (active + (e.key === "ArrowDown" ? 1 : matches.length - 1)) % matches.length;
        var q = mentionQuery();
        if (q) renderMenu(q.query);
      } else if (e.key === "Enter" || e.key === "Tab") {
        e.preventDefault();
        pick(active);
      } else if (e.key === "Escape") {
        e.preventDefault();
        closeMenu();
      }
    });
    area.addEventListener("blur", function () { setTimeout(closeMenu, 150); });

    window.addEventListener("beforeunload", function () { setComposing(false); });
  }

  // Scroll to the newest message after sending.
  try {
    if (sessionStorage.getItem("sd-scroll-latest") === ticketId) {
      sessionStorage.removeItem("sd-scroll-latest");
      var last = thread && thread.lastElementChild;
      if (last) last.scrollIntoView({ block: "center" });
    }
  } catch (_) { /* ignore */ }

  // ---------------------------------------------------------------- keyboard

  doc.addEventListener("keydown", function (e) {
    if (e.ctrlKey || e.metaKey || e.altKey || typing(e.target) || doc.querySelector(".modal.show") || !composer) return;
    if (e.key === "r" && composer.querySelector('[data-sd-mode-btn="reply"]')) { e.preventDefault(); setMode("reply", true); area.scrollIntoView({ block: "center" }); }
    if (e.key === "n" && composer.querySelector('[data-sd-mode-btn="note"]')) { e.preventDefault(); setMode("note", true); area.scrollIntoView({ block: "center" }); }
  });

  doc.addEventListener("click", function (e) {
    if (e.target.closest("[data-sd-focus-composer]") && composer) {
      e.preventDefault();
      setMode(composer.querySelector('[data-sd-mode-btn="reply"]') ? "reply" : "note", true);
      area.scrollIntoView({ block: "center" });
    }
    if (e.target.closest("[data-sd-reload]")) window.location.reload();
    if (e.target.closest("[data-sd-print]")) window.print();
    if (e.target.closest("[data-sd-copy-link]")) {
      var link = window.location.origin + url;
      if (navigator.clipboard) navigator.clipboard.writeText(link).then(function () { sb.toast("Link copied."); });
    }
  });

  // ---------------------------------------------------------------- presence and live changes

  var presence = doc.querySelector("[data-sd-presence]");
  var lastEvent = "";

  function renderPresence(viewers) {
    if (!presence) return;
    var others = viewers.filter(function (v) { return !same(v.userId, me); });
    presence.textContent = "";
    if (others.length === 0) return;
    var replying = others.filter(function (v) { return v.composing; });
    others.slice(0, 4).forEach(function (v) {
      var a = doc.createElement("span");
      a.className = "sb-avatar sb-avatar--sm" + (v.composing ? " sd-is-composing" : "");
      var parts = String(v.name || "?").split(" ").filter(Boolean);
      a.textContent = ((parts[0] || "?")[0] + (parts.length > 1 ? parts[parts.length - 1][0] : "")).toUpperCase();
      a.title = v.name + (v.composing ? " is writing" : " is viewing");
      a.setAttribute("aria-hidden", "true");
      presence.appendChild(a);
    });
    var text = doc.createElement("span");
    text.className = "sd-presence-text";
    var names = function (list) {
      return list.length === 1 ? list[0].name : list.length === 2 ? list[0].name + " and " + list[1].name : list[0].name + " and " + (list.length - 1) + " others";
    };
    if (replying.length > 0) {
      var icon = doc.createElement("i");
      icon.className = "bi bi-pencil me-1";
      icon.setAttribute("aria-hidden", "true");
      text.appendChild(icon);
      text.appendChild(doc.createTextNode(names(replying) + (replying.length === 1 ? " is writing a reply" : " are writing replies")));
      text.classList.add("is-composing");
    } else {
      text.textContent = names(others) + (others.length === 1 ? " is viewing" : " are viewing");
    }
    presence.appendChild(text);
  }

  doc.addEventListener("sb:live", function (e) {
    var d = e.detail || {};
    var p = d.payload || {};
    if (!same(p.ticketId, ticketId)) return;
    if (d.type === "ticket.presence") { renderPresence(p.viewers || []); return; }
    if (same(p.byUserId, me)) return;
    var signature = d.type + JSON.stringify(p);
    if (signature === lastEvent) return; // sent to both the ticket and the workspace group
    lastEvent = signature;
    var who = p.by || "Someone";
    if (d.type === "ticket.updated") {
      var fields = (p.changes || []).map(function (f) { return String(f).toLowerCase(); });
      showConflict(who + " changed " + (fields.length ? fields.join(", ") : "this ticket") + ". Reload to see the update.");
    } else if (d.type === "ticket.conversation") {
      var what = p.kind === "RequesterMessage" ? "replied" : p.kind === "Note" ? (p.isPrivate ? "added a private note" : "added a public note") : p.kind === "Forward" ? "forwarded the ticket" : "replied";
      showConflict(who + " " + what + ". Reload to see it.");
    }
  });

  // A property save that lost a race: say who changed what instead of a generic error.
  page.addEventListener("htmx:responseError", function (e) {
    var xhr = e.detail.xhr;
    if (!xhr || xhr.status !== 409) return;
    e.stopPropagation();
    var message = "Someone else changed this ticket. Reload to see the update.";
    try { message = JSON.parse(xhr.responseText).detail || message; } catch (_) { /* not JSON */ }
    showConflict(message);
  });

  // ---------------------------------------------------------------- subject

  var subjectForm = doc.querySelector("[data-sd-subject-form]");
  function editSubject(on) {
    var h1 = doc.getElementById("sdSubject");
    var btn = doc.querySelector("[data-sd-edit-subject]");
    subjectForm.hidden = !on;
    if (h1) h1.hidden = on;
    if (btn) btn.hidden = on;
    if (on) {
      var input = subjectForm.querySelector("input[name=Value]");
      input.value = h1 ? h1.textContent.trim() : input.value;
      input.focus();
      input.select();
    } else if (btn) {
      btn.focus();
    }
  }
  if (subjectForm) {
    doc.querySelector("[data-sd-edit-subject]").addEventListener("click", function () { editSubject(true); });
    subjectForm.querySelector("[data-sd-cancel-subject]").addEventListener("click", function () { editSubject(false); });
    subjectForm.addEventListener("keydown", function (e) { if (e.key === "Escape") { e.preventDefault(); editSubject(false); } });
    subjectForm.addEventListener("htmx:afterRequest", function (e) { if (e.detail.successful) editSubject(false); });
  }

  // ---------------------------------------------------------------- merge dialog

  var mergeForm = doc.querySelector("[data-sd-merge-form]");
  if (mergeForm) {
    var chosen = mergeForm.querySelector("[data-sd-chosen]");
    var submit = mergeForm.querySelector("[data-sd-merge-submit]");
    var syncMerge = function () { submit.disabled = mergeForm.querySelectorAll("[data-sd-merge-pick]:checked").length === 0; };
    mergeForm.addEventListener("change", syncMerge);
    mergeForm.addEventListener("htmx:beforeSwap", function (e) {
      if (e.detail.target.id !== "mergeResults") return;
      e.detail.target.querySelectorAll("[data-sd-merge-pick]:checked").forEach(function (box) {
        chosen.appendChild(box.closest(".sd-candidate"));
      });
    });
    mergeForm.addEventListener("htmx:afterSwap", function (e) {
      if (e.detail.target.id !== "mergeResults") return;
      e.detail.target.querySelectorAll("[data-sd-candidate]").forEach(function (row) {
        if (chosen.querySelector('[data-sd-candidate="' + row.getAttribute("data-sd-candidate") + '"]')) row.remove();
      });
      syncMerge();
    });
    mergeForm.addEventListener("submit", function (e) {
      var picked = mergeForm.querySelectorAll("[data-sd-merge-pick]:checked").length;
      var primary = mergeForm.querySelector("input[name=primaryId]:checked");
      if (primary && primary.closest(".sd-candidate") && !primary.closest(".sd-candidate").querySelector("[data-sd-merge-pick]:checked") && primary.id !== "primary-self") {
        e.preventDefault();
        sb.toast("Tick the ticket you want to keep, so it's part of the merge.", "error");
        return;
      }
      if (!window.confirm("Merge " + (picked + 1) + " tickets? The others close and become read-only.")) e.preventDefault();
    });
  }

  // ---------------------------------------------------------------- tabs and activity filter

  doc.querySelectorAll("[data-sd-tabs] [data-bs-toggle=tab]").forEach(function (btn) {
    btn.addEventListener("shown.bs.tab", function () {
      var tab = btn.getAttribute("data-bs-target").replace("#tab-", "");
      var next = new URL(window.location.href);
      if (tab === "conversation") next.searchParams.delete("tab"); else next.searchParams.set("tab", tab);
      history.replaceState(null, "", next.pathname + next.search);
    });
  });

  // Other modules add tabs through the ticket.tabs slot: <section data-sb-tab-title="Assets">…</section>.
  var extra = doc.querySelector("[data-sd-extra-tabs]");
  var tabList = doc.querySelector("[data-sd-tabs]");
  if (extra && tabList) {
    Array.prototype.forEach.call(extra.querySelectorAll(":scope > [data-sb-tab-title]"), function (section, i) {
      var id = section.id || "tab-extra-" + i;
      section.id = id;
      section.classList.add("tab-pane");
      section.setAttribute("role", "tabpanel");
      section.setAttribute("tabindex", "0");
      section.setAttribute("aria-labelledby", "tabbtn-" + id);
      section.parentNode.parentNode.appendChild(section);
      var li = doc.createElement("li");
      li.className = "nav-item";
      li.setAttribute("role", "presentation");
      var b = doc.createElement("button");
      b.className = "nav-link";
      b.id = "tabbtn-" + id;
      b.type = "button";
      b.setAttribute("role", "tab");
      b.setAttribute("data-bs-toggle", "tab");
      b.setAttribute("data-bs-target", "#" + id);
      b.setAttribute("aria-controls", id);
      b.setAttribute("aria-selected", "false");
      b.textContent = section.getAttribute("data-sb-tab-title");
      li.appendChild(b);
      tabList.appendChild(li);
    });
  }

  doc.addEventListener("change", function (e) {
    if (!e.target.matches("[data-sd-activity-filter]")) return;
    var kind = e.target.value;
    doc.querySelectorAll("[data-sd-activity] > li").forEach(function (li) {
      li.hidden = kind !== "" && li.getAttribute("data-sd-kind") !== kind;
    });
  });

})();
