// Small behaviours for automation admin forms. Elements with data-aut-show-when="selectId=value1,value2"
// are shown only while the select (or checkbox group) has one of those values.
(function () {
  "use strict";

  function update() {
    document.querySelectorAll("[data-aut-show-when]").forEach(function (el) {
      var rule = el.getAttribute("data-aut-show-when").split("=");
      var source = document.getElementById(rule[0]);
      if (!source) return;
      var show = rule[1].split(",").indexOf(source.value) >= 0;
      el.hidden = !show;
      el.querySelectorAll("input, select, textarea").forEach(function (c) { c.disabled = !show; });
    });
  }

  document.addEventListener("change", function (e) {
    if (e.target.matches("select, input")) update();
  });
  document.addEventListener("DOMContentLoaded", update);
  update();
})();
