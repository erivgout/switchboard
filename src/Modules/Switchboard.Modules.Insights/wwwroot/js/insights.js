// Insights charts: a crosshair and tooltip that snap to the nearest day, on hover and with the arrow keys.
// Values come from data-ins-points and are written with textContent only.
(function () {
  "use strict";

  function el(tag, cls, text) {
    var node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function initChart(figure) {
    var plot = figure.querySelector(".ins-plot");
    if (!plot) return;
    var points;
    try { points = JSON.parse(plot.getAttribute("data-ins-points") || "[]"); } catch (e) { return; }
    var n = points.length;
    if (n === 0) return;
    var cols = plot.querySelectorAll(".ins-col");
    var cross = plot.querySelector(".ins-cross");
    var tip = plot.querySelector(".ins-tip");
    var active = -1;

    function row(keyClass, value, label) {
      var r = el("div", "ins-tip-row");
      r.appendChild(el("span", "ins-key " + keyClass));
      r.appendChild(el("strong", "", String(value)));
      r.appendChild(el("span", "", label));
      return r;
    }

    function show(i) {
      i = Math.max(0, Math.min(n - 1, i));
      if (active >= 0 && cols[active]) cols[active].classList.remove("is-active");
      active = i;
      if (cols[i]) cols[i].classList.add("is-active");
      var p = points[i];
      var x = ((i + 0.5) / n) * 100;
      cross.style.left = x + "%";
      cross.hidden = false;
      tip.replaceChildren(el("div", "ins-tip-day", p.d), row("ins-key--bar", p.c, "created"), row("ins-key--line", p.r, "resolved"));
      tip.hidden = false;
      // Keep the tooltip inside the plot: right of the crosshair on the left half, left of it on the right half.
      if (x < 50) {
        tip.style.left = "calc(" + x + "% + 0.75rem)";
        tip.style.right = "auto";
      } else {
        tip.style.left = "auto";
        tip.style.right = "calc(" + (100 - x) + "% + 0.75rem)";
      }
    }

    function hide() {
      if (active >= 0 && cols[active]) cols[active].classList.remove("is-active");
      active = -1;
      cross.hidden = true;
      tip.hidden = true;
    }

    plot.addEventListener("pointermove", function (e) {
      var rect = plot.getBoundingClientRect();
      show(Math.floor(((e.clientX - rect.left) / rect.width) * n));
    });
    plot.addEventListener("pointerleave", function () {
      if (document.activeElement !== plot) hide();
    });
    plot.addEventListener("focus", function () { show(active >= 0 ? active : n - 1); });
    plot.addEventListener("blur", hide);
    plot.addEventListener("keydown", function (e) {
      var next = { ArrowLeft: active - 1, ArrowRight: active + 1, Home: 0, End: n - 1 }[e.key];
      if (next === undefined) {
        if (e.key === "Escape") hide();
        return;
      }
      e.preventDefault();
      show(next);
    });
  }

  // Picking dates switches the period to custom dates.
  document.addEventListener("change", function (e) {
    var form = e.target.closest("[data-period-form]");
    if (!form || e.target.type !== "date") return;
    var period = form.querySelector("select[name=period]");
    if (period) period.value = "custom";
  });

  document.addEventListener("DOMContentLoaded", function () {
    document.querySelectorAll("[data-ins-chart]").forEach(initChart);
  });
})();
