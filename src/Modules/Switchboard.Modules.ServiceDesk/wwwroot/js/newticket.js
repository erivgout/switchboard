// New ticket and template forms: requester picker, category cascade, group-filtered agents, priority hint.
(function () {
  "use strict";

  var doc = document;
  var form = doc.querySelector("[data-sd-new-ticket]");
  if (!form) return;

  var summary = doc.querySelector("[data-sd-error-summary]");
  if (summary) summary.focus();

  doc.querySelectorAll("[data-sd-autosubmit]").forEach(function (el) {
    el.addEventListener("change", function () { el.form.submit(); });
  });

  var workspace = doc.querySelector("[data-sd-workspace-switch]");
  if (workspace) workspace.addEventListener("change", function () {
    window.location.href = workspace.getAttribute("data-sd-url") + "&ws=" + encodeURIComponent(workspace.value);
  });

  // ---------------------------------------------------------------- requester picker (combobox)

  var picker = doc.querySelector("[data-sd-requester-picker]");
  if (picker) {
    var input = picker.querySelector("[data-sd-people-input]");
    var list = picker.querySelector("[data-sd-people]");
    var idField = picker.querySelector("[data-sd-requester-id]");
    var emailField = picker.querySelector("[data-sd-requester-email]");
    var labelField = picker.querySelector("[data-sd-requester-label]");
    var picked = picker.querySelector("[data-sd-picked]");
    var combo = picker.querySelector("[data-sd-combobox]");
    var newName = picker.querySelector("[data-sd-new-name]");
    var timer = null;
    var seq = 0;
    var active = -1;

    var options = function () { return Array.prototype.slice.call(list.querySelectorAll("[role=option]")); };

    var open = function (show) {
      list.classList.toggle("show", show);
      input.setAttribute("aria-expanded", show ? "true" : "false");
      if (!show) { active = -1; input.removeAttribute("aria-activedescendant"); }
    };

    var highlight = function (i) {
      var opts = options();
      if (opts.length === 0) return;
      active = (i + opts.length) % opts.length;
      opts.forEach(function (o, n) { o.setAttribute("aria-selected", n === active ? "true" : "false"); o.classList.toggle("active", n === active); });
      input.setAttribute("aria-activedescendant", opts[active].id);
      opts[active].scrollIntoView({ block: "nearest" });
    };

    var choose = function (option) {
      if (!option) return;
      var email = option.getAttribute("data-sd-person-new");
      if (email) {
        idField.value = "";
        emailField.value = email;
        labelField.value = "";
        picked.querySelector("[data-sd-picked-text]").textContent = "New requester: " + email;
        newName.hidden = false;
      } else {
        idField.value = option.getAttribute("data-sd-person-id");
        emailField.value = "";
        labelField.value = option.getAttribute("data-sd-person-label");
        picked.querySelector("[data-sd-picked-text]").textContent = labelField.value;
        newName.hidden = true;
      }
      open(false);
      combo.hidden = true;
      picked.hidden = false;
      var subject = doc.getElementById("sdSubjectNew");
      if (subject) subject.focus();
    };

    input.addEventListener("input", function () {
      clearTimeout(timer);
      var q = input.value.trim();
      if (q.length < 2) { list.textContent = ""; open(false); return; }
      timer = setTimeout(function () {
        var mine = ++seq;
        sb.fetch(picker.getAttribute("data-sd-people-url") + "&q=" + encodeURIComponent(q), { headers: { "HX-Request": "true" } })
          .then(function (r) { return r.ok ? r.text() : ""; })
          .then(function (html) {
            if (mine !== seq) return;
            list.innerHTML = html;
            open(list.children.length > 0);
            if (options().length > 0) highlight(0);
          });
      }, 200);
    });

    input.addEventListener("keydown", function (e) {
      if (e.key === "ArrowDown") { e.preventDefault(); if (!list.classList.contains("show") && options().length) open(true); highlight(active + 1); }
      else if (e.key === "ArrowUp") { e.preventDefault(); highlight(active - 1); }
      else if (e.key === "Enter") { if (list.classList.contains("show") && active >= 0) { e.preventDefault(); choose(options()[active]); } }
      else if (e.key === "Escape") { open(false); }
    });
    input.addEventListener("blur", function () { setTimeout(function () { open(false); }, 150); });

    list.addEventListener("mousedown", function (e) {
      var option = e.target.closest("[role=option]");
      if (option) { e.preventDefault(); choose(option); }
    });

    picker.querySelector("[data-sd-change-requester]").addEventListener("click", function () {
      idField.value = "";
      emailField.value = "";
      labelField.value = "";
      picked.hidden = true;
      combo.hidden = false;
      newName.hidden = true;
      input.value = "";
      input.focus();
    });

    // Someone typed a full email and submitted without picking: treat it as a new requester.
    form.addEventListener("submit", function () {
      var q = input.value.trim();
      if (!idField.value && !emailField.value && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(q)) emailField.value = q;
    });
  }

  // ---------------------------------------------------------------- category cascade

  var cats = doc.querySelector("[data-sd-categories]");
  if (cats) {
    var tree = [];
    try { tree = JSON.parse(cats.getAttribute("data-sd-categories") || "[]"); } catch (_) { tree = []; }
    var level = function (n) { return cats.querySelector('[data-sd-cat-level="' + n + '"]'); };
    var wrap = function (n) { return cats.querySelector('[data-sd-cat-wrap="' + n + '"]'); };

    var fill = function (n, parentId) {
      var select = level(n);
      if (!select) return;
      var keep = select.value || select.getAttribute("data-sd-selected") || "";
      select.removeAttribute("data-sd-selected");
      var children = parentId ? tree.filter(function (c) { return c.level === n && c.parent === parentId; }) : [];
      select.textContent = "";
      var none = doc.createElement("option");
      none.value = "";
      none.textContent = "None";
      select.appendChild(none);
      children.forEach(function (c) {
        var o = doc.createElement("option");
        o.value = c.id;
        o.textContent = c.name;
        if (c.id === keep) o.selected = true;
        select.appendChild(o);
      });
      var w = wrap(n);
      if (w) w.hidden = children.length === 0;
      if (n < 3) fill(n + 1, select.value);
    };

    [1, 2].forEach(function (n) {
      var select = level(n);
      if (select) select.addEventListener("change", function () { fill(n + 1, select.value); });
    });
    fill(2, level(1) ? level(1).value : "");
  }

  // ---------------------------------------------------------------- agents filtered by group

  var group = doc.querySelector("[data-sd-group]");
  var agent = doc.querySelector("[data-sd-agent]");
  if (group && agent) {
    var filterAgents = function () {
      var g = group.value;
      Array.prototype.forEach.call(agent.options, function (o) {
        if (!o.value) return;
        var groups = (o.getAttribute("data-sd-groups") || "").split(" ");
        var show = !g || groups.indexOf(g) >= 0;
        o.hidden = !show;
        o.disabled = !show;
        if (!show && o.selected) agent.value = "";
      });
    };
    group.addEventListener("change", filterAgents);
    filterAgents();
  }

  // ---------------------------------------------------------------- priority matrix hint (Appendix C)

  var impact = doc.querySelector("[data-sd-impact]");
  var urgency = doc.querySelector("[data-sd-urgency]");
  var priority = doc.querySelector("[data-sd-priority]");
  var hint = doc.querySelector("[data-sd-priority-hint]");
  if (impact && urgency && priority && hint) {
    var rank = { Low: 1, Medium: 2, High: 3 };
    var derive = function (i, u) {
      if (i === 3 && u === 3) return "Urgent";
      if ((i === 3 && u === 2) || (i === 2 && u === 3)) return "High";
      if ((i === 3 && u === 1) || (i === 2 && u === 2) || (i === 1 && u === 3)) return "Medium";
      return "Low";
    };
    var update = function () {
      if (priority.value) {
        hint.textContent = "Set by hand. Choose “From impact and urgency” to calculate it.";
        return;
      }
      var p = derive(rank[impact.value] || 1, rank[urgency.value] || 1);
      hint.textContent = "Priority will be " + p + ", from " + impact.value.toLowerCase() + " impact and " + urgency.value.toLowerCase() + " urgency.";
    };
    [impact, urgency, priority].forEach(function (el) { el.addEventListener("change", update); });
    update();
  }
})();
