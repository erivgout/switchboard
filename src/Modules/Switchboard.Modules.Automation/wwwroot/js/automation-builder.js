// Workflow and scenario builder. Renders When / If / Then from a JSON definition and writes the edited
// definition back to a hidden input. No inline scripts or handlers (CSP); all text goes in via textContent.
(function () {
  "use strict";

  var root = document.getElementById("autBuilder");
  if (!root) return;

  var catalog = JSON.parse(root.getAttribute("data-catalog"));
  var state = JSON.parse(root.getAttribute("data-definition"));
  var mode = root.getAttribute("data-mode") || "workflow";
  var output = document.getElementById(root.getAttribute("data-output"));
  var allowed = (root.getAttribute("data-allowed-actions") || "").split(",").filter(Boolean);
  var seq = 0;
  var dirty = false;

  state.trigger = state.trigger || { event: "ticket.created", changes: [] };
  state.trigger.changes = state.trigger.changes || [];
  state.conditions = state.conditions || { match: "all", groups: [] };
  state.conditions.groups = state.conditions.groups || [];
  state.actions = state.actions || [];

  var fields = {};
  catalog.fields.forEach(function (f) { fields[f.key] = f; });
  var actions = {};
  catalog.actions.forEach(function (a) { actions[a.type] = a; });
  var noValue = ["empty", "not_empty"];

  // ---------------------------------------------------------------- helpers

  function el(tag, attrs, children) {
    var node = document.createElement(tag);
    Object.keys(attrs || {}).forEach(function (k) {
      var v = attrs[k];
      if (v === null || v === undefined || v === false) return;
      if (k === "text") node.textContent = v;
      else if (k === "className") node.className = v;
      else node.setAttribute(k, v === true ? "" : v);
    });
    (children || []).forEach(function (c) { if (c) node.appendChild(typeof c === "string" ? document.createTextNode(c) : c); });
    return node;
  }

  function uid(prefix) { seq += 1; return "aut-" + prefix + "-" + seq; }

  function newId() { return Math.random().toString(16).slice(2, 12); }

  function selectBox(options, value, attrs, placeholder) {
    var s = el("select", Object.assign({ className: "form-select" }, attrs || {}));
    if (placeholder !== undefined) s.appendChild(el("option", { value: "", text: placeholder }));
    var found = false;
    options.forEach(function (o) {
      if (o.group) {
        var g = el("optgroup", { label: o.group });
        o.items.forEach(function (i) {
          var opt = el("option", { value: i.value, text: i.label });
          if (same(i.value, value)) { opt.selected = true; found = true; }
          g.appendChild(opt);
        });
        s.appendChild(g);
        return;
      }
      var opt = el("option", { value: o.value, text: o.label });
      if (same(o.value, value)) { opt.selected = true; found = true; }
      s.appendChild(opt);
    });
    if (!found && value) {
      // Keep a value that no longer exists visible, so publishing can point it out.
      var missing = el("option", { value: value, text: "A deleted choice" });
      missing.selected = true;
      s.appendChild(missing);
    }
    return s;
  }

  function same(a, b) { return String(a || "").toLowerCase() === String(b || "").toLowerCase(); }

  function iconButton(icon, label, onClick, disabled) {
    var b = el("button", { type: "button", className: "btn btn-ghost btn-icon btn-sm", "aria-label": label, title: label, disabled: !!disabled },
      [el("i", { className: "bi " + icon, "aria-hidden": "true" })]);
    b.addEventListener("click", onClick);
    return b;
  }

  function ghostButton(icon, text, onClick) {
    var b = el("button", { type: "button", className: "btn btn-ghost btn-sm" }, [el("i", { className: "bi " + icon + " me-1", "aria-hidden": "true" }), text]);
    b.addEventListener("click", onClick);
    return b;
  }

  function grouped(list, keyOf, filter) {
    var out = [];
    var map = {};
    list.forEach(function (item) {
      if (filter && !filter(item)) return;
      var g = keyOf(item);
      if (!map[g]) { map[g] = { group: g, items: [] }; out.push(map[g]); }
      map[g].items.push({ value: item.key || item.type, label: item.label });
    });
    return out;
  }

  function operatorsFor(field) {
    var ops = (catalog.operators[field.kind] || []).slice();
    if (field.kind === "Choice" && !field.nullable) ops = ops.filter(function (o) { return noValue.indexOf(o.value) < 0; });
    return ops;
  }

  function sync() {
    output.value = JSON.stringify(state);
    dirty = true;
    document.body.dispatchEvent(new CustomEvent("aut:changed", { bubbles: true }));
  }

  function last(list) { return list.length ? list[list.length - 1] : null; }

  // Re-renders, then moves focus to the control the person is working on (a selector or a function).
  function rerender(focus) {
    render();
    sync();
    var target = typeof focus === "function" ? focus() : focus ? root.querySelector(focus) : null;
    if (target) target.focus();
  }

  // ---------------------------------------------------------------- When

  function renderWhen() {
    var t = state.trigger;
    var choicesId = uid("when");
    var fs = el("fieldset", { className: "aut-choices" }, [el("legend", { className: "visually-hidden", text: "What starts this workflow" })]);
    catalog.triggers.forEach(function (trig) {
      var input = el("input", { type: "radio", name: choicesId, value: trig.key, className: "form-check-input" });
      input.checked = trig.key === t.event;
      input.addEventListener("change", function () {
        t.event = trig.key;
        t.changes = [];
        t.conversation = trig.key === "ticket.conversation" ? "any" : null;
        t.clock = trig.key === "ticket.time" ? (t.clock || "created") : null;
        t.hours = trig.key === "ticket.time" ? (t.hours || 24) : null;
        rerender('input[name="' + choicesId + '"]:checked');
      });
      fs.appendChild(el("label", { className: "aut-choice" }, [
        input,
        el("span", { className: "aut-choice-label", text: trig.label }),
        el("span", { className: "aut-choice-hint", text: trig.hint }),
      ]));
    });

    var body = el("div", { className: "aut-block-body" }, [fs]);
    var detail = triggerDetail(t);
    if (detail) body.appendChild(detail);
    return block("When", "What starts this workflow", null, body, false);
  }

  function triggerDetail(t) {
    if (t.event === "ticket.updated") {
      var wrap = el("div", { className: "aut-trigger-detail" });
      var changeFields = catalog.fields.filter(function (f) { return f.changeName; });
      if (t.changes.length === 0) {
        wrap.appendChild(el("p", { className: "aut-empty-line", text: "Runs on any change. To run only when a particular field changes, add it here." }));
      } else {
        wrap.appendChild(el("p", { className: "aut-block-hint mb-2", text: "Only when any of these change:" }));
      }
      t.changes.forEach(function (c, i) {
        var field = fields[c.field];
        var fieldId = uid("chg");
        var fieldSelect = selectBox(grouped(changeFields, function (f) { return f.group; }), c.field, { id: fieldId, "aria-label": "Field that changes" });
        fieldSelect.addEventListener("change", function () { c.field = fieldSelect.value; c.to = null; rerender("#" + fieldId); });
        var toControl;
        if (field && field.options.length > 0) {
          toControl = selectBox(field.options, c.to, { "aria-label": "Changes to" }, "to any value");
          toControl.addEventListener("change", function () { c.to = toControl.value || null; sync(); });
        } else if (field && field.kind === "Tags") {
          toControl = el("input", { className: "form-control", type: "text", value: c.to || "", placeholder: "Tag added (optional)", "aria-label": "Tag added" });
          toControl.addEventListener("input", function () { c.to = toControl.value.trim() || null; sync(); });
        } else {
          toControl = el("span", { className: "text-muted small", text: "to any value" });
        }
        wrap.appendChild(el("div", { className: "aut-row aut-row--pair" }, [
          fieldSelect, toControl,
          iconButton("bi-x-lg", "Remove this field", function () { t.changes.splice(i, 1); rerender(); }),
        ]));
      });
      wrap.appendChild(el("div", { className: "mt-2" }, [ghostButton("bi-plus-lg", "Add a field", function () {
        t.changes.push({ field: "ticket.status", to: null });
        rerender(function () { var row = last(root.querySelectorAll(".aut-trigger-detail .aut-row")); return row && row.querySelector("select"); });
      })]));
      return wrap;
    }
    if (t.event === "ticket.conversation") {
      var id = uid("conv");
      var s = selectBox(catalog.conversationKinds, t.conversation || "any", { id: id });
      s.addEventListener("change", function () { t.conversation = s.value; sync(); });
      return el("div", { className: "aut-trigger-detail" }, [
        el("label", { className: "form-label", for: id, text: "Which messages" }), s,
      ]);
    }
    if (t.event === "ticket.time") {
      var hoursId = uid("hours");
      var hours = el("input", { className: "form-control aut-hours", type: "number", min: "1", max: "8760", step: "1", id: hoursId, value: t.hours || 24 });
      hours.addEventListener("input", function () { t.hours = Number(hours.value) || null; sync(); });
      var clock = selectBox(catalog.clocks, t.clock || "created", { "aria-label": "Measured from" });
      clock.addEventListener("change", function () { t.clock = clock.value; sync(); });
      return el("div", { className: "aut-trigger-detail" }, [
        el("label", { className: "form-label", for: hoursId, text: "Run once a ticket reaches" }),
        el("div", { className: "aut-inline" }, [hours, el("span", { text: "hours" }), clock]),
        el("p", { className: "aut-block-hint mt-2", text: "Checked every few minutes against tickets that aren't closed. Each ticket runs once per crossing: if it is updated or changes status, the clock starts again." }),
      ]);
    }
    return null;
  }

  // ---------------------------------------------------------------- If

  function renderIf() {
    var c = state.conditions;
    var body = el("div", { className: "aut-block-body" });
    var groups = c.groups;
    var empty = groups.every(function (g) { return !g.rules || g.rules.length === 0; });

    if (empty) {
      body.appendChild(el("p", { className: "aut-empty-line", text: "Runs for every ticket the trigger catches. Add a condition to narrow it down." }));
      body.appendChild(ghostButton("bi-plus-lg", "Add a condition", function () {
        c.groups = [{ match: "all", rules: [newRule()] }];
        rerender(".aut-group select");
      }));
      return block("If", "Which tickets it applies to", null, body, true);
    }

    if (groups.length > 1) {
      var topId = uid("top");
      var top = selectBox([{ value: "all", label: "every group matches" }, { value: "any", label: "any one group matches" }], c.match, { id: topId });
      top.addEventListener("change", function () { c.match = top.value; rerender("#" + topId); });
      body.appendChild(el("div", { className: "aut-inline mb-3" }, [el("label", { for: topId, text: "Run when" }), top]));
    }

    groups.forEach(function (g, gi) {
      if (gi > 0) body.appendChild(el("div", { className: "aut-group-joiner", text: c.match === "any" ? "or" : "and" }));
      body.appendChild(renderGroup(g, gi));
    });

    body.appendChild(el("div", { className: "mt-3" }, [ghostButton("bi-plus-square", "Add another group", function () {
      c.groups.push({ match: "all", rules: [newRule()] });
      if (c.groups.length === 2) c.match = c.match || "any";
      rerender(function () { var g = last(root.querySelectorAll(".aut-group")); return g && g.querySelector(".aut-row select"); });
    })]));
    return block("If", "Which tickets it applies to", null, body, false);
  }

  function newRule() { return { field: "ticket.priority", op: "is", value: "High" }; }

  function renderGroup(g, gi) {
    var matchId = uid("match");
    var match = selectBox([{ value: "all", label: "all of these" }, { value: "any", label: "any of these" }], g.match, { id: matchId });
    match.addEventListener("change", function () { g.match = match.value; rerender("#" + matchId); });
    var head = el("div", { className: "aut-group-head" }, [
      el("div", { className: "aut-match-label" }, [el("label", { for: matchId, text: "Match" }), match]),
      iconButton("bi-trash3", "Remove this group", function () { state.conditions.groups.splice(gi, 1); rerender(); }),
    ]);
    var box = el("div", { className: "aut-group" }, [head]);
    g.rules.forEach(function (r, ri) {
      if (ri > 0) box.appendChild(el("div", { className: "aut-joiner", text: g.match === "any" ? "or" : "and" }));
      box.appendChild(renderRule(g, r, ri));
    });
    box.appendChild(el("div", { className: "mt-2" }, [ghostButton("bi-plus-lg", "Add a condition", function () {
      g.rules.push(newRule());
      rerender(function () {
        var box = root.querySelectorAll(".aut-group")[gi];
        var row = box && last(box.querySelectorAll(".aut-row"));
        return row && row.querySelector("select");
      });
    })]));
    return box;
  }

  function renderRule(g, r, ri) {
    var field = fields[r.field];
    var fieldId = uid("field");
    var fieldSelect = selectBox(grouped(catalog.fields, function (f) { return f.group; }), r.field, { id: fieldId, "aria-label": "Field" });
    fieldSelect.addEventListener("change", function () {
      r.field = fieldSelect.value;
      var f = fields[r.field];
      var ops = f ? operatorsFor(f) : [];
      r.op = ops.length ? ops[0].value : "is";
      r.value = f && f.options.length ? f.options[0].value : "";
      rerender("#" + fieldId);
    });
    var ops = field ? operatorsFor(field) : [{ value: r.op, label: r.op }];
    var opId = uid("op");
    var opSelect = selectBox(ops, r.op, { id: opId, "aria-label": "Comparison" });
    opSelect.addEventListener("change", function () { r.op = opSelect.value; rerender("#" + opId); });

    var value;
    if (noValue.indexOf(r.op) >= 0) {
      value = el("span", { "aria-hidden": "true" });
    } else if (field && (field.kind === "Choice" || field.kind === "Ordinal" || field.kind === "Bool")) {
      value = selectBox(field.options, r.value, { "aria-label": "Value" }, field.options.length === 0 ? "Nothing to choose yet" : undefined);
      value.addEventListener("change", function () { r.value = value.value; sync(); });
      if (!r.value && field.options.length) { r.value = field.options[0].value; }
    } else {
      value = el("input", {
        className: "form-control", type: field && field.kind === "Number" ? "number" : "text", value: r.value || "", "aria-label": "Value",
        placeholder: field && field.kind === "Tags" ? "tag" : "",
      });
      value.addEventListener("input", function () { r.value = value.value; sync(); });
    }
    var row = el("div", { className: "aut-row" }, [fieldSelect, opSelect, value,
      iconButton("bi-x-lg", "Remove this condition", function () {
        g.rules.splice(ri, 1);
        if (g.rules.length === 0) state.conditions.groups.splice(state.conditions.groups.indexOf(g), 1);
        rerender();
      })]);
    if (field && field.hint) row.title = field.hint;
    return row;
  }

  // ---------------------------------------------------------------- Then

  function availableActions() {
    return catalog.actions.filter(function (a) { return allowed.length === 0 || allowed.indexOf(a.type) >= 0; });
  }

  function renderThen() {
    var body = el("div", { className: mode === "actions" ? "aut-actions-only" : "aut-block-body" });
    if (state.actions.length === 0) {
      body.appendChild(el("p", { className: "aut-empty-line", text: mode === "actions"
        ? "Add the steps this scenario runs, in order."
        : "Add what should happen. Steps run in order, and ticket changes are saved together." }));
    } else {
      var list = el("ol", { className: "aut-steps" });
      state.actions.forEach(function (a, i) { list.appendChild(renderStep(a, i)); });
      body.appendChild(list);
    }

    var addId = uid("add");
    var add = selectBox(grouped(availableActions(), function (a) { return a.group; }), "", { id: addId }, "Choose an action");
    add.addEventListener("change", function () {
      if (!add.value) return;
      var meta = actions[add.value];
      var params = {};
      meta.params.forEach(function (p) { if (p.default) params[p.key] = p.default; });
      state.actions.push({ id: newId(), type: add.value, params: params });
      rerender(function () { var step = last(root.querySelectorAll(".aut-step")); return step && step.querySelector(".aut-step-body input, .aut-step-body select, .aut-step-body textarea"); });
    });
    body.appendChild(el("div", { className: "aut-add" }, [el("label", { className: "form-label", for: addId, text: "Add an action" }), add]));

    if (mode === "actions") return body;
    return block("Then", "What it does, in order", null, body, state.actions.length === 0);
  }

  function renderStep(a, i) {
    var meta = actions[a.type];
    var title = meta ? meta.label : "Unknown action";
    var tools = el("div", { className: "aut-step-tools" }, [
      iconButton("bi-arrow-up", "Move up", function () { move(i, -1); }, i === 0),
      iconButton("bi-arrow-down", "Move down", function () { move(i, 1); }, i === state.actions.length - 1),
      iconButton("bi-trash3", "Remove this step", function () { state.actions.splice(i, 1); rerender(); }),
    ]);
    var body = el("div", { className: "aut-step-body" });
    if (meta && meta.external) {
      body.appendChild(el("p", { className: "aut-step-note", text: "Sent once the ticket changes are saved. Never sent during a test." }));
    }
    (meta ? meta.params : []).forEach(function (p) {
      if (p.showWhen && (p.showWhenIn || []).indexOf(a.params[p.showWhen] || "") < 0) return;
      body.appendChild(renderParam(a, p));
    });
    return el("li", { className: "aut-step" }, [
      el("div", { className: "aut-step-head" }, [el("h3", { className: "aut-step-title", text: title }), tools]),
      body,
    ]);
  }

  function move(i, delta) {
    var j = i + delta;
    if (j < 0 || j >= state.actions.length) return;
    var item = state.actions.splice(i, 1)[0];
    state.actions.splice(j, 0, item);
    rerender(function () {
      var step = root.querySelectorAll(".aut-step")[j];
      return step && step.querySelector(".aut-step-tools button:not([disabled])");
    });
  }

  function renderParam(a, p) {
    var id = uid("p");
    var value = a.params[p.key];
    var control;
    var dependents = (actions[a.type].params || []).some(function (q) { return q.showWhen === p.key; });
    if (p.kind === "select") {
      control = selectBox(p.options || [], value, { id: id }, p.required ? (value ? undefined : "Choose") : undefined);
      control.addEventListener("change", function () {
        a.params[p.key] = control.value || null;
        if (dependents) rerender("#" + id); else sync();
      });
    } else if (p.kind === "textarea") {
      control = el("textarea", { className: "form-control", id: id, rows: "4", "data-sb-autosize": true });
      control.value = value || "";
      control.addEventListener("input", function () { a.params[p.key] = control.value; sync(); });
    } else if (p.kind === "secret") {
      var stored = a.params.authValueSet === "true";
      control = el("input", { className: "form-control", id: id, type: "password", autocomplete: "new-password",
        placeholder: stored ? "Saved. Type a new value to replace it." : "" });
      control.addEventListener("input", function () { a.params[p.key] = control.value; sync(); });
    } else {
      control = el("input", { className: "form-control", id: id, type: p.kind === "number" ? "number" : "text", value: value || "" });
      control.addEventListener("input", function () { a.params[p.key] = control.value; sync(); });
    }
    var wrap = el("div", {}, [el("label", { className: "form-label", for: id, text: p.label + (p.required ? "" : " (optional)") }), control]);
    if (p.help) {
      var helpId = id + "-help";
      control.setAttribute("aria-describedby", helpId);
      wrap.appendChild(el("div", { className: "aut-param-help", id: helpId, text: p.help }));
    }
    if (p.placeholders) wrap.appendChild(placeholderPicker(control, a, p));
    return wrap;
  }

  function placeholderPicker(control, a, p) {
    var s = selectBox(catalog.placeholders.map(function (ph) { return { value: ph.value, label: ph.label }; }), "",
      { className: "form-select form-select-sm aut-insert", "aria-label": "Insert a placeholder into " + p.label }, "Insert a placeholder");
    s.addEventListener("change", function () {
      if (!s.value) return;
      var token = "{{" + s.value + "}}";
      var start = control.selectionStart != null ? control.selectionStart : control.value.length;
      var end = control.selectionEnd != null ? control.selectionEnd : control.value.length;
      control.value = control.value.slice(0, start) + token + control.value.slice(end);
      a.params[p.key] = control.value;
      s.value = "";
      control.focus();
      control.setSelectionRange(start + token.length, start + token.length);
      sync();
    });
    return s;
  }

  // ---------------------------------------------------------------- layout

  function block(tab, title, hint, body, empty) {
    var headingId = uid("h");
    return el("section", { className: "aut-block sb-pane", "aria-labelledby": headingId }, [
      el("span", { className: "aut-tab" + (empty ? " is-empty" : ""), "aria-hidden": "true", text: tab }),
      el("div", { className: "aut-block-head" }, [
        el("h2", { className: "aut-block-title", id: headingId }, [el("span", { className: "visually-hidden", text: tab + ": " }), title]),
        hint ? el("p", { className: "aut-block-hint", text: hint }) : null,
      ]),
      body,
    ]);
  }

  function render() {
    root.textContent = "";
    if (mode === "actions") {
      root.appendChild(renderThen());
      return;
    }
    root.appendChild(el("div", { className: "aut-flow" }, [renderWhen(), renderIf(), renderThen()]));
  }

  render();
  output.value = JSON.stringify(state);

  var form = output.form;
  if (form) form.addEventListener("submit", function () { output.value = JSON.stringify(state); dirty = false; });
  document.querySelectorAll("button[form='" + (form && form.id) + "']").forEach(function (b) {
    b.addEventListener("click", function () { output.value = JSON.stringify(state); dirty = false; });
  });
  window.addEventListener("beforeunload", function (e) {
    if (dirty) { e.preventDefault(); e.returnValue = ""; }
  });
})();
