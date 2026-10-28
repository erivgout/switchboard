// Knowledge editor: a small contenteditable editor with a toolbar, paste cleanup, image upload,
// draft autosave, a related-article picker and file attachments. The server sanitizes everything
// again (IHtmlContent); the cleanup here only keeps pasted content tidy.
(function () {
  "use strict";

  var doc = document;
  var ALLOWED = {
    P: [], H2: [], H3: [], H4: [], UL: [], OL: [], LI: [], A: ["href"], STRONG: [], B: [], EM: [], I: [], U: [],
    CODE: [], PRE: [], BLOCKQUOTE: [], BR: [], IMG: ["src", "alt"], TABLE: [], THEAD: [], TBODY: [], TR: [], TH: [], TD: [], HR: [],
  };
  var RENAME = { H1: "H2", H5: "H4", H6: "H4", DIV: "P", SECTION: "P", ARTICLE: "P" };

  function debounce(fn, ms) {
    var t;
    return function () {
      var args = arguments, self = this;
      clearTimeout(t);
      t = setTimeout(function () { fn.apply(self, args); }, ms);
    };
  }

  function clean(html) {
    var tpl = doc.createElement("template");
    tpl.innerHTML = html;
    (function walk(node) {
      Array.prototype.slice.call(node.childNodes).forEach(function (child) {
        if (child.nodeType === 8) { child.remove(); return; }
        if (child.nodeType !== 1) return;
        var tag = child.tagName;
        if (tag === "SCRIPT" || tag === "STYLE" || tag === "META" || tag === "LINK" || tag === "TITLE") { child.remove(); return; }
        if (RENAME[tag]) {
          var renamed = doc.createElement(RENAME[tag]);
          while (child.firstChild) renamed.appendChild(child.firstChild);
          child.replaceWith(renamed);
          child = renamed;
          tag = renamed.tagName;
        }
        walk(child);
        if (!ALLOWED[tag]) {
          // Keep the text, drop the wrapper (spans, fonts, Word markup).
          while (child.firstChild) child.parentNode.insertBefore(child.firstChild, child);
          child.remove();
          return;
        }
        Array.prototype.slice.call(child.attributes).forEach(function (attr) {
          if (ALLOWED[tag].indexOf(attr.name) < 0) child.removeAttribute(attr.name);
        });
      });
    })(tpl.content);
    return tpl.innerHTML;
  }

  function initEditor(form) {
    var editable = form.querySelector("[data-kb-editable]");
    var body = form.querySelector("[data-kb-body]");
    if (!editable || !body) return;
    var toolbar = form.querySelector(".kb-toolbar");
    var block = form.querySelector("[data-kb-block]");
    var imageInput = form.querySelector("[data-kb-image-input]");
    var linkbar = form.querySelector("[data-kb-linkbar]");
    var linkUrl = form.querySelector("[data-kb-link-url]");
    var state = form.querySelector("[data-kb-save-state]");
    var revision = form.querySelector("[data-kb-revision]");
    var mode = form.getAttribute("data-kb-autosave");
    var dirty = false, saving = false, queued = false, submitting = false, savedRange = null;

    try { doc.execCommand("styleWithCSS", false, false); } catch (_) { /* older engines */ }
    try { doc.execCommand("defaultParagraphSeparator", false, "p"); } catch (_) { /* older engines */ }
    if (!editable.innerHTML.trim()) editable.innerHTML = "";

    function sync() { body.value = editable.innerHTML.trim() === "<br>" ? "" : editable.innerHTML; }

    // The title is a textarea so long titles wrap; it stays one logical line.
    var titleBox = form.querySelector("textarea[data-kb-title]");
    function fitTitle() {
      if (!titleBox) return;
      titleBox.style.height = "auto";
      titleBox.style.height = titleBox.scrollHeight + "px";
    }
    if (titleBox) {
      titleBox.addEventListener("keydown", function (e) {
        if (e.key === "Enter") { e.preventDefault(); editable.focus(); }
      });
      titleBox.addEventListener("input", function () {
        if (/\n/.test(titleBox.value)) titleBox.value = titleBox.value.replace(/\s*\n\s*/g, " ");
        fitTitle();
      });
      window.addEventListener("resize", fitTitle);
      fitTitle();
    }

    function setState(text, error) {
      if (!state) return;
      state.textContent = text;
      state.classList.toggle("is-error", !!error);
    }

    function selectionInEditor() {
      var sel = window.getSelection();
      return sel && sel.rangeCount > 0 && editable.contains(sel.getRangeAt(0).commonAncestorContainer) ? sel : null;
    }

    function saveRange() {
      var sel = selectionInEditor();
      savedRange = sel ? sel.getRangeAt(0).cloneRange() : null;
    }

    function restoreRange() {
      editable.focus();
      if (!savedRange) return;
      var sel = window.getSelection();
      sel.removeAllRanges();
      sel.addRange(savedRange);
    }

    function closest(tagNames) {
      var sel = selectionInEditor();
      if (!sel) return null;
      var node = sel.anchorNode;
      while (node && node !== editable) {
        if (node.nodeType === 1 && tagNames.indexOf(node.tagName) >= 0) return node;
        node = node.parentNode;
      }
      return null;
    }

    function updateToolbar() {
      if (!selectionInEditor()) return;
      ["bold", "italic", "insertUnorderedList", "insertOrderedList"].forEach(function (cmd) {
        var btn = toolbar.querySelector('[data-kb-cmd="' + cmd + '"]');
        if (btn) btn.setAttribute("aria-pressed", doc.queryCommandState(cmd) ? "true" : "false");
      });
      var code = toolbar.querySelector('[data-kb-cmd="code"]');
      if (code) code.setAttribute("aria-pressed", closest(["CODE"]) && !closest(["PRE"]) ? "true" : "false");
      var quote = toolbar.querySelector('[data-kb-cmd="quote"]');
      if (quote) quote.setAttribute("aria-pressed", closest(["BLOCKQUOTE"]) ? "true" : "false");
      if (block) {
        var b = closest(["H2", "H3", "H4", "PRE", "P"]);
        block.value = b ? (b.tagName === "H4" ? "h3" : b.tagName.toLowerCase()) : "p";
      }
    }

    function changed() {
      sync();
      dirty = true;
      if (mode) { setState("Unsaved changes"); autosave(); }
    }

    function toggleInlineCode() {
      var sel = selectionInEditor();
      if (!sel) return;
      var existing = closest(["CODE"]);
      if (existing && !closest(["PRE"])) {
        var text = doc.createTextNode(existing.textContent);
        existing.replaceWith(text);
      } else {
        var range = sel.getRangeAt(0);
        var code = doc.createElement("code");
        code.appendChild(range.collapsed ? doc.createTextNode("code") : range.extractContents());
        range.insertNode(code);
        sel.removeAllRanges();
        var after = doc.createRange();
        after.selectNodeContents(code);
        sel.addRange(after);
      }
      changed();
    }

    function toggleQuote() {
      var existing = closest(["BLOCKQUOTE"]);
      if (existing) {
        while (existing.firstChild) existing.parentNode.insertBefore(existing.firstChild, existing);
        existing.remove();
        changed();
      } else {
        doc.execCommand("formatBlock", false, "<blockquote>");
        // Chrome puts text straight into the blockquote; give it a paragraph so it reads like the rest.
        var q = closest(["BLOCKQUOTE"]);
        if (q && !q.querySelector("p")) {
          var p = doc.createElement("p");
          while (q.firstChild) p.appendChild(q.firstChild);
          q.appendChild(p);
        }
        changed();
      }
    }

    function openLinkbar() {
      saveRange();
      var a = closest(["A"]);
      linkbar.hidden = false;
      linkUrl.value = a ? a.getAttribute("href") : "";
      linkbar.querySelector("[data-kb-link-remove]").hidden = !a;
      linkUrl.focus();
    }

    function closeLinkbar() {
      linkbar.hidden = true;
      restoreRange();
    }

    function applyLink() {
      var url = linkUrl.value.trim();
      if (!url) { closeLinkbar(); return; }
      if (!/^(https?:\/\/|mailto:|tel:|\/)/i.test(url)) url = "https://" + url;
      restoreRange();
      var sel = window.getSelection();
      if (sel.rangeCount && sel.getRangeAt(0).collapsed && !closest(["A"])) {
        var a = doc.createElement("a");
        a.href = url;
        a.textContent = url.replace(/^https?:\/\//i, "");
        sel.getRangeAt(0).insertNode(a);
      } else {
        doc.execCommand("createLink", false, url);
      }
      linkbar.hidden = true;
      changed();
    }

    function upload(file, inline) {
      var data = new FormData();
      data.append("file", file);
      if (inline) data.append("inline", "true");
      return window.sb.fetch("/uploads", { method: "POST", body: data }).then(function (r) {
        return r.json().then(function (json) {
          if (!r.ok) throw new Error(json && json.error ? json.error : "The file couldn't be uploaded.");
          return json;
        });
      });
    }

    function insertImage(file) {
      if (!/^image\/(png|jpe?g|gif|webp)$/i.test(file.type)) {
        window.sb.toast("Add images as PNG, JPEG, GIF or WebP.", "error");
        return;
      }
      restoreRange();
      var img = doc.createElement("img");
      img.alt = file.name.replace(/\.[^.]+$/, "");
      img.className = "is-uploading";
      img.src = URL.createObjectURL(file);
      var sel = window.getSelection();
      if (sel.rangeCount && editable.contains(sel.getRangeAt(0).commonAncestorContainer)) sel.getRangeAt(0).insertNode(img);
      else editable.appendChild(img);
      setState("Uploading image…");
      upload(file, true).then(function (saved) {
        URL.revokeObjectURL(img.src);
        img.src = saved.url;
        img.removeAttribute("class");
        changed();
      }).catch(function (err) {
        img.remove();
        setState(err.message, true);
        window.sb.toast(err.message, "error");
      });
    }

    toolbar.addEventListener("mousedown", function (e) {
      // Keep the editor's selection when clicking toolbar buttons.
      if (e.target.closest("button")) e.preventDefault();
    });

    toolbar.addEventListener("click", function (e) {
      var btn = e.target.closest("[data-kb-cmd]");
      if (!btn) return;
      var cmd = btn.getAttribute("data-kb-cmd");
      if (cmd === "image") { saveRange(); imageInput.click(); return; }
      if (cmd === "link") { openLinkbar(); return; }
      editable.focus();
      if (cmd === "code") toggleInlineCode();
      else if (cmd === "quote") toggleQuote();
      else { doc.execCommand(cmd, false, null); changed(); }
      updateToolbar();
    });

    if (block) {
      block.addEventListener("focus", saveRange);
      block.addEventListener("change", function () {
        restoreRange();
        doc.execCommand("formatBlock", false, "<" + block.value + ">");
        changed();
        updateToolbar();
      });
    }

    imageInput.addEventListener("change", function () {
      Array.prototype.forEach.call(imageInput.files, insertImage);
      imageInput.value = "";
    });

    linkbar.querySelector("[data-kb-link-apply]").addEventListener("click", applyLink);
    linkbar.querySelector("[data-kb-link-cancel]").addEventListener("click", closeLinkbar);
    linkbar.querySelector("[data-kb-link-remove]").addEventListener("click", function () {
      restoreRange();
      var a = closest(["A"]);
      if (a) { while (a.firstChild) a.parentNode.insertBefore(a.firstChild, a); a.remove(); }
      linkbar.hidden = true;
      changed();
    });
    linkUrl.addEventListener("keydown", function (e) {
      if (e.key === "Enter") { e.preventDefault(); applyLink(); }
      if (e.key === "Escape") { e.preventDefault(); closeLinkbar(); }
    });

    editable.addEventListener("input", function () {
      // Typing into an empty editor leaves a bare text node; make it a paragraph like the rest.
      var first = editable.firstChild;
      if (first && first.nodeType === 3 && first.textContent.trim()) doc.execCommand("formatBlock", false, "<p>");
      changed();
    });
    editable.addEventListener("keydown", function (e) {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); openLinkbar(); }
    });
    doc.addEventListener("selectionchange", updateToolbar);

    editable.addEventListener("paste", function (e) {
      var data = e.clipboardData;
      if (!data) return;
      var files = Array.prototype.filter.call(data.files || [], function (f) { return /^image\//.test(f.type); });
      if (files.length) { e.preventDefault(); saveRange(); files.forEach(insertImage); return; }
      var html = data.getData("text/html");
      if (html) {
        e.preventDefault();
        doc.execCommand("insertHTML", false, clean(html));
        changed();
      }
    });

    editable.addEventListener("drop", function (e) {
      var files = e.dataTransfer && Array.prototype.filter.call(e.dataTransfer.files || [], function (f) { return /^image\//.test(f.type); });
      if (files && files.length) {
        e.preventDefault();
        if (doc.caretRangeFromPoint) {
          var r = doc.caretRangeFromPoint(e.clientX, e.clientY);
          if (r) { savedRange = r; }
        }
        files.forEach(insertImage);
      }
    });

    // Autosave: the server keeps one working draft, so saving often is cheap.
    var autosave = debounce(function () {
      if (!mode || submitting) return;
      var title = form.querySelector('[name="Input.Title"]');
      if (!title || !title.value.trim()) { setState("Add a title to save a draft."); return; }
      if (saving) { queued = true; return; }
      saving = true;
      setState("Saving…");
      sync();
      var data = new FormData(form);
      data.delete("intent");
      window.sb.fetch(form.getAttribute("action") + "?handler=Autosave", { method: "POST", body: data, headers: { Accept: "application/json" } })
        .then(function (r) {
          return r.json().catch(function () { return {}; }).then(function (json) {
            if (!r.ok) throw new Error(json.error || json.detail || "The draft couldn't be saved. Check your connection.");
            return json;
          });
        })
        .then(function (json) {
          dirty = false;
          if (revision) revision.value = json.revision;
          if (mode === "create" && json.id) {
            mode = "update";
            form.setAttribute("action", json.editUrl);
            form.setAttribute("data-kb-article", json.id);
            if (window.history && window.history.replaceState) window.history.replaceState(null, "", json.editUrl);
          }
          var at = new Date(json.savedAt);
          setState("Draft saved at " + at.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }));
        })
        .catch(function (err) { setState(err.message, true); })
        .then(function () {
          saving = false;
          if (queued) { queued = false; autosave(); }
        });
    }, 2500);

    form.addEventListener("input", function (e) {
      if (e.target === editable || editable.contains(e.target)) return;
      if (e.target.matches("[data-kb-pick], [data-kb-link-url]")) return;
      dirty = true;
      if (mode) { setState("Unsaved changes"); autosave(); }
    });

    form.addEventListener("submit", function () {
      sync();
      submitting = true;
    });

    window.addEventListener("beforeunload", function (e) {
      if (dirty && !submitting) { e.preventDefault(); e.returnValue = ""; }
    });

    // Publish or submit, depending on the chosen folder.
    var folder = form.querySelector("[data-kb-folder]");
    var modes = {};
    (form.getAttribute("data-kb-folder-modes") || "").split(",").forEach(function (pair) {
      var i = pair.indexOf(":");
      if (i > 0) modes[pair.slice(0, i)] = pair.slice(i + 1);
    });
    function syncIntent() {
      if (!folder) return;
      var m = modes[folder.value] || "submit";
      form.querySelectorAll("[data-kb-intent]").forEach(function (b) { b.hidden = b.getAttribute("data-kb-intent") !== m; });
    }
    if (folder) folder.addEventListener("change", syncIntent);

    initPicker(form, changed);
    initFiles(form, upload, function () { dirty = true; if (mode) autosave(); });
    sync();
  }

  function initPicker(form, changed) {
    var input = form.querySelector("[data-kb-pick]");
    var results = form.querySelector("[data-kb-pick-results]");
    var chips = form.querySelector("[data-kb-chips]");
    if (!input || !results || !chips) return;

    function chosen() {
      return Array.prototype.map.call(chips.querySelectorAll('input[name="Input.RelatedIds"]'), function (i) { return i.value; });
    }

    function close() { results.hidden = true; input.setAttribute("aria-expanded", "false"); }

    var search = debounce(function () {
      var q = input.value.trim();
      if (q.length < 2) { close(); return; }
      var exclude = form.getAttribute("data-kb-article") || "";
      window.sb.fetch("/kb/pick?q=" + encodeURIComponent(q) + (exclude ? "&exclude=" + exclude : ""))
        .then(function (r) { return r.ok ? r.json() : []; })
        .then(function (items) {
          results.innerHTML = "";
          var taken = chosen();
          items.filter(function (i) { return taken.indexOf(i.id) < 0; }).forEach(function (item) {
            var li = doc.createElement("li");
            var b = doc.createElement("button");
            b.type = "button";
            b.textContent = item.displayId + "  " + item.title;
            b.addEventListener("click", function () {
              var chip = doc.createElement("li");
              var span = doc.createElement("span");
              span.textContent = item.title;
              var hidden = doc.createElement("input");
              hidden.type = "hidden";
              hidden.name = "Input.RelatedIds";
              hidden.value = item.id;
              var remove = doc.createElement("button");
              remove.type = "button";
              remove.className = "btn btn-ghost btn-sm";
              remove.setAttribute("data-kb-chip-remove", "");
              remove.setAttribute("aria-label", "Remove " + item.title);
              remove.innerHTML = '<i class="bi bi-x" aria-hidden="true"></i>';
              chip.append(span, hidden, remove);
              chips.appendChild(chip);
              input.value = "";
              close();
              input.focus();
              changed();
            });
            li.appendChild(b);
            results.appendChild(li);
          });
          if (!results.children.length) {
            var none = doc.createElement("li");
            none.className = "sb-meta px-2 py-1";
            none.textContent = "No other articles match.";
            results.appendChild(none);
          }
          results.hidden = false;
          input.setAttribute("aria-expanded", "true");
        });
    }, 250);

    input.addEventListener("input", search);
    input.addEventListener("keydown", function (e) {
      if (e.key === "Escape") close();
      if (e.key === "ArrowDown") { var first = results.querySelector("button"); if (first) { e.preventDefault(); first.focus(); } }
      if (e.key === "Enter") e.preventDefault();
    });
    results.addEventListener("keydown", function (e) {
      var btn = e.target.closest("button");
      if (!btn) return;
      var li = btn.parentElement;
      if (e.key === "ArrowDown" && li.nextElementSibling) { e.preventDefault(); li.nextElementSibling.querySelector("button").focus(); }
      if (e.key === "ArrowUp") { e.preventDefault(); if (li.previousElementSibling) li.previousElementSibling.querySelector("button").focus(); else input.focus(); }
      if (e.key === "Escape") { close(); input.focus(); }
    });
    doc.addEventListener("click", function (e) { if (!e.target.closest(".kb-picker")) close(); });
    chips.addEventListener("click", function (e) {
      var btn = e.target.closest("[data-kb-chip-remove]");
      if (!btn) return;
      btn.closest("li").remove();
      changed();
      input.focus();
    });
  }

  function initFiles(form, upload, changed) {
    var add = form.querySelector("[data-kb-file-add]");
    var input = form.querySelector("[data-kb-file-input]");
    var chips = form.querySelector("[data-kb-file-chips]");
    if (!add || !input || !chips) return;
    add.addEventListener("click", function () { input.click(); });
    input.addEventListener("change", function () {
      Array.prototype.forEach.call(input.files, function (file) {
        var chip = doc.createElement("li");
        var icon = doc.createElement("i");
        icon.className = "bi bi-hourglass-split";
        icon.setAttribute("aria-hidden", "true");
        var span = doc.createElement("span");
        span.textContent = file.name;
        chip.append(icon, span);
        chips.appendChild(chip);
        upload(file, false).then(function (saved) {
          icon.className = "bi bi-paperclip";
          var hidden = doc.createElement("input");
          hidden.type = "hidden";
          hidden.name = "Input.AttachmentIds";
          hidden.value = saved.id;
          var remove = doc.createElement("button");
          remove.type = "button";
          remove.className = "btn btn-ghost btn-sm";
          remove.setAttribute("data-kb-chip-remove", "");
          remove.setAttribute("aria-label", "Remove " + saved.fileName);
          remove.innerHTML = '<i class="bi bi-x" aria-hidden="true"></i>';
          chip.append(hidden, remove);
          changed();
        }).catch(function (err) {
          chip.remove();
          window.sb.toast(err.message, "error");
        });
      });
      input.value = "";
    });
    chips.addEventListener("click", function (e) {
      var pending = e.target.closest("[data-kb-chip-remove]");
      var existing = e.target.closest("[data-kb-file-remove]");
      if (pending) { pending.closest("li").remove(); changed(); }
      if (existing) {
        var hidden = doc.createElement("input");
        hidden.type = "hidden";
        hidden.name = "Input.RemoveAttachmentIds";
        hidden.value = existing.getAttribute("data-kb-file-remove");
        form.appendChild(hidden);
        existing.closest("li").remove();
        changed();
      }
    });
  }

  doc.addEventListener("DOMContentLoaded", function () {
    doc.querySelectorAll("form[data-kb-editor]").forEach(initEditor);
  });
})();
