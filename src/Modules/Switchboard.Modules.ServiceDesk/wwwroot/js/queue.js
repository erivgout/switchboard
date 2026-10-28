// Ticket queue: quick filters, bulk actions, live updates, keyboard and the Kanban board.
(function () {
  "use strict";

  var doc = document;
  var root = doc.querySelector("[data-sd-queue]");
  if (!root) return;

  var me = root.getAttribute("data-sd-me");
  var form = doc.querySelector("[data-sd-filter]");
  var queryInput = doc.querySelector("[data-sd-query]");

  // ---------------------------------------------------------------- filter language helpers

  /** Splits a query into top-level terms, keeping quoted phrases and parenthesized groups whole. */
  function tokenize(query) {
    var tokens = [], current = "", depth = 0, quoted = false;
    for (var i = 0; i < query.length; i++) {
      var c = query[i];
      if (c === "\\" && quoted && i + 1 < query.length) { current += c + query[++i]; continue; }
      if (c === '"') quoted = !quoted;
      else if (!quoted && c === "(") depth++;
      else if (!quoted && c === ")") depth = Math.max(0, depth - 1);
      if (!quoted && depth === 0 && /\s/.test(c)) {
        if (current) tokens.push(current);
        current = "";
        continue;
      }
      current += c;
    }
    if (current) tokens.push(current);
    return tokens;
  }

  function fieldOf(token) {
    var m = /^-?([A-Za-z_]+)(>=|<=|!=|:|>|<|=)/.exec(token);
    return m ? m[1].toLowerCase() : null;
  }

  /** Replaces any terms for these fields with the given term, or removes it when it was already there. */
  function toggleTerm(query, fields, term) {
    var tokens = tokenize(query);
    var had = tokens.indexOf(term) >= 0;
    tokens = tokens.filter(function (t) { return fields.indexOf(fieldOf(t)) < 0; });
    if (!had) tokens.push(term);
    return tokens.join(" ");
  }

  function submitFilter() {
    if (form) form.requestSubmit ? form.requestSubmit() : form.submit();
  }

  doc.addEventListener("click", function (e) {
    var item = e.target.closest("[data-sd-term]");
    if (item && queryInput) {
      var fields = (item.getAttribute("data-sd-field") || "").split(" ");
      queryInput.value = toggleTerm(queryInput.value, fields, item.getAttribute("data-sd-term"));
      submitFilter();
      return;
    }
    var insert = e.target.closest("[data-sd-insert]");
    if (insert && queryInput) {
      var text = insert.getAttribute("data-sd-insert");
      var value = queryInput.value;
      var at = queryInput.selectionStart != null ? queryInput.selectionStart : value.length;
      var before = value.slice(0, at), after = value.slice(at);
      var pad = before && !/\s$/.test(before) ? " " : "";
      queryInput.value = before + pad + text + after;
      queryInput.focus();
      var caret = (before + pad + text).length;
      queryInput.setSelectionRange(caret, caret);
    }
  });

  doc.querySelectorAll("[data-sd-autosubmit]").forEach(function (el) {
    el.addEventListener("change", submitFilter);
  });

  var viewPicker = doc.querySelector("[data-sd-view-picker]");
  if (viewPicker) viewPicker.addEventListener("change", function () { if (viewPicker.value) window.location.href = viewPicker.value; });

  var boardBy = doc.querySelector("[data-sd-board-by]");
  if (boardBy) boardBy.addEventListener("change", function () {
    var url = new URL(boardBy.getAttribute("data-sd-board-url"), window.location.origin);
    if (boardBy.value === "status") url.searchParams.delete("by"); else url.searchParams.set("by", boardBy.value);
    window.location.href = url.pathname + url.search;
  });

  var cols = doc.querySelector("[data-sd-cols]");
  if (cols) cols.addEventListener("change", function () {
    var picked = Array.prototype.map.call(cols.querySelectorAll("input:checked"), function (i) { return i.value; });
    if (picked.length === 0) return;
    var url = new URL(cols.getAttribute("data-sd-cols-url"), window.location.origin);
    url.searchParams.set("cols", picked.join(","));
    window.location.href = url.pathname + url.search;
  });

  // ---------------------------------------------------------------- bulk actions

  var bulkBar = doc.getElementById("sdBulkBar");
  var bulkForm = doc.querySelector("[data-sd-bulk-form]");
  var allMatching = false;

  function results() { return doc.querySelector("[data-sd-results]"); }
  function totalMatching() { var r = results(); return r ? parseInt(r.getAttribute("data-sd-total") || "0", 10) : 0; }
  function selectedBoxes() { return Array.prototype.slice.call(doc.querySelectorAll("input[data-sb-select]:checked")); }
  function pageBoxes() { return doc.querySelectorAll("input[data-sb-select]"); }

  function plural(n, word) { return n === 1 ? "1 " + word : n.toLocaleString() + " " + word + "s"; }

  function syncMatching() {
    if (!bulkBar) return;
    var link = bulkBar.querySelector("[data-sd-select-matching]");
    var count = bulkBar.querySelector("[data-sb-selected-count]");
    var boxes = pageBoxes();
    var checked = selectedBoxes().length;
    var everyOnPage = boxes.length > 0 && checked === boxes.length;
    var total = totalMatching();
    if (link) link.hidden = !(everyOnPage && total > boxes.length) || allMatching;
    if (allMatching && count) count.textContent = "All " + total.toLocaleString() + " matching selected";
  }

  doc.addEventListener("change", function (e) {
    if (e.target.matches("input[data-sb-select], input[data-sb-select-all]")) {
      allMatching = false;
      setTimeout(syncMatching, 0);
    }
  });

  if (bulkBar) {
    bulkBar.addEventListener("click", function (e) {
      if (e.target.closest("[data-sd-select-matching]")) {
        allMatching = true;
        syncMatching();
        return;
      }
      if (e.target.closest("[data-sd-clear-selection]")) {
        allMatching = false;
        pageBoxes().forEach(function (b) { b.checked = false; });
        var all = doc.querySelector("input[data-sb-select-all]");
        if (all) { all.checked = false; all.dispatchEvent(new Event("change", { bubbles: true })); }
        else if (pageBoxes()[0]) pageBoxes()[0].dispatchEvent(new Event("change", { bubbles: true }));
        return;
      }
      var btn = e.target.closest("[data-sd-bulk]");
      if (btn) openBulk(btn.getAttribute("data-sd-bulk"));
    });
  }

  var titles = {
    assign: ["Assign {n}", "Assign {n}"],
    status: ["Change the status of {n}", "Update {n}"],
    priority: ["Change the priority of {n}", "Update {n}"],
    tags: ["Add tags to {n}", "Add tags"],
    merge: ["Merge {n}", "Merge tickets"],
    spam: ["Mark {n} as spam", "Mark as spam"],
    not_spam: ["Move {n} out of spam", "Not spam"],
    trash: ["Move {n} to trash", "Move to trash"],
    restore: ["Restore {n}", "Restore {n}"],
    purge: ["Permanently delete {n}", "Delete permanently"]
  };
  var summaries = {
    spam: "Spam tickets leave the queue and stop lighting lamps. You can restore them from the Spam view.",
    not_spam: "They go back to the queue with their previous status.",
    trash: "You can restore them from Trash until they're permanently deleted.",
    restore: "They go back to the queue with their previous status.",
    purge: "This can't be undone."
  };

  function openBulk(action) {
    if (!bulkForm) return;
    var count = allMatching ? totalMatching() : selectedBoxes().length;
    if (count === 0) return;
    if (action === "merge" && (allMatching || count < 2)) {
      sb.toast(allMatching ? "Select the tickets to merge on this page." : "Select at least two tickets to merge.", "error");
      return;
    }
    var limit = parseInt(bulkForm.getAttribute("data-sd-inline-limit") || "100", 10);
    var n = plural(count, "ticket");
    var t = titles[action] || ["Update {n}", "Update {n}"];
    bulkForm.querySelector("[data-sd-bulk-action]").value = action;
    bulkForm.querySelector("[data-sd-all-matching]").value = allMatching ? "true" : "false";
    bulkForm.querySelector("[data-sd-bulk-title]").textContent = t[0].replace("{n}", n);
    bulkForm.querySelector("[data-sd-bulk-summary]").textContent = summaries[action] || "";
    var submit = bulkForm.querySelector("[data-sd-bulk-submit]");
    submit.textContent = t[1].replace("{n}", n);
    submit.className = "btn " + (action === "purge" ? "btn-danger" : "btn-primary");

    bulkForm.querySelectorAll("[data-sd-bulk-section]").forEach(function (section) {
      var on = section.getAttribute("data-sd-bulk-section") === action;
      section.hidden = !on;
      section.disabled = !on;
    });

    var confirm = bulkForm.querySelector("[data-sd-confirm-count]");
    var over = count > limit && action !== "merge";
    confirm.hidden = !over;
    confirm.querySelector("input").disabled = !over;
    confirm.querySelector("input").value = "";
    confirm.querySelector("[data-sd-count-text]").textContent = count.toLocaleString();

    if (action === "merge") buildMergeChoices();

    var modal = bootstrap.Modal.getOrCreateInstance(doc.getElementById("sdBulkDialog"));
    modal.show();
  }

  function buildMergeChoices() {
    var host = bulkForm.querySelector("[data-sd-merge-choices]");
    host.textContent = "";
    selectedBoxes().forEach(function (box, i) {
      var wrap = doc.createElement("div");
      wrap.className = "form-check";
      var input = doc.createElement("input");
      input.className = "form-check-input";
      input.type = "radio";
      input.name = "PrimaryId";
      input.id = "mergePrimary" + i;
      input.value = box.value;
      input.required = true;
      if (i === 0) input.checked = true;
      var label = doc.createElement("label");
      label.className = "form-check-label";
      label.htmlFor = input.id;
      var stamp = doc.createElement("span");
      stamp.className = "sb-stamp me-2";
      stamp.textContent = box.getAttribute("data-sd-display");
      label.appendChild(stamp);
      label.appendChild(doc.createTextNode(box.getAttribute("data-sd-subject")));
      wrap.append(input, label);
      host.appendChild(wrap);
    });
  }

  // ---------------------------------------------------------------- live updates

  var live = doc.getElementById("sdLive");
  var pending = 0;

  doc.addEventListener("sb:live", function (e) {
    var d = e.detail || {};
    if (d.type !== "ticket.created" && d.type !== "ticket.updated") return;
    var p = d.payload || {};
    if (p.byUserId && p.byUserId === me) return;
    pending++;
    if (!live) return;
    live.querySelector("[data-sd-live-text]").textContent = (pending === 1 ? "1 update" : pending + " updates") + ". Show them";
    live.hidden = false;
  });

  if (live) live.addEventListener("click", function (e) {
    if (e.target.closest("[data-sd-show-updates]")) refresh();
  });

  function refresh() {
    var container = doc.getElementById("sdResults");
    if (!container) return;
    var before = new Set(Array.prototype.map.call(container.querySelectorAll("[data-sd-id]"), function (el) { return el.getAttribute("data-sd-id"); }));
    var checked = new Set(selectedBoxes().map(function (b) { return b.value; }));
    sb.fetch(root.getAttribute("data-sd-rows-url"), { headers: { "HX-Request": "true" } })
      .then(function (r) { if (!r.ok) throw new Error(); return r.text(); })
      .then(function (html) {
        container.innerHTML = html;
        container.querySelectorAll("input[data-sb-select]").forEach(function (b) { b.checked = checked.has(b.value); });
        container.querySelectorAll("[data-sd-id]").forEach(function (row) {
          if (before.has(row.getAttribute("data-sd-id"))) return;
          var lamp = row.querySelector(".sb-lamp--lit, .sb-lamp--alert");
          if (lamp) lamp.classList.add("sb-lamp--arriving");
        });
        if (window.htmx) htmx.process(container);
        sb.init(container);
        var first = container.querySelector("input[data-sb-select]");
        if (first) first.dispatchEvent(new Event("change", { bubbles: true }));
        pending = 0;
        if (live) live.hidden = true;
        refreshCounts();
      })
      .catch(function () { sb.toast("Couldn't load the updates. Reload the page.", "error"); });
  }

  function refreshCounts() {
    var nav = doc.getElementById("sdViewsNav");
    if (!nav) return;
    sb.fetch(root.getAttribute("data-sd-counts-url"), { headers: { "HX-Request": "true" } })
      .then(function (r) { return r.ok ? r.text() : null; })
      .then(function (html) {
        if (html == null) return;
        nav.innerHTML = html;
        var picker = nav.querySelector("[data-sd-view-picker]");
        if (picker) picker.addEventListener("change", function () { if (picker.value) window.location.href = picker.value; });
      });
  }

  // ---------------------------------------------------------------- keyboard

  function typing(el) {
    var tag = (el.tagName || "").toLowerCase();
    return tag === "input" || tag === "textarea" || tag === "select" || el.isContentEditable;
  }

  function rows() { return Array.prototype.slice.call(doc.querySelectorAll("#sdResults tbody tr[data-sd-id], #sdResults .sd-card")); }

  doc.addEventListener("keydown", function (e) {
    if (e.ctrlKey || e.metaKey || e.altKey || typing(e.target) || doc.querySelector(".modal.show")) return;
    var list = rows();
    if (list.length === 0) return;
    var index = list.indexOf(doc.activeElement && doc.activeElement.closest("tr[data-sd-id], .sd-card"));
    if (e.key === "j" || e.key === "k") {
      e.preventDefault();
      var next = e.key === "j" ? Math.min(index + 1, list.length - 1) : Math.max(index - 1, 0);
      if (index < 0) next = 0;
      list[next].focus();
      list[next].scrollIntoView({ block: "nearest" });
    } else if (e.key === "x" && index >= 0) {
      var box = list[index].querySelector("input[data-sb-select]");
      if (box) { e.preventDefault(); box.checked = !box.checked; box.dispatchEvent(new Event("change", { bubbles: true })); }
    } else if ((e.key === "Enter" || e.key === "o") && index >= 0 && doc.activeElement === list[index]) {
      e.preventDefault();
      window.location.href = list[index].getAttribute("data-sb-href");
    }
  });

  // ---------------------------------------------------------------- board

  var board = doc.querySelector("[data-sd-board]");
  if (!board) return;
  var by = board.getAttribute("data-sd-by");
  var dragged = null;

  function columnOf(card) { return card.closest("[data-sd-col]"); }

  function updateCounts() {
    board.querySelectorAll("[data-sd-col]").forEach(function (col) {
      var count = col.querySelector("[data-sd-col-count]");
      if (count) count.textContent = col.querySelectorAll(".sd-card").length;
    });
  }

  function move(card, targetCol) {
    var from = columnOf(card);
    if (!targetCol || from === targetCol) return;
    var nextSibling = card.nextElementSibling;
    targetCol.querySelector("[data-sd-dropzone]").prepend(card);
    updateCounts();
    var body = new FormData();
    body.append("Id", card.getAttribute("data-sd-id"));
    body.append("By", by);
    body.append("Value", targetCol.getAttribute("data-sd-col"));
    sb.fetch("/tickets?handler=Move", { method: "POST", body: body, headers: { Accept: "application/json" } })
      .then(function (r) {
        return r.json().catch(function () { return {}; }).then(function (data) {
          if (!r.ok) throw new Error(data.detail || data.title || "That move didn't work.");
          sb.toast(data.message || "Moved.");
        });
      })
      .catch(function (err) {
        from.querySelector("[data-sd-dropzone]").insertBefore(card, nextSibling && nextSibling.parentNode ? nextSibling : null);
        updateCounts();
        sb.toast(err.message, "error");
      });
  }

  board.addEventListener("dragstart", function (e) {
    var card = e.target.closest(".sd-card");
    if (!card) return;
    dragged = card;
    card.setAttribute("aria-grabbed", "true");
    e.dataTransfer.effectAllowed = "move";
    e.dataTransfer.setData("text/plain", card.getAttribute("data-sd-id"));
  });
  board.addEventListener("dragend", function () {
    if (dragged) dragged.removeAttribute("aria-grabbed");
    dragged = null;
    board.querySelectorAll(".is-drop-target").forEach(function (c) { c.classList.remove("is-drop-target"); });
  });
  board.addEventListener("dragover", function (e) {
    var col = e.target.closest("[data-sd-col]");
    if (!col || !dragged) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
    board.querySelectorAll(".is-drop-target").forEach(function (c) { if (c !== col) c.classList.remove("is-drop-target"); });
    col.classList.add("is-drop-target");
  });
  board.addEventListener("drop", function (e) {
    var col = e.target.closest("[data-sd-col]");
    if (!col || !dragged) return;
    e.preventDefault();
    col.classList.remove("is-drop-target");
    move(dragged, col);
  });

  // Keyboard and touch alternative: the card's "Move to" menu.
  board.addEventListener("show.bs.dropdown", function (e) {
    var card = e.target.closest(".sd-card");
    if (!card) return;
    var current = columnOf(card).getAttribute("data-sd-col");
    card.querySelectorAll("[data-sd-move]").forEach(function (item) {
      var here = item.getAttribute("data-sd-move") === current;
      item.disabled = here;
      item.setAttribute("aria-current", here ? "true" : "false");
    });
  });
  board.addEventListener("click", function (e) {
    var item = e.target.closest("[data-sd-move]");
    if (!item) return;
    var card = item.closest(".sd-card");
    var target = board.querySelector('[data-sd-col="' + CSS.escape(item.getAttribute("data-sd-move")) + '"]');
    move(card, target);
    card.focus();
  });
  board.addEventListener("keydown", function (e) {
    var card = e.target.closest && e.target.closest(".sd-card");
    if (card && e.target === card && e.key === "Enter") window.location.href = card.getAttribute("data-sb-href");
  });
})();
