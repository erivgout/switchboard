// Service levels admin: drag to reorder policies, calendar hour rows, workspace picker.
(function () {
  "use strict";
  var doc = document;

  // ---------------------------------------------------------------- workspace picker
  doc.addEventListener("change", function (e) {
    var select = e.target.closest("[data-sla-autosubmit]");
    if (select && select.form) select.form.submit();
  });

  // ---------------------------------------------------------------- policy order
  function initReorder(table) {
    var url = table.getAttribute("data-sla-reorder");
    var body = table.tBodies[0];
    var dragging = null;
    var startIndex = -1;

    function rows() { return Array.prototype.slice.call(body.rows); }

    function renumber() {
      var all = rows();
      all.forEach(function (row, i) {
        var n = row.querySelector(".sla-order");
        if (n) n.textContent = String(i + 1);
        var up = row.querySelector("button .bi-arrow-up");
        var down = row.querySelector("button .bi-arrow-down");
        if (up) up.parentElement.disabled = i === 0;
        if (down) down.parentElement.disabled = i === all.length - 1;
      });
    }

    function save() {
      var data = new FormData();
      rows().forEach(function (row) { data.append("ids", row.getAttribute("data-id")); });
      sb.fetch(url, { method: "POST", body: data }).then(function (r) {
        if (r.ok) {
          renumber();
          sb.toast("Order saved");
        } else {
          sb.toast("The order wasn't saved. Reload the page and try again.", "error");
        }
      }, function () {
        sb.toast("The order wasn't saved. Check your connection and try again.", "error");
      });
    }

    body.addEventListener("dragstart", function (e) {
      var row = e.target.closest("tr[data-id]");
      if (!row) return;
      dragging = row;
      startIndex = rows().indexOf(row);
      row.classList.add("is-dragging");
      e.dataTransfer.effectAllowed = "move";
      e.dataTransfer.setData("text/plain", row.getAttribute("data-id"));
    });

    body.addEventListener("dragover", function (e) {
      if (!dragging) return;
      e.preventDefault();
      var over = e.target.closest("tr[data-id]");
      if (!over || over === dragging) return;
      var rect = over.getBoundingClientRect();
      var after = e.clientY > rect.top + rect.height / 2;
      body.insertBefore(dragging, after ? over.nextSibling : over);
    });

    body.addEventListener("drop", function (e) { if (dragging) e.preventDefault(); });

    body.addEventListener("dragend", function () {
      if (!dragging) return;
      dragging.classList.remove("is-dragging");
      var moved = rows().indexOf(dragging) !== startIndex;
      dragging = null;
      if (moved) save();
    });
  }

  // ---------------------------------------------------------------- calendar hours
  function initHours(root) {
    var hours = root.querySelector("[data-sla-hours]");
    if (!hours) return;
    root.querySelectorAll("[data-sla-247]").forEach(function (radio) {
      radio.addEventListener("change", function () {
        if (radio.checked) hours.hidden = radio.getAttribute("data-sla-247") === "on";
      });
    });

    function sync(row) {
      var open = row.querySelector("[data-sla-open]");
      row.classList.toggle("is-closed", !open.checked);
      var starts = row.querySelectorAll("[data-sla-start]");
      var ends = row.querySelectorAll("[data-sla-end]");
      for (var i = 0; i < ends.length; i++) {
        var hint = ends[i].parentElement.querySelector(".sla-overnight");
        var s = starts[i].value;
        var en = ends[i].value;
        if (hint) hint.hidden = !(s && en && en <= s && en !== "00:00");
      }
    }

    root.querySelectorAll("[data-sla-day]").forEach(function (row) {
      sync(row);
      row.addEventListener("input", function () { sync(row); });
      row.addEventListener("change", function () { sync(row); });
    });
  }

  // Picking dates switches the period to custom dates.
  doc.addEventListener("change", function (e) {
    var form = e.target.closest("[data-period-form]");
    if (!form || e.target.type !== "date") return;
    var period = form.querySelector("select[name=period]");
    if (period) period.value = "custom";
  });

  // Escalation levels show their settings only while turned on.
  doc.addEventListener("change", function (e) {
    var toggle = e.target.closest("[data-sla-level-toggle]");
    if (!toggle) return;
    var body = doc.getElementById(toggle.getAttribute("aria-controls"));
    if (body) body.hidden = !toggle.checked;
  });

  doc.addEventListener("DOMContentLoaded", function () {
    doc.querySelectorAll("table[data-sla-reorder]").forEach(initReorder);
    initHours(doc);
  });
})();
