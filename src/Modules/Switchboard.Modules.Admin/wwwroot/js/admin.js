// Admin console behaviors. Opt in with data- attributes; no inline scripts (CSP script-src 'self').
(function () {
  "use strict";

  var doc = document;

  function debounce(fn, ms) {
    var t;
    return function () {
      var args = arguments, self = this;
      clearTimeout(t);
      t = setTimeout(function () { fn.apply(self, args); }, ms);
    };
  }

  // ---------------------------------------------------------------- person search
  // <input data-sb-person-search="selectId" data-sb-kind="agents|requesters|all" data-sb-none="No one">
  var search = debounce(function (input) {
    var select = doc.getElementById(input.getAttribute("data-sb-person-search"));
    if (!select) return;
    var params = new URLSearchParams({
      q: input.value,
      kind: input.getAttribute("data-sb-kind") || "all",
      selected: select.value || "",
      none: input.getAttribute("data-sb-none") || "",
    });
    sb.fetch("/admin/people/lookup?" + params.toString(), { headers: { "HX-Request": "true" } })
      .then(function (r) { return r.ok ? r.text() : Promise.reject(r); })
      .then(function (html) {
        var current = select.value;
        select.innerHTML = html;
        if (input.value.trim().length > 0) {
          // Pick the first match so typing a name and saving does what people expect.
          var first = Array.prototype.find.call(select.options, function (o) { return o.value; });
          if (first && !Array.prototype.some.call(select.options, function (o) { return o.value === current && o.value; })) first.selected = true;
        }
      })
      .catch(function () { sb.toast("Couldn't search people. Try again.", "error"); });
  }, 250);

  doc.addEventListener("input", function (e) {
    if (e.target.matches("[data-sb-person-search]")) search(e.target);
  });

  // ---------------------------------------------------------------- auto-submitting filters
  doc.addEventListener("change", function (e) {
    var form = e.target.closest("form[data-sb-autosubmit]");
    if (form && e.target.matches("select")) form.requestSubmit();
  });

  // ---------------------------------------------------------------- typed confirmation
  // <input data-sb-confirm-text="dana@example.com" data-sb-confirm-button="#forgetButton">
  function syncConfirm(input) {
    var button = doc.querySelector(input.getAttribute("data-sb-confirm-button"));
    if (!button) return;
    var expected = (input.getAttribute("data-sb-confirm-text") || "").trim().toLowerCase();
    button.disabled = input.value.trim().toLowerCase() !== expected;
  }
  doc.addEventListener("input", function (e) {
    if (e.target.matches("[data-sb-confirm-text]")) syncConfirm(e.target);
  });

  // ---------------------------------------------------------------- show parts of a form for one choice
  // <div data-sb-show-when="Input.Action=ReassignToGroup,ReassignToAgent">
  function syncShowWhen(root) {
    root.querySelectorAll("[data-sb-show-when]").forEach(function (el) {
      var rule = el.getAttribute("data-sb-show-when").split("=");
      var form = el.closest("form") || doc;
      var controls = form.querySelectorAll('[name="' + rule[0] + '"]');
      var value = null;
      controls.forEach(function (c) {
        if ((c.type === "radio" || c.type === "checkbox") ? c.checked : true) value = c.value;
      });
      el.hidden = rule[1].split(",").indexOf(value) < 0;
    });
  }
  doc.addEventListener("change", function () { syncShowWhen(doc); });

  // ---------------------------------------------------------------- drag to reorder siblings
  // <ul data-sb-sortable="/admin/categories?handler=Reorder" data-sb-parent="..." data-sb-ws="..."> <li draggable="true" data-id="...">
  var dragged = null;

  function siblingsOf(li) {
    return Array.prototype.filter.call(li.parentElement.children, function (c) { return c.matches("li[data-id]"); });
  }

  function clearMarks() {
    doc.querySelectorAll(".is-drop-before, .is-drop-after").forEach(function (el) { el.classList.remove("is-drop-before", "is-drop-after"); });
  }

  doc.addEventListener("dragstart", function (e) {
    var li = e.target.closest && e.target.closest("[data-sb-sortable] > li[data-id]");
    if (!li) return;
    dragged = li;
    li.classList.add("is-dragging");
    e.dataTransfer.effectAllowed = "move";
    e.dataTransfer.setData("text/plain", li.getAttribute("data-id"));
  });

  doc.addEventListener("dragover", function (e) {
    if (!dragged) return;
    var li = e.target.closest && e.target.closest("li[data-id]");
    if (!li || li === dragged || li.parentElement !== dragged.parentElement) return;
    e.preventDefault();
    clearMarks();
    var box = li.getBoundingClientRect();
    li.classList.add(e.clientY < box.top + box.height / 2 ? "is-drop-before" : "is-drop-after");
  });

  doc.addEventListener("drop", function (e) {
    if (!dragged) return;
    var li = e.target.closest && e.target.closest("li[data-id]");
    if (!li || li === dragged || li.parentElement !== dragged.parentElement) return;
    e.preventDefault();
    var before = li.classList.contains("is-drop-before");
    li.parentElement.insertBefore(dragged, before ? li : li.nextSibling);
    saveOrder(dragged.parentElement);
  });

  doc.addEventListener("dragend", function () {
    if (dragged) dragged.classList.remove("is-dragging");
    dragged = null;
    clearMarks();
  });

  function saveOrder(list) {
    var body = new URLSearchParams();
    siblingsOf(list.querySelector("li[data-id]")).forEach(function (li) { body.append("ids", li.getAttribute("data-id")); });
    body.append("parentId", list.getAttribute("data-sb-parent") || "");
    body.append("ws", list.getAttribute("data-sb-ws") || "");
    sb.fetch(list.getAttribute("data-sb-sortable"), {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded", "HX-Request": "true" },
      body: body.toString(),
    }).then(function (r) {
      if (!r.ok) return Promise.reject(r);
      sb.toast("Order saved");
    }).catch(function () {
      sb.toast("The order couldn't be saved. Reload the page and try again.", "error");
    });
  }

  doc.addEventListener("DOMContentLoaded", function () {
    syncShowWhen(doc);
    doc.querySelectorAll("[data-sb-confirm-text]").forEach(syncConfirm);
  });
  doc.addEventListener("htmx:afterSettle", function (e) { syncShowWhen(e.target); });
})();
