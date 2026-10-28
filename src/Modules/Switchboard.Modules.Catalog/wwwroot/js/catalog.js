// Service catalog behaviors. Opt in with data- attributes; no inline scripts.
(function () {
  "use strict";

  var doc = document;

  // Live total: <input data-cat-qty data-unit="129900" data-currency="USD"> and <span data-cat-total>.
  function updateTotal(input) {
    var form = input.closest("form") || doc;
    var total = form.querySelector("[data-cat-total]") || doc.querySelector("[data-cat-total]");
    var unit = parseInt(input.getAttribute("data-unit"), 10);
    if (!total || isNaN(unit)) return;
    var qty = Math.max(1, parseInt(input.value, 10) || 1);
    try {
      total.textContent = new Intl.NumberFormat(undefined, { style: "currency", currency: input.getAttribute("data-currency") || "USD" })
        .format((unit * qty) / 100);
    } catch (_) { /* unknown currency: keep the server text */ }
  }

  // Field type picker shows the options box only for dropdown and multi-select.
  function syncOptions(select) {
    var scope = select.closest("[data-cat-field-form]");
    if (!scope) return;
    var box = scope.querySelector("[data-cat-options]");
    if (!box) return;
    var uses = select.value === "Dropdown" || select.value === "MultiSelect";
    box.hidden = !uses;
    var area = box.querySelector("textarea");
    if (area) area.required = uses;
  }

  // Approval source rows show their settings only when the source is ticked (the server ignores unticked ones).
  function syncSource(check) {
    var row = check.closest("[data-cat-source]");
    var body = row && row.querySelector("[data-cat-source-body]");
    if (body) body.hidden = !check.checked;
  }

  doc.addEventListener("input", function (e) {
    if (e.target.matches("[data-cat-qty]")) updateTotal(e.target);
  });
  doc.addEventListener("change", function (e) {
    if (e.target.matches("[data-cat-type]")) syncOptions(e.target);
    if (e.target.matches("[data-cat-source-toggle]")) syncSource(e.target);
  });

  function init(root) {
    root.querySelectorAll("[data-cat-qty]").forEach(updateTotal);
    root.querySelectorAll("[data-cat-type]").forEach(syncOptions);
    root.querySelectorAll("[data-cat-source-toggle]").forEach(syncSource);
  }

  doc.addEventListener("DOMContentLoaded", function () { init(doc); });
  doc.addEventListener("htmx:afterSettle", function (e) { init(e.target); });
})();
