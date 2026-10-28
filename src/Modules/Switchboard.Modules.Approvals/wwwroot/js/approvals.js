// People picker: <div class="apr-picker" data-apr-picker data-name="Field" data-multiple="true|false" data-agents="true|false">.
// Selected people are chips holding hidden inputs, so the surrounding form posts their ids.
(function () {
  "use strict";

  var doc = document;
  var seq = 0;

  function chip(picker, id, name) {
    var field = picker.getAttribute("data-name");
    var wrap = doc.createElement("span");
    wrap.className = "apr-chip";
    var input = doc.createElement("input");
    input.type = "hidden";
    input.name = field;
    input.value = id;
    var label = doc.createElement("span");
    label.textContent = name;
    var remove = doc.createElement("button");
    remove.type = "button";
    remove.setAttribute("data-apr-remove", "");
    remove.setAttribute("aria-label", "Remove " + name);
    remove.innerHTML = '<i class="bi bi-x-lg" aria-hidden="true"></i>';
    wrap.append(input, label, remove);
    return wrap;
  }

  function sync(picker) {
    var has = picker.querySelector(".apr-chip") !== null;
    picker.classList.toggle("has-value", has);
    var input = picker.querySelector("[data-apr-picker-input]");
    if (input && picker.getAttribute("data-required") === "true") input.required = !has;
  }

  function init(picker) {
    if (picker.dataset.aprInit) return;
    picker.dataset.aprInit = "1";
    var multiple = picker.getAttribute("data-multiple") === "true";
    picker.classList.toggle("is-single", !multiple);
    var input = picker.querySelector("[data-apr-picker-input]");
    var list = picker.querySelector("[data-apr-picker-list]");
    var chips = picker.querySelector("[data-apr-picker-chips]");
    if (!input || !list || !chips) return;
    var listId = list.id || "aprList" + (++seq);
    list.id = listId;
    input.setAttribute("aria-controls", listId);
    var timer = null;
    var active = -1;
    var results = [];

    function close() {
      list.hidden = true;
      input.setAttribute("aria-expanded", "false");
      input.removeAttribute("aria-activedescendant");
      active = -1;
    }

    function highlight(i) {
      var options = list.querySelectorAll(".apr-option");
      options.forEach(function (o, n) { o.setAttribute("aria-selected", n === i ? "true" : "false"); });
      active = i;
      if (options[i]) {
        input.setAttribute("aria-activedescendant", options[i].id);
        options[i].scrollIntoView({ block: "nearest" });
      }
    }

    function choose(person) {
      if (!multiple) chips.innerHTML = "";
      var exists = Array.prototype.some.call(chips.querySelectorAll("input"), function (h) { return h.value === person.id; });
      if (!exists) chips.appendChild(chip(picker, person.id, person.name));
      input.value = "";
      close();
      sync(picker);
      picker.dispatchEvent(new CustomEvent("apr:change", { bubbles: true }));
      var removeButton = chips.querySelector(".apr-chip:last-child [data-apr-remove]");
      if (!multiple && removeButton) removeButton.focus(); else input.focus();
    }

    function render(people) {
      results = people;
      list.innerHTML = "";
      if (people.length === 0) {
        var empty = doc.createElement("li");
        empty.className = "apr-option-empty";
        empty.textContent = "No one matches. Try part of a name or an email address.";
        list.appendChild(empty);
      }
      people.forEach(function (p, i) {
        var li = doc.createElement("li");
        li.className = "apr-option";
        li.id = listId + "-" + i;
        li.setAttribute("role", "option");
        li.setAttribute("aria-selected", "false");
        var strong = doc.createElement("span");
        strong.textContent = p.name;
        li.appendChild(strong);
        if (p.detail) {
          var small = doc.createElement("small");
          small.textContent = p.detail;
          li.appendChild(small);
        }
        li.addEventListener("mousedown", function (e) { e.preventDefault(); choose(p); });
        list.appendChild(li);
      });
      list.hidden = false;
      input.setAttribute("aria-expanded", "true");
      active = -1;
    }

    function search() {
      var q = input.value.trim();
      if (q.length < 2) { close(); return; }
      var url = "/approvals/people?q=" + encodeURIComponent(q) + (picker.getAttribute("data-agents") === "true" ? "&agents=true" : "");
      fetch(url, { credentials: "same-origin", headers: { Accept: "application/json" } })
        .then(function (r) { return r.ok ? r.json() : []; })
        .then(function (people) { if (input.value.trim() === q) render(people); })
        .catch(function () { close(); });
    }

    input.addEventListener("input", function () {
      clearTimeout(timer);
      timer = setTimeout(search, 200);
    });
    input.addEventListener("keydown", function (e) {
      if (list.hidden) return;
      if (e.key === "ArrowDown") { e.preventDefault(); highlight(Math.min(active + 1, results.length - 1)); }
      else if (e.key === "ArrowUp") { e.preventDefault(); highlight(Math.max(active - 1, 0)); }
      else if (e.key === "Enter") { if (active >= 0 && results[active]) { e.preventDefault(); choose(results[active]); } }
      else if (e.key === "Escape") { e.preventDefault(); close(); }
    });
    input.addEventListener("blur", function () { setTimeout(close, 150); });
    chips.addEventListener("click", function (e) {
      var btn = e.target.closest("[data-apr-remove]");
      if (!btn) return;
      btn.closest(".apr-chip").remove();
      sync(picker);
      picker.dispatchEvent(new CustomEvent("apr:change", { bubbles: true }));
      input.focus();
    });
    close();
    sync(picker);
  }

  function initAll(root) { root.querySelectorAll("[data-apr-picker]").forEach(init); }

  window.aprInitPickers = initAll;
  doc.addEventListener("DOMContentLoaded", function () { initAll(doc); });
  doc.addEventListener("htmx:afterSettle", function (e) { initAll(e.target); });
})();
